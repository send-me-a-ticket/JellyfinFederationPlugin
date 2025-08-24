const federationConfigurationPage = {
    pluginUniqueId: "931820b5-4177-4f48-be30-f0a34db3693f",
    loadConfiguration: (page) => {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(federationConfigurationPage.pluginUniqueId).then(
            (config) => {
                federationConfigurationPage.populateServers(page, config.FederatedServers);
                Dashboard.hideLoadingMsg();
            }
        ).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Error loading configuration');
        });
    },
    populateServers: (page, servers) => {
        var serverList = page.querySelector('#serverList');
        serverList.innerHTML = '';

        if (!Array.isArray(servers)) servers = [];

        servers.forEach(function (server) {
            federationConfigurationPage.addServerEntry(page, server);
        });
    },
    addServerEntry: (page, server) => {
        var serverList = page.querySelector('#serverList');
        var entryDiv = document.createElement('div');
        entryDiv.className = 'listItem paperListItem';
        entryDiv.style.padding = '1em';
        entryDiv.style.margin = '0.5em 0';

        var listItemBody = document.createElement('div');
        listItemBody.className = 'listItemBody three-line';

        var urlText = document.createElement('h4');
        urlText.textContent = server.ServerUrl || 'N/A';
        listItemBody.appendChild(urlText);

        var keyText = document.createElement('p');
        keyText.textContent = 'API Key: ' + (server.ApiKey ? 'Set' : 'Not Set');
        listItemBody.appendChild(keyText);

        var portText = document.createElement('p');
        portText.textContent = 'Port: ' + (server.Port || 'N/A');
        listItemBody.appendChild(portText);

        var removeButton = document.createElement('button');
        removeButton.textContent = 'Remove';
        removeButton.className = 'raised button-cancel emby-button';
        removeButton.style.marginLeft = '1em';
        removeButton.addEventListener('click', function () {
            removeFederatedServer(server.ServerUrl);
        });

        entryDiv.appendChild(listItemBody);
        entryDiv.appendChild(removeButton);
        serverList.appendChild(entryDiv);
    },
    removeFederatedServer: function (serverUrl) {
        Dashboard.showLoadingMsg();

        fetch('/Federation/RemoveServer', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ serverUrl: serverUrl })
        })
            .then(function (response) {
                if (!response.ok) throw new Error('Failed to remove server');
                return response.json();
            })
            .then(function (result) {
                Dashboard.hideLoadingMsg();
                federationConfigurationPage.loadConfiguration(view);
            })
            .catch(function (err) {
                Dashboard.hideLoadingMsg();
                Dashboard.alert('Error removing server');
            });
    },
    showAddServerDialog: (page) => {
        page.querySelector('#addServerDialog').style.display = 'block';
    },
    hideAddServerDialog: (page) => {
        page.querySelector('#addServerDialog').style.display = 'none';
        page.querySelector('#newServerUrl').value = '';
        page.querySelector('#newServerApiKey').value = '';
        page.querySelector('#newServerPort').value = '8096';
    },
    addFederatedServer: (page) => {
        var serverUrl = page.querySelector('#newServerUrl').value;
        var apiKey = page.querySelector('#newServerApiKey').value;
        var port = parseInt(page.querySelector('#newServerPort').value, 10);

        if (!serverUrl || !apiKey || !port) {
            Dashboard.alert('Please fill in all fields');
            return;
        }

        console.log('addFederatedServer called');
        console.log('serverUrl:', serverUrl);
        console.log('apiKey:', apiKey);
        console.log('port:', port);

        Dashboard.showLoadingMsg();

        fetch('/Federation/AddServer', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ serverUrl: serverUrl, apiKey: apiKey, port: port })
        })
            .then(function (response) {
                if (!response.ok) throw new Error('Failed to add server');
                return response.json();
            })
            .then(function (result) {
                Dashboard.hideLoadingMsg();
                federationConfigurationPage.hideAddServerDialog(page);
                federationConfigurationPage.loadConfiguration(page);
            })
            .catch(function (err) {
                Dashboard.hideLoadingMsg();
                Dashboard.alert('Error adding server');
            });
    },
    saveConfig: (page, currentConfig) => {
        Dashboard.showLoadingMsg();
        ApiClient.updatePluginConfiguration(federationConfigurationPage.pluginUniqueId, currentConfig).then(function () {
            Dashboard.hideLoadingMsg();
            Dashboard.processPluginConfigurationUpdateResult();
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Error saving configuration');
        });
    },
    resetConfig: (page) => {
        currentConfig = {};
        currentConfig.FederatedServers = [];
        federationConfigurationPage.populateServers(page, currentConfig);
    },
    saveConfiguration: (page, currentConfig) => {
        Dashboard.showLoadingMsg();
        ApiClient.updatePluginConfiguration(federationConfigurationPage.pluginUniqueId, currentConfig).then(function () {
            Dashboard.hideLoadingMsg();
            Dashboard.processPluginConfigurationUpdateResult();
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Error saving configuration');
        });
    }
};

export default function (view) {
    'use strict';

    let currentConfig = {};

    federationConfigurationPage.loadConfiguration(view);

   view.querySelector('#addServer').addEventListener('click', function () {
        try {
            federationConfigurationPage.showAddServerDialog(view);
        } catch (e) {
            console.error(e);
        }
    });

    view.querySelector('#addNewServerBtn').addEventListener('click', function () {
        try {
            federationConfigurationPage.addFederatedServer(view);
        } catch (e) {
            console.error(e);
        }
    });

    view.querySelector('#cancelAddServerBtn').addEventListener('click', function () {
        try {
            federationConfigurationPage.hideAddServerDialog(view);
        } catch (e) {
            console.error(e);
        }
    });

     view.querySelector('#FederationConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        ApiClient.getPluginConfiguration(federationConfigurationPage.pluginUniqueId).then(
            (config) => {
                federationConfigurationPage.saveConfiguration(view, config);
            }
        );
        return false;
    });

    view.querySelector('#resetConfig').addEventListener('click', function () {
        federationConfigurationPage.resetConfig(view);
    });
};
