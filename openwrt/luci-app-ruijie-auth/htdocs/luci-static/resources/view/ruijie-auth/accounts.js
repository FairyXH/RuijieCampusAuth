'use strict';
'require view';
'require form';
'require uci';
'require fs';

return view.extend({
	load: function() {
		return uci.load('ruijie-auth');
	},

	render: function() {
		var m = new form.Map('ruijie-auth', _('Accounts'),
			_('Up to 5 accounts. The account marked Current is tried first; ' +
			  'if it fails, the remaining enabled accounts are tried in order.'));

		var s = m.section(form.GridSection, 'account', _('Account pool'));
		s.addremove = true;
		s.anonymous = true;
		s.sortable = true;

		var o = s.option(form.Value, 'username', _('Username'));
		o.rmempty = false;

		o = s.option(form.Value, 'password', _('Password'));
		o.password = true;
		o.modalonly = true;
		o.rmempty = false;

		o = s.option(form.Value, 'note', _('Note'));

		o = s.option(form.Flag, 'enabled', _('Enabled'));
		o.default = '1';

		o = s.option(form.Flag, 'active', _('Current'));
		o.default = '0';

		return m.render();
	},

	handleSaveApply: function(ev, mode) {
		if (uci.sections('ruijie-auth', 'account').length > 5) {
			window.alert(_('At most 5 accounts are supported'));
			return Promise.reject(new Error('too many accounts'));
		}
		return this.super('handleSaveApply', [ ev, mode ]).then(function() {
			return fs.exec('/usr/sbin/ruijie-auth', [ 'reload' ]).catch(function() {});
		});
	}
});
