using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO.Compression;
using TraderProMax.Contracts;
using TraderProMax.Core;
using TraderProMax.Execution;
using TraderProMax.Research;
using TraderProMax.Transport;

CultureInfo.DefaultThreadCurrentCulture=CultureInfo.InvariantCulture;
string root=Workspace.Root(args);string command=args.Length>0?args[0]:"help";
if(command=="export")
{await ArchiveExport.Export(Workspace.Get(args,"--input",""),Workspace.Get(args,"--output",Path.Combine(root,"data/v2/exports",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))));return;}
if(command=="audit")
{
    var volume=await Historical.Load(root,"volume5",["2026-09"]);var dollar=await Historical.Load(root,"dollar30",["2026-09"]);
    var controls=new[]{(Name:"Volume5",Rows:volume,Cap:1),(Name:"Volume5",Rows:volume,Cap:3),(Name:"Dollar30",Rows:dollar,Cap:1)};
    var output=new List<object>();var md=new StringBuilder("# Barrier success versus net win audit\n\nOwned September saved labels, simulated execution. No new model selection or fitting.\n\n| Control | Cycles | At least one TP | Net positive | Both SL | TP + net loss | Mean win USD | Mean loss USD | Net USD |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|\n");
    foreach(var c in controls)
    {
        var p=Historical.Portfolio(c.Rows,_=>true,c.Cap,0);output.Add(new {control=c.Name,cap=c.Cap,portfolio=p});
        md.AppendLine($"| {c.Name} cap {c.Cap} | {p.Completed} | {p.BarrierSuccessRate:P2} | {p.WinRate:P2} | {p.BothSl} | {p.OneTpNetLoss} | {p.MeanWin:F3} | {p.MeanLoss:F3} | {p.Net:F2} |");
    }
    md.AppendLine("\nA TP/SL cycle can still lose money if an adverse stop gap exceeds the planned small surplus. Consequently, a net-loss cycle need not be a −2R double stop. The simple break-even success rate assumes fixed binary payoffs; applying it to the net-positive rate of these variable-payoff episodes is invalid. None of these three selected controls contains a two-TP cycle. The historical entry separation remains +200 ms and +400 ms; this is a remaining execution-parity limitation, not evidence that delayed entries created their profit. Financing and exact broker fills remain unverified.");
    string dir=Path.Combine(root,"research/csharp_joint");Workspace.Atomic(Path.Combine(dir,"payoff-audit.json"),Json.Bytes(output));await File.WriteAllTextAsync(Path.Combine(dir,"PAYOFF_AUDIT.md"),md.ToString());Console.WriteLine(md);return;
}
if(command is "state" or "control" or "history" or "checkpoint")
{
    var b=Workspace.Bridge(root);using var client=Wire.Client();await client.ConnectAsync(IPAddress.Loopback,b.Port);var s=client.GetStream();
    bool state=command=="state";await Wire.WriteAsync(s,MessageType.Hello,Json.Bytes(new FeedHello(b.Token,state?"observer":"control","research-cli")));await Wire.ReadAsync(s);
    if(!state){string action=command is "history" or "checkpoint"?command:Workspace.Get(args,"--action","stop");string profile=Workspace.Get(args,"--profile","a0-volume-1");await Wire.WriteAsync(s,MessageType.Control,Json.Bytes(new ControlRequest(Guid.NewGuid(),action,profile)));}
    var f=await Wire.ReadAsync(s);Console.WriteLine(f is {} value?Encoding.UTF8.GetString(value.Payload):"Disconnected");return;
}
if(command=="replay")
{
    string path=Workspace.Get(args,"--input","");if(path.Length==0)throw new ArgumentException("--input events.bin required");
    long count=0,trades=0,volume=0,gaps=0;var books=new Dictionary<Asset,DepthBook>{{Asset.ES,new()},{Asset.NQ,new()}};
    await using var file=File.OpenRead(path);using var zipped=path.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)?new GZipStream(file,CompressionMode.Decompress,true):null;
    Stream s=zipped is null?file:zipped;while(await Wire.ReadAsync(s) is {} f)
    {
        if(f.Type!=MessageType.Market)continue;var e=Wire.DecodeMarket(f.Payload);count++;if(e.Kind==EventKind.Trade){trades++;volume+=e.Quantity;}
        if(e.Quality.HasFlag(Quality.SequenceGap))gaps++;books[e.Asset].Apply(e);
    }
    Console.WriteLine(Encoding.UTF8.GetString(Json.Bytes(new {count,trades,volume,gaps,depth=books.ToDictionary(x=>x.Key.ToString(),x=>x.Value.Depth),mode="offline_no_orders"})));return;
}
if(command=="historical")
{
    string output=Path.Combine(root,"research/csharp_joint");Directory.CreateDirectory(output);
    var rows=await Historical.Load(root,"volume5");
    Console.WriteLine($"Loaded {rows.Count:N0} owned R20 volume5 observations, C# Parquet reader");
    var train=rows.Where(x=>x.Day.CompareTo("2026-07-01")<0&&x.ExitNs<Historical.Ns("2026-07-01")).ToArray();
    var validation=rows.Where(x=>x.Day.CompareTo("2026-07-01")>=0&&x.Day.CompareTo("2026-08-01")<0&&x.ExitNs<Historical.Ns("2026-08-01")).ToArray();
    var test=rows.Where(x=>x.Day.CompareTo("2026-09-01")>=0&&x.Day.CompareTo("2026-10-01")<0).ToArray();
    var dollar=await Historical.Load(root,"dollar30",["2026-09"]);
    var results=new List<object>();
    var controls=new[]{(Name:"Volume5",Data:test,Cap:1),(Name:"Volume5",Data:test,Cap:3),(Name:"Dollar30",Data:dollar.ToArray(),Cap:1)};
    foreach(var control in controls)foreach(var raw in new[]{false,true})
    {
        var p=Historical.Portfolio(control.Data,_=>true,control.Cap,raw?.1:0);
        results.Add(new {family="A0",variant=control.Name,cap=control.Cap,commissionScenario=raw?"Raw_commission_only_on_Standard_quotes":"Standard",p.Completed,p.Net,p.WinRate,p.Drawdown,p.Opened,p.Unresolved,p.BothTp,p.IdealBinaryNet,p.Daily});
        string csv="day,starting_balance,daily_pnl,ending_balance,completed_cycles\n"+string.Join("\n",p.Daily.Select(d=>$"{d.Day},{d.Opening:F2},{d.Pnl:F2},{d.Closing:F2},{d.Completed}"))+"\n";
        await File.WriteAllTextAsync(Path.Combine(output,$"daily-{control.Name}-cap{control.Cap}-{(raw?"raw-stress":"standard")}.csv"),csv);
    }
    var candidates=new List<(string Name,HistoricalModel Model,double Cutoff,PortfolioResult Validation)>();
    var diagnostics=new List<object>();
    foreach(var family in new[]{"price","price_tape","price_tape_depth"})
    {
        var featureNames=Historical.Features(family);var model=HistoricalModel.Fit(train,featureNames);
        diagnostics.Add(new {family,train=model.Diagnostic(train),validation=model.Diagnostic(validation)});
        foreach(double cutoff in new[]{.90,.93,.96})
        {
            var pv=Historical.Portfolio(validation,r=>model.Predict(r)>=cutoff,1,0);
            candidates.Add((family,model,cutoff,pv));
            Console.WriteLine($"Validation {family} cutoff {cutoff}: {pv.Completed} cycles, ${pv.Net:F2}");
        }
    }
    var chosen=candidates.Where(x=>x.Validation.Completed>=30&&x.Validation.Net>0).OrderByDescending(x=>x.Validation.Net).FirstOrDefault();
    if(chosen.Model!=null)
    {
        var selected=Historical.Portfolio(test,r=>chosen.Model.Predict(r)>=chosen.Cutoff,1,0);
        results.Add(new {family="JH1_historical_proxy",variant=chosen.Name,cutoff=chosen.Cutoff,selected.Completed,selected.Net,selected.WinRate,selected.Drawdown,selected.Daily});
        Workspace.Atomic(Path.Combine(output,"model.historical-only.json"),Json.Bytes(new {schema="historical-joint-proxy-v1_NOT_LIVE_COMPATIBLE",trainedThrough="2026-06-30",selectedOn="2026-07",cutoff=chosen.Cutoff,model=chosen.Model,liveEligible=false}));
    }
    else results.Add(new {family="JH1_historical_proxy",selection="SKIP",reason="No candidate met the frozen validation coverage/net conditions"});
    var report=new {scope="C# migration and retrospective joint-feature diagnostic; all historical dates previously inspected",validatedAlpha=false,liveModelReady=false,
        nativeFeatureParity="historical schema differs; this model cannot be loaded by native JointModel",trainRows=train.Length,validationRows=validation.Length,testRows=test.Length,
        diagnostics,attempts=candidates.Select(x=>new{x.Name,x.Cutoff,x.Validation.Completed,x.Validation.Net}),results,
        missing=new[]{"NQ historical source","independent final test","exact Raw bid/ask repricing","JD1 raw event-level setup/5-minute exit replay","live-native feature parity"}};
    Workspace.Atomic(Path.Combine(output,"results.json"),Json.Bytes(report));
    var md=new StringBuilder("# C# historical joint-feature diagnostic\n\nRetrospective development results, not validated alpha. Existing owned ES/US500 data only; $0 additional data spend.\n\n");
    md.AppendLine($"Training rows {train.Length:N0}; purged July validation rows {validation.Length:N0}; September rows {test.Length:N0}. Each candidate uses 20-point SL and 1.16R target through the original saved broker outcome labels.\n");
    md.AppendLine("| Variant | Cap | Completed | Net USD | Net-positive cycles | Realized cash drawdown |\n|---|---:|---:|---:|---:|---:|");
    foreach(var control in controls)foreach(var raw in new[]{false,true}){var p=Historical.Portfolio(control.Data,_=>true,control.Cap,raw?.1:0);md.AppendLine($"| {control.Name} control {(raw?"Raw commission scenario":"Standard")} | {control.Cap} | {p.Completed} | {p.Net:F2} | {p.WinRate:P2} | {p.Drawdown:F2} |");}
    if(chosen.Model!=null){var p=Historical.Portfolio(test,r=>chosen.Model.Predict(r)>=chosen.Cutoff,1,0);md.AppendLine($"| Joint proxy {chosen.Name} p≥{chosen.Cutoff} | 1 | {p.Completed} | {p.Net:F2} | {p.WinRate:P2} | {p.Drawdown:F2} |");}else md.AppendLine("\nJoint model selection: **SKIP**. No qualifying validation candidate.");
    md.AppendLine("\nPrice/tape/depth ablations are historical proxies. Native features and historical features are not yet identical; the output model is explicitly incompatible with live deployment. Historical source timestamps stored as doubles retain their original precision limitation.\n\nRaw commission scenario subtracts $0.10 per US500 hedge on Standard quotes; it does not simulate a Raw account's different price feed. Labels already contain bid/ask/gaps; spread is not subtracted twice. Financing, floating-equity drawdown, broker margin, uncertain fills and independent forward validation require separate tests.\n\nNQ and JD1 event-level comparisons remain unavailable rather than populated with synthetic or guessed depth data.");
    md.AppendLine("\nDaily files start at $2,000 and settle each leg's cash at its actual saved exit timestamp. They retain unresolved month-end inventory. Only completed valid two-leg R20 labels are used; excluded partial/censored labels make this a parity/development diagnostic, not a fresh deployable backtest. Native sessions use the XNYS holiday/early-close calendar and exclude dates the legacy research feed may include.\n\nThere were nine frozen feature-family/cutoff combinations. Detailed validation coverage, probability distributions, Brier scores and log losses are in results.json. No cutoff was lowered after looking at September. The Brownian gross reference for RR 1.16 is 2/(2+0.16)=92.59%; cost-bearing saved broker episodes can have a different win rate/payoff distribution. A small positive retrospective total does not establish predictive alpha.");
    md.AppendLine("\n## Execution and payoff audit\n\nSaved research entries place the first leg at +200 ms and the inverse leg a further 200 ms later. Native shadow entries share one contemporaneous quote; demo leg separation will be measured rather than deliberately delayed. Matching the saved P/L ledger does not prove native execution parity. None of the three controls contains a two-TP cycle.\n\n| Standard control | At least one TP | Net positive | Both SL | TP with net loss |\n|---|---:|---:|---:|---:|");
    foreach(var c in controls){var p=Historical.Portfolio(c.Data,_=>true,c.Cap,0);md.AppendLine($"| {c.Name} cap {c.Cap} | {p.BarrierSuccessRate:P2} | {p.WinRate:P2} | {p.BothSl} | {p.OneTpNetLoss} |");}
    md.AppendLine("\nA TP/SL cycle can still lose money when an adverse stop gap exceeds its small planned surplus. A net-loss cycle is therefore not always a −2R double stop. Applying a fixed binary-payoff break-even formula to these net-positive counts is invalid. See PAYOFF_AUDIT.md for mean winning/losing payoffs. Broker latency, spread and adverse stop gaps require forward comparison. Commission stress is charged per side at entry and exit; daily cash drawdown includes those times.");
    await File.WriteAllTextAsync(Path.Combine(output,"REPORT.md"),md.ToString());Console.WriteLine(Path.Combine(output,"REPORT.md"));return;
}
Console.WriteLine("TraderProMax C# research: state | control --action start/stop --profile ID | history --profile ID | checkpoint | replay --input events.bin[.gz] | export --input events.bin[.gz] --output NEW_DIRECTORY | historical | audit");
