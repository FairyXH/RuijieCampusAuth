'use strict';
'require view';
'require fs';
'require poll';

return view.extend({
	load: function() {
		return fs.exec('/usr/sbin/ruijie-auth', [ 'log', '--lines', '400' ])
			.then(function(res) { return res.stdout || ''; })
			.catch(function() { return ''; });
	},

	render: function(data) {
		var pre = E('pre', {
			'style': 'white-space: pre-wrap; max-height: 70vh; overflow: auto; margin: 0;'
		}, data || _('No log file yet.'));

		poll.add(function() {
			return fs.exec('/usr/sbin/ruijie-auth', [ 'log', '--lines', '400' ]).then(function(res) {
				pre.textContent = res.stdout || '';
			}).catch(function() {});
		}, 3);

		return E('div', { 'class': 'cbi-map' }, [
			E('h2', {}, _('Logs')),
			E('div', { 'class': 'cbi-section' }, [ pre ])
		]);
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
