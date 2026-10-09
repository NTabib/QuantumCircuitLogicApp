<#
.SYNOPSIS
  Provisions the Azure infrastructure, builds the Q# library + custom connector + Logic App,
  deploys the package to Logic Apps Standard and tests teleportation, measurement/reset/branching,
  the X-gate, beginner lessons, reusable typed operations, visual circuits, and algorithms.

.EXAMPLE
  .\deploy.ps1 -ResourceGroup rg-quantum-circuit -Location westeurope
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)] [string] $ResourceGroup,
	[string] $Location = 'westeurope',
	[string] $BaseName = 'qcirc',
	[string] $SubscriptionId
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$zipPath = Join-Path $root 'artifacts\logicapp.zip'

function Invoke-Checked([scriptblock] $Command, [string] $Step) {
	Write-Host "==> $Step" -ForegroundColor Cyan
	& $Command
	if ($LASTEXITCODE -ne 0) { throw "Step failed: $Step (exit code $LASTEXITCODE)" }
}

function Get-WorkflowCallbackUrl([string] $WorkflowName) {
	$callbackUri = "https://management.azure.com$logicAppId/hostruntime/runtime/webhooks/workflow/api/management/workflows/$WorkflowName/triggers/When_a_HTTP_request_is_received/listCallbackUrl?api-version=2023-12-01"
	for ($i = 0; $i -lt 20; $i++) {
		Start-Sleep -Seconds 15
		$url = az rest --method post --uri $callbackUri --query value --output tsv 2>$null
		if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($url)) {
			return $url
		}
	}
	throw "Could not retrieve the callback URL for '$WorkflowName'. Check the Logic App in the portal."
}

. (Join-Path $PSScriptRoot 'Test-AlgorithmWorkflows.ps1')

Invoke-Checked {
	dotnet run --project (Join-Path $root 'tests\QuantumCircuit.ReferenceTests\QuantumCircuit.ReferenceTests.csproj') -c Release
} 'Comparing circuit execution with the native Q# reference program'

if ($SubscriptionId) {
	Invoke-Checked { az account set --subscription $SubscriptionId } 'Selecting subscription'
}

# 1. Infrastructure
Invoke-Checked { az group create --name $ResourceGroup --location $Location --output none } 'Creating resource group'

Write-Host '==> Deploying main.bicep' -ForegroundColor Cyan
$outputs = az deployment group create `
	--resource-group $ResourceGroup `
	--name quantum-circuit-infra `
	--template-file (Join-Path $PSScriptRoot 'main.bicep') `
	--parameters baseName=$BaseName `
	--query properties.outputs --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Bicep deployment failed.' }

$logicAppName = $outputs.logicAppName.value
$logicAppId = $outputs.logicAppId.value
Write-Host "Logic App: $logicAppName"

# 2. Build: Q# library -> connector -> NuGet-based Logic App project
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Invoke-Checked {
	dotnet publish (Join-Path $root 'src\QuantumCircuit.LogicApp\QuantumCircuit.LogicApp.csproj') -c Release -o $publishDir
} 'Building and publishing the Logic App (Q# + connector + workflows)'

# 3. Zip the publish output (forward-slash entries, required by Kudu zip deploy)
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath)

# 4. Zip deploy to Logic Apps Standard
Invoke-Checked {
	az logicapp deployment source config-zip --resource-group $ResourceGroup --name $logicAppName --src $zipPath --output none
} 'Deploying package (zip deploy)'

# 5. Get trigger callback URLs (contain SAS signatures) and test the workflows
Write-Host '==> Waiting for the workflow host to restart' -ForegroundColor Cyan
$callbackUrl = Get-WorkflowCallbackUrl 'XGateDemoWorkflow'

Write-Host '==> Testing XGateDemoWorkflow through createCircuit -> addX -> runCircuit' -ForegroundColor Cyan
$xDemoBody = @{ shots = 100 } | ConvertTo-Json
$xDemoResult = Invoke-RestMethod -Method Post -Uri $callbackUrl -ContentType 'application/json' -Body $xDemoBody
$xGates = @($xDemoResult.circuit.gates)
if ($xDemoResult.circuit.initialState -ne '0' -or $xGates.Count -ne 1 -or $xGates[0].gate -ne 'X' -or $xGates[0].target -ne 0) {
	throw 'X-gate workflow smoke test failed: expected circuit |0> -> X(0) built by the generic gate action.'
}

$xCircuitResult = $xDemoResult.circuitResult
if ($xCircuitResult.simulator -ne 'FullState' -or $xCircuitResult.qubitCount -ne 1 `
	-or $xCircuitResult.gateCount -ne 1 -or $xCircuitResult.shots -ne 100 -or $null -eq $xCircuitResult.counts) {
	throw 'X-gate workflow smoke test failed: unexpected circuit execution metadata.'
}
$totalXShots = 0
foreach ($count in $xCircuitResult.counts.psobject.Properties) {
	if ($count.Name -ne '1' -or ($count.Value -isnot [int] -and $count.Value -isnot [long]) -or $count.Value -lt 1) {
		throw 'X-gate workflow smoke test failed: every shot from X|0> must measure 1.'
	}
	$totalXShots += $count.Value
}
if ($totalXShots -ne 100 -or $xDemoResult.xGateVerified -ne $true) {
	throw 'X-gate workflow smoke test failed: measured counts or workflow verification are incorrect.'
}
Write-Host 'X-gate builder smoke test passed.' -ForegroundColor Green

