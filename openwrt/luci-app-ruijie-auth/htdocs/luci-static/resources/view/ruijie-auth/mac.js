'use strict';
'require view';
'require fs';

function exec(args) {
	return fs.exec('/usr/sbin/ruijie-auth', args);
}

function run(args) {
	return exec(args).then(function(res) {
		if (res.code !== 0) {
			window.alert(res.stderr || _('Command failed'));
			return;
		}
		window.setTimeout(function() { window.location.reload(); }, 800);
	}).catch(function(err) {
		window.alert(err && err.message ? err.message : String(err));
	});
}

return view.extend({
	load: function() {
		return exec([ 'mac', 'show', '--json' ]).catch(function() {
			return { stdout: '{}' };
		});
	},

	render: function(res) {
		var info = {};
		try {
			info = JSON.parse(res.stdout || '{}');
		} catch (e) {}

		var sectionInput = E('input', {
			'class': 'cbi-input-text',
			'type': 'text',
			'value': info.section || 'wan'
		});
		var macInput = E('input', {
			'class': 'cbi-input-text',
			'type': 'text',
			'placeholder': 'aa:bb:cc:dd:ee:ff'
		});

		function section() {
			return (sectionInput.value || 'wan').trim();
		}

		return E('div', { 'class': 'cbi-map' }, [
			E('h2', {}, _('MAC address')),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Current')),
				E('table', { 'class': 'table' }, [
					E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td' }, _('Network section')),
						E('td', { 'class': 'td' }, info.section || 'wan')
					]),
					E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td' }, _('Device')),
						E('td', { 'class': 'td' }, info.device || '-')
					]),
					E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td' }, _('Configured override')),
						E('td', { 'class': 'td' }, info.configured || '-')
					]),
					E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td' }, _('Effective MAC')),
						E('td', { 'class': 'td' }, info.effective || '-')
					])
				])
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('h3', {}, _('Change MAC')),
				E('div', { 'class': 'cbi-value' }, [
					E('label', { 'class': 'cbi-value-title' }, _('Network section')),
					E('div', { 'class': 'cbi-value-field' }, [ sectionInput ])
				]),
				E('div', { 'class': 'cbi-value' }, [
					E('label', { 'class': 'cbi-value-title' }, _('New MAC')),
					E('div', { 'class': 'cbi-value-field' }, [ macInput ])
				]),
				E('button', {
					'class': 'btn cbi-button cbi-button-apply',
					'click': function() {
						var mac = (macInput.value || '').trim();
						if (!mac) {
							window.alert(_('Enter a MAC address'));
							return;
						}
						return run([ 'mac', 'set', '--section', section(), mac ]);
					}
				}, _('Set MAC')),
				' ',
				E('button', {
					'class': 'btn cbi-button cbi-button-action',
					'click': function() {
						return run([ 'mac', 'random', '--section', section() ]);
					}
				}, _('Random MAC')),
				' ',
				E('button', {
					'class': 'btn cbi-button cbi-button-reset',
					'click': function() {
						if (!window.confirm(_('Remove the MAC override and restore the hardware MAC?')))
							return;
						return run([ 'mac', 'clear', '--section', section() ]);
					}
				}, _('Restore hardware')),
				' ',
				E('button', {
					'class': 'btn cbi-button cbi-button-action',
					'click': function() { window.location.reload(); }
				}, _('Refresh'))
			]),
			E('div', { 'class': 'cbi-section' }, [
				E('p', {}, _('Setting a MAC writes the macaddr option of the selected network section, runs ifup and reloads the auth daemon. The WAN link may drop briefly. Clone the laptop NIC MAC if the campus binds the account to a MAC, or use a random locally administered MAC for testing.'))
			])
		]);
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
