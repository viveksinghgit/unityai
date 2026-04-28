param name string
param location string
param secondaryLocation string
param tags object
param keyVaultName string

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-02-15-preview' = {
  name: name
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: true
      }
      {
        locationName: secondaryLocation
        failoverPriority: 1
        isZoneRedundant: false
      }
    ]
    enableMultipleWriteLocations: true
    enableAutomaticFailover: false
    capabilities: []
    backupPolicy: {
      type: 'Continuous'
      continuousModeProperties: {
        tier: 'Continuous7Days'
      }
    }
    publicNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-02-15-preview' = {
  parent: account
  name: 'NpcSoulEngine'
  properties: {
    resource: {
      id: 'NpcSoulEngine'
    }
  }
}

// Hierarchical partition key: /npcId, /playerId — prevents hot partition on popular NPCs
resource memoryContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: database
  name: 'npc-memory-graphs'
  properties: {
    resource: {
      id: 'npc-memory-graphs'
      partitionKey: {
        paths: ['/npcId', '/playerId']
        kind: 'MultiHash'
        version: 2
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/npcId/?' }
          { path: '/playerId/?' }
          { path: '/trustScore/?' }
          { path: '/lastEncounterTimestamp/?' }
          { path: '/playerArchetype/?' }
        ]
        excludedPaths: [
          { path: '/salientEvents/*' }
          { path: '/memorySummaryBlob/?' }
          { path: '/personalityVector/*' }
          { path: '/"_etag"/?' }
        ]
        compositeIndexes: [
          [
            { path: '/npcId', order: 'ascending' }
            { path: '/lastEncounterTimestamp', order: 'descending' }
          ]
        ]
      }
      defaultTtl: -1
    }
    options: {
      autoscaleSettings: {
        maxThroughput: 10000
      }
    }
  }
}

resource gossipContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: database
  name: 'gossip-events'
  properties: {
    resource: {
      id: 'gossip-events'
      partitionKey: {
        paths: ['/targetNpcId']
        kind: 'Hash'
        version: 2
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/targetNpcId/?' }
          { path: '/processed/?' }
          { path: '/timestamp/?' }
        ]
        excludedPaths: [
          { path: '/*' }
        ]
      }
      defaultTtl: 604800  // 7 days
    }
    options: {
      autoscaleSettings: {
        maxThroughput: 4000
      }
    }
  }
}

resource archetypeContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: database
  name: 'player-archetypes'
  properties: {
    resource: {
      id: 'player-archetypes'
      partitionKey: {
        paths: ['/playerId']
        kind: 'Hash'
        version: 2
      }
      defaultTtl: -1
    }
    options: {
      autoscaleSettings: {
        maxThroughput: 4000
      }
    }
  }
}

var connectionString = account.listConnectionStrings().connectionStrings[0].connectionString

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource connectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'CosmosConnectionString'
  properties: {
    value: connectionString
  }
}

output accountName string = account.name
output accountId string = account.id
output connectionStringSecretUri string = connectionStringSecret.properties.secretUri
output databaseName string = database.name
