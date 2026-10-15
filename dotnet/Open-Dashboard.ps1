$ErrorActionPreference='Stop'
$taskBridge=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime/bridge.json') -Raw|ConvertFrom-Json
$taskPort=8765;if($taskBridge.dashboardPort){$taskPort=$taskBridge.dashboardPort}
Start-Process ('http://127.0.0.1:'+$taskPort+'/')
