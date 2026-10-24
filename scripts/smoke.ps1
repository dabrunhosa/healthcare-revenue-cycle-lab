param([int]$ApiPort = 5221, [int]$PayerPort = 5222, [switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$runDirectory = Join-Path $repoRoot "artifacts/smoke/$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$apiUrl = "http://127.0.0.1:$ApiPort"
$payerUrl = "http://127.0.0.1:$PayerPort"
$script:processes = @()
$script:sequence = 0

function Start-LabHost($project, $port, $mode = 'Accept') {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
    try { $listener.Start() } finally { $listener.Stop() }
    $script:sequence++
    $dll = Join-Path $repoRoot "src/$project/bin/Release/net10.0/$project.dll"
    $storage = Join-Path $runDirectory $project
    $startOptions = @{
        FilePath = 'dotnet'
        ArgumentList = @("`"$dll`"", '--urls', "http://127.0.0.1:$port", '--Storage:Directory', "`"$storage`"",
            '--Payer:BaseUrl', $payerUrl, '--Payer:Mode', $mode, '--Workflow:BaseDelaySeconds', '0.2',
            '--Workflow:MaxDelaySeconds', '0.5', '--Telemetry:Console', 'true')
        WorkingDirectory = $repoRoot
        PassThru = $true
        RedirectStandardOutput = Join-Path $runDirectory "$project-$script:sequence.stdout.log"
        RedirectStandardError = Join-Path $runDirectory "$project-$script:sequence.stderr.log"
    }
    if ($IsWindows) { $startOptions.WindowStyle = 'Hidden' }
    $process = Start-Process @startOptions
    $script:processes += $process
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($process.HasExited) { throw "$project exited. See $runDirectory." }
        try {
            $health = Invoke-WebRequest "http://127.0.0.1:$port/health" -NoProxy -TimeoutSec 2
            if ($health.StatusCode -eq 200 -and $health.Content -eq 'Healthy') { return $process }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    throw "$project did not become healthy. See $runDirectory."
}

function Stop-LabHost($process) {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
}

function Send-Encounter($suffix) {
    $inputData = Get-Content (Join-Path $repoRoot 'samples/encounter.json') -Raw | ConvertFrom-Json
    $inputData.encounter.id = "syn-encounter-$suffix"
    $body = $inputData | ConvertTo-Json -Depth 10
    $response = Invoke-WebRequest "$apiUrl/workflows" -Method Post -Headers @{ 'Idempotency-Key' = $suffix } -Body $body -ContentType 'application/json' -NoProxy
    if ($response.StatusCode -ne 201) { throw "Expected created workflow." }
    return @{ Workflow = $response.Content | ConvertFrom-Json; Body = $body; Key = $suffix }
}

function Wait-Stage($id, $stage) {
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        $workflow = Invoke-RestMethod "$apiUrl/workflows/$id" -NoProxy
        if ($workflow.stage -eq $stage) { return $workflow }
        Start-Sleep -Milliseconds 250
    }
    throw "Workflow did not reach $stage. Last stage: $($workflow.stage). See $runDirectory."
}

try {
    if (-not $NoBuild) {
        dotnet build (Join-Path $repoRoot 'RevenueCycle.slnx') --configuration Release --disable-build-servers -m:1
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE." }
    }
    $payer = Start-LabHost 'RevenueCycle.MockPayer' $PayerPort
    $api = Start-LabHost 'RevenueCycle.Api' $ApiPort
    $metadata = Invoke-RestMethod $apiUrl -NoProxy
    if ($metadata.stage -ne 'phase-1b' -or -not $metadata.workflowProcessingAvailable) { throw 'Incorrect API metadata.' }
    $openapi = Invoke-RestMethod "$apiUrl/openapi/v1.json" -NoProxy
    if (-not $openapi.paths.'/workflows') { throw 'Missing workflow OpenAPI contract.' }
    $normal = Send-Encounter 'normal'
    $id = $normal.Workflow.correlation.workflowId
    $submitted = Wait-Stage $id 'Submitted'
    if (-not $submitted.correlation.payerClaimId -or $submitted.correlation.traceNumber -or $submitted.correlation.paymentReference) { throw 'Incorrect correlation lifecycle.' }
    $duplicate = Invoke-RestMethod "$apiUrl/workflows" -Method Post -Headers @{ 'Idempotency-Key' = $normal.Key } -Body $normal.Body -ContentType 'application/json' -NoProxy
    if ($duplicate.correlation.workflowId -ne $id) { throw 'Duplicate created another workflow.' }
    $conflicting = ($normal.Body | ConvertFrom-Json)
    $conflicting.billing.lines[0].units = 2
    $response = Invoke-WebRequest "$apiUrl/workflows" -Method Post -Headers @{ 'Idempotency-Key' = $normal.Key } -Body ($conflicting | ConvertTo-Json -Depth 10) -ContentType 'application/json' -NoProxy -SkipHttpErrorCheck
    if ($response.StatusCode -ne 409) { throw 'Conflicting key was not rejected.' }
    $invalid = ($normal.Body | ConvertFrom-Json)
    $invalid.patient | Add-Member -NotePropertyName name -NotePropertyValue 'Not supported'
    $response = Invoke-WebRequest "$apiUrl/workflows" -Method Post -Headers @{ 'Idempotency-Key' = 'unsupported' } -Body ($invalid | ConvertTo-Json -Depth 10) -ContentType 'application/json' -NoProxy -SkipHttpErrorCheck
    if ($response.StatusCode -ne 400) { throw 'Unsupported FHIR fields were not rejected.' }
    $artifact = Invoke-WebRequest "$apiUrl/workflows/$id/claim" -NoProxy
    if ($artifact.Content -notmatch 'ST\*837' -or $artifact.Content -notmatch '125.50') { throw 'Claim artifact missing.' }
    Write-Output 'PASS: ingestion, 837 artifact, payer receipt, idempotency, conflict, input validation, and OpenAPI.'

    Stop-LabHost $payer
    $payer = Start-LabHost 'RevenueCycle.MockPayer' $PayerPort 'Reject'
    $rejected = Send-Encounter 'rejected'
    $rejectedFlow = Wait-Stage $rejected.Workflow.correlation.workflowId 'ManualReview'
    if ($rejectedFlow.attempts.Count -ne 1 -or $rejectedFlow.attempts[0].outcome -ne 'PayerRejected') { throw 'Permanent rejection was retried.' }
    Write-Output 'PASS: permanent rejection enters manual review after one attempt.'

    Stop-LabHost $payer
    $payer = Start-LabHost 'RevenueCycle.MockPayer' $PayerPort 'Unavailable'
    Stop-LabHost $api
    $api = Start-LabHost 'RevenueCycle.Api' $ApiPort
    $failure = Send-Encounter 'unavailable'
    $failed = Wait-Stage $failure.Workflow.correlation.workflowId 'ManualReview'
    if ($failed.attempts.Count -ne 4 -or 'CircuitOpen' -notin $failed.attempts.outcome) { throw 'Retry budget or circuit breaker failed.' }
    Stop-LabHost $payer
    $payer = Start-LabHost 'RevenueCycle.MockPayer' $PayerPort
    Start-Sleep -Seconds 11
    $null = Invoke-RestMethod "$apiUrl/workflows/$($failed.correlation.workflowId)/recover" -Method Post -ContentType 'application/json' -Body '{"reasonCode":"PayerRestored"}' -NoProxy
    $recovered = Wait-Stage $failed.correlation.workflowId 'Submitted'
    if ($recovered.correlation.claimId -ne $failed.correlation.claimId -or $recovered.recoveryCycle -ne 1) { throw 'Manual recovery changed claim identity.' }
    Write-Output 'PASS: bounded retries, open circuit, circuit recovery, and audited manual requeue.'

    Stop-LabHost $payer
    $payer = Start-LabHost 'RevenueCycle.MockPayer' $PayerPort 'AcceptThenTimeout'
    $ambiguous = Send-Encounter 'ambiguous'
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $receipts = Invoke-RestMethod "$payerUrl/claims" -NoProxy
        if ($ambiguous.Workflow.correlation.claimId -in $receipts.claimId) { break }
        Start-Sleep -Milliseconds 50
    }
    if ($ambiguous.Workflow.correlation.claimId -notin $receipts.claimId) { throw 'Payer did not persist ambiguous receipt.' }
    Stop-LabHost $api
    Stop-LabHost $payer
    $payer = Start-LabHost 'RevenueCycle.MockPayer' $PayerPort 'AcceptThenTimeout'
    $api = Start-LabHost 'RevenueCycle.Api' $ApiPort
    $resolved = Wait-Stage $ambiguous.Workflow.correlation.workflowId 'Submitted'
    $receipts = Invoke-RestMethod "$payerUrl/claims" -NoProxy
    if (@($receipts | Where-Object claimId -EQ $resolved.correlation.claimId).Count -ne 1 -or
        'UnknownAfterRestart' -notin $resolved.attempts.outcome) { throw "Ambiguous restart failed: matching receipts=$(@($receipts | Where-Object claimId -EQ $resolved.correlation.claimId).Count), outcomes=$($resolved.attempts.outcome -join ',')." }
    $original = Invoke-RestMethod "$apiUrl/workflows/$id" -NoProxy
    if ($original.correlation.payerClaimId -ne $submitted.correlation.payerClaimId) { throw 'Restart lost state.' }
    $payerPayload = @{ claimId = $resolved.claim.id; encounterId = $resolved.clinical.encounterId; amount = 999; currency = 'USD'; artifact = $resolved.claim.artifact } | ConvertTo-Json
    $conflict = Invoke-WebRequest "$payerUrl/claims" -Method Post -Headers @{ 'Idempotency-Key' = $resolved.claim.id } -Body $payerPayload -ContentType 'application/json' -NoProxy -SkipHttpErrorCheck
    if ($conflict.StatusCode -ne 409) { throw 'Payer accepted a conflicting duplicate.' }
    Write-Output 'PASS: payer acceptance with lost response, restart of both services, stable receipt, and payer conflict detection.'
    $logs = (Get-ChildItem $runDirectory -Filter '*.stdout.log' | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    if ($logs -notmatch 'payer.submit' -or $logs -notmatch 'submission.outcome') { throw 'Submission traces missing.' }
    if ($logs -match 'syn-patient-001|DEMO001|Z00.00|SYNTHETIC PROVIDER') { throw 'Clinical payload leaked into logs.' }
    Write-Output "PASS: real submission traces and metadata-only log check. Evidence: $runDirectory"
} finally {
    foreach ($process in $script:processes) { Stop-LabHost $process; $process.Dispose() }
}
