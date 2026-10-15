$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'Resolve-DotNet.ps1')
$sdk = Get-TraderProMaxDotNet $taskRoot
$profilesFile = Join-Path $taskRoot 'dotnet/runtime/profiles.json'
$demoBound = $false
if (Test-Path -LiteralPath $profilesFile) { $demoBound = @((Get-Content -LiteralPath $profilesFile -Raw | ConvertFrom-Json) | Where-Object {$_.execution -eq 'demo' -and $_.login -gt 0}).Count -gt 0 }
$state = $null
$dashboardPort = 8765
$bridgeFile = Join-Path $taskRoot 'dotnet/runtime/bridge.json'
if (Test-Path -LiteralPath $bridgeFile) { $bridgeSettings = Get-Content -LiteralPath $bridgeFile -Raw | ConvertFrom-Json; if ($bridgeSettings.dashboardPort) { $dashboardPort = $bridgeSettings.dashboardPort } }
try { $state = Invoke-RestMethod -Uri "http://127.0.0.1:$dashboardPort/api/state" -TimeoutSec 3 } catch {}
if ($demoBound -and ($null -eq $state -or @($state.profiles | Where-Object {$_.active -gt 0 -or $_.running -and $_.mode -eq 'demo'}).Count -gt 0)) {
    throw 'Stop new demo entries and reconcile flat inventory before stopping/upgrading services. Broker terminals are kept running.'
}
# Only this workspace's SDK and three exact C# application names qualify. No process-tree termination.
$owned = @(Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object {
    $cmd = [string]$_.CommandLine
    $normal = $cmd.Replace('/','\')
    $_.ExecutablePath -eq $sdk -and $normal.Contains($taskRoot) -and $normal -match 'TraderProMax\.(MarketHost|ExecutionWorker|Web)\.dll'
})
foreach ($item in $owned) { Stop-Process -Id $item.ProcessId -ErrorAction SilentlyContinue; Write-Output "Stopped C# PID $($item.ProcessId)" }
