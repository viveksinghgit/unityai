param name string
param location string
param tags object
@allowed(['Y1', 'EP1', 'EP2', 'EP3'])
param sku string
param storageAccountName string
param appInsightsConnectionString string
param cosmosConnectionSecretUri string
param serviceBusConnectionSecretUri string
param openAiEndpointSecretUri string
param openAiKeySecretUri string
param cognitiveServicesKeySecretUri string
param cognitiveServicesEndpointSecretUri string
param keyVaultName string

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource hostingPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${name}-plan'
  location: location
  tags: tags
  sku: {
    name: sku
    tier: sku == 'Y1' ? 'Dynamic' : 'ElasticPremium'
  }
  properties: {
    reserved: true  // Linux
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      http20Enabled: true
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccountName};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=core.windows.net'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        // All secrets resolved from Key Vault at startup via Key Vault references
        {
          name: 'CosmosConnectionString'
          value: '@Microsoft.KeyVault(SecretUri=${cosmosConnectionSecretUri})'
        }
        {
          name: 'ServiceBusConnectionString'
          value: '@Microsoft.KeyVault(SecretUri=${serviceBusConnectionSecretUri})'
        }
        {
          name: 'OpenAiEndpoint'
          value: '@Microsoft.KeyVault(SecretUri=${openAiEndpointSecretUri})'
        }
        {
          name: 'OpenAiKey'
          value: '@Microsoft.KeyVault(SecretUri=${openAiKeySecretUri})'
        }
        {
          name: 'CognitiveServicesKey'
          value: '@Microsoft.KeyVault(SecretUri=${cognitiveServicesKeySecretUri})'
        }
        {
          name: 'CognitiveServicesEndpoint'
          value: '@Microsoft.KeyVault(SecretUri=${cognitiveServicesEndpointSecretUri})'
        }
        {
          name: 'CosmosDatabaseName'
          value: 'NpcSoulEngine'
        }
        {
          name: 'GossipTopicName'
          value: 'npc-gossip'
        }
        {
          name: 'ConsolidationQueueName'
          value: 'memory-consolidation'
        }
      ]
    }
  }
}

// Grant the Function app's managed identity access to Key Vault secrets
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource kvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, functionApp.id, kvSecretsUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output name string = functionApp.name
output id string = functionApp.id
output principalId string = functionApp.identity.principalId
output defaultHostName string = functionApp.properties.defaultHostName
