param name string
param location string
param tags object
@allowed(['Standard', 'Premium'])
param sku string
param keyVaultName string

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: sku
    tier: sku
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

// Topic for zone-based gossip routing
resource gossipTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: namespace
  name: 'npc-gossip'
  properties: {
    defaultMessageTimeToLive: 'P7D'
    maxSizeInMegabytes: 5120
    requiresDuplicateDetection: false
    enableBatchedOperations: true
    supportOrdering: false
    enablePartitioning: sku == 'Standard'
  }
}

// Subscription per zone — designers add zones as needed
var zones = ['zone_default', 'zone_village', 'zone_castle', 'zone_market', 'zone_wilderness']

resource gossipSubscriptions 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = [for zone in zones: {
  parent: gossipTopic
  name: zone
  properties: {
    lockDuration: 'PT1M'
    maxDeliveryCount: 5
    defaultMessageTimeToLive: 'P7D'
    deadLetteringOnMessageExpiration: true
    enableBatchedOperations: true
  }
}]

// Queue for memory consolidation jobs (from MemoryDecayJob → MemoryConsolidationJob)
resource consolidationQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: namespace
  name: 'memory-consolidation'
  properties: {
    lockDuration: 'PT5M'
    maxSizeInMegabytes: 1024
    maxDeliveryCount: 3
    defaultMessageTimeToLive: 'P1D'
    deadLetteringOnMessageExpiration: true
  }
}

var sendListenRule = namespace.listKeys('RootManageSharedAccessKey')

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource connectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'ServiceBusConnectionString'
  properties: {
    value: sendListenRule.primaryConnectionString
  }
}

output namespaceName string = namespace.name
output namespaceId string = namespace.id
output connectionStringSecretUri string = connectionStringSecret.properties.secretUri
output gossipTopicName string = gossipTopic.name
output consolidationQueueName string = consolidationQueue.name
