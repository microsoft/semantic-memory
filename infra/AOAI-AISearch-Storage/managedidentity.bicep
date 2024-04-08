param salt string = uniqueString(resourceGroup().id)

@description('Managed Identity name.')
@minLength(2)
@maxLength(60)
param name string = 'km-UAidentity-${salt}'

@description('Location for all resources.')
param location string = resourceGroup().location

/////////////////////////////////////////////////////////////////////////
/////////////////////////////////////////////////////////////////////////
/////////////////////////////////////////////////////////////////////////

resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
}

var bootstrapRoleAssignmentId = guid('${resourceGroup().id}contributor')
var contributorRoleDefinitionId = '/subscriptions/${subscription().subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c'

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: bootstrapRoleAssignmentId
  properties: {
    roleDefinitionId: contributorRoleDefinitionId
    principalId: managedIdentity.properties.principalId
    scope: resourceGroup().id
    principalType: 'ServicePrincipal'
  }
}

output managedIdentityId string = managedIdentity.id
output managedIdentityClientId string = managedIdentity.properties.clientId
