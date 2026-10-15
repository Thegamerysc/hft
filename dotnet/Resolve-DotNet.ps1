function Get-TraderProMaxDotNet([string]$Root) {
    $privateHost = Join-Path $Root 'tools/dotnet10/dotnet.exe'
    if (Test-Path -LiteralPath $privateHost) { return [IO.Path]::GetFullPath($privateHost) }
    $installedHost = Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($installedHost) { return $installedHost.Source }
    throw 'No .NET host found. Use the Windows release ZIP with its bundled runtime, or install the .NET 10 SDK for a source build.'
}
