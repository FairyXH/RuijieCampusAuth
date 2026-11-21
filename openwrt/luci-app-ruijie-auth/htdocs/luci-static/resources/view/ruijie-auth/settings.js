'use strict';
'require view';
'require form';
'require fs';
'require uci';

return view.extend({
	load: function() {
		return Promise.all([
			uci.load('ruijie-auth'),
			fs.exec('/usr/sbin/ruijie-auth', [ 'interfaces', '--json' ])
				.catch(function() { return { stdout: '[]' }; })
		]);
	},

	render: function(data) {
		var interfaces = [];
		try {
			interfaces = JSON.parse(data[1].stdout || '[]');
		} catch (e) {}

		var m = new form.Map('ruijie-auth', _('Settings'),
			_('Values are read by the authentication daemon on reload.'));

		var s = m.section(form.NamedSection, 'main', 'auth');
		s.anonymous = true;
		s.tab('auth', _('Authentication'));
		s.tab('runtime', _('Protocol'));
		s.tab('guard', _('Guard'));
		s.tab('profile', _('Profile data'));
		s.tab('misc', _('Other'));

		var o = s.taboption('auth', form.Flag, 'enabled', _('Enable authentication'));
		o.default = '1';
		o.rmempty = false;

		o = s.taboption('auth', form.ListValue, 'auth_mode', _('Authentication mode'),
			_('EAP-MD5 is the classic wired 802.1X path; PEAP and EAP-TLS use a TLS tunnel.'));
		o.value('EAPMD5', _('EAP-MD5'));
		o.value('PEAP', _('PEAP + MSCHAPv2'));
		o.value('EAP-TLS', _('EAP-TLS'));
		o.default = 'EAPMD5';
		o.rmempty = false;

		o = s.taboption('auth', form.MultiValue, 'device', _('WAN interfaces'),
			_('Leave only Auto-detect, or pick the physical ports that may carry the campus network. ' +
			  'The daemon tries them in this order and prefers one with link.'));
		o.value('auto', _('Auto-detect the campus WAN port'));
		interfaces.forEach(function(item) {
			o.value(item.name, item.description || item.name);
		});
		o.default = 'auto';
		o.rmempty = false;

		o = s.taboption('auth', form.Value, 'service_name', _('Service name'),
			_('Private-tail attribute 0x39. Usually comes from the school profile.'));
		o.placeholder = _('Provided by the school profile');

		o = s.taboption('auth', form.Value, 'static_id', _('Static ID'),
			_('Private-tail attribute 0x54. Usually comes from the school profile.'));
		o.placeholder = _('Provided by the school profile');

		o = s.taboption('auth', form.Value, 'school_code', _('School code'),
			_('Ruijie school code, for reference and diagnostics.'));
		o.placeholder = _('Provided by the school profile');

		o = s.taboption('runtime', form.Value, 'timeout', _('Authentication timeout (s)'));
		o.datatype = 'uinteger';
		o.default = '20';

		o = s.taboption('runtime', form.Value, 'retries', _('EAPOL-Start retries'));
		o.datatype = 'uinteger';
		o.default = '4';

		o = s.taboption('runtime', form.Value, 'start_timeout', _('Start retry interval (s)'));
		o.datatype = 'ufloat';
		o.default = '3';

		o = s.taboption('runtime', form.Value, 'repeats', _('Frames per request'));
		o.datatype = 'uinteger';
		o.default = '3';

		o = s.taboption('runtime', form.Value, 'response_delay', _('Response delay (s)'));
		o.datatype = 'ufloat';
		o.default = '0.04';

		o = s.taboption('runtime', form.Value, 'reauth_interval', _('Periodic re-auth interval (s)'),
			_('The server value SU_REAUTH_INTERV overrides this when present.'));
		o.datatype = 'uinteger';
		o.default = '21600';

		o = s.taboption('runtime', form.Flag, 'private_tail', _('Attach private tail'),
			_('Append the Ruijie 569-byte tail to EAPOL and EAP frames.'));
		o.default = '1';

		o = s.taboption('runtime', form.Flag, 'logoff_first', _('Send Logoff before Start'));
		o.default = '1';

		o = s.taboption('runtime', form.Flag, 'logoff_on_stop', _('Send Logoff on stop'));
		o.default = '0';

		o = s.taboption('guard', form.Flag, 'autoguard', _('Auto guard'),
			_('Re-authenticate when the connectivity probe fails.'));
		o.default = '1';

		o = s.taboption('guard', form.Value, 'check_interval', _('Online check interval (s)'));
		o.datatype = 'ufloat';
		o.default = '2';

		o = s.taboption('guard', form.Flag, 'preempt', _('Periodic preemption'),
			_('Re-authenticate periodically to keep the session alive.'));
		o.default = '1';

		o = s.taboption('guard', form.Flag, 'pause_on_home', _('Pause on a non-802.1X network'),
			_('When no authenticator answers but the probe URL is reachable, wait instead of retrying.'));
		o.default = '1';

		o = s.taboption('guard', form.Value, 'retry_interval', _('Retry interval (s)'));
		o.datatype = 'ufloat';
		o.default = '3';

		o = s.taboption('guard', form.Value, 'connectivity_url', _('Connectivity probe URL'));
		o.default = 'http://www.baidu.com/';

		o = s.taboption('guard', form.Flag, 'discovery_cache', _('Discovery cache'));
		o.default = '1';

		o = s.taboption('profile', form.Value, 'self_service_url', _('Self-service URL'));
		o.placeholder = _('Provided by the school profile');

		o = s.taboption('profile', form.Value, 'update_url', _('Update check URL'));
		o.placeholder = _('Provided by the school profile');

		o = s.taboption('profile', form.Value, 'portal_url', _('Portal URL'));
		o.placeholder = _('Provided by the school profile');

		o = s.taboption('profile', form.Value, 'sam_host', _('SAM host'));
		o.placeholder = _('Optional');

		o = s.taboption('profile', form.Value, 'sam_port', _('SAM port'));
		o.datatype = 'port';
		o.placeholder = '0';

		o = s.taboption('profile', form.ListValue, 'dhcp_mode', _('DHCP mode'));
		o.value('none', _('None'));
		o.value('before', _('Renew before authentication'));
		o.value('after', _('Renew after authentication'));
		o.default = 'none';

		o = s.taboption('profile', form.Value, 'dhcp_timeout', _('DHCP timeout (s)'));
		o.datatype = 'ufloat';
		o.default = '30';

		o = s.taboption('profile', form.Flag, 'dhcp_option82', _('DHCP Option 82'));
		o.default = '0';

		o = s.taboption('profile', form.Value, 'ad_domain', _('AD domain'));
		o.placeholder = _('Optional');

		o = s.taboption('profile', form.Value, 'ad_timeout', _('AD timeout (s)'));
		o.datatype = 'ufloat';
		o.default = '120';

		o = s.taboption('profile', form.Value, 'engine_version', _('Engine version'),
			_('Engine version declared by the matching official client build.'));
		o.placeholder = _('Provided by the school profile');

		o = s.taboption('misc', form.ListValue, 'disguise', _('Frame disguise'),
			_('Basic rewrites TTL and TCP MSS; Full also runs UA3F for IPID, TCP timestamp and window.'));
		o.value('off', _('Off'));
		o.value('basic', _('Basic (TTL/MSS)'));
		o.value('full', _('Full (UA3F + L3 rewrite)'));
		o.default = 'full';

		o = s.taboption('misc', form.ListValue, 'log_level', _('Log level'));
		o.value('error', _('Error'));
		o.value('info', _('Info'));
		o.value('debug', _('Debug'));
		o.default = 'info';

		return m.render();
	},

	handleSaveApply: function(ev, mode) {
		return this.super('handleSaveApply', [ ev, mode ]).then(function() {
			return fs.exec('/usr/sbin/ruijie-auth', [ 'reload' ]).catch(function() {});
		}).then(function() {
			return fs.exec('/etc/init.d/ruijie-disguise', [ 'restart' ]).catch(function() {});
		});
	}
});
