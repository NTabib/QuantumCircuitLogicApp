# Dot-source this file and supply a callback resolver for Azure or the local Logic Apps host.
function Test-TeleportationWorkflow {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory = $true)] [scriptblock] $GetCallbackUrl,
		[ValidateRange(1, 1000)] [int] $Shots = 200
	)

	$workflowName = 'TeleportationWorkflow'
	Write-Host "==> Testing $workflowName" -ForegroundColor Cyan
	$url = & $GetCallbackUrl $workflowName
	if ([string]::IsNullOrWhiteSpace($url)) { throw "No callback URL for $workflowName." }
	$response = Invoke-RestMethod -Method Post -Uri $url -ContentType 'application/json' -Body (@{ shots = $Shots } | ConvertTo-Json) -TimeoutSec 180
	$circuit = $response.circuit
	$result = $response.circuitResult
	$gates = @($circuit.gates)
	if ($response.algorithm -ne 'QuantumTeleportation' -or $response.inputState -ne '|+>' `
		-or $circuit.initialState -ne '000' -or $gates.Count -ne 12 `
		-or $result.simulator -ne 'FullState' -or $result.qubitCount -ne 3 `
		-or $result.gateCount -ne 8 -or $result.shots -ne $Shots `
		-or @($result.measurements).Count -ne $Shots -or @($result.classicalMeasurements).Count -ne $Shots) {
		throw "$workflowName returned unexpected circuit or execution metadata."
	}

	$expectedGates = @('H', 'H', 'CNOT', 'CNOT', 'H', 'MEASURE', 'MEASURE', 'RESET', 'RESET', 'IF', 'IF', 'H')
	if ($gates.Count -ne $expectedGates.Count) { throw "$workflowName should contain $($expectedGates.Count) instructions." }
	for ($index = 0; $index -lt $expectedGates.Count; $index++) {
		if ($gates[$index].gate -ne $expectedGates[$index]) {
			throw "$workflowName instruction $index should be $($expectedGates[$index])."
		}
	}
	if ($gates[5].measurement -ne 'm0' -or $gates[6].measurement -ne 'm1' `
		-or $gates[9].measurement -ne 'm1' -or $gates[9].then[0].gate -ne 'X' -or $gates[9].then[0].target -ne 2 `
		-or $gates[10].measurement -ne 'm0' -or $gates[10].then[0].gate -ne 'Z' -or $gates[10].then[0].target -ne 2) {
		throw "$workflowName Bell measurements or Pauli feed-forward corrections are incorrect."
	}

	$total = 0
	foreach ($entry in $result.counts.psobject.Properties) {
		if ($entry.Name -notin @('000', '001') -or ($entry.Value -isnot [int] -and $entry.Value -isnot [long]) -or $entry.Value -lt 1) {
			throw "$workflowName produced an invalid X-basis verification outcome '$($entry.Name)' for teleported |+>."
		}
		$total += $entry.Value
	}
	if ($total -ne $Shots) { throw "$workflowName terminal counts do not total $Shots shots." }
	for ($index = 0; $index -lt $Shots; $index++) {
		$named = $result.classicalMeasurements[$index]
		$terminal = $result.measurements[$index]
		if ($named.m0 -isnot [bool] -or $named.m1 -isnot [bool] `
			-or @($terminal).Count -ne 3 -or $terminal[0] -isnot [bool] -or $terminal[0] `
			-or $terminal[1] -isnot [bool] -or $terminal[1] -or $terminal[2] -isnot [bool] -or $terminal[2]) {
			throw "$workflowName returned invalid mid-circuit or terminal measurement values for shot $index."
		}
	}
	Write-Host "$workflowName Bell measurements, feed-forward corrections, and teleported-state samples passed." -ForegroundColor Green
}
