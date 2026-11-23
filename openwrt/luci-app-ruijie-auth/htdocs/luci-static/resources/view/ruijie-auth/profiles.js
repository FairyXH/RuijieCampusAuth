'use strict';
'require view';
'require fs';
'require uci';
'require ui';

var PROFILE_DIR = '/etc/ruijie-auth/profiles';

function slugify(name) {
	var base = String(name || 'profile').replace(/[^\w\u4e00-\u9fa5.-]+/g, '_');
	return base.replace(/^_+|_+$/g, '') || 'profile';
}

function authModeLabel(mode) {
	var labels = {
		EAPMD5: _('EAP-MD5'),
		PEAP: _('PEAP + MSCHAPv2'),
		PEAPV1: _('PEAPv1 + MSCHAPv2'),
		'EAP-TLS': _('EAP-TLS')
	};
	return labels[mode] || mode || '-';
}

function dhcpModeLabel(mode) {
	var labels = {
		none: _('None'),
		before: _('Renew before authentication'),
		after: _('Renew after authentication')
	};
	return labels[mode] || mode || '-';
}

return view.extend({
	load: function() {
		return Promise.all([
			uci.load('ruijie-auth'),
			fs.exec('/usr/sbin/ruijie-auth', [ 'profile', 'list', '--json' ])
				.catch(function() { return { stdout: '[]' }; })
		]);
	},

	render: function(data) {
		var installed = [];
		try {
			installed = JSON.parse(data[1].stdout || '[]');
		} catch (e) {}

		var current = {
			name: uci.get('ruijie-auth', 'main', 'profile_name') || '',
			auth_mode: uci.get('ruijie-auth', 'main', 'auth_mode') || 'EAPMD5',
			school_code: uci.get('ruijie-auth', 'main', 'school_code') || '',
			service_name: uci.get('ruijie-auth', 'main', 'service_name') || '',
			static_id: uci.get('ruijie-auth', 'main', 'static_id') || '',
			self_service_url: uci.get('ruijie-auth', 'main', 'self_service_url') || '',
			update_url: uci.get('ruijie-auth', 'main', 'update_url') || '',
			portal_url: uci.get('ruijie-auth', 'main', 'portal_url') || '',
			dhcp_mode: uci.get('ruijie-auth', 'main', 'dhcp_mode') || 'none',
			engine_version: uci.get('ruijie-auth', 'main', 'engine_version') || ''
		};

		function currentRow(label, value) {
			return E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td', 'width': '30%' }, label),
				E('td', { 'class': 'td' }, value || '-')
			]);
		}

		var currentTable = E('table', { 'class': 'table' }, [
			currentRow(_('Profile name'), current.name),
			currentRow(_('Authentication mode'), authModeLabel(current.auth_mode)),
			currentRow(_('School code'), current.school_code),
			currentRow(_('Service name'), current.service_name),
			currentRow(_('Static ID'), current.static_id),
			currentRow(_('Self-service URL'), current.self_service_url),
			currentRow(_('Update URL'), current.update_url),
			currentRow(_('Portal URL'), current.portal_url),
			currentRow(_('DHCP mode'), dhcpModeLabel(current.dhcp_mode)),
			currentRow(_('Engine version'), current.engine_version)
		]);

		function reloadDaemon() {
			return fs.exec('/usr/sbin/ruijie-auth', [ 'reload' ]).catch(function() {});
		}

		function applyProfile(doc, rawText, sourcePath) {
			var fields = [];
			function push(key, value) {
				if (value === undefined || value === null || value === '')
					return;
				fields.push([ key, String(value) ]);
			}
			fields.push([ 'profile_name', doc.name || _('Imported profile') ]);
			fields.push([ 'auth_mode', doc.auth_mode || 'EAPMD5' ]);
			push('school_code', doc.school_code);
			push('service_name', doc.service_name);
			push('static_id', doc.static_id);
			push('self_service_url', doc.self_service_url);
			push('update_url', doc.update_url);
			push('portal_url', doc.portal_url);
			push('sam_host', doc.sam_host);
			if (doc.sam_port > 0)
				push('sam_port', doc.sam_port);
			if ([ 'before', 'after', 'none' ].indexOf(doc.dhcp_mode) >= 0)
				push('dhcp_mode', doc.dhcp_mode);
			if (doc.dhcp_timeout > 0)
				push('dhcp_timeout', doc.dhcp_timeout);
			fields.push([ 'dhcp_option82', doc.dhcp_option82 ? '1' : '0' ]);
			push('ad_domain', doc.ad_domain);
			if (doc.ad_timeout > 0)
				push('ad_timeout', doc.ad_timeout);
			push('engine_version', doc.engine_version);
			if (sourcePath)
				push('profile_path', sourcePath);

			var chain = Promise.resolve();
			if (rawText) {
				var path = sourcePath || (PROFILE_DIR + '/' + slugify(doc.name) + '.json');
				chain = fs.write(path, rawText).then(function() {
					fields.push([ 'profile_path', path ]);
				}).catch(function() {
					/* A read-only overlay is fine: the values still apply. */
				});
			}
			return chain.then(function() {
				fields.forEach(function(pair) {
					uci.set('ruijie-auth', 'main', pair[0], pair[1]);
				});
				return uci.save();
			}).then(function() {
				return uci.apply();
			}).then(reloadDaemon).then(function() {
				window.location.reload();
			});
		}

		function importInstalled(path) {
			return fs.exec('/usr/sbin/ruijie-auth', [ 'profile', 'import', '--file', path ])
				.then(function(result) {
					if (result.code !== 0) {
						ui.addNotification(null, E('p', {}, result.stderr || _('Import failed')), 'danger');
						return;
					}
					return reloadDaemon().then(function() {
						window.location.reload();
					});
				})
				.catch(function(err) {
					ui.addNotification(null, E('p', {}, String(err)), 'danger');
				});
		}

		function showProfile(entry) {
			return fs.exec('/usr/sbin/ruijie-auth', [ 'profile', 'show', '--file', entry.path ])
				.then(function(result) {
					ui.showModal(_('Profile details') + ': ' + (entry.name || entry.path), [
						E('pre', {
							'style': 'white-space:pre-wrap;max-height:60vh;overflow:auto;margin:0'
						}, result.stdout || result.stderr || ''),
						E('div', { 'class': 'right', 'style': 'margin-top:12px' }, [
							E('button', {
								'class': 'btn',
								'click': function() { ui.hideModal(); }
							}, _('Close'))
						])
					]);
				})
				.catch(function(err) {
					ui.addNotification(null, E('p', {}, String(err)), 'danger');
				});
		}

		var installedBody = E('tbody');
		if (installed.length === 0) {
			installedBody.appendChild(E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td', 'colspan': 3 },
					_('No profile files found in /etc/ruijie-auth/profiles or /usr/share/ruijie-auth/profiles'))
			]));
		}
		else {
			installed.forEach(function(entry) {
				installedBody.appendChild(E('tr', { 'class': 'tr' }, [
					E('td', { 'class': 'td' }, entry.name || '-'),
					E('td', { 'class': 'td' }, E('code', {}, entry.path)),
					E('td', { 'class': 'td' }, [
						E('button', {
							'class': 'btn cbi-button',
							'click': function() { return showProfile(entry); }
						}, _('View')),
						' ',
						E('button', {
							'class': 'btn cbi-button cbi-button-apply',
							'click': function() { return importInstalled(entry.path); }
						}, _('Import'))
					])
				]));
			});
		}

		var installedTable = E('table', { 'class': 'table' }, [
			E('thead', { 'class': 'thead' }, E('tr', { 'class': 'tr' }, [
				E('th', { 'class': 'th' }, _('Profile')),
				E('th', { 'class': 'th' }, _('File')),
				E('th', { 'class': 'th' }, _('Action'))
			])),
			installedBody
		]);

		var textarea = E('textarea', {
			'class': 'cbi-input-textarea',
			'style': 'width:100%;min-height:260px;font-family:monospace'
		});
		var preview = E('div');
		var pending = null;

		function parseInput() {
			preview.innerHTML = '';
			pending = null;
			var text = textarea.value.trim();
			if (!text) {
				preview.appendChild(E('p', {}, _('Paste a profile JSON document first.')));
				return;
			}
			try {
				var doc = JSON.parse(text);
				if (!doc || typeof doc !== 'object' || Array.isArray(doc))
					throw new Error(_('the JSON document must be an object'));
				pending = { doc: doc, text: text };
				preview.appendChild(E('table', { 'class': 'table' }, [
					E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td' }, _('Profile name')),
						E('td', { 'class': 'td' }, doc.name || '-')
					]),
					E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td' }, _('Authentication mode')),
						E('td', { 'class': 'td' }, authModeLabel(doc.auth_mode || 'EAPMD5'))
					]),
					E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td' }, _('Service name')),
						E('td', { 'class': 'td' }, doc.service_name || '-')
					]),
					E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td' }, _('Static ID')),
						E('td', { 'class': 'td' }, doc.static_id || '-')
					]),
					E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td' }, _('School code')),
						E('td', { 'class': 'td' }, doc.school_code || '-')
					])
				]));
			} catch (e) {
				preview.appendChild(E('p', { 'class': 'alert-message danger' },
					_('Invalid JSON: ') + e.message));
			}
		}

		var fileInput = E('input', {
			'type': 'file',
			'accept': '.json,application/json',
			'change': function(ev) {
				var file = ev.target.files && ev.target.files[0];
				if (!file)
					return;
				var reader = new FileReader();
				reader.onload = function() {
					textarea.value = String(reader.result || '');
					parseInput();
				};
				reader.readAsText(file);
			}
		});

		var uploadSection = E('div', { 'class': 'cbi-section' }, [
			E('h3', {}, _('Upload or paste a school profile')),
			E('p', {}, _('The JSON schema is identical to the desktop client profiles/*.json.')),
			E('div', {}, fileInput),
			E('div', { 'style': 'margin-top:8px' }, textarea),
			E('div', { 'style': 'margin-top:8px' }, [
				E('button', {
					'class': 'btn cbi-button',
					'click': function() { parseInput(); }
				}, _('Validate')),
				' ',
				E('button', {
					'class': 'btn cbi-button cbi-button-apply',
					'click': function() {
						if (!pending) {
							parseInput();
							if (!pending) {
								ui.addNotification(null,
									E('p', {}, _('Validate the profile before applying it.')), 'warning');
								return;
							}
						}
						return applyProfile(pending.doc, pending.text, null);
					}
				}, _('Apply profile'))
			]),
			E('div', { 'style': 'margin-top:12px' }, preview)
		]);

		return E('div', { 'class': 'cbi-map' }, [
			E('h2', {}, _('School profiles')),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Active profile')),
				currentTable
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Installed profiles')),
				installedTable
			]),
			uploadSection
		]);
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
