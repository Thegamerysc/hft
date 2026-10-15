param([Parameter(Mandatory=$true,Position=0)][string]$Command,[Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'Resolve-DotNet.ps1')
$taskHost=Get-TraderProMaxDotNet $taskRoot
$taskRelease=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime/active-release.json') -Raw | ConvertFrom-Json).directory
$taskResearch=Join-Path $taskRelease 'bin/TraderProMax.Research/release/TraderProMax.Research.dll'
& $taskHost $taskResearch $Command @Arguments --root $taskRoot
if($LASTEXITCODE -ne 0){throw 'Research command failed.'}
