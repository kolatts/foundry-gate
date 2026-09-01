using '../main.bicep'

param environmentName = 'test'
param location = 'eastus2'
param nameSuffix = 'e7k2'
param publisherEmail = 'kolatts@gmail.com'
param publisherName = 'FoundryGate Test'
param anthropicProviderData = {
  industry: 'Software'
  organizationName: 'Imagile'
  countryCode: 'US'
}
param defaultDeveloperTpm = 20000
param defaultDeveloperMonthlyTokenQuota = 0
