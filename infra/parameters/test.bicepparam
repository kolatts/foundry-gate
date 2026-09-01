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
// Small quotas so the tier boundaries are actually reachable in a test deploy.
param quotaTiers = [
  {
    name: 'standard'
    displayName: 'Standard'
    description: 'Test tier — small monthly budget so the 403 path is reachable.'
    monthlyTokenQuota: 100000
    tpm: 20000
  }
  {
    name: 'power'
    displayName: 'Power'
    description: 'Test tier — larger budget.'
    monthlyTokenQuota: 1000000
    tpm: 40000
  }
  {
    name: 'unlimited'
    displayName: 'Unlimited'
    description: 'Test tier — no native monthly quota, TPM smoothing only.'
    monthlyTokenQuota: 0
    tpm: 100000
  }
]

// Flip to true ONLY for the very first deployment. Anthropic deployments are
// create-once under ARM — re-running with true re-PUTs them into a Failed state
// (see modules/foundry.bicep). Model lifecycle after day 0 belongs to the control
// plane, not ARM.
param createModelDeployments = false
