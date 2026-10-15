param(
    [ValidateRange(1024,65535)][int]$DashboardPort=8765,
    [ValidateRange(1024,65535)][int]$FeedPort=8876,
    [ValidateRange(1024,65531)][int]$WorkerPortBase=8901,
    [string]$EsContract='', [string]$NqContract=''
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($PSScriptRoot)
if(-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess){throw 'Use 64-bit Windows and 64-bit PowerShell.'}
$taskPorts=@($DashboardPort,$FeedPort)+@($WorkerPortBase..($WorkerPortBase+4))
if(@($taskPorts | Select-Object -Unique).Count -ne 7){throw 'Dashboard, feed and five worker ports must be distinct.'}
$taskRuntime=Join-Path $taskRoot 'dotnet/runtime'
New-Item -ItemType Directory -Path $taskRuntime -Force | Out-Null
$taskQuarter=[int]([Math]::Ceiling([DateTime]::UtcNow.Month/3.0)*3)
$taskYear=[DateTime]::UtcNow.ToString('yy')
if(-not $EsContract){$EsContract='ES {0:D2}-{1}' -f $taskQuarter,$taskYear}
if(-not $NqContract){$NqContract='NQ {0:D2}-{1}' -f $taskQuarter,$taskYear}
$taskBridgePath=Join-Path $taskRuntime 'bridge.json'
if(-not(Test-Path -LiteralPath $taskBridgePath)){
    $taskRandom=New-Object byte[] 32;$taskRng=[Security.Cryptography.RandomNumberGenerator]::Create()
    try{$taskRng.GetBytes($taskRandom)}finally{$taskRng.Dispose()}
    $taskToken=([BitConverter]::ToString($taskRandom)).Replace('-','')
    $taskBridge=@{token=$taskToken;port=$FeedPort;dashboardPort=$DashboardPort;contracts=@($EsContract,$NqContract)}
    [IO.File]::WriteAllText($taskBridgePath,($taskBridge|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
}
$taskProfilesPath=Join-Path $taskRuntime 'profiles.json'
if(-not(Test-Path -LiteralPath $taskProfilesPath)){
    $taskProfiles=Get-Content -LiteralPath (Join-Path $taskRoot 'dotnet/configs/profiles.json') -Raw | ConvertFrom-Json
    for($taskIndex=0;$taskIndex -lt $taskProfiles.Count;$taskIndex++){
        $taskProfiles[$taskIndex].workerPort=$WorkerPortBase+$taskIndex
        $taskProfiles[$taskIndex].execution='shadow';$taskProfiles[$taskIndex].login=0
        $taskProfiles[$taskIndex].server='';$taskProfiles[$taskIndex].terminalPath=''
    }
    [IO.File]::WriteAllText($taskProfilesPath,($taskProfiles|ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
}
. (Join-Path $taskRoot 'dotnet/Resolve-DotNet.ps1')
$taskDotNet=Get-TraderProMaxDotNet $taskRoot
$taskActive=Join-Path $taskRuntime 'active-release.json'
if(-not(Test-Path -LiteralPath $taskActive)){
    $taskDistribution=Join-Path $taskRoot 'dotnet/distribution.json'
    if(Test-Path -LiteralPath $taskDistribution){
        $taskRelease=Join-Path $taskRoot ((Get-Content -LiteralPath $taskDistribution -Raw | ConvertFrom-Json).release)
    }else{
        $taskRelease=Join-Path $taskRoot ('dotnet/builds/'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
        Push-Location (Join-Path $taskRoot 'dotnet')
        try{& $taskDotNet build (Join-Path $taskRoot 'dotnet/TraderProMax.sln') -c Release --artifacts-path $taskRelease --nologo
            if($LASTEXITCODE -ne 0){throw 'Source build failed. Install .NET SDK 10.0.402, or download the Windows runtime ZIP from Releases.'}
        }finally{Pop-Location}
    }
    $taskRelease=[IO.Path]::GetFullPath($taskRelease)
    $taskAllowed=[IO.Path]::GetFullPath((Join-Path $taskRoot 'dotnet/builds'))+[IO.Path]::DirectorySeparatorChar
    if(-not $taskRelease.StartsWith($taskAllowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Release path must stay inside this project.'}
    foreach($taskService in @('MarketHost','ExecutionWorker','Web','Research')){
        if(-not(Test-Path -LiteralPath (Join-Path $taskRelease "bin/TraderProMax.$taskService/release/TraderProMax.$taskService.dll"))){throw "Missing release service: $taskService"}
    }
    [IO.File]::WriteAllText($taskActive,(@{directory=$taskRelease;activatedUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
}
$taskBridge=Get-Content -LiteralPath $taskBridgePath -Raw|ConvertFrom-Json
Write-Output "Setup complete. Dashboard: http://127.0.0.1:$($taskBridge.dashboardPort)/"
Write-Output "Futures: $($taskBridge.contracts -join ', '). Verify the active dated contracts before collection."
Write-Output 'Profiles default to SHADOW. Existing bindings and settings were preserved. Run Launch.cmd next.'
