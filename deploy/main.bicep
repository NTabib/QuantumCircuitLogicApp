// Infrastructure for the Logic App Standard that hosts the "Quantum Circuit (Q#)" custom built-in connector.
// Resources: Storage account (runtime state), Log Analytics + Application Insights (monitoring),
// Workflow Standard App Service plan (WS1, Windows) and the Logic App (Standard) site.

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Short prefix used to build resource names (3-11 lowercase letters/digits).')
@minLength(3)
@maxLength(11)
param baseName string = 'qcirc'

@description('Workflow Standard plan SKU.')
@allowed([
  'WS1'
  'WS2'
  'WS3'
])
param planSku string = 'WS1'

var suffix = uniqueString(resourceGroup().id, baseName)
var storageAccountName = take('${baseName}st${suffix}', 24)
var logicAppName = '${baseName}-la-${suffix}'
var planName = '${baseName}-plan-${suffix}'
var contentShareName = toLower(logicAppName)

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
	name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
	minimumTlsVersion: 'TLS1_2'
	allowBlobPublicAccess: false
	supportsHttpsTrafficOnly: true
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource contentShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: contentShareName
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${baseName}-law-${suffix}'
  location: location
  properties: {
	sku: {
	  name: 'PerGB2018'
	}
	retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai-${suffix}'
  location: location
  kind: 'web'
  properties: {
	Application_Type: 'web'
	WorkspaceResourceId: logAnalytics.id
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  kind: 'elastic'
  sku: {
	name: planSku
	tier: 'WorkflowStandard'
  }
  properties: {
	// Windows plan: the QDK full-state simulator ships as a win-x64 native library.
	reserved: false
	maximumElasticWorkerCount: 20
  }
}

var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storage.listKeys().keys[0].value}'

resource logicApp 'Microsoft.Web/sites@2023-12-01' = {
  name: logicAppName
  location: location
  kind: 'functionapp,workflowapp'
  identity: {
	type: 'SystemAssigned'
  }
  properties: {
	serverFarmId: plan.id
	httpsOnly: true
	siteConfig: {
	  netFrameworkVersion: 'v8.0'
	  use32BitWorkerProcess: false
	  ftpsState: 'Disabled'
	  minTlsVersion: '1.2'
	  appSettings: [
		{
		  name: 'APP_KIND'
		  value: 'workflowApp'
		}
		{
		  name: 'FUNCTIONS_EXTENSION_VERSION'
		  value: '~4'
		}
		{
		  // NuGet-based Logic App project (required for custom built-in connectors).
		  name: 'FUNCTIONS_WORKER_RUNTIME'
		  value: 'dotnet'
		}
		{
		  name: 'FUNCTIONS_INPROC_NET8_ENABLED'
		  value: '1'
		}
		{
		  name: 'AzureWebJobsStorage'
		  value: storageConnectionString
		}
		{
		  name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
		  value: storageConnectionString
		}
		{
		  name: 'WEBSITE_CONTENTSHARE'
		  value: contentShareName
		}
		{
		  name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
		  value: appInsights.properties.ConnectionString
		}
	  ]
	}
  }
  dependsOn: [
	contentShare
  ]
}

output logicAppName string = logicApp.name
output logicAppId string = logicApp.id
output logicAppHostName string = logicApp.properties.defaultHostName
output storageAccountName string = storage.name
