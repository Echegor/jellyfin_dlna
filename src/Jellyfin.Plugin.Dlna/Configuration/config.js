const DlnaConfigurationPage = {
    pluginUniqueId: '33EBA9CD-7DA1-4720-967F-DD7DAE7B74A1',
    defaultDiscoveryInterval: 60,
    defaultAliveInterval: 100,
    loadConfiguration: function (page) {
        ApiClient.getPluginConfiguration(this.pluginUniqueId)
            .then(function(config) {
                page.querySelector('#dlnaPlayTo').checked = config.EnablePlayTo;
                page.querySelector('#dlnaDiscoveryInterval').value = parseInt(config.ClientDiscoveryIntervalSeconds) || DlnaConfigurationPage.defaultDiscoveryInterval;
                page.querySelector('#dlnaBlastAlive').checked = config.BlastAliveMessages;
                page.querySelector('#dlnaAliveInterval').value = parseInt(config.AliveMessageIntervalSeconds) || DlnaConfigurationPage.defaultAliveInterval;
                page.querySelector('#dlnaMatchedHost').checked = config.SendOnlyMatchedHost;

                return ApiClient.getUsers().then(function(users) {
                    const select = page.querySelector('#dlnaSelectUser');
                    select.replaceChildren(new Option('Show user picker', ''));
                    users.forEach(user => select.add(new Option(user.Name, user.Id)));
                    const configured = config.DefaultUserId || '';
                    const matching = Array.from(select.options).find(option =>
                        option.value.replaceAll('-', '').toLowerCase() === configured.replaceAll('-', '').toLowerCase());
                    if (configured && !matching) {
                        select.add(new Option('Configured user (unavailable)', configured));
                    }
                    select.value = matching ? matching.value : configured;
                });
            }).finally(() => Dashboard.hideLoadingMsg());
    },

    save: function(page) {
        Dashboard.showLoadingMsg();
        return ApiClient.getPluginConfiguration(this.pluginUniqueId)
            .then(function(config) {
                config.EnablePlayTo = page.querySelector('#dlnaPlayTo').checked;
                config.ClientDiscoveryIntervalSeconds = parseInt(page.querySelector('#dlnaDiscoveryInterval').value) || DlnaConfigurationPage.defaultDiscoveryInterval;
                config.BlastAliveMessages = page.querySelector('#dlnaBlastAlive').checked;
                config.AliveMessageIntervalSeconds = parseInt(page.querySelector('#dlnaAliveInterval').value) || DlnaConfigurationPage.defaultAliveInterval;
                config.SendOnlyMatchedHost = page.querySelector('#dlnaMatchedHost').checked;
                config.DefaultUserId = page.querySelector('#dlnaSelectUser').value || null;
                return ApiClient.updatePluginConfiguration(DlnaConfigurationPage.pluginUniqueId, config);
            }).then(Dashboard.processPluginConfigurationUpdateResult)
            .finally(() => Dashboard.hideLoadingMsg());
    }
}

export default function(view) {
    view.querySelector('#dlnaForm').addEventListener('submit', function(e) {
        DlnaConfigurationPage.save(view);
        e.preventDefault();
        return false;
    });
    
    window.addEventListener('pageshow', function(_) {
        Dashboard.showLoadingMsg();
        DlnaConfigurationPage.loadConfiguration(view);
    });
}