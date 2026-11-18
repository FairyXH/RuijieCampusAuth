'use strict';
'require view';
'require fs';
'require poll';

return view.extend({
	load: function() {
		return fs.read('/var/log/hxxy-auth.log')
			.catch(function() { return ''; });
	},

	render: function(data) {
		var pre = E('pre', {
			'style': 'white-space: pre-wrap; max-height: 70vh; overflow: auto; margin: 0;'
		}, data || _('No log file yet.'));

		poll.add(function() {
			return fs.read('/var/log/hxxy-auth.log').then(function(text) {
				pre.textContent = text || '';
			}).catch(function() {});
		}, 3);

		return E('div', { 'class': 'cbi-map' }, [
			E('h2', {}, _('HXXY Auth Log')),
			E('div', { 'class': 'cbi-section' }, [ pre ])
		]);
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
