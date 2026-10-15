using System.Security.Cryptography;
using TraderProMax.Contracts;
using TraderProMax.Transport;

namespace TraderProMax.Recorder;
public static class RunManifest
{
    public static string Save(string root,string directory,string process,object? config=null)
    {
        string run=Guid.NewGuid().ToString("N");Directory.CreateDirectory(Path.Combine(directory,"manifests"));
        string[] sources=["dotnet/src/TraderProMax.Core/Market.cs","dotnet/src/TraderProMax.Core/Features.cs","dotnet/src/TraderProMax.Core/BrokerAlignment.cs","dotnet/src/TraderProMax.Execution/CycleEngine.cs","dotnet/adapters/NinjaTrader/TraderProMaxNativeFeed.cs","dotnet/adapters/MT5/TraderProMaxBridge.mq5"];
        var hashes=sources.ToDictionary(p=>p,p=>File.Exists(Path.Combine(root,p))?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,p)))):"missing");
        Workspace.Atomic(Path.Combine(directory,"manifests",run+".json"),Json.Bytes(new {runId=run,process,startedUtc=DateTime.UtcNow,protocol="TMP2 framing-v1 market-body-120",featureSchema="native-joint-v1.1",config,sourceHashes=hashes,
            precision="UTC integer ns representation; underlying NinjaTrader/MT5 timestamp accuracy is not nanoseconds",quality="source flags preserved; rejected events sampled at most once/second/root, valid events recorded in full; gaps quarantine state",
            storage="gzip frame segments, rotated on new events after five minutes or 8 MiB compressed; explicit checkpoint closes the active segment; unique process segments; a crashed active tail is censored, closed segments retain gzip CRC",
            quoteFields="quote updates include side/price/displayed size from the current adapter; earlier adapter recordings stored zero in those quote fields; executed volume uses Trade rows only",
            execution="demo-or-shadow-only",counterfactual="shadow fills are simulated; no unobserved action propensity inferred",invariants="fixed 20 point R, 1.16 TP, .20 lot; A0 no early exit; survivors occupy inventory"}));
        return run;
    }
}
