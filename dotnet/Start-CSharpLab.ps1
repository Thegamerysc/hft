param([switch]$Build, [string]$ReleasePath = '')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'Resolve-DotNet.ps1')
$sdk = Get-TraderProMaxDotNet $taskRoot
$runtime = Join-Path $taskRoot 'dotnet/runtime'
$releaseFile = Join-Path $runtime 'active-release.json'
$distribution = Join-Path $PSScriptRoot 'distribution.json'
if ($Build -and $ReleasePath) { throw 'Choose a new build or a staged release path.' }
if ($ReleasePath) {
    $buildDir = $ReleasePath
} elseif (-not $Build -and -not (Test-Path -LiteralPath $releaseFile) -and (Test-Path -LiteralPath $distribution)) {
    $buildDir = Join-Path $taskRoot ((Get-Content -LiteralPath $distribution -Raw | ConvertFrom-Json).release)
} elseif ($Build -or -not (Test-Path -LiteralPath $releaseFile)) {
    $buildDir = Join-Path $taskRoot ('dotnet/builds/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Push-Location $PSScriptRoot
    try { & $sdk build (Join-Path $taskRoot 'dotnet/TraderProMax.sln') -c Release --artifacts-path $buildDir --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Build failed. Source builds require .NET SDK 10.0.402; the running release was kept.' }
    } finally { Pop-Location }
} else {
    $buildDir = (Get-Content -LiteralPath $releaseFile -Raw | ConvertFrom-Json).directory
}
$resolvedBuild = [IO.Path]::GetFullPath($buildDir)
$allowedBuild = [IO.Path]::GetFullPath((Join-Path $taskRoot 'dotnet/builds')) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedBuild.StartsWith($allowedBuild,[StringComparison]::OrdinalIgnoreCase)) { throw 'Build must stay in this workspace.' }
foreach ($service in @('MarketHost','ExecutionWorker','Web','Research')) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedBuild "bin/TraderProMax.$service/release/TraderProMax.$service.dll"))) { throw "Missing build: $service" }
}
& (Join-Path $PSScriptRoot 'Stop-CSharpLab.ps1')
$dashboardPort = 8765
$feedPort = 8876
$bridgeFile = Join-Path $runtime 'bridge.json'
if (Test-Path -LiteralPath $bridgeFile) { $bridgeSettings = Get-Content -LiteralPath $bridgeFile -Raw | ConvertFrom-Json; $feedPort = $bridgeSettings.port; if ($bridgeSettings.dashboardPort) { $dashboardPort = $bridgeSettings.dashboardPort } }
$ports = @($dashboardPort,$feedPort)
$profileFile = Join-Path $runtime 'profiles.json'
if (Test-Path -LiteralPath $profileFile) { $ports += @((Get-Content -LiteralPath $profileFile -Raw | ConvertFrom-Json) | Select-Object -ExpandProperty workerPort) }
$occupied = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalAddress -in @('127.0.0.1','0.0.0.0','::') -and $_.LocalPort -in $ports })
if ($occupied.Count) { throw ('Required ports are already in use: ' + (($occupied | Select-Object -ExpandProperty LocalPort -Unique) -join ', ')) }
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$releaseJson = @{directory=$resolvedBuild; activatedUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json
[IO.File]::WriteAllText($releaseFile,$releaseJson,(New-Object Text.UTF8Encoding($false)))
foreach ($service in @('MarketHost','Web')) {
    $dll = Join-Path $resolvedBuild "bin/TraderProMax.$service/release/TraderProMax.$service.dll"
    $arguments = '"' + $dll + '" --root "' + $taskRoot + '"'
    if ($service -eq 'Web') { $arguments += ' --shadow' }
    $stdout = Join-Path $runtime ($service.ToLowerInvariant()+'.log')
    $stderr = Join-Path $runtime ($service.ToLowerInvariant()+'.errors.log')
    $process = Start-Process -FilePath $sdk -ArgumentList $arguments -WorkingDirectory $taskRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    Write-Output "$service PID $($process.Id)"
}
$deadline = [DateTime]::UtcNow.AddSeconds(20)
$healthy = $false
while ([DateTime]::UtcNow -lt $deadline) {
    try { $healthy = (Invoke-RestMethod -Uri "http://127.0.0.1:$dashboardPort/health" -TimeoutSec 1).ok; if ($healthy) { break } } catch {}
    Start-Sleep -Milliseconds 250
}
if (-not $healthy) { throw 'Dashboard failed its startup check. Inspect dotnet/runtime/web.errors.log and markethost.errors.log.' }
Write-Output "Shadow lab: http://127.0.0.1:$dashboardPort/ - demo execution is not automatically armed."
