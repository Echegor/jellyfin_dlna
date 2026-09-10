const DlnaConfigurationPage = {
    pluginUniqueId: '33EBA9CD-7DA1-4720-967F-DD7DAE7B74A1',
    defaultDiscoveryInterval: 60,
    defaultAliveInterval: 100,
    loadConfiguration: function (page) {
        ApiClient.getPluginConfiguration(this.pluginUniqueId)
            .then(function(config) {
                page.querySelector('#dlnaPlayTo').checked = config.EnablePlayTo;
                page.querySelector('#dlnaDiscoveryInterval').value = parseInt(config.ClientDiscoveryIntervalSeconds) || this.defaultDiscoveryInterval;
                page.querySelector('#dlnaBlastAlive').checked = config.BlastAliveMessages;
                page.querySelector('#dlnaAliveInterval').value = parseInt(config.AliveMessageIntervalSeconds) || this.defaultAliveInterval;
                page.querySelector('#dlnaMatchedHost').checked = config.SendOnlyMatchedHost;

                Dashboard.hideLoadingMsg();
            });
    },

    save: function(page) {
        Dashboard.showLoadingMsg();
        return new Promise((_) => {
            ApiClient.getPluginConfiguration(this.pluginUniqueId)
                .then(function(config) {
                    config.EnablePlayTo = page.querySelector('#dlnaPlayTo').checked;
                    config.ClientDiscoveryIntervalSeconds = parseInt(page.querySelector('#dlnaDiscoveryInterval').value) || this.defaultDiscoveryInterval;
                    config.BlastAliveMessages = page.querySelector('#dlnaBlastAlive').checked;
                    config.AliveMessageIntervalSeconds = parseInt(page.querySelector('#dlnaAliveInterval').value) || this.defaultAliveInterval;
                    config.SendOnlyMatchedHost = page.querySelector('#dlnaMatchedHost').checked;
                    


                    ApiClient.updatePluginConfiguration(DlnaConfigurationPage.pluginUniqueId, config).then(Dashboard.processPluginConfigurationUpdateResult);
                });
        })
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