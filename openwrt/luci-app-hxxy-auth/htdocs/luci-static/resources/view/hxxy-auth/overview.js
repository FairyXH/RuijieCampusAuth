'use strict';
'require view';
'require fs';
'require poll';

return view.extend({
	load: function() {
		return fs.exec('/usr/sbin/hxxy-auth', [ 'status', '--json' ])
			.catch(function() { return { stdout: '' }; });
	},

	render: function(res) {
		var status = {};
		try {
			status = JSON.parse(res.stdout || '{}');
		} catch (e) {}

		var state = E('span', {}, status.state || 'unknown');
		var message = E('span', {}, status.message || '');
		var account = E('span', {}, status.account || '-');
		var iface = E('span', {}, status.interface || '-');
		var server = E('span', {}, status.server || '-');
		var updated = E('span', {}, status.time || '-');

		var table = E('table', { 'class': 'table' }, [
			E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, _('State')),
				E('td', { 'class': 'td' }, state)
			]),
			E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, _('Message')),
				E('td', { 'class': 'td' }, message)
			]),
			E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, _('Account')),
				E('td', { 'class': 'td' }, account)
			]),
			E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, _('Interface')),
				E('td', { 'class': 'td' }, iface)
			]),
			E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, _('Server')),
				E('td', { 'class': 'td' }, server)
			]),
			E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, _('Updated')),
				E('td', { 'class': 'td' }, updated)
			])
		]);

		poll.add(function() {
			return fs.exec('/usr/sbin/hxxy-auth', [ 'status', '--json' ]).then(function(r) {
				var s = {};
				try {
					s = JSON.parse(r.stdout || '{}');
				} catch (e) {}
				state.textContent = s.state || 'unknown';
				message.textContent = s.message || '';
				account.textContent = s.account || '-';
				iface.textContent = s.interface || '-';
				server.textContent = s.server || '-';
				updated.textContent = s.time || '-';
			}).catch(function() {});
		}, 3);

		function run(command) {
			return fs.exec('/usr/sbin/hxxy-auth', [ command ]).then(function(res) {
				if (res.code !== 0) {
					window.alert(res.stderr || _('Command failed'));
					return;
				}
				window.setTimeout(function() { window.location.reload(); }, 300);
			}).catch(function(err) {
				window.alert(err && err.message ? err.message : String(err));
			});
		}

		return E('div', { 'class': 'cbi-map' }, [
			E('h2', {}, _('HXXY Auth')),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Status')),
				table
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Actions')),
				E('button', {
					'class': 'btn cbi-button cbi-button-apply',
					'click': function() { return run('auth'); }
				}, _('Auth now')),
				' ',
				E('button', {
					'class': 'btn cbi-button cbi-button-reset',
					'click': function() { return run('logoff'); }
				}, _('Logoff')),
				' ',
				E('button', {
					'class': 'btn cbi-button cbi-button-action',
					'click': function() { return run('pause'); }
				}, _('Pause')),
				' ',
				E('button', {
					'class': 'btn cbi-button cbi-button-action',
					'click': function() { return run('resume'); }
				}, _('Resume')),
				' ',
				E('button', {
					'class': 'btn cbi-button cbi-button-action',
					'click': function() { return run('reload'); }
				}, _('Reload'))
			])
		]);
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
