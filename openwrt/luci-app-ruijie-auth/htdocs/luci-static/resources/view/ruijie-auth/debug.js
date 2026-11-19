'use strict';
'require view';
'require fs';
'require poll';

function exec(args) {
	return fs.exec('/usr/sbin/ruijie-auth', args);
}

return view.extend({
	load: function() {
		return Promise.all([
			exec([ 'version' ]).catch(function() { return { stdout: '' }; }),
			exec([ 'status', '--json' ]).catch(function() { return { stdout: '{}' }; }),
			exec([ 'interfaces', '--json' ]).catch(function() { return { stdout: '[]' }; })
		]);
	},

	render: function(data) {
		var output = E('pre', {
			'style': 'max-height:340px;overflow:auto;background:#f5f5f5;padding:10px;font-size:12px'
		}, _('Run a self-test to see the algorithm vectors.'));

		var versionText = (data[0].stdout || '-').trim();
		var statusText = data[1].stdout || '{}';
		var interfaces = [];
		try {
			interfaces = JSON.parse(data[2].stdout || '[]');
		} catch (e) {}

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

		function run(args) {
			output.textContent = _('Running...');
			return exec(args).then(function(result) {
				output.textContent =
					'$ ruijie-auth ' + args.join(' ') +
					'\nexit=' + result.code +
					'\n' + (result.stdout || '') +
					(result.stderr ? '\n[stderr]\n' + result.stderr : '');
			}).catch(function(err) {
				output.textContent = String(err);
			});
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
