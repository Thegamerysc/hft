# Third-party components

The Windows release redistributes Microsoft's .NET 10 host, Microsoft.NETCore.App and Microsoft.AspNetCore.App. Their original `LICENSE.txt` and `ThirdPartyNotices.txt` are retained under `tools/dotnet10`. The full SDK and Windows Desktop shared framework are not bundled.

The C# research/export executable uses these NuGet packages and their original upstream licenses:

| Package | Version | Upstream |
|---|---|---|
| Parquet.Net | 6.1.0 | https://github.com/aloneguid/parquet-dotnet |
| CommunityToolkit.HighPerformance | 8.4.2 | https://github.com/CommunityToolkit/dotnet |
| K4os.Compression.LZ4 | 1.3.8 | https://github.com/MiloszKrajewski/K4os.Compression.LZ4 |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream |
| Snappier | 1.3.1 | https://github.com/brantburnett/Snappier |
| ZstdSharp.Port | 0.8.8 | https://github.com/oleg-st/ZstdSharp |

NuGet metadata is authoritative for each package's licensing; packaging includes the available original package license/notice files. NinjaTrader, MetaTrader and Exness are external products, are not bundled and are not represented as sponsors. Their names identify integration requirements. No exchange market data is redistributed.
