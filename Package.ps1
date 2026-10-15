param([string]$OutputDirectory='', [string]$Version='2.0.0-shadow', [switch]$SkipTests)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($PSScriptRoot)
. (Join-Path $taskRoot 'dotnet/Resolve-DotNet.ps1')
$taskDotNet=Get-TraderProMaxDotNet $taskRoot
if(-not $OutputDirectory){$OutputDirectory=Join-Path $taskRoot 'artifacts'}
$taskOutput=[IO.Path]::GetFullPath($OutputDirectory)
$taskPackage=Join-Path $taskOutput ('TraderProMax-win-x64-'+$Version)
if(Test-Path -LiteralPath $taskPackage){throw 'Use a new output directory/version; an existing package is never overwritten.'}
New-Item -ItemType Directory -Path $taskPackage -Force | Out-Null
function Copy-PackageFile([string]$Relative){
    $taskFrom=Join-Path $taskRoot $Relative;$taskTo=Join-Path $taskPackage $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskTo) -Force|Out-Null
    Copy-Item -LiteralPath $taskFrom -Destination $taskTo
}
foreach($taskName in @('README.md','REQUIREMENTS.md','THIRD_PARTY_NOTICES.md','.traderpromax','Setup.ps1','Launch.cmd','Stop.cmd','Package.ps1')){Copy-PackageFile $taskName}
foreach($taskDir in @('dotnet/src','dotnet/tests','dotnet/configs','dotnet/adapters','docs/portable')){
    Get-ChildItem -LiteralPath (Join-Path $taskRoot $taskDir) -File -Recurse | Where-Object {
        $taskRelative=$_.FullName.Substring($taskRoot.Length+1)
        $taskRelative -notmatch '(^|[\\/])(bin|obj|runtime|builds)([\\/]|$)' -and $_.Extension -notin @('.log','.pdb')
    } | ForEach-Object {Copy-PackageFile ($_.FullName.Substring($taskRoot.Length+1))}
}
foreach($taskName in @('TraderProMax.sln','Directory.Build.props','global.json','Start-CSharpLab.ps1','Stop-CSharpLab.ps1','Resolve-DotNet.ps1','Research.ps1','Install-NinjaTraderAdapter.ps1','Install-MT5Bridge.ps1','start-lab.cmd')){Copy-PackageFile ('dotnet/'+$taskName)}
$taskBuild=Join-Path $taskOutput 'build'
Push-Location (Join-Path $taskRoot 'dotnet')
try{
    & $taskDotNet build (Join-Path $taskRoot 'dotnet/TraderProMax.sln') -c Release --artifacts-path $taskBuild --nologo
    if($LASTEXITCODE -ne 0){throw 'Build failed.'}
    if(-not $SkipTests){
        & $taskDotNet (Join-Path $taskBuild 'bin/TraderProMax.Tests/release/TraderProMax.Tests.dll') --root $taskRoot
        if($LASTEXITCODE -ne 0){throw 'Tests failed; no release ZIP was produced.'}
    }
    foreach($taskService in @('MarketHost','ExecutionWorker','Web','Research')){
        $taskDestination=Join-Path $taskPackage "dotnet/builds/portable/bin/TraderProMax.$taskService/release"
        & $taskDotNet publish (Join-Path $taskRoot "dotnet/src/TraderProMax.$taskService/TraderProMax.$taskService.csproj") -c Release -r win-x64 --self-contained false --artifacts-path $taskBuild -o $taskDestination -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false --nologo
        if($LASTEXITCODE -ne 0){throw "Publish failed: $taskService"}
    }
}finally{Pop-Location}
$taskAssets=Get-Content -LiteralPath (Join-Path $taskBuild 'obj/TraderProMax.Research/project.assets.json') -Raw | ConvertFrom-Json
$taskPackageCache=@($taskAssets.packageFolders.PSObject.Properties | Select-Object -ExpandProperty Name)[0]
foreach($taskLibrary in @($taskAssets.libraries.PSObject.Properties | Where-Object {$_.Value.type -eq 'package'})){
    $taskLibraryRoot=Join-Path $taskPackageCache $taskLibrary.Value.path
    $taskLicenseTarget=Join-Path $taskPackage ('licenses/'+$taskLibrary.Name)
    New-Item -ItemType Directory -Path $taskLicenseTarget -Force | Out-Null
    Get-ChildItem -LiteralPath $taskLibraryRoot -File -Recurse | Where-Object { $_.Name -match '(?i)license|notice|copying|\.nuspec$' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskLicenseTarget $_.Name) -Force
    }
}
# Bundle the redistributable shared runtime once, rather than an SDK or four duplicate runtimes.
$taskHostDir=Split-Path -Parent $taskDotNet
$taskRuntimeRoot=Join-Path $taskPackage 'tools/dotnet10'
New-Item -ItemType Directory -Path $taskRuntimeRoot -Force | Out-Null
foreach($taskName in @('dotnet.exe','LICENSE.txt','ThirdPartyNotices.txt')){
    Copy-Item -LiteralPath (Join-Path $taskHostDir $taskName) -Destination (Join-Path $taskRuntimeRoot $taskName)
}
$taskRuntimeVersions=@{}
foreach($taskShared in @('Microsoft.NETCore.App','Microsoft.AspNetCore.App')){
    $taskSharedRoot=Join-Path $taskHostDir ('shared/'+$taskShared)
    $taskSelected=Get-ChildItem -LiteralPath $taskSharedRoot -Directory | Where-Object Name -Match '^10\.0\.\d+$' | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
    if(-not $taskSelected){throw "Missing .NET 10 shared runtime $taskShared"}
    $taskTarget=Join-Path $taskRuntimeRoot ('shared/'+$taskShared+'/'+$taskSelected.Name)
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskTarget) -Force | Out-Null
    Copy-Item -LiteralPath $taskSelected.FullName -Destination $taskTarget -Recurse
    $taskRuntimeVersions[$taskShared]=$taskSelected.Name
}
$taskFxr=Get-ChildItem -LiteralPath (Join-Path $taskHostDir 'host/fxr') -Directory | Where-Object Name -Match '^10\.0\.\d+$' | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
New-Item -ItemType Directory -Path (Join-Path $taskRuntimeRoot 'host/fxr') -Force | Out-Null
Copy-Item -LiteralPath $taskFxr.FullName -Destination (Join-Path $taskRuntimeRoot ('host/fxr/'+$taskFxr.Name)) -Recurse
$taskDistribution=@{version=$Version;platform='win-x64';release='dotnet/builds/portable';runtimeBundled=$true;runtimeVersions=$taskRuntimeVersions;defaultMode='shadow';containsCredentials=$false;containsHistoricalData=$false}
[IO.File]::WriteAllText((Join-Path $taskPackage 'dotnet/distribution.json'),($taskDistribution|ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
$taskManifest=@(Get-ChildItem -LiteralPath $taskPackage -File -Recurse | ForEach-Object {
    [pscustomobject]@{path=$_.FullName.Substring($taskPackage.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
[IO.File]::WriteAllText((Join-Path $taskPackage 'MANIFEST.json'),($taskManifest|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
$taskZip=$taskPackage+'.zip'
Compress-Archive -LiteralPath $taskPackage -DestinationPath $taskZip -CompressionLevel Optimal
$taskHash=(Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($taskZip+'.sha256'),($taskHash+'  '+[IO.Path]::GetFileName($taskZip)+[Environment]::NewLine),(New-Object Text.UTF8Encoding($false)))
Write-Output "Portable ZIP: $taskZip"
Write-Output "SHA256: $taskHash"
