param name string
param location string
param tags object
param storageAccountId string
param appInsightsId string
param keyVaultId string
param aiFoundryId string

resource mlWorkspace 'Microsoft.MachineLearningServices/workspaces@2024-04-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    storageAccount: storageAccountId
    applicationInsights: appInsightsId
    keyVault: keyVaultId
    friendlyName: 'NPC Soul Engine ML Workspace'
    description: 'Player archetype classification and on-device model training'
  }
}

// Compute cluster for training runs
resource computeCluster 'Microsoft.MachineLearningServices/workspaces/computes@2024-04-01' = {
  parent: mlWorkspace
  name: 'training-cluster'
  location: location
  properties: {
    computeType: 'AmlCompute'
    properties: {
      vmSize: 'Standard_DS3_v2'
      scaleSettings: {
        minNodeCount: 0
        maxNodeCount: 4
        nodeIdleTimeBeforeScaleDown: 'PT120S'
      }
      osType: 'Linux'
      remoteLoginPortPublicAccess: 'Disabled'
    }
  }
}

// Real-time inference endpoint for archetype classification
resource inferenceEndpoint 'Microsoft.MachineLearningServices/workspaces/onlineEndpoints@2024-04-01' = {
  parent: mlWorkspace
  name: 'archetype-classifier'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    authMode: 'Key'
    publicNetworkAccess: 'Enabled'
  }
}

output workspaceName string = mlWorkspace.name
output workspaceId string = mlWorkspace.id
output inferenceEndpointName string = inferenceEndpoint.name
output inferenceEndpointUri string = inferenceEndpoint.properties.scoringUri
