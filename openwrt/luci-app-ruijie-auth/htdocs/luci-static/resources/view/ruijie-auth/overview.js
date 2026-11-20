'use strict';
'require view';
'require fs';
'require poll';

return view.extend({
	load: function() {
		return fs.exec('/usr/sbin/ruijie-auth', [ 'status', '--json' ])
			.catch(function() { return { stdout: '' }; });
	},

	render: function(res) {
		var status = {};
		try {
			status = JSON.parse(res.stdout || '{}');
		} catch (e) {}

		function stateLabel(state) {
			var labels = {
				starting: _('Starting'),
				authenticating: _('Authenticating'),
				authenticated: _('Authenticated'),
				retrying: _('Retrying'),
				paused: _('Paused'),
				idle: _('Idle'),
				disabled: _('Disabled'),
				error: _('Error'),
				stopped: _('Stopped')
			};
			return labels[state] || state || _('Unknown');
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

		function badge(state) {
			var map = {
				authenticated: 'cbi-button-success',
				error: 'cbi-button-negative',
				authenticating: 'cbi-button-action',
				retrying: 'cbi-button-action',
				paused: 'cbi-button-reset'
			};
			return E('span', {
				'class': 'btn ' + (map[state] || ''),
				'style': 'cursor:default'
			}, stateLabel(state));
		}

		var stateCell = E('td', { 'class': 'td' }, badge(status.state));
		var messageCell = E('td', { 'class': 'td' }, status.message || '-');
		var accountCell = E('td', { 'class': 'td' }, status.account || '-');
		var interfaceCell = E('td', { 'class': 'td' }, status.interface || '-');
		var serverCell = E('td', { 'class': 'td' }, status.server || '-');
		var modeCell = E('td', { 'class': 'td' }, authModeLabel(status.auth_mode));
		var profileCell = E('td', { 'class': 'td' }, status.profile || _('Not imported'));
		var reauthCell = E('td', { 'class': 'td' },
			status.reauth_interval ? String(status.reauth_interval) + ' ' + _('s') : '-');
		var updatedCell = E('td', { 'class': 'td' }, status.time || '-');

		function row(label, cell) {
			return E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td', 'width': '30%' }, label),
				cell
			]);
		}

		var summary = E('table', { 'class': 'table' }, [
			row(_('State'), stateCell),
			row(_('Message'), messageCell),
			row(_('Account'), accountCell),
			row(_('Interface'), interfaceCell),
			row(_('Server'), serverCell),
			row(_('Authentication mode'), modeCell),
			row(_('School profile'), profileCell),
			row(_('Re-auth interval'), reauthCell),
			row(_('Updated'), updatedCell)
		]);

		var propertyBody = E('tbody');
		function renderProperties(properties) {
			while (propertyBody.firstChild)
				propertyBody.removeChild(propertyBody.firstChild);
			if (!properties || properties.length === 0) {
				propertyBody.appendChild(E('tr', { 'class': 'tr' }, [
					E('td', { 'class': 'td', 'colspan': 3 },
						_('No server properties captured yet'))
				]));
				return;
			}
			properties.forEach(function(property) {
				propertyBody.appendChild(E('tr', { 'class': 'tr' }, [
					E('td', { 'class': 'td' }, property.name),
					E('td', { 'class': 'td' }, property.value || '-'),
					E('td', { 'class': 'td' }, E('code', {}, property.hex || ''))
				]));
			});
		}
		renderProperties(status.properties);

		var propertyTable = E('table', { 'class': 'table' }, [
			E('thead', { 'class': 'thead' }, E('tr', { 'class': 'tr' }, [
				E('th', { 'class': 'th' }, _('Property')),
				E('th', { 'class': 'th' }, _('Value')),
				E('th', { 'class': 'th' }, _('Hex'))
			])),
			propertyBody
		]);

		poll.add(function() {
			return fs.exec('/usr/sbin/ruijie-auth', [ 'status', '--json' ])
				.then(function(r) {
					var next = {};
					try {
						next = JSON.parse(r.stdout || '{}');
					} catch (e) { return; }
					stateCell.innerHTML = '';
					stateCell.appendChild(badge(next.state));
					messageCell.textContent = next.message || '-';
					accountCell.textContent = next.account || '-';
					interfaceCell.textContent = next.interface || '-';
					serverCell.textContent = next.server || '-';
					modeCell.textContent = authModeLabel(next.auth_mode);
					profileCell.textContent = next.profile || _('Not imported');
					reauthCell.textContent = next.reauth_interval ?
						String(next.reauth_interval) + ' ' + _('s') : '-';
					updatedCell.textContent = next.time || '-';
					renderProperties(next.properties);
				})
				.catch(function() {});
		}, 3);

		function run(command) {
			return fs.exec('/usr/sbin/ruijie-auth', [ command ]).then(function(result) {
				if (result.code !== 0) {
					window.alert(result.stderr || _('Command failed'));
					return;
				}
				window.setTimeout(function() { window.location.reload(); }, 300);
			}).catch(function(err) {
				window.alert(err && err.message ? err.message : String(err));
			});
		}

		function button(label, command, style) {
			return E('button', {
				'class': 'btn cbi-button ' + (style || 'cbi-button-action'),
				'click': function() { return run(command); }
			}, label);
		}

		return E('div', { 'class': 'cbi-map' }, [
			E('h2', {}, _('Ruijie Authenticator')),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Status')),
				summary
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Actions')),
				button(_('Authenticate now'), 'auth', 'cbi-button-apply'),
				' ',
				button(_('Logoff'), 'logoff', 'cbi-button-reset'),
				' ',
				button(_('Pause'), 'pause'),
				' ',
				button(_('Resume'), 'resume', 'cbi-button-apply'),
				' ',
				button(_('Reload configuration'), 'reload')
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Server properties')),
				E('p', {}, _('Decoded private properties from the last EAP-Success or EAP-Failure frame.')),
				propertyTable
			])
		]);
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
