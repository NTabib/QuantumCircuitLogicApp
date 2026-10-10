# Dot-source this file and supply a callback resolver for Azure or the local Logic Apps host.
function Test-Shor15Workflow {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory = $true)] [scriptblock] $GetCallbackUrl,
		[ValidateRange(1, 1000)] [int] $Shots = 100
	)

	$workflowName = 'Shor15Workflow'
	Write-Host "==> Testing $workflowName" -ForegroundColor Cyan
	$url = & $GetCallbackUrl $workflowName
	if ([string]::IsNullOrWhiteSpace($url)) { throw "No callback URL for $workflowName." }
	$response = Invoke-RestMethod -Method Post -Uri $url -ContentType 'application/json' -Body (@{ shots = $Shots } | ConvertTo-Json) -TimeoutSec 180
	$result = $response.circuitResult
	if ($response.algorithm -ne 'Shor15' -or $response.modulus -ne 15 -or $response.base -ne 2 `
		-or $result.simulator -ne 'FullState' -or $result.qubitCount -ne 6 `
		-or $result.gateCount -ne 25 -or $result.shots -ne $Shots -or $null -eq $result.counts) {
		throw 'Shor 15 workflow returned incorrect circuit execution metadata.'
	}

	$expectedOrbit = @('0001', '0010', '0100', '1000')
	$total = 0
	$informative = 0
	foreach ($entry in $result.counts.psobject.Properties) {
		$bits = $entry.Name
		if ($bits -notmatch '^[01]{6}$' -or $expectedOrbit -notcontains $bits.Substring(2) `
			-or ($entry.Value -isnot [int] -and $entry.Value -isnot [long]) -or $entry.Value -lt 1) {
			throw "Shor 15 workflow returned invalid phase, work register, or count '$bits'."
		}
		$total += $entry.Value
		if ($bits[1] -eq '1') { $informative += $entry.Value }
	}
	if ($total -ne $Shots -or $response.informativeShots -ne $informative `
		-or $expectedOrbit -notcontains $result.sample.Substring(2) -or @($result.measurements).Count -ne $Shots) {
		throw 'Shor 15 workflow returned inconsistent sample or informative shot count.'
	}
	$firstPhase = $null
	foreach ($measurement in $result.measurements) {
		if (@($measurement).Count -ne 6) { throw 'Shor 15 workflow returned an incomplete per-shot measurement.' }
		if ($measurement[1] -and $null -eq $firstPhase) {
			$firstPhase = if ($measurement[0]) { '11' } else { '01' }
		}
	}

	if ($informative -gt 0) {
		if ($response.status -ne 'factored' -or $response.order -ne 4 `
			-or @($response.factors).Count -ne 2 -or $response.factors[0] -ne 3 -or $response.factors[1] -ne 5 `
			-or $response.observedPhaseBits -ne $firstPhase) {
			throw 'Shor 15 workflow failed to recover order 4 and factors 3, 5 from an informative phase.'
		}
	} elseif ($response.status -ne 'inconclusive' -or $response.order -ne 0 `
		-or @($response.factors).Count -ne 0 -or $response.observedPhaseBits -ne 'none') {
		throw 'Shor 15 workflow must not claim factors when no informative phase was measured.'
	}
	Write-Host "$workflowName smoke test passed." -ForegroundColor Green
}
