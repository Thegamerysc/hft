# Requirements

## Supported release target

The packaged target is **Windows 11 x64**. The application includes a private .NET 10 runtime and ASP.NET Core shared framework, so the release ZIP needs no global .NET installation or SDK. Run it as a normal desktop user from a writable folder. Internet is needed for connected market/broker feeds, not to open the local dashboard.

NinjaTrader's currently documented minimum includes Windows 11/Windows Server 2016+, a 64-bit processor, 2 GB RAM and .NET Framework 4.8. Its recommended configuration includes a four-core CPU, 8 GB RAM and an SSD. For this complete multi-process platform, use **at least 8 GB RAM in practice, preferably 16 GB**, and reserve disk space separately for growing market recordings. These are deployment recommendations, not latency guarantees. [NinjaTrader installation requirements](https://support.ninjatrader.com/s/article/NinjaTrader-Desktop-Installation-Guide?language=en_US)

The release is tested as a Windows desktop application. Linux/macOS, Wine, containers, ARM emulation and unattended Windows Server operation are not validated targets for the native adapters. [Microsoft .NET Windows support](https://learn.microsoft.com/en-us/dotnet/core/install/windows)

## External applications and accounts

- NinjaTrader Desktop 8, tested against the installed 8.1.8.3 interface, with .NET Framework 4.8 and access to the selected dated ES/NQ contracts. Install the included source AddOn through the helper and compile it in NinjaScript Editor.
- A genuine market-data entitlement: A0 requires executed trade volume and quotes; joint experiments require observed, fresh L2 depth as well. A trial may have different data permissions and an expiry. The software does not grant or purchase entitlements.
- Exness MT5 64-bit demo accounts, USD currency and retail hedging mode. Each separately bound experiment uses a distinct account and `terminal64.exe` installation. Actual symbol/contract/tick/lot constraints must match the bridge checks.
- MT5's own permission for the local socket address. User sign-in and permission changes remain manual. No passwords are entered into the dashboard.

The NinjaTrader **Simulation** connection can use genuine live market data. **Simulated Data Feed** generates artificial prices; it is a separate connection and is not accepted as genuine input. [NinjaTrader data-feed explanation](https://support.ninjatrader.com/s/article/Simulated-Data-Feed?language=en_US)

## Local networking

Default loopback-only ports are dashboard 8765, feed 8876 and workers 8901–8905. Do not forward them to the public internet. `Setup.ps1` accepts alternative ports for an independent copy. Native applications still need outbound access to their own vendor servers.

The source computer experienced intermittent TCP timeouts to the Tradovate account API while DNS resolved correctly. Moving the project does not prove that a receiving PC's network or data entitlements work. Verify actual growing event counts after installation.

## Source and research work

Source builds require the **.NET 10.0.402 SDK**, internet access to NuGet for the first restore, and Windows x64 for the packaged runtime. NinjaTrader's own installation supplies its proprietary assemblies/compiler; these are not redistributed. The retail MT5 integration remains a small MQL5 EA.

No Python, Node.js, SQL server, Databento API key, historical archive or paid ML package is required to launch this C# release. Historical replay/training still requires the appropriate separately supplied data; no live-compatible trained JH1 model is included.

The included cash-session calendar covers 2025–2027. Verify/update it before later use, and update the dated ES/NQ contract symbols at rollover.
