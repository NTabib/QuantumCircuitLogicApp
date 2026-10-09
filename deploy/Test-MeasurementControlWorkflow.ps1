# Dot-source this file and supply a callback resolver for Azure or the local Logic Apps host.
function Test-MeasurementControlWorkflow {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory = $true)] [scriptblock] $GetCallbackUrl,
		[ValidateRange(1, 1000)] [int] $Shots = 20
	)

	$workflowName = 'MeasurementControlWorkflow'
	Write-Host "==> Testing $workflowName" -ForegroundColor Cyan
	$url = & $GetCallbackUrl $workflowName
	if ([string]::IsNullOrWhiteSpace($url)) { throw "No callback URL for $workflowName." }
	$response = Invoke-RestMethod -Method Post -Uri $url -ContentType 'application/json' -Body (@{ shots = $Shots } | ConvertTo-Json) -TimeoutSec 180
	$circuit = $response.circuit
	$result = $response.circuitResult
	if ($response.referenceProgram -notmatch 'Reset\(q0\)' -or @($circuit.gates).Count -ne 6 `
		-or $result.gateCount -ne 5 -or $result.shots -ne $Shots `
		-or @($result.measurements).Count -ne $Shots -or @($result.classicalMeasurements).Count -ne $Shots) {
		throw "$workflowName returned unexpected instruction or result metadata."
	}

	if ($result.counts.psobject.Properties.Count -ne 1 -or $result.counts.'00' -ne $Shots) {
		throw "$workflowName must reset qubit 0 and finish in |00> for every shot."
	}
	for ($index = 0; $index -lt $Shots; $index++) {
		$named = $result.classicalMeasurements[$index]
		$terminal = $result.measurements[$index]
		if ($named.measuredZero -isnot [bool] -or $named.measuredZero `
			-or $named.measuredOne -isnot [bool] -or -not $named.measuredOne `
			-or @($terminal).Count -ne 2 -or $terminal[0] -isnot [bool] -or $terminal[0] `
			-or $terminal[1] -isnot [bool]) {
			throw "$workflowName returned incorrect collapse, reset, or true/else branch behavior for shot $index."
		}
	}

	$manualUrl = & $GetCallbackUrl 'QuantumCircuitWorkflow'
	$manualBody = @{
		initialState = '00'
		gates = @(
			@{ gate = 'MEASURE'; target = 0; measurement = 'measuredZero' },
			@{ gate = 'X'; target = 0 },
			@{ gate = 'MEASURE'; target = 0; measurement = 'measuredOne' },
			@{ gate = 'RESET'; target = 0 },
			@{ gate = 'IF'; measurement = 'measuredOne'; equals = $true; then = @(@{ gate = 'X'; target = 1 }); else = @(@{ gate = 'X'; target = 0 }) },
			@{ gate = 'IF'; measurement = 'measuredZero'; equals = $true; then = @(@{ gate = 'X'; target = 0 }); else = @(@{ gate = 'X'; target = 1 }) }
		)
		shots = $Shots
	} | ConvertTo-Json -Depth 10
	$manualResult = (Invoke-RestMethod -Method Post -Uri $manualUrl -ContentType 'application/json' -Body $manualBody -TimeoutSec 180).circuitResult
	if ($manualResult.counts.'00' -ne $Shots -or @($manualResult.classicalMeasurements).Count -ne $Shots) {
		throw 'QuantumCircuitWorkflow did not execute the ordered measurement/reset/IF instruction stream.'
	}

	$invalidBody = @{
		initialState = '00'
		gates = @(@{ gate = 'IF'; measurement = 'notRecorded'; equals = $true; then = @(@{ gate = 'X'; target = 0 }) })
		shots = 1
	} | ConvertTo-Json -Depth 10
	try {
		Invoke-RestMethod -Method Post -Uri $manualUrl -ContentType 'application/json' -Body $invalidBody -TimeoutSec 180 | Out-Null
		throw 'The generic workflow accepted an IF instruction referencing an unrecorded measurement.'
	} catch {
		if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 400) { throw }
	}

	Write-Host "$workflowName and generic mid-circuit instruction smoke tests passed." -ForegroundColor Green
}
