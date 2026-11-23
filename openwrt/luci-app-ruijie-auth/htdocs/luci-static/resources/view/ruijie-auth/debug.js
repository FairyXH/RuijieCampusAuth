'use strict';
'require view';
'require fs';
'require poll';
'require uci';

function exec(args) {
	return fs.exec('/usr/sbin/ruijie-auth', args);
}

function parseJSON(text, fallback) {
	try {
		return JSON.parse(text || '');
	} catch (e) {
		return fallback;
	}
}

return view.extend({
	load: function() {
		return Promise.all([
			uci.load('ruijie-auth'),
			exec([ 'version' ]).catch(function() { return { stdout: '' }; }),
			exec([ 'status', '--json' ]).catch(function() { return { stdout: '{}' }; }),
			exec([ 'interfaces', '--json' ]).catch(function() { return { stdout: '[]' }; }),
			exec([ 'mac', 'show', '--json' ]).catch(function() { return { stdout: '{}' }; })
		]);
	},

	render: function(data) {
		var output = E('pre', {
			'style': 'max-height:340px;overflow:auto;background:#f5f5f5;padding:10px;font-size:12px'
		}, _('Run a self-test to see the algorithm vectors.'));

		var probeOutput = E('pre', {
			'style': 'max-height:340px;overflow:auto;background:#f5f5f5;padding:10px;font-size:12px'
		}, _('Run the probe to see the egress path.'));

		var tailOutput = E('pre', {
			'style': 'max-height:340px;overflow:auto;background:#f5f5f5;padding:10px;font-size:12px'
		}, _('Generate a tail to see the 569-byte frame.'));

		var versionText = (data[1].stdout || '-').trim();
		var statusText = data[2].stdout || '{}';
		var interfaces = parseJSON(data[3].stdout || '[]', []);
		var macInfo = parseJSON(data[4].stdout || '{}', {});

		var interfaceRows = interfaces.map(function(item) {
			return E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, item.name),
				E('td', { 'class': 'td' }, item.logical || '-'),
				E('td', { 'class': 'td' }, item.mac || '-'),
				E('td', { 'class': 'td' }, item.carrier ? _('Link up') : _('No link')),
				E('td', { 'class': 'td' }, item.is_wan ? _('Campus WAN') : _('Other'))
			]);
		});
		if (interfaceRows.length === 0) {
			interfaceRows.push(E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td', 'colspan': 5 }, _('No interfaces detected'))
			]));
		}

		var probeDevice = E('select', {
			'class': 'cbi-input-select'
		}, [
			E('option', { 'value': '' }, _('Auto (default route)'))
		].concat(interfaces.map(function(item) {
			var label = item.name;
			if (item.logical)
				label += ' (' + item.logical + ')';
			return E('option', { 'value': item.name }, label);
		})));

		var accounts = uci.sections('ruijie-auth', 'account') || [];
		var account = accounts.filter(function(s) { return s.active === '1'; })[0] ||
			accounts.filter(function(s) { return s.enabled !== '0'; })[0] ||
			accounts[0];

		function run(args, target, display) {
			target = target || output;
			target.textContent = _('Running...');
			return exec(args).then(function(result) {
				target.textContent =
					'$ ruijie-auth ' + (display || args.join(' ')) +
					'\n' + _('exit code') + '=' + result.code +
					'\n' + (result.stdout || '') +
					(result.stderr ? '\n[' + _('stderr') + ']\n' + result.stderr : '');
			}).catch(function(err) {
				target.textContent = String(err);
			});
		}

		function runProbe() {
			var args = [ 'probe' ];
			if (probeDevice.value)
				args.push('--device', probeDevice.value);
			return run(args, probeOutput);
		}

		function generateTail() {
			if (!account || !account.username || !account.password) {
				window.alert(_('No active account with a password.'));
				return;
			}
			var mac = macInfo.effective || macInfo.configured || '';
			if (!mac) {
				window.alert(_('No WAN MAC available.'));
				return;
			}
			var args = [
				'tail',
				'--username', account.username,
				'--password', account.password,
				'--mac', mac
			];
			var service = uci.get('ruijie-auth', 'main', 'service_name');
			if (service)
				args.push('--service-name', service);
			var staticID = uci.get('ruijie-auth', 'main', 'static_id');
			if (staticID)
				args.push('--static-id', staticID);
			return run(args, tailOutput, _('tail (credentials hidden)'));
		}

		return E('div', { 'class': 'cbi-map' }, [
			E('h2', {}, _('Debug')),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Build')),
				E('table', { 'class': 'table' }, [
					E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td' }, _('Version')),
						E('td', { 'class': 'td' }, E('code', {}, versionText || '-'))
					])
				])
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Detected interfaces')),
				E('table', { 'class': 'table' }, [
					E('tr', { 'class': 'tr' }, [
						E('th', { 'class': 'th' }, _('Device')),
						E('th', { 'class': 'th' }, _('Logical interface')),
						E('th', { 'class': 'th' }, _('MAC')),
						E('th', { 'class': 'th' }, _('Link')),
						E('th', { 'class': 'th' }, _('Role'))
					])
				].concat(interfaceRows))
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Protocol self-test')),
				E('p', {}, _('Checks RC4, MD4, MSCHAPv2, the 569-byte private tail, ' +
					'the obfuscation transform and the private-property parser.')),
				E('button', {
					'class': 'btn cbi-button cbi-button-action',
					'click': function() { return run([ 'selftest' ]); }
				}, _('Run self-test')),
				' ',
				E('button', {
					'class': 'btn cbi-button',
					'click': function() { return run([ 'interfaces', '--json' ]); }
				}, _('Dump interfaces')),
				' ',
				E('button', {
					'class': 'btn cbi-button',
					'click': function() { return run([ 'status', '--json' ]); }
				}, _('Dump status')),
				' ',
				E('button', {
					'class': 'btn cbi-button cbi-button-reset',
					'click': function() {
						return exec([ 'discovery', 'clear' ]).then(function() {
							output.textContent = _('Discovery cache cleared.');
						});
					}
				}, _('Clear discovery cache')),
				E('div', { 'style': 'margin-top:10px' }, output)
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Connectivity probe')),
				E('p', {}, _('Checks DNS, TCP 53 and HTTP egress. Bind to the campus WAN if the default route is another uplink.')),
				E('div', { 'class': 'cbi-value' }, [
					E('label', { 'class': 'cbi-value-title' }, _('Egress device')),
					E('div', { 'class': 'cbi-value-field' }, [ probeDevice ])
				]),
				E('button', {
					'class': 'btn cbi-button cbi-button-action',
					'click': runProbe
				}, _('Run probe')),
				E('div', { 'style': 'margin-top:10px' }, probeOutput)
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Private tail generator (debug)')),
				E('p', {}, _('Builds the 569-byte private tail from the active account and WAN MAC. Debug only.')),
				E('p', {}, _('Reads the active account password from UCI and passes it to the local CLI.')),
				E('button', {
					'class': 'btn cbi-button cbi-button-action',
					'click': generateTail
				}, _('Generate tail')),
				E('div', { 'style': 'margin-top:10px' }, tailOutput)
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Raw status document')),
				E('pre', {
					'style': 'max-height:340px;overflow:auto;background:#f5f5f5;padding:10px;font-size:12px'
				}, statusText)
			])
		]);
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
