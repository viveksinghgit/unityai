targetScope = 'subscription'

@description('Environment name: dev | staging | prod')
@allowed(['dev', 'staging', 'prod'])
param env string = 'dev'

@description('Primary Azure region')
param location string = 'eastus'

@description('Secondary region for Cosmos multi-region writes')
param secondaryLocation string = 'westus2'

@description('GPT-4o model deployment name')
param gpt4oDeploymentName string = 'gpt-4o'

var prefix = 'npc-soul'
var rgName = 'rg-${prefix}-${env}'
var tags = {
  project: 'NpcSoulEngine'
  environment: env
}

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: rgName
  location: location
  tags: tags
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'kv-deploy'
  scope: rg
  params: {
    name: 'kv-${prefix}-${env}'
    location: location
    tags: tags
  }
}

module cosmos 'modules/cosmos.bicep' = {
  name: 'cosmos-deploy'
  scope: rg
  params: {
    name: 'cosmos-${prefix}-${env}'
    location: location
    secondaryLocation: secondaryLocation
    tags: tags
    keyVaultName: keyVault.outputs.name
  }
}

module serviceBus 'modules/servicebus.bicep' = {
  name: 'sb-deploy'
  scope: rg
  params: {
    name: 'sb-${prefix}-${env}'
    location: location
    tags: tags
    sku: env == 'prod' ? 'Premium' : 'Standard'
    keyVaultName: keyVault.outputs.name
  }
}

module storage 'modules/storage.bicep' = {
  name: 'st-deploy'
  scope: rg
  params: {
    name: 'st${replace(prefix, '-', '')}${env}'
    location: location
    tags: tags
  }
}

module appInsights 'modules/appinsights.bicep' = {
  name: 'ai-deploy'
  scope: rg
  params: {
    name: 'ai-${prefix}-${env}'
    location: location
    tags: tags
    keyVaultName: keyVault.outputs.name
  }
}

module aiFoundry 'modules/aifoundry.bicep' = {
  name: 'aif-deploy'
  scope: rg
  params: {
    name: 'aif-${prefix}-${env}'
    location: location
    tags: tags
    gpt4oDeploymentName: gpt4oDeploymentName
    keyVaultName: keyVault.outputs.name
  }
}

module cognitiveServices 'modules/cognitiveservices.bicep' = {
  name: 'cog-deploy'
  scope: rg
  params: {
    name: 'cog-${prefix}-${env}'
    location: location
    tags: tags
    keyVaultName: keyVault.outputs.name
  }
}

module mlWorkspace 'modules/mlworkspace.bicep' = {
  name: 'ml-deploy'
  scope: rg
  params: {
    name: 'ml-${prefix}-${env}'
    location: location
    tags: tags
    storageAccountId: storage.outputs.id
    appInsightsId: appInsights.outputs.id
    keyVaultId: keyVault.outputs.id
    aiFoundryId: aiFoundry.outputs.id
  }
}

module functions 'modules/functions.bicep' = {
  name: 'func-deploy'
  scope: rg
  params: {
    name: 'func-${prefix}-${env}'
    location: location
    tags: tags
    sku: env == 'prod' ? 'EP1' : 'Y1'
    storageAccountName: storage.outputs.name
    appInsightsConnectionString: appInsights.outputs.connectionString
    cosmosConnectionSecretUri: cosmos.outputs.connectionStringSecretUri
    serviceBusConnectionSecretUri: serviceBus.outputs.connectionStringSecretUri
    openAiEndpointSecretUri: aiFoundry.outputs.endpointSecretUri
    openAiKeySecretUri: aiFoundry.outputs.keySecretUri
    cognitiveServicesKeySecretUri: cognitiveServices.outputs.keySecretUri
    cognitiveServicesEndpointSecretUri: cognitiveServices.outputs.endpointSecretUri
    keyVaultName: keyVault.outputs.name
  }
  dependsOn: [cosmos, serviceBus, aiFoundry, cognitiveServices, appInsights, storage]
}

output resourceGroupName string = rg.name
output functionsAppName string = functions.outputs.name
output cosmosAccountName string = cosmos.outputs.accountName
