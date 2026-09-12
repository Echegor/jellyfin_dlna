const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const alice = '74970596cb7849c8acf8dd8a1353e776';
const evelyn = '11111111222233334444555555555555';
let config = { DefaultUserId: '74970596-cb78-49c8-acf8-dd8a1353e776' };
let saved;
const controls = {};
const select = { options: [], value: '', replaceChildren(...values) { this.options = values; }, add(value) { this.options.push(value); } };
controls['#dlnaSelectUser'] = select;
const view = { querySelector(id) { return controls[id] ??= {}; } };
const context = vm.createContext({
    Option: function(text, value) { this.text = text; this.value = value; },
    ApiClient: {
        getPluginConfiguration: async () => ({ ...config }),
        getUsers: async () => [{ Name: 'Admin', Id: alice }, { Name: 'Evelyn', Id: evelyn }],
        updatePluginConfiguration: async (_, value) => { saved = value; }
    },
    Dashboard: { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} }
});
const source = fs.readFileSync(path.join(__dirname, '../src/Jellyfin.Plugin.Dlna/Configuration/config.js'), 'utf8');
vm.runInContext(source.slice(0, source.indexOf('export default')) + '\nglobalThis.page = DlnaConfigurationPage;', context);
(async () => {
    await context.page.loadConfiguration(view);
    assert.deepEqual(select.options.map(o => o.text), ['None', 'Admin', 'Evelyn']);
    assert.equal(select.value, alice);
    select.value = evelyn;
    await context.page.save(view);
    assert.equal(saved.DefaultUserId, evelyn);
    select.value = '';
    await context.page.save(view);
    assert.equal(saved.DefaultUserId, null);
    config.DefaultUserId = null;
    await context.page.loadConfiguration(view);
    assert.equal(select.value, '');
    config.DefaultUserId = 'deleted-user';
    await context.page.loadConfiguration(view);
    assert.equal(select.value, 'deleted-user');
    assert.equal(select.options.length, 4);
    console.log('Default-user dropdown: population, GUID matching, user/None saving, and unavailable-user handling passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
