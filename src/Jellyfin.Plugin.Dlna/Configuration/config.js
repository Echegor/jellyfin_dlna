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