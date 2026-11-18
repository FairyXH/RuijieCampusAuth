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
				iface.textContent = s.interface || '-';
				server.textContent = s.server || '-';
				updated.textContent = s.time || '-';
			}).catch(function() {});
		}, 3);

		return E('div', { 'class': 'cbi-map' }, [
			E('h2', {}, _('HXXY Auth')),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Status')),
				table
			])
		]);
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
