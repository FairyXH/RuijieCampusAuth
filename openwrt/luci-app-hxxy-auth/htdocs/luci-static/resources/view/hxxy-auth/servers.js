'use strict';
'require view';
'require fs';

function exec(args) {
	return fs.exec('/usr/sbin/hxxy-auth', args);
}

function parseServers(result) {
	try {
		var value = JSON.parse(result.stdout || '[]');
		return Array.isArray(value) ? value : [];
	} catch (e) {
		return [];
	}
}

function parseDiscovery(result) {
	try {
		return JSON.parse(result.stdout || '{}') || {};
	} catch (e) {
		return {};
	}
}

function run(args, reload) {
	return exec(args).then(function() {
		return exec([ 'reload' ]).catch(function() {});
	}).then(function() {
		if (reload)
			window.location.reload();
	}).catch(function(err) {
		window.alert(err && err.message ? err.message : String(err));
	});
}

return view.extend({
	load: function() {
		return Promise.all([
			exec([ 'servers', 'list', '--json' ]).catch(function() { return { stdout: '[]' }; }),
			exec([ 'discovery', 'show', '--json' ]).catch(function() { return { stdout: '{}' }; })
		]);
	},

	render: function(data) {
		var servers = parseServers(data[0]);
		var discovery = parseDiscovery(data[1]);

		var rows = servers.map(function(server) {
			var lockLabel = server.locked ? _('Unlock') : _('Lock');
			return E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, server.mac || '-'),
				E('td', { 'class': 'td' }, server.name || '-'),
				E('td', { 'class': 'td' }, server.description || '-'),
				E('td', { 'class': 'td' }, String(server.success_count || 0)),
				E('td', { 'class': 'td' }, server.last_success || '-'),
				E('td', { 'class': 'td' }, [
					E('button', {
						'class': 'btn cbi-button cbi-button-action',
						'click': function() {
							return run(server.locked
								? [ 'servers', 'unlock' ]
								: [ 'servers', 'lock', server.mac ], true);
						}
					}, lockLabel),
					' ',
					E('button', {
						'class': 'btn cbi-button cbi-button-action',
						'click': function() {
							var name = window.prompt(_('Name'), server.name || '');
							if (name === null)
								return;
							return run([ 'servers', 'rename', server.mac, name ], true);
						}
					}, _('Rename')),
					' ',
					E('button', {
						'class': 'btn cbi-button cbi-button-remove',
						'click': function() {
							if (!window.confirm(_('Delete this server?')))
								return;
							return run([ 'servers', 'remove', server.mac ], true);
						}
					}, _('Delete'))
				])
			]);
		});

		if (rows.length === 0) {
			rows.push(E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td', 'colspan': 6 }, _('No learned servers yet. Authenticate once and the authenticator will appear here.'))
			]));
		}

		var discoveryTable = E('table', { 'class': 'table' }, [
			E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, _('Interface')),
				E('td', { 'class': 'td' }, discovery.interface || '-')
			]),
			E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, _('Local MAC')),
				E('td', { 'class': 'td' }, discovery.mac || '-')
			]),
			E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, _('Server MAC')),
				E('td', { 'class': 'td' }, discovery.server_mac || '-')
			]),
			E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, _('Updated')),
				E('td', { 'class': 'td' }, discovery.updated_at || '-')
			])
		]);

		return E('div', { 'class': 'cbi-map' }, [
			E('h2', {}, _('HXXY Auth Servers')),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Discovery cache')),
				discoveryTable,
				E('button', {
					'class': 'btn cbi-button cbi-button-reset',
					'click': function() {
						return run([ 'discovery', 'clear' ], true);
					}
				}, _('Clear cache'))
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Learned authenticators')),
				E('table', { 'class': 'table' }, [
					E('tr', { 'class': 'tr' }, [
						E('th', { 'class': 'th' }, _('MAC')),
						E('th', { 'class': 'th' }, _('Name')),
						E('th', { 'class': 'th' }, _('Interface')),
						E('th', { 'class': 'th' }, _('Successes')),
						E('th', { 'class': 'th' }, _('Last success')),
						E('th', { 'class': 'th' }, _('Actions'))
					])
				].concat(rows)),
				E('button', {
					'class': 'btn cbi-button cbi-button-action',
					'click': function() { window.location.reload(); }
				}, _('Refresh'))
			])
		]);
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
