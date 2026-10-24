param(
    [int]$ApiPort = 5221,
    [int]$PayerPort = 5222
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$processes = @()
$runDirectory = Join-Path $repoRoot "artifacts/smoke/$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

try {
    dotnet build (Join-Path $repoRoot 'RevenueCycle.slnx') --configuration Release --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

    $services = @(
        @{ Project = 'RevenueCycle.Api'; Port = $ApiPort; Name = 'revenue-cycle-api'; Capability = 'workflowProcessingAvailable' },
        @{ Project = 'RevenueCycle.MockPayer'; Port = $PayerPort; Name = 'mock-payer'; Capability = 'claimSubmissionAvailable' }
    )

    foreach ($service in $services) {
        $baseUrl = "http://127.0.0.1:$($service.Port)"
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $service.Port)
        try { $listener.Start() } finally { $listener.Stop() }
        $dll = Join-Path $repoRoot "src/$($service.Project)/bin/Release/net10.0/$($service.Project).dll"
        $startOptions = @{
            FilePath = 'dotnet'
            ArgumentList = @("`"$dll`"", '--urls', $baseUrl)
            WorkingDirectory = $repoRoot
            PassThru = $true
            RedirectStandardOutput = Join-Path $runDirectory "$($service.Project).stdout.log"
            RedirectStandardError = Join-Path $runDirectory "$($service.Project).stderr.log"
        }
        if ($IsWindows) { $startOptions.WindowStyle = 'Hidden' }
        $process = Start-Process @startOptions
        $processes += $process
        $healthy = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            if ($process.HasExited) { throw "$($service.Name) exited with code $($process.ExitCode). See $runDirectory." }
            try {
                $response = Invoke-WebRequest "$baseUrl/health" -TimeoutSec 2 -NoProxy
                $healthy = $response.StatusCode -eq 200 -and $response.Content -eq 'Healthy'
            } catch { $healthy = $false }
            if ($healthy) { break }
            Start-Sleep -Milliseconds 250
        }
        if (-not $healthy) { throw "$($service.Name) did not become healthy. See $runDirectory." }
        $metadata = Invoke-RestMethod $baseUrl -TimeoutSec 2 -NoProxy
        if ($metadata.service -ne $service.Name -or $metadata.stage -ne 'scaffold' -or
            $metadata.syntheticDataOnly -ne $true -or $metadata.($service.Capability) -ne $false) {
            throw "Unexpected metadata from $($service.Name)."
        }
        Write-Output "PASS: $($service.Name) health and scaffold metadata."
    }
} finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}
