param([string]$NinjaTraderDocuments='')
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if(-not $NinjaTraderDocuments){$NinjaTraderDocuments=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NinjaTrader 8'}
$taskNinjaRoot=[IO.Path]::GetFullPath($NinjaTraderDocuments)
$taskAddons=Join-Path $taskNinjaRoot 'bin/Custom/AddOns'
if(-not(Test-Path -LiteralPath $taskAddons -PathType Container)){throw 'Install/start NinjaTrader first, or pass its actual Documents/NinjaTrader 8 folder.'}
$taskBridge=Join-Path $taskRoot 'dotnet/runtime/bridge.json'
if(-not(Test-Path -LiteralPath $taskBridge)){throw 'Run Setup.ps1 first.'}
$taskSource=Join-Path $PSScriptRoot 'adapters/NinjaTrader/TraderProMaxNativeFeed.cs'
$taskTarget=Join-Path $taskAddons 'TraderProMaxNativeFeed.cs'
$taskSettings=Join-Path $taskNinjaRoot 'TraderProMax'
New-Item -ItemType Directory -Path $taskSettings -Force | Out-Null
if(Test-Path -LiteralPath $taskTarget){
    if((Get-FileHash -LiteralPath $taskSource).Hash -ne (Get-FileHash -LiteralPath $taskTarget).Hash){
        $taskBackup=Join-Path $taskSettings ('backups/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N')+'.cs.bak')
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskBackup) -Force | Out-Null
        Copy-Item -LiteralPath $taskTarget -Destination $taskBackup
    }
}
Copy-Item -LiteralPath $taskSource -Destination $taskTarget -Force
[IO.File]::WriteAllText((Join-Path $taskSettings 'bridge-location.txt'),$taskBridge,(New-Object Text.UTF8Encoding($false)))
Write-Output 'Adapter installed. In NinjaTrader, open New > NinjaScript Editor and press F5 to compile.'
Write-Output 'Connect Simulation with a genuine market-data entitlement. This installer does not sign in, change permissions or send orders.'
Write-Output 'If you have an older TraderProMax exporter, disable that older exporter to avoid duplicate producers.'
