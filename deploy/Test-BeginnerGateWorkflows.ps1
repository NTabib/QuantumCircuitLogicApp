# Dot-source this file and supply a callback resolver for Azure or the local Logic Apps host.
function Test-BeginnerGateWorkflows {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory = $true)] [scriptblock] $GetCallbackUrl,
		[ValidateRange(1, 1000)] [int] $Shots = 100
	)

	$cases = @(
		@{ Name = 'PauliX'; InitialState = '0'; Gates = @('X'); Outcomes = @('1') },
		@{ Name = 'Hadamard'; InitialState = '0'; Gates = @('H'); Outcomes = @('0', '1') },
		@{ Name = 'PhaseInterference'; InitialState = '0'; Gates = @('H', 'Z', 'H'); Outcomes = @('1') },
		@{ Name = 'Cnot'; LessonId = 'CNOT'; InitialState = '10'; Gates = @('CNOT'); Outcomes = @('11') },
		@{ Name = 'BellState'; InitialState = '00'; Gates = @('H', 'CNOT'); Outcomes = @('00', '11') },
		@{ Name = 'Rotation'; InitialState = '0'; Gates = @('RY'); Outcomes = @('0', '1') }
	)

	foreach ($case in $cases) {
		$workflowName = "Lesson$($case.Name)Workflow"
		Write-Host "==> Testing $workflowName" -ForegroundColor Cyan
		$url = & $GetCallbackUrl $workflowName
		if ([string]::IsNullOrWhiteSpace($url)) { throw "No callback URL for $workflowName." }
		$body = @{ shots = $Shots } | ConvertTo-Json
		$response = Invoke-RestMethod -Method Post -Uri $url -ContentType 'application/json' -Body $body -TimeoutSec 180
		$result = $response.circuitResult
		$lessonId = $case.Name
		if ($case.LessonId) { $lessonId = $case.LessonId }
		if ($response.lesson.id -ne $lessonId -or $response.circuit.initialState -ne $case.InitialState `
			-or @($response.circuit.gates).Count -ne $case.Gates.Count -or $null -eq $result `
			-or $result.simulator -ne 'FullState' -or $result.qubitCount -ne $case.InitialState.Length `
			-or $result.gateCount -ne $case.Gates.Count -or $result.shots -ne $Shots -or $null -eq $result.counts) {
			throw "$workflowName returned an unexpected lesson, circuit, or execution result."
		}

		for ($index = 0; $index -lt $case.Gates.Count; $index++) {
			if ($response.circuit.gates[$index].gate -ne $case.Gates[$index]) {
				throw "$workflowName did not build its expected $($case.Gates[$index]) gate sequence."
			}
		}
		switch ($case.Name) {
			'Cnot' {
				if ($response.circuit.gates[0].control -ne 0 -or $response.circuit.gates[0].target -ne 1) {
					throw 'LessonCnotWorkflow must use qubit 0 as control and qubit 1 as target.'
				}
			}
			'BellState' {
				if ($response.circuit.gates[0].target -ne 0 -or $response.circuit.gates[1].control -ne 0 -or $response.circuit.gates[1].target -ne 1) {
					throw 'LessonBellStateWorkflow must apply H(0) then CNOT(0,1).'
				}
			}
			'Rotation' {
				if ($response.circuit.gates[0].target -ne 0 -or [Math]::Abs($response.circuit.gates[0].angle - [Math]::PI / 2) -gt 1e-10) {
					throw 'LessonRotationWorkflow must apply Ry(pi/2) to qubit 0.'
				}
			}
			default {
				foreach ($gate in $response.circuit.gates) {
					if ($gate.target -ne 0) { throw "$workflowName must apply all gates to qubit 0." }
				}
			}
		}

		$total = 0
		foreach ($entry in $result.counts.psobject.Properties) {
			if ($case.Outcomes -notcontains $entry.Name -or ($entry.Value -isnot [int] -and $entry.Value -isnot [long]) -or $entry.Value -lt 1) {
				throw "$workflowName returned unexpected outcome '$($entry.Name)' or an invalid measurement count."
			}
			$total += $entry.Value
		}
		if ($total -ne $Shots -or $case.Outcomes -notcontains $result.sample) {
			throw "$workflowName measurement counts do not total $Shots or the sample is unexpected."
		}
		Write-Host "$workflowName smoke test passed." -ForegroundColor Green
	}
}
