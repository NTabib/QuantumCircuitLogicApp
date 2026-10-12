<#
.SYNOPSIS
  Deploys the separate Python Rigetti backend and grants it access to an Azure Quantum workspace.

.EXAMPLE
  .\Deploy-AzureQuantumBackend.ps1 -ResourceGroup rg-quantum -LogicAppName qcirc-la `
    -FunctionAppName qcirc-qdk-backend -StorageAccountName qcircstorage `
    -Location westeurope -WorkspaceResourceId /subscriptions/.../providers/Microsoft.Quantum/workspaces/demo `
    -TargetId rigetti.sim.qvm
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)] [string] $ResourceGroup,
	[Parameter(Mandatory = $true)] [string] $LogicAppName,
	[Parameter(Mandatory = $true)] [string] $FunctionAppName,
	[Parameter(Mandatory = $true)] [string] $StorageAccountName,
	[Parameter(Mandatory = $true)] [string] $Location,
	[Parameter(Mandatory = $true)] [string] $WorkspaceResourceId,
	# [ValidatePattern('^rigetti\.(sim\.qvm|qpu\.[a-zA-Z0-9-]+)$')]
	[string] $TargetId = 'rigetti.sim.qvm'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$backendDirectory = Join-Path $root 'src\QuantumCircuit.AzureQuantumBackend'

function Invoke-Checked([scriptblock] $Command, [string] $Step) {
	Write-Host "==> $Step" -ForegroundColor Cyan
	& $Command
	if ($LASTEXITCODE -ne 0) { throw "Step failed: $Step (exit code $LASTEXITCODE)" }
}

Invoke-Checked {
	az functionapp create `
		--resource-group $ResourceGroup `
		--name $FunctionAppName `
		--storage-account $StorageAccountName `
		--consumption-plan-location $Location `
		--runtime python `
		--runtime-version 3.11 `
		--functions-version 4 `
		--os-type Linux `
		--output none
} 'Creating the Linux Python 3.11 Function App'

Invoke-Checked {
	az functionapp config appsettings set `
		--resource-group $ResourceGroup `
		--name $FunctionAppName `
		--settings `
			'FUNCTIONS_WORKER_RUNTIME=python' `
			'FUNCTIONS_EXTENSION_VERSION=~4' `
			"AZURE_QUANTUM_WORKSPACE_RESOURCE_ID=$WorkspaceResourceId" `
			"AZURE_QUANTUM_TARGET_ID=$TargetId" `
			'AZURE_QUANTUM_JOB_TIMEOUT_SECONDS=180' `
		--output none
} 'Configuring the workspace and target'

$identity = az functionapp identity assign --resource-group $ResourceGroup --name $FunctionAppName --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($identity.principalId)) {
	throw 'Could not enable the backend Function App managed identity.'
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
	} 'Granting the backend identity Azure Quantum job access'
}

$archivePath = Join-Path $root 'artifacts\azure-quantum-backend.zip'
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $archivePath) -Force
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archiveStream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::Create)
$archive = New-Object System.IO.Compression.ZipArchive($archiveStream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
	foreach ($fileName in @('function_app.py', 'host.json', 'requirements.txt')) {
		$null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
			$archive, (Join-Path $backendDirectory $fileName), $fileName)
	}
} finally {
	$archive.Dispose()
	$archiveStream.Dispose()
}

Invoke-Checked {
	az functionapp deployment source config-zip `
		--resource-group $ResourceGroup `
		--name $FunctionAppName `
		--src $archivePath `
		--build-remote true `
		--timeout 1200 `
		--output none
} "Publishing the Python backend and waiting for its remote build (up to 20 minutes). On failure, inspect https://$FunctionAppName.scm.azurewebsites.net/api/deployments"

$functionAppId = az functionapp show --resource-group $ResourceGroup --name $FunctionAppName --query id --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionAppId)) {
	throw 'Could not retrieve the Function App resource ID for trigger synchronization.'
}
Invoke-Checked {
	az rest --method post --uri "$functionAppId/syncfunctiontriggers?api-version=2023-12-01" --output none
} 'Synchronizing the deployed backend function triggers'

$backendUrl = "https://$FunctionAppName.azurewebsites.net/api/run-quantum-circuit"
$functionKey = $null
for ($attempt = 1; $attempt -le 12; $attempt++) {
	$functionNames = az functionapp function list `
		--resource-group $ResourceGroup `
		--name $FunctionAppName `
		--query "[].name" `
		--output tsv
	if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the published backend functions.' }

	if (@($functionNames | Where-Object { ($_ -split '/')[-1] -eq 'run_quantum_circuit' }).Count -gt 0) {
		$functionKey = az functionapp function keys list `
			--resource-group $ResourceGroup `
			--name $FunctionAppName `
			--function-name run_quantum_circuit `
			--query default `
			--output tsv
		if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($functionKey)) { break }
		$functionKey = $null
	}

	Write-Warning "Waiting for run_quantum_circuit discovery and keys ($attempt/12)."
	if ($attempt -lt 12) { Start-Sleep -Seconds 10 }
}
if ([string]::IsNullOrWhiteSpace($functionKey)) {
	throw 'run_quantum_circuit was not ready after publishing. Check Function App Log stream or Application Insights for Python import/indexing errors and the remote build log for requirements.txt installation failures. The Logic App endpoint was not configured.'
}

Invoke-Checked {
	az webapp config appsettings set `
		--resource-group $ResourceGroup `
		--name $LogicAppName `
		--settings `
			"AZURE_QUANTUM_BACKEND_URL=$backendUrl" `
			"AZURE_QUANTUM_BACKEND_FUNCTION_KEY=$functionKey" `
		--output none
} 'Configuring the secured backend endpoint on the Logic App'

Write-Host ''
Write-Host "Backend endpoint: $backendUrl"
Write-Host "Function: run_quantum_circuit"
Write-Host 'The function-scoped key was configured as a Logic App app setting. For production, use a Key Vault reference.'
