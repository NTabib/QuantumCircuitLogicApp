# Dot-source this file and supply a callback resolver for Azure or the local Logic Apps host.
function Test-AlgorithmWorkflows {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory = $true)] [scriptblock] $GetCallbackUrl,
		[ValidateRange(1, 1000)] [int] $Shots = 100,
		[string] $OnlyWorkflow
	)

	function Assert-Histogram($Result, [int] $Qubits, [int] $Gates, [string[]] $Allowed, [string] $Label) {
		if ($null -eq $Result -or $Result.simulator -ne 'FullState' -or $Result.qubitCount -ne $Qubits `
			-or $Result.gateCount -ne $Gates -or $Result.shots -ne $Shots -or $null -eq $Result.counts) {
			throw "$Label failed: unexpected circuit execution metadata."
		}
		$total = 0
		foreach ($entry in $Result.counts.psobject.Properties) {
			if ($Allowed -notcontains $entry.Name -or ($entry.Value -isnot [int] -and $entry.Value -isnot [long]) -or $entry.Value -lt 1) {
				throw "$Label failed: unexpected measurement '$($entry.Name)' or invalid count."
			}
			$total += $entry.Value
		}
		if ($total -ne $Shots -or $Allowed -notcontains $Result.sample) {
			throw "$Label failed: shot total or sampled outcome is incorrect."
		}
	}

	function Count-Outcome($Counts, [string] $Outcome) {
		$property = $Counts.psobject.Properties[$Outcome]
		if ($null -eq $property) { return 0 }
		return $property.Value
	}

	$cases = @(
		@{ Name = 'DeutschJozsa'; Qubits = 3; Gates = 7; Outcomes = @('110', '111') },
		@{ Name = 'BernsteinVazirani'; Qubits = 4; Gates = 9; Outcomes = @('1010', '1011') },
		@{ Name = 'Grover'; Qubits = 2; Gates = 12; Outcomes = @('11') },
		@{ Name = 'GroverThreeQubit'; Qubits = 3; Gates = 21; Outcomes = @('000', '001', '010', '011', '100', '101', '110', '111') },
		@{ Name = 'QuantumFourierTransform'; Qubits = 2; Gates = 8; Outcomes = @('00', '01', '10', '11') },
		@{ Name = 'QuantumPhaseEstimation'; Qubits = 3; Gates = 16; Outcomes = @('011') },
		@{ Name = 'Simon'; Qubits = 3; Gates = 6; Outcomes = @('000', '001', '110', '111') },
		@{ Name = 'SuperdenseCoding'; Qubits = 2; Gates = 6; Outcomes = @('11') },
		@{ Name = 'Ghz'; Qubits = 3; Gates = 3; Outcomes = @('000', '111') },
		@{ Name = 'Vqe' },
		@{ Name = 'Qaoa' }
	)
	if ($OnlyWorkflow) {
		$cases = @($cases | Where-Object { "$($_.Name)Workflow" -eq $OnlyWorkflow })
		if ($cases.Count -ne 1) { throw "Unknown algorithm workflow '$OnlyWorkflow'." }
	}

	foreach ($case in $cases) {
		$workflowName = "$($case.Name)Workflow"
		Write-Host "==> Testing $workflowName" -ForegroundColor Cyan
		$url = & $GetCallbackUrl $workflowName
		if ([string]::IsNullOrWhiteSpace($url)) { throw "No callback URL for $workflowName." }
		$body = @{ shots = $Shots } | ConvertTo-Json
		$response = Invoke-RestMethod -Method Post -Uri $url -ContentType 'application/json' -Body $body -TimeoutSec 180
		if ($response.algorithm -ne $case.Name) { throw "$workflowName returned the wrong algorithm identifier." }

		if ($case.Name -notin @('Vqe', 'Qaoa')) {
			Assert-Histogram $response.circuitResult $case.Qubits $case.Gates $case.Outcomes $workflowName
			if (@($response.circuit.gates).Count -ne $case.Gates) { throw "$workflowName returned the wrong gate count." }
			switch ($case.Name) {
				'DeutschJozsa' {
					if ($response.classification -ne 'balanced') { throw 'Deutsch-Jozsa failed to identify the balanced oracle.' }
				}
				'BernsteinVazirani' {
					if ($response.recoveredSecret -ne '101') { throw 'Bernstein-Vazirani failed to recover 101.' }
				}
				'GroverThreeQubit' {
					if ($response.circuit.initialState -ne '000' -or $response.markedState -ne '111') {
						throw 'Three-qubit Grover must start at 000 and mark 111.'
					}
					$gateNames = @($response.circuit.gates | ForEach-Object { $_.gate })
					$expected = @('H','H','H','H','TOFFOLI','H','H','H','H','X','X','X','H','TOFFOLI','H','X','X','X','H','H','H')
					if (($gateNames -join ',') -ne ($expected -join ',') `
						-or $response.circuit.gates[4].control -ne 0 -or $response.circuit.gates[4].control2 -ne 1 -or $response.circuit.gates[4].target -ne 2 `
						-or $response.circuit.gates[13].control -ne 0 -or $response.circuit.gates[13].control2 -ne 1 -or $response.circuit.gates[13].target -ne 2) {
						throw 'Three-qubit Grover oracle or diffuser is incomplete.'
					}
					$markedProbability = (Count-Outcome $response.circuitResult.counts '111') / [double]$Shots
					if ($Shots -ge 100 -and $markedProbability -lt 0.55) {
						throw 'Three-qubit Grover did not amplify 111 above the uniform baseline.'
					}
					if ($Shots -ge 500 -and ($markedProbability -lt 0.70 -or $markedProbability -gt 0.86)) {
						throw 'Three-qubit Grover deviated from the ideal 25/32 marked-state probability.'
					}
				}
				'QuantumFourierTransform' {
					Assert-Histogram $response.verificationResult 2 16 @('01') "$workflowName round trip"
					if (@($response.verificationCircuit.gates).Count -ne 16) { throw 'QFT round-trip circuit is incomplete.' }
				}
				'QuantumPhaseEstimation' {
					if ($null -eq $response.estimatedPhase -or [Math]::Abs($response.estimatedPhase - 0.25) -gt 1e-10) { throw 'Phase estimation failed to recover 1/4.' }
				}
				'Simon' {
					$informative = (Count-Outcome $response.circuitResult.counts '110') + (Count-Outcome $response.circuitResult.counts '111')
					$expected = 'inconclusive'
					if ($informative -gt 0) { $expected = '11' }
					if ($response.recoveredSecret -ne $expected) { throw 'Simon classical recovery disagrees with its measurements.' }
				}
				'SuperdenseCoding' {
					if ($response.sentMessage -ne '11' -or $response.decodedMessage -ne '11') { throw 'Superdense coding failed to decode 11.' }
				}
			}
		} else {
			$evaluations = @($response.evaluations)
			if ($evaluations.Count -ne 3) { throw "$workflowName must evaluate all three parameter candidates." }
			$objectives = @()
			foreach ($evaluation in $evaluations) {
				if ($case.Name -eq 'Vqe') {
					Assert-Histogram $evaluation.circuitResult 1 1 @('0', '1') "$workflowName evaluation"
					$score = ((Count-Outcome $evaluation.circuitResult.counts '0') - (Count-Outcome $evaluation.circuitResult.counts '1')) / [double]$Shots
				} else {
					Assert-Histogram $evaluation.circuitResult 2 7 @('00', '01', '10', '11') "$workflowName evaluation"
					$score = ((Count-Outcome $evaluation.circuitResult.counts '01') + (Count-Outcome $evaluation.circuitResult.counts '10')) / [double]$Shots
				}
				if ($null -eq $evaluation.objective -or [Math]::Abs($score - $evaluation.objective) -gt 1e-10) {
					throw "$workflowName objective is inconsistent with measurement counts."
				}
				$objectives += $score
			}
			if ($case.Name -eq 'Vqe') {
				$best = ($objectives | Measure-Object -Minimum).Minimum
				if ([Math]::Abs($best + 1) -gt 1e-10) { throw 'VQE did not find the ground-state energy -1.' }
			} else {
				$best = ($objectives | Measure-Object -Maximum).Maximum
				if ([Math]::Abs($best - 1) -gt 1e-10) { throw 'QAOA did not find the maximum cut value 1.' }
			}
			if ($null -eq $response.bestObjective -or [Math]::Abs($response.bestObjective - $best) -gt 1e-10) {
				throw "$workflowName failed to select its best evaluation."
			}
			$selected = @($evaluations | Where-Object { ($_.parameters | ConvertTo-Json -Compress) -eq ($response.bestParameters | ConvertTo-Json -Compress) })
			if ($selected.Count -ne 1 -or [Math]::Abs($selected[0].objective - $best) -gt 1e-10 `
				-or ($selected[0].circuitResult.counts | ConvertTo-Json -Compress) -ne ($response.bestCounts | ConvertTo-Json -Compress)) {
				throw "$workflowName best parameters or counts do not match the selected evaluation."
			}
		}
		Write-Host "$workflowName smoke test passed." -ForegroundColor Green
	}
}
