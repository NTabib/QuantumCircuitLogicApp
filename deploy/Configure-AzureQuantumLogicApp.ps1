<#
.SYNOPSIS
  Configures the Logic App managed identity to submit jobs directly to Azure Quantum.

.EXAMPLE
  .\Configure-AzureQuantumLogicApp.ps1 -ResourceGroup rg-quantum -LogicAppName qcirc-la `
    -WorkspaceResourceId /subscriptions/.../providers/Microsoft.Quantum/workspaces/demo `
    -WorkspaceLocation westeurope -TargetId rigetti.sim.qvm
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)] [string] $ResourceGroup,
	[Parameter(Mandatory = $true)] [string] $LogicAppName,
	[Parameter(Mandatory = $true)] [string] $WorkspaceResourceId,
	[Parameter(Mandatory = $true)] [ValidatePattern('^[a-z0-9-]+$')] [string] $WorkspaceLocation,
	[ValidatePattern('^(rigetti\.(sim\.qvm|qpu\.[a-zA-Z0-9-]+)|ionq\.(simulator|qpu\.[a-zA-Z0-9-]+)|quantinuum\.(sim|qpu)\.[a-zA-Z0-9-]+)$')]
	[string] $TargetId = 'rigetti.sim.qvm',
	[ValidateRange(1, 180)]
	[int] $JobTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

function Invoke-Checked([scriptblock] $Command, [string] $Step) {
	Write-Host "==> $Step" -ForegroundColor Cyan
	& $Command
	if ($LASTEXITCODE -ne 0) { throw "Step failed: $Step (exit code $LASTEXITCODE)" }
}

if ($WorkspaceResourceId -notmatch '^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\.Quantum/workspaces/[^/]+$') {
	throw 'WorkspaceResourceId must be a full Azure Quantum workspace resource ID.'
}

$identity = az webapp identity assign `
	--resource-group $ResourceGroup `
	--name $LogicAppName `
	--output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($identity.principalId)) {
	throw 'Could not enable or retrieve the Logic App system-assigned managed identity.'
}

$existingRoleCount = az role assignment list `
	--assignee-object-id $identity.principalId `
	--scope $WorkspaceResourceId `
	--query "[?roleDefinitionName=='Quantum Workspace Data Contributor'] | length(@)" `
	--output tsv
if ($LASTEXITCODE -ne 0) { throw 'Could not check the Azure Quantum workspace role assignment.' }
if ([int]$existingRoleCount -eq 0) {
	Invoke-Checked {
		az role assignment create `
			--assignee-object-id $identity.principalId `
			--assignee-principal-type ServicePrincipal `
			--role 'Quantum Workspace Data Contributor' `
			--scope $WorkspaceResourceId `
			--output none
	} 'Granting the Logic App identity Azure Quantum job and result access'
}

$endpoint = "https://$WorkspaceLocation.quantum.azure.com/"
Invoke-Checked {
	az webapp config appsettings set `
		--resource-group $ResourceGroup `
		--name $LogicAppName `
		--settings `
			"AZURE_QUANTUM_ENDPOINT=$endpoint" `
			"AZURE_QUANTUM_WORKSPACE_RESOURCE_ID=$WorkspaceResourceId" `
			"AZURE_QUANTUM_TARGET_ID=$TargetId" `
			"AZURE_QUANTUM_JOB_TIMEOUT_SECONDS=$JobTimeoutSeconds" `
		--output none
} 'Configuring the in-host Azure Quantum endpoint and target'

Write-Host ''
Write-Host "Logic App: $LogicAppName"
Write-Host "Workspace endpoint: $endpoint"
Write-Host "Target: $TargetId"
Write-Host 'No Function App, Function key, or backend endpoint is used by the Logic App workflows.'
