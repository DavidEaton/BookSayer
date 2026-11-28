@description('Public image with the BookSayer Dockerfile, preferably pinned by digest.')
param image string
param clientId string
@secure()
param clientSecret string
param location string = resourceGroup().location

var suffix = uniqueString(resourceGroup().id)
var appName = 'booksayer-${suffix}'
var containerNames = ['manuscripts', 'audio', 'keys']
var blobWriter = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var blobReader = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
var cryptoUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')

resource webId 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'booksayer-web'
  location: location
}
resource workerId 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'booksayer-worker'
  location: location
}
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'booksayer${suffix}'
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for name in containerNames: {
  parent: blobs
  name: name
  properties: { publicAccess: 'None' }
}]
resource webBlobRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in containerNames: {
  scope: containers[i]
  name: guid(storage.id, name, webId.id)
  properties: {
    roleDefinitionId: name == 'audio' ? blobReader : blobWriter
    principalId: webId.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]
resource workerBlobRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in take(containerNames, 2): {
  scope: containers[i]
  name: guid(storage.id, name, workerId.id)
  properties: {
    roleDefinitionId: blobWriter
    principalId: workerId.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'bs-${suffix}'
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
  }
}
resource cookieKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: 'cookies'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
  }
}
resource keyRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, webId.id, cryptoUser)
  properties: {
    roleDefinitionId: cryptoUser
    principalId: webId.properties.principalId
    principalType: 'ServicePrincipal'
  }
}
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'booksayer'
  location: location
  properties: {}
}
var origin = 'https://${appName}.${environment.properties.defaultDomain}'
resource web 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${webId.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: { external: true, targetPort: 8080, allowInsecure: false }
      secrets: [{ name: 'oidc-secret', value: clientSecret }]
    }
    template: {
      scale: { minReplicas: 0, maxReplicas: 3 }
      containers: [{
        name: 'web'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: [
          { name: 'StorageUri', value: storage.properties.primaryEndpoints.blob }
          { name: 'AZURE_CLIENT_ID', value: webId.properties.clientId }
          { name: 'PublicOrigin', value: origin }
          { name: 'ProtectionKeyUri', value: '${vault.properties.vaultUri}keys/${cookieKey.name}' }
          { name: 'Entra__TenantId', value: tenant().tenantId }
          { name: 'Entra__ClientId', value: clientId }
          { name: 'Entra__ClientSecret', secretRef: 'oidc-secret' }
        ]
      }]
    }
  }
  dependsOn: [webBlobRoles, keyRole]
}
resource worker 'Microsoft.App/jobs@2024-03-01' = {
  name: 'booksayer-narrator'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${workerId.id}': {} }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 3600
      replicaRetryLimit: 1
      scheduleTriggerConfig: { cronExpression: '*/5 * * * *', parallelism: 1, replicaCompletionCount: 1 }
    }
    template: {
      containers: [{
        name: 'worker'
        image: image
        args: ['--worker']
        resources: { cpu: json('1.0'), memory: '2Gi' }
        env: [
          { name: 'StorageUri', value: storage.properties.primaryEndpoints.blob }
          { name: 'AZURE_CLIENT_ID', value: workerId.properties.clientId }
        ]
      }]
    }
  }
  dependsOn: [workerBlobRoles]
}
output webUrl string = origin
output redirectUri string = '${origin}/signin-oidc'
