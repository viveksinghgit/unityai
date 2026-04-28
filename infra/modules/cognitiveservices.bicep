param name string
param location string
param tags object
param keyVaultName string

// Multi-service Cognitive Services account: Speech + Text Analytics under one key
resource cogAccount 'Microsoft.CognitiveServices/accounts@2024-04-01-preview' = {
  name: name
  location: location
  tags: tags
  kind: 'CognitiveServices'
  sku: {
    name: 'S0'
  }
  properties: {
    publicNetworkAccess: 'Enabled'
    customSubDomainName: name
  }
}

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource keySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'CognitiveServicesKey'
  properties: {
    value: cogAccount.listKeys().key1
  }
}

resource endpointSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'CognitiveServicesEndpoint'
  properties: {
    value: cogAccount.properties.endpoint
  }
}

output name string = cogAccount.name
output id string = cogAccount.id
output endpoint string = cogAccount.properties.endpoint
output keySecretUri string = keySecret.properties.secretUri
output endpointSecretUri string = endpointSecret.properties.secretUri
