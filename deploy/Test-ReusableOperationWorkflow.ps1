# Dot-source this file and supply a callback resolver for Azure or the local Logic Apps host.
function Test-ReusableOperationWorkflow {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory = $true)] [scriptblock] $GetCallbackUrl,
		[ValidateRange(1, 1000)] [int] $Shots = 100
	)

	$workflowName = 'ReusableOperationWorkflow'
	Write-Host "==> Testing $workflowName" -ForegroundColor Cyan
	$url = & $GetCallbackUrl $workflowName
	if ([string]::IsNullOrWhiteSpace($url)) { throw "No callback URL for $workflowName." }
	$body = @{ shots = $Shots } | ConvertTo-Json
	$response = Invoke-RestMethod -Method Post -Uri $url -ContentType 'application/json' -Body $body -TimeoutSec 180
	$circuit = $response.circuit
	$result = $response.circuitResult
	$operations = @($circuit.operations)
	$gates = @($circuit.gates)
	if ($response.lesson.id -ne 'ReusableOperation' -or $circuit.initialState -ne '00' `
		-or $operations.Count -ne 1 -or $operations[0].name -ne 'RotateYByAngle' `
		-or @($operations[0].parameters).Count -ne 2 -or $operations[0].parameters[0].type -ne 'Int' `
		-or $operations[0].parameters[1].type -ne 'Double' -or $gates.Count -ne 2 `
		-or $null -eq $result -or $result.simulator -ne 'FullState' -or $result.qubitCount -ne 2 `
		-or $result.gateCount -ne 2 -or $result.shots -ne $Shots -or $null -eq $result.measurements) {
		throw "$workflowName returned an unexpected operation definition, typed result, or execution result."
	}

	for ($index = 0; $index -lt 2; $index++) {
		if ($gates[$index].gate -ne 'CALL' -or $gates[$index].operation -ne 'RotateYByAngle' `
			-or $gates[$index].arguments.target -ne $index `
			-or [Math]::Abs($gates[$index].arguments.angle - [Math]::PI / 2) -gt 1e-10) {
			throw "$workflowName did not build both parameterized operation calls."
		}
	}

	if (@($result.measurements).Count -ne $Shots) { throw "$workflowName did not return one typed measurement array per shot." }
	foreach ($measurement in $result.measurements) {
		if (@($measurement).Count -ne 2 -or $measurement[0] -isnot [bool] -or $measurement[1] -isnot [bool]) {
			throw "$workflowName measurement results must be two-element Boolean arrays."
		}
	}

	$total = 0
	foreach ($entry in $result.counts.psobject.Properties) {
		if ($entry.Name -notin @('00', '01', '10', '11') -or ($entry.Value -isnot [int] -and $entry.Value -isnot [long]) -or $entry.Value -lt 1) {
			throw "$workflowName returned invalid measurement outcome '$($entry.Name)'."
		}
		$total += $entry.Value
	}
	if ($total -ne $Shots) { throw "$workflowName measurement counts do not total $Shots." }

	$manualUrl = & $GetCallbackUrl 'QuantumCircuitWorkflow'
	$operation = @{
		name = 'RotateYByAngle'
		parameters = @(@{ name = 'target'; type = 'Int' }, @{ name = 'angle'; type = 'Double' })
		gates = @(@{ gate = 'RY'; target = '$target'; angle = '$angle' })
	}
	$manualBody = @{
		initialState = '00'
		operations = @($operation)
		gates = @(
			@{ gate = 'CALL'; operation = 'RotateYByAngle'; arguments = @{ target = 0; angle = [Math]::PI / 2 } },
			@{ gate = 'CALL'; operation = 'RotateYByAngle'; arguments = @{ target = 1; angle = [Math]::PI / 2 } }
		)
		shots = $Shots
	} | ConvertTo-Json -Depth 10
	$manualResult = (Invoke-RestMethod -Method Post -Uri $manualUrl -ContentType 'application/json' -Body $manualBody -TimeoutSec 180).circuitResult
	if ($manualResult.gateCount -ne 2 -or $manualResult.shots -ne $Shots -or @($manualResult.measurements).Count -ne $Shots) {
		throw 'QuantumCircuitWorkflow did not execute the supplied reusable operation definition.'
	}

	$invalidBody = @{
		initialState = '00'
		operations = @($operation)
		gates = @(@{ gate = 'CALL'; operation = 'RotateYByAngle'; arguments = @{ target = '0'; angle = [Math]::PI / 2 } })
		shots = 1
	} | ConvertTo-Json -Depth 10
	try {
		Invoke-RestMethod -Method Post -Uri $manualUrl -ContentType 'application/json' -Body $invalidBody -TimeoutSec 180 | Out-Null
		throw 'The generic workflow accepted a string for its typed Int argument.'
	} catch {
		if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 400) { throw }
	}
	Write-Host "$workflowName and generic operation smoke tests passed." -ForegroundColor Green
}
