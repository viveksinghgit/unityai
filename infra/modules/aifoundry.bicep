param name string
param location string
param tags object
param gpt4oDeploymentName string
param keyVaultName string

// Azure AI Services account (AI Foundry uses this as the backing resource)
resource aiServices 'Microsoft.CognitiveServices/accounts@2024-04-01-preview' = {
  name: name
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  properties: {
    publicNetworkAccess: 'Enabled'
    customSubDomainName: name
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

resource gpt4oDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: aiServices
  name: gpt4oDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: 40  // 40K TPM
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-11-20'
    }
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
}

// gpt-4o-mini for low-significance interactions (15x cheaper)
resource gpt4oMiniDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: aiServices
  name: '${gpt4oDeploymentName}-mini'
  sku: {
    name: 'GlobalStandard'
    capacity: 100  // 100K TPM — higher volume, lower cost
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o-mini'
      version: '2024-07-18'
    }
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
  dependsOn: [gpt4oDeployment]
}

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource endpointSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'OpenAiEndpoint'
  properties: {
    value: aiServices.properties.endpoint
  }
}

resource keySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'OpenAiKey'
  properties: {
    value: aiServices.listKeys().key1
  }
}

output id string = aiServices.id
output name string = aiServices.name
output endpoint string = aiServices.properties.endpoint
output endpointSecretUri string = endpointSecret.properties.secretUri
output keySecretUri string = keySecret.properties.secretUri
output gpt4oDeploymentName string = gpt4oDeployment.name
output gpt4oMiniDeploymentName string = gpt4oMiniDeployment.name
