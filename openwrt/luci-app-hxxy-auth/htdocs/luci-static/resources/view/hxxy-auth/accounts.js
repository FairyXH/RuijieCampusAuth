'use strict';
'require view';
'require form';
'require uci';
'require fs';

return view.extend({
	load: function() {
		return uci.load('hxxy-auth');
	},

	render: function() {
		var m = new form.Map('hxxy-auth', _('HXXY Auth Accounts'),
			_('最多 5 个账号。勾选 Current 的账号优先使用，失败后按顺序尝试其他已启用账号。'));

		var s = m.section(form.GridSection, 'account', _('Accounts'));
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
		if (uci.sections('hxxy-auth', 'account').length > 5) {
			window.alert(_('最多 5 个账号'));
			return Promise.reject(new Error('too many accounts'));
		}
		return this.super('handleSaveApply', [ ev, mode ]).then(function() {
			return fs.exec('/usr/sbin/hxxy-auth', [ 'reload' ]).catch(function() {});
		});
	}
});
