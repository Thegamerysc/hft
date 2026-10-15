# Development and packaging

Install .NET SDK 10.0.402 on Windows x64. The repository's SDK pin is in `dotnet/global.json`. Start from the repository root:

```powershell
.\Setup.ps1
.\Launch.cmd
```

`Setup.ps1` builds a source checkout when no prepared release exists. Normal release users do not need a compiler. Build a new staged source version with `dotnet/Start-CSharpLab.ps1 -Build`; compilation finishes before the running services are switched. Bound demo execution with active or unavailable state blocks an upgrade.

To build a portable release:

```powershell
.\Package.ps1 -OutputDirectory 'C:\Builds\TraderProMax-2' -Version '2.0.0-shadow'
```

The script builds the solution, runs its tests, publishes MarketHost, ExecutionWorker, Web and Research for win-x64, and includes the .NET Core/ASP.NET Core shared runtimes once. It creates a file/hash manifest and ZIP SHA256 file. It uses a whitelist of project files and excludes user configuration, logs, execution state, historical data, the SDK and proprietary NinjaTrader/MT5 installations.

The hosted release workflow uses Windows, the pinned SDK and repository-scoped permissions. Ordinary pushes/PRs build and test. A `v*` tag publishes a prerelease ZIP. Inspect generated artifacts before promoting a release. No GitHub secret is required to compile or test.

The native NinjaTrader source must still be compiled in the receiving installation. The MQL5 source and its compiled EA are included; after changing MQL5 source, recompile with MetaEditor and replace the EA before publishing. Do not claim C# fixture tests are native broker transaction tests.

To validate portability, copy/extract the ZIP into a different path containing spaces, initialize distinct loopback ports, start only its own processes and verify `/health`, the Blazor script and five shadow profiles. Its bundled runtime should report no installed SDK. Stop only that copy after the check. Keep the original trading workspace running.

Private local diagnostics and data are intentionally outside version control. Source import/replay extensions can be developed separately without inserting research training or file tailing into the live signal path.
