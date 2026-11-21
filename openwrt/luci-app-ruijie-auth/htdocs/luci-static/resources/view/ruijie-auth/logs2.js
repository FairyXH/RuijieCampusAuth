'use strict';
'require view';
'require fs';
'require poll';

function readLog() {
	return fs.exec('/usr/sbin/ruijie-auth', [ 'log', '--lines', '400' ])
		.then(function(res) { return res.stdout || ''; })
		.catch(function(err) {
			return _('Log read failed') + ': ' + (err && err.message ? err.message : err);
		});
}

return view.extend({
	load: function() {
		return readLog();
	},

	render: function(data) {
		var pre = E('pre', {
			'style': 'white-space: pre-wrap; max-height: 70vh; overflow: auto; margin: 0;'
		}, data || _('No log file yet.'));

		poll.add(function() {
			return readLog().then(function(text) {
				pre.textContent = text || '';
			});
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
