'use strict';
'require view';
'require form';
'require fs';

return view.extend({
	render: function() {
		var m = new form.Map('hxxy-auth', _('HXXY Auth Settings'),
			_('These values are consumed by the hxxy-auth daemon on reload.'));

		var s = m.section(form.NamedSection, 'main', 'auth', _('Authentication'));
		s.anonymous = true;

		var o = s.option(form.Value, 'device', _('Device'));
		o.default = 'wan';
		o.placeholder = 'wan';
		o.rmempty = false;

		o = s.option(form.Value, 'service_name', _('Service name'));
		o.default = '电信宽带服务';
		o.rmempty = false;

		o = s.option(form.Value, 'static_id', _('Static ID'));
		o.default = 'Static:AB45A862';
		o.rmempty = false;

		o = s.option(form.Value, 'timeout', _('Authentication timeout (s)'));
		o.datatype = 'uinteger';
		o.default = '20';

		o = s.option(form.Value, 'retries', _('Start retries'));
		o.datatype = 'uinteger';
		o.default = '4';

		o = s.option(form.Value, 'repeats', _('Frames per request'));
		o.datatype = 'uinteger';
		o.default = '3';

		o = s.option(form.Value, 'start_timeout', _('Start retry interval (s)'));
		o.datatype = 'ufloat';
		o.default = '3';

		o = s.option(form.Value, 'reauth_interval', _('Periodic re-auth interval (s)'));
		o.datatype = 'uinteger';
		o.default = '21600';

		o = s.option(form.Value, 'check_interval', _('Online check interval (s)'));
		o.datatype = 'ufloat';
		o.default = '2';

		o = s.option(form.Value, 'retry_interval', _('Retry interval (s)'));
		o.datatype = 'ufloat';
		o.default = '3';

		o = s.option(form.Flag, 'autoguard', _('Auto guard (re-auth on offline)'));
		o.default = '1';

		o = s.option(form.Flag, 'preempt', _('Continuous preemption (periodic re-auth)'));
		o.default = '1';

		o = s.option(form.Flag, 'discovery_cache', _('Discovery cache'));
		o.default = '1';

		o = s.option(form.Flag, 'logoff_on_stop', _('Send Logoff on stop'));
		o.default = '0';

		o = s.option(form.Flag, 'pause_on_home', _('Pause on non-802.1X network'));
		o.default = '1';

		o = s.option(form.Value, 'connectivity_url', _('Connectivity probe URL'));
		o.default = 'http://www.baidu.com/';

		o = s.option(form.ListValue, 'disguise', _('Disguise level'));
		o.value('off', _('Off'));
		o.value('basic', _('Basic'));
		o.value('full', _('Full (UA3F)'));
		o.default = 'basic';

		o = s.option(form.ListValue, 'log_level', _('Log level'));
		o.value('error', _('Error'));
		o.value('info', _('Info'));
		o.value('debug', _('Debug'));
		o.default = 'info';

		return m.render();
	},

	handleSaveApply: function(ev, mode) {
		return this.super('handleSaveApply', [ ev, mode ]).then(function() {
			return fs.exec('/usr/sbin/hxxy-auth', [ 'reload' ]).catch(function() {});
		});
	}
});
