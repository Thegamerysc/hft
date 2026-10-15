# First run on a new PC

## Application

Download the Windows release asset, extract its whole folder and double-click `Launch.cmd`. It runs an idempotent setup, generates a new local random bridge token, writes default shadow profiles, resolves the build path for this PC, starts hidden C# processes and opens the dashboard. It preserves existing settings on subsequent runs.

Do not copy a different PC's `dotnet/runtime` folder into a new installation. Source-code ZIP users must install .NET SDK 10.0.402 first. To use different local ports on the **first** setup:

```powershell
.\Setup.ps1 -DashboardPort 18765 -FeedPort 18876 -WorkerPortBase 18901
```

The bridge configuration is `dotnet/runtime/bridge.json`. Check its dated `contracts` before collection; defaults use the current quarterly month, not an automatically verified active contract. Near rollover, supply the actual available names with `-EsContract 'ES 12-26' -NqContract 'NQ 12-26'` on the first setup, or edit the local file while services are stopped. After moving an already-used folder to another PC, archive its local runtime settings and run setup afresh rather than reusing account bindings and absolute release paths.

## NinjaTrader input

1. Install NinjaTrader, sign in yourself and confirm genuine market-data access for ES/NQ.
2. Run `dotnet/Install-NinjaTraderAdapter.ps1`. If NinjaTrader uses a non-default data folder, pass `-NinjaTraderDocuments 'D:\YourFolder\NinjaTrader 8'`.
3. In NinjaTrader, open **New → NinjaScript Editor** and press **F5**. Resolve any compile error before continuing. Restart NinjaTrader if the newly compiled AddOn is not active.
4. Connect **Simulation**. A green connection alone is not sufficient: the dashboard's actual ES/NQ event counters must grow. The adapter uses a local `TraderProMax/bridge-location.txt` in NinjaTrader's data directory, rather than a developer's hard-coded workspace path.
5. Press the history-refresh button to request the 20 preceding cash-session totals. Zero-volume or incomplete responses do not warm up a clock. This fetch does not reconstruct historical depth.

A pre-existing legacy TraderProMax exporter should be disabled to prevent duplicate feed producers. The included helper installs only this AddOn; it does not rewrite unrelated scripts or place orders.

## Exness MT5 input

Prepare a separate demo account and terminal installation for each profile you want independently tested. Accounts must be USD, hedging mode and supported Exness symbols/contracts. In each terminal, keep the password inside MT5 and use **File → Open Data Folder** to locate its data directory.

1. Stop the chosen dashboard profile and ensure its inventory is reconciled/flat before rebinding.
2. Open its account form. Enter the dedicated `terminal64.exe` path, demo login, Exness server and actual symbol. Select **shadow** initially. Choose US500/ES or US100/NQ deliberately; an ES model is not presumed valid in NQ.
3. Save the binding. The profile settings file appears at `dotnet/runtime/bindings/PROFILE.set`.
4. Copy the EA into that terminal with:

```powershell
.\dotnet\Install-MT5Bridge.ps1 -TerminalDataPath 'C:\Your\MT5\DataFolder'
```

5. Refresh MT5 Navigator, attach `TraderProMaxBridge` to the relevant chart and load the generated `.set` file. Permit the local `127.0.0.1` address in MT5's own network settings yourself. Keep `AllowDemoOrders=false` in shadow mode.
6. Verify broker input, account identity and reconciliation in the dashboard before expecting simulated cycles. A source connection without broker quotes does not produce invented cost-aware fills.

Real accounts are rejected. Demo orders require a later explicit switch to demo mode plus native permission, account/terminal matching and reconciliation. Starting the application does not automatically arm demo execution.

## Recording and stopping

Raw feed segments live in `data/v2/raw/ninjatrader`, profile decisions in `data/v2/decisions`, and recovery journals in `data/v2/execution`. Keep these local and back them up separately. Stop disables new profile entries while preserving existing position protections. Process restart is blocked while inventory remains active.

For a planned application stop or export, close the host's current recording segment first:

```powershell
.\dotnet\Research.ps1 checkpoint
.\Stop.cmd
```

A forcibly terminated active gzip tail may be incomplete and must be censored. Execution state has a separate durable journal; do not delete it to clear a reconciliation problem. Use the exported complete segment path with `Research.ps1 replay --input PATH` or `export --input PATH --output NEW_EMPTY_DIRECTORY`. These commands do not send orders.

## Troubleshooting

- **No .NET host:** use the runtime release asset, or install the pinned SDK for a source build.
- **Port already in use:** stop the other copy or initialize this copy with distinct ports.
- **Dashboard opens but buttons do nothing:** inspect `dotnet/runtime/web.errors.log` and the request to the Blazor framework script. Release publishing must retain `wwwroot` and the static-assets endpoint manifest.
- **NinjaTrader says Logon failed:** read the inner exception in its local trace. A TCP timeout is different from a rejected credential. Compare DNS and repeated HTTPS/TCP tests; do not assume a password reset fixes a network failure.
- **Waiting for terminal:** the dedicated MT5 input is absent or has not passed its checks.
- **History incomplete:** inspect the dates, requested contract and positive session totals; a cached book cannot replace executed-volume history.
- **Joint filter takes no entries:** no qualified live JH1 model ships with this release; missing depth/model inputs are expected gates.
