# TraderProMax — C# shadow research lab

An event-driven Windows trading research platform with a compact Blazor dashboard, three A0 double-hedge controls, two joint order-flow experiments, NinjaTrader market-data input and separate Exness MT5 demo adapters.

**Starts in shadow mode. No account credentials, market-data archive or trained live model is included.** The joint filtered hedge has no qualified deployed model. This release does not establish a profitable trading edge.

## Download and launch on another PC

1. Open [Releases](https://github.com/Thegamerysc/hft/releases) and download **`TraderProMax-win-x64-2.0.0-shadow.zip`**. Use this asset for a ready-built application; GitHub's **Source code ZIP** requires the SDK and a build.
2. Extract the entire folder to a writable location, such as `C:\TraderProMax`. Keep all its files together. Avoid a synchronized OneDrive folder for continuously written recordings.
3. Double-click **`Launch.cmd`**. The release includes the .NET runtime and does not require an SDK for normal use.
4. Open **http://127.0.0.1:8765/** if the browser does not open automatically. Use **`Stop.cmd`** to stop this project's services.

The dashboard can open without market/broker connections. **Waiting for terminal** and empty results are expected until the inputs below are connected. A started shadow worker does not invent prices or fills.

## Requirements

| Item | Needed for |
|---|---|
| **Windows 11 x64**; PowerShell 5.1+ | Supported desktop deployment |
| 8 GB RAM, four CPU cores, SSD recommended; 16 GB preferred for multiple terminals | Practical research configuration |
| Approximately 1 GB application space; reserve additional disk space for recordings | Runtime and data collection |
| NinjaTrader Desktop 8 with .NET Framework 4.8 and a working ES/NQ market-data connection | Genuine futures trades/quotes; fresh depth for joint experiments |
| Exness MT5 **demo**, USD, hedging account, and a dedicated terminal per bound profile | Broker quotes for cost-aware shadow fills; later explicit demo execution |
| .NET SDK **10.0.402** | Source builds only; not needed by the Windows release asset |

Data access and exchange entitlements are separate from this software. A0 needs executed futures volume; the joint experiments additionally need fresh L2 and their specified calibration/model. ES results do not transfer automatically to NQ. See [requirements and official sources](REQUIREMENTS.md).

## Connect the inputs

Start with [the setup guide](docs/portable/SETUP.md). In brief:

```powershell
# Run from the extracted project directory after Setup.ps1/Launch.cmd.
.\dotnet\Install-NinjaTraderAdapter.ps1
```

Compile the installed adapter with F5 in NinjaTrader's NinjaScript Editor. Connect **Simulation** with genuine market data. The separately named **Simulated Data Feed** generates artificial prices and is unsuitable for these experiments.

In the dashboard, choose a profile, stop it and bind its own demo terminal/account in **shadow** mode. Install the EA into that terminal's data folder and load the generated profile `.set` file. Passwords stay inside MT5. Instructions, native permissions and first-run checks are in the setup guide.

## Experiments

| Profile | Trigger | Concurrent cycles |
|---|---|---:|
| Volume · 3 hedges | Volume5 | 3 |
| Dollar · 1 hedge | Dollar30 | 1 |
| Volume · 1 hedge | Volume5 | 1 |
| Joint · filtered hedge | Volume5 plus frozen joint model | 1 |
| Joint · breakout | Event-driven joint setup | 1 |

The registered A0 settings are **20-point SL, 1.16R TP and 0.20 lot per leg**, before native tick rounding. Both opposing positions are opened; after the first SL, the survivor remains until its TP or SL. Volume5/Dollar30 are activity-normalized bar clocks, not fixed 5/30-second intervals. Their thresholds require 20 preceding complete cash-session totals. See [architecture and limitations](docs/portable/ARCHITECTURE.md).

## Build from source

Install the pinned SDK, then run:

```powershell
.\Setup.ps1
.\Launch.cmd
```

Build a distributable ZIP with `./Package.ps1`. It runs the C# tests, publishes the four services, bundles the shared Windows runtime once and writes SHA256 hashes. The GitHub workflow also builds/tests the source. See [development and packaging](docs/portable/DEVELOPMENT.md).

The repository intentionally excludes local accounts, tokens, `.set` files, logs, execution journals, historical L2/L3 files and the older Python experiments. Research import commands need separately supplied data. Keep recordings local; never commit `dotnet/runtime`, `data`, or broker credentials.
