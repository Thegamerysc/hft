param([Parameter(Mandatory=$true)][string]$TerminalDataPath)
$ErrorActionPreference='Stop'
$taskData=[IO.Path]::GetFullPath($TerminalDataPath)
if(-not(Test-Path -LiteralPath (Join-Path $taskData 'MQL5') -PathType Container)){throw 'Pass the MT5 data folder shown by File > Open Data Folder, not the program installation folder.'}
$taskDestination=Join-Path $taskData 'MQL5/Experts/TraderProMax'
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
foreach($taskName in @('TraderProMaxBridge.mq5','TraderProMaxBridge.ex5','MiniJson.mqh')){
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('adapters/MT5/'+$taskName)) -Destination (Join-Path $taskDestination $taskName) -Force
}
Write-Output 'EA copied. Refresh Navigator in this dedicated MT5 demo terminal, attach the EA, and load the profile .set file.'
Write-Output 'Keep AllowDemoOrders=false for shadow. Account identity and native order permissions are checked separately.'