$visualCallbackUrl = Get-WorkflowCallbackUrl 'VisualQuantumCircuitWorkflow'
Write-Host '==> Testing VisualQuantumCircuitWorkflow (Create -> H -> CNOT -> Execute)' -ForegroundColor Cyan
$visualBody = @{ initialState = '00'; shots = 100 } | ConvertTo-Json
$visualResult = Invoke-RestMethod -Method Post -Uri $visualCallbackUrl -ContentType 'application/json' -Body $visualBody

$gates = @($visualResult.circuit.gates)
if ($visualResult.circuit.initialState -ne '00' -or $gates.Count -ne 2 `
	-or $gates[0].gate -ne 'H' -or $gates[0].target -ne 0 `
	-or $gates[1].gate -ne 'CNOT' -or $gates[1].control -ne 0 -or $gates[1].target -ne 1) {
	throw 'Visual workflow smoke test failed: expected circuit |00> -> H(0) -> CNOT(0,1).'
}

$circuitResult = $visualResult.circuitResult
if ($circuitResult.simulator -ne 'FullState' -or $circuitResult.qubitCount -ne 2 `
	-or $circuitResult.gateCount -ne 2 -or $circuitResult.shots -ne 100 -or $null -eq $circuitResult.counts) {
	throw 'Visual workflow smoke test failed: unexpected circuit execution metadata.'
}

$totalShots = 0
foreach ($count in $circuitResult.counts.psobject.Properties) {
	if ($count.Name -notin @('00', '11') -or ($count.Value -isnot [int] -and $count.Value -isnot [long]) -or $count.Value -lt 1) {
		throw 'Visual workflow smoke test failed: Bell measurements must contain only positive integer counts for 00 or 11.'
	}
	$totalShots += $count.Value
}
if ($totalShots -ne 100) {
	throw 'Visual workflow smoke test failed: measurement counts do not total 100 shots.'
}

$visualResult | ConvertTo-Json -Depth 10
Write-Host 'Visual quantum circuit smoke test passed.' -ForegroundColor Green

Test-AlgorithmWorkflows -GetCallbackUrl {
	param($workflowName)
	Get-WorkflowCallbackUrl $workflowName
} -Shots 100

. (Join-Path $PSScriptRoot 'Test-BeginnerGateWorkflows.ps1')
Test-BeginnerGateWorkflows -GetCallbackUrl {
	param($workflowName)
	Get-WorkflowCallbackUrl $workflowName
} -Shots 100

. (Join-Path $PSScriptRoot 'Test-ReusableOperationWorkflow.ps1')
Test-ReusableOperationWorkflow -GetCallbackUrl {
	param($workflowName)
	Get-WorkflowCallbackUrl $workflowName
} -Shots 100

. (Join-Path $PSScriptRoot 'Test-MeasurementControlWorkflow.ps1')
Test-MeasurementControlWorkflow -GetCallbackUrl {
	param($workflowName)
	Get-WorkflowCallbackUrl $workflowName
} -Shots 20

. (Join-Path $PSScriptRoot 'Test-TeleportationWorkflow.ps1')
Test-TeleportationWorkflow -GetCallbackUrl {
	param($workflowName)
	Get-WorkflowCallbackUrl $workflowName
} -Shots 200
Write-Host ''
Write-Host "XGateDemoWorkflow URL (keep it secret, it contains a SAS key):" -ForegroundColor Yellow
Write-Host $callbackUrl
