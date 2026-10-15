using System.Buffers.Binary;
using System.Diagnostics;
using TraderProMax.Contracts;
using TraderProMax.Core;
using TraderProMax.Execution;
using TraderProMax.Recorder;
using TraderProMax.Transport;

int passed=0;var failures=new List<string>();string temp=Path.Combine(Path.GetTempPath(),"TraderProMax-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
long now=Clock.UtcNs;Guid epoch=Guid.NewGuid();
MarketEvent Ev(long seq=1,long volume=5,long? ns=null,EventKind kind=EventKind.Trade)=>new(epoch,seq,ns??now,Clock.Mono,Stopwatch.Frequency,Asset.ES,kind,Quality.Genuine,6000L*MarketEvent.Scale,volume,6000L*MarketEvent.Scale,600025000000,0,0,0,ns??now,Clock.Mono);
TradingCalendar Cal()=>new([new("2026-10-14",now-23_400_000_000_000,now+23_400_000_000_000)]);
void Check(bool condition,string message="assertion"){if(!condition)throw new Exception(message);}
async Task Test(string name,Func<Task> test){try{await test();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failures.Add(name+": "+ex.Message);Console.WriteLine("FAIL "+name+": "+ex.Message);}}
Task Sync(Action a){a();return Task.CompletedTask;}
ProfileConfig Config()=>new(){Id="a0-volume-1",Name="Test",Magic=26101403,Login=42,Server="Exness-DEMO",WorkerPort=8903};
SignalIntent Intent(ProfileConfig c)=>new(Guid.NewGuid(),c.Id,"test",c.Asset,1,Clock.UtcNs,Clock.UtcNs+60_000_000_000,20,1.16,.2,Side.None,true,1,"fixture");
BrokerHello Hello(ProfileConfig c,bool demo=true)=>new("test",42,"Exness-DEMO","USD",demo,true,"US500m",1,.01,.14,.01,c.Magic);
BrokerReport Report(string kind,Guid cmd,Guid cycle,ulong pos=1,ulong deal=1,double volume=.2,double price=6000,double sl=0,double tp=0,double pnl=0)=>new(kind,cmd,cycle,1,1,deal,pos,"LONG",volume,price,sl,tp,pnl,0,0,0,Clock.UtcNs,10009,"");

await Test("binary market roundtrip preserves scale/epoch/timestamps",()=>Sync(()=>{var e=Ev();var d=Wire.DecodeMarket(Wire.EncodeMarket(e));Check(d.Price==e.Price&&d.Epoch==e.Epoch&&d.ReceivedNs==e.ReceivedNs);}));
await Test("partial frames are reassembled exactly",async()=>{var ms=new MemoryStream();await Wire.WriteAsync(ms,MessageType.Market,Wire.EncodeMarket(Ev()));ms.Position=0;var f=await Wire.ReadAsync(new FragmentStream(ms));Check(f?.Payload.Length==Wire.MarketSize);});
await Test("oversized/invalid frame rejected before allocation",async()=>{byte[] h=new byte[12];BinaryPrimitives.WriteInt32LittleEndian(h,Wire.MaxPayload+1);try{await Wire.ReadAsync(new MemoryStream(h));throw new Exception("accepted invalid frame");}catch(InvalidDataException){}});
await Test("whole-print overshoot is not split/reused",()=>Sync(()=>{var c=new PrintClock(Asset.ES,"volume5",new(10,100,"test",[]),Cal());Check(c.Add(Ev(volume:6))==null);var b=c.Add(Ev(2,7));Check(b?.Volume==13);Check(c.Add(Ev(3,3))==null);}));
await Test("sequence gap quarantines incomplete bar",()=>Sync(()=>{var c=new PrintClock(Asset.ES,"volume5",new(10,100,"test",[]),Cal());c.Add(Ev(volume:9));Check(c.Add(Ev(3,5) with {Quality=Quality.Genuine|Quality.SequenceGap})==null);Check(c.Add(Ev(4,6))==null);}));
await Test("dollar bars use actual notional/multiplier",()=>Sync(()=>{var c=new PrintClock(Asset.ES,"dollar30",new(10,1_000_000,"test",[]),Cal());var b=c.Add(Ev(volume:4));Check(b?.Notional==1_200_000&&b?.Volume==4);}));
await Test("past-only thresholds exclude current/future data",()=>Sync(()=>{var rows=Enumerable.Range(1,20).Select(i=>new SessionTotal("ES",$"2026-09-{i:00}",1000,1_000_000,true)).Append(new("ES","2026-10-20",9_000_000,999_000_000,true));var lim=Thresholds.Calculate(rows,Asset.ES,"2026-10-14","2026-09-20");Check(Math.Abs(lim.Volume5-1000*5.0/23400)<1e-10);}));
await Test("missing latest prior session cannot warm clock",()=>Sync(()=>{try{Thresholds.Calculate([],Asset.ES,"2026-10-14","2026-10-13");throw new Exception("missing seed accepted");}catch(InvalidDataException){}}));
await Test("duplicate feed events cannot double-count",()=>Sync(()=>{var q=new FeedQuality();Check(q.Accept(Ev(),now,out _));Check(!q.Accept(Ev(),now,out _));Check(q.Accept(Ev(3),now,out var n)&&n.Quality.HasFlag(Quality.SequenceGap));}));
await Test("artificial/stale/future feed rejected",()=>Sync(()=>{var q=new FeedQuality();Check(!q.Accept(Ev() with {Quality=Quality.None},now,out _));Check(!q.Accept(Ev(ns:now-3_000_000_000),now,out _));Check(!q.Accept(Ev(ns:now+500_000_000),now,out _));}));
await Test("book insert/update/remove and unknown position reset",()=>Sync(()=>{var b=new DepthBook();var e=Ev(kind:EventKind.Depth) with {Quality=Quality.Genuine|Quality.DepthEntitled,Side=1,Price=6000L*MarketEvent.Scale,Quantity=10,Operation=1};b.Apply(e);b.Apply(e with {Side=-1,Price=600025000000});Check(b.Ready);b.Apply(e with {Operation=2,Quantity=20});Check(b.Imbalance(1)>0);b.Apply(e with {Position=10,Operation=2});Check(!b.Ready);}));
await Test("real-account and netting bindings rejected",async()=>{await using var j=new DurableJournal(Path.Combine(temp,"identity.bin"));var c=Config();var engine=new CycleEngine(c,j,_=>Task.CompletedTask);try{engine.Bind(Hello(c,false));throw new Exception("real accepted");}catch(InvalidDataException){}try{engine.Bind(Hello(c) with {Hedging=false});throw new Exception("netting accepted");}catch(InvalidDataException){}});
await Test("hedge establishes with protected fill-anchored barriers",async()=>
{
    await using var j=new DurableJournal(Path.Combine(temp,"hedge.bin"));var commands=new List<BrokerCommand>();var c=Config();var engine=new CycleEngine(c,j,x=>{commands.Add(x);return Task.CompletedTask;});engine.Bind(Hello(c));engine.Armed=true;engine.Account=new(42,c.Server,2000,2000,1900,0,true,0,Clock.UtcNs);engine.Quote=new(6000,6000.25,Clock.UtcNs,Clock.UtcNs);
    Check(await engine.AcceptAsync(Intent(c),Clock.UtcNs));var cycle=engine.Cycles.Single();var first=commands[0];
    var fill=Report("fill",first.Id,cycle.Id,price:6000.25);await engine.ApplyAsync(fill);await engine.ApplyAsync(fill);Check(commands.Count==2,"duplicate fill re-protected");
    await engine.ApplyAsync(Report("protected",commands[1].Id,cycle.Id,sl:5980.25,tp:6023.45));Check(commands.Count==3&&commands[2].Side=="SHORT");
    await engine.ApplyAsync(Report("fill",commands[2].Id,cycle.Id,2,2,price:6000));await engine.ApplyAsync(Report("protected",commands[3].Id,cycle.Id,2,3,sl:6020,tp:5976.8));Check(cycle.Phase=="Active");
    int before=commands.Count;await engine.ApplyAsync(Report("close",commands[2].Id,cycle.Id,2,4,pnl:-4));Check(cycle.Phase=="Survivor"&&commands.Count==before,"first SL closed survivor");
    Check(!await engine.AcceptAsync(Intent(c),Clock.UtcNs),"survivor released capacity");await engine.ApplyAsync(Report("close",first.Id,cycle.Id,1,5,pnl:4.64));Check(cycle.Phase=="Closed"&&Math.Abs(cycle.Net-.64)<1e-8);
});
await Test("partial fill blocks additional hedge requests",async()=>{await using var j=new DurableJournal(Path.Combine(temp,"partial.bin"));var c=Config();var sent=new List<BrokerCommand>();var e=new CycleEngine(c,j,x=>{sent.Add(x);return Task.CompletedTask;});e.Bind(Hello(c));e.Armed=true;e.Account=new(42,c.Server,2000,2000,1900,0,true,0,Clock.UtcNs);e.Quote=new(6000,6000.25,Clock.UtcNs,Clock.UtcNs);await e.AcceptAsync(Intent(c),Clock.UtcNs);var cycle=e.Cycles.Single();await e.ApplyAsync(Report("fill",sent[0].Id,cycle.Id,volume:.1));Check(!e.Reconciled&&sent.Count==1&&cycle.Phase=="ReconcileRequired");});
await Test("journal restart retains intent and requires reconciliation",async()=>{var path=Path.Combine(temp,"restart.bin");Guid id;await using(var j=new DurableJournal(path)){var c=new Cycle {Id=Guid.NewGuid(),SignalId=Guid.NewGuid(),Profile="a0-volume-1",Phase="Opening"};id=c.Id;await j.AppendAsync(c);}await using(var j=new DurableJournal(path)){var e=new CycleEngine(Config(),j,_=>throw new Exception("unexpected retry"));Check(!e.Reconciled&&e.Cycles.Single().Id==id);}});
await Test("truncated journal tail preserves completed records",async()=>{var path=Path.Combine(temp,"tail.bin");await using(var j=new DurableJournal(path)){await j.AppendAsync(new Cycle {Id=Guid.NewGuid(),SignalId=Guid.NewGuid(),Phase="Closed"});}using(var f=new FileStream(path,FileMode.Append)){f.Write([9,0]);}await using(var j=new DurableJournal(path)){Check(j.Recover().Count==1);}});
await Test("shadow RR and raw commission cash reconciliation",()=>Sync(()=>{var c=Config() with {AccountType="raw"};var p=new ShadowPortfolio(c);var s=Intent(c);Check(p.Enter(s,new(6000,6000.2,now,now),now));p.Mark(new(6019.8,6020,now,now),now);Check(p.Active==1);p.Mark(new(6023.4,6023.6,now,now),now);Check(p.Active==0);Check(Math.Abs(p.Balance-2000.54)<1e-7&&Math.Abs(p.Cycles.Single().Net-.54)<1e-7);}));
await Test("joint strategy cannot invent absent L2/model inputs",()=>Sync(()=>{var p=new Policy(Config() with {Id="joint-hedge-jh1",Family="JH1"},null);var f=new FeatureSnapshot {AvailableNs=now,BrokerReady=true};Check(p.OnBar(new(),f,true,true,0)==null&&p.Reason.Contains("model"));}));
await Test("asynchronous recorder writes complete replayable frames",async()=>{string dir=Path.Combine(temp,"recorder");await using(var r=new EventRecorder(dir)){for(int i=0;i<1000;i++)Check(r.Submit(MessageType.Market,Wire.EncodeMarket(Ev(i+1))));}var file=Directory.GetFiles(dir,"events.bin",SearchOption.AllDirectories).Single();await using var s=File.OpenRead(file);int count=0;while(await Wire.ReadAsync(s) is {} f){Check(f.Type==MessageType.Market);count++;}Check(count==1000);});
await Test("foreign account inventory blocks entry without sending",async()=>
{
    await using var j=new DurableJournal(Path.Combine(temp,"foreign.bin"));var c=Config();var e=new CycleEngine(c,j,_=>throw new Exception("unexpected order"));e.Bind(Hello(c));e.Armed=true;
    e.Account=new(42,c.Server,2000,2000,1900,0,true,1,Clock.UtcNs,ForeignPositions:1);e.Quote=new(6000,6000.25,Clock.UtcNs,Clock.UtcNs);Check(!await e.AcceptAsync(Intent(c),Clock.UtcNs));Check(e.Cycles.Count==0);
});
await Test("partial exit does not mark entire position closed",async()=>
{
    await using var j=new DurableJournal(Path.Combine(temp,"exitpartial.bin"));var c=Config();var commands=new List<BrokerCommand>();var e=new CycleEngine(c,j,x=>{commands.Add(x);return Task.CompletedTask;});e.Bind(Hello(c));e.Armed=true;e.Account=new(42,c.Server,2000,2000,1900,0,true,0,Clock.UtcNs);e.Quote=new(6000,6000.25,Clock.UtcNs,Clock.UtcNs);
    await e.AcceptAsync(Intent(c),Clock.UtcNs);var cycle=e.Cycles.Single();await e.ApplyAsync(Report("fill",commands[0].Id,cycle.Id));await e.ApplyAsync(Report("close",commands[0].Id,cycle.Id,deal:2,volume:.1,pnl:-2));
    Check(!cycle.Legs[0].Closed&&Math.Abs(cycle.Legs[0].ExitVolume-.1)<1e-8&&!e.Reconciled&&e.Active==1);
});
await Test("late protection after first-leg exit cannot open second leg",async()=>
{
    await using var j=new DurableJournal(Path.Combine(temp,"late.bin"));var c=Config();var commands=new List<BrokerCommand>();var e=new CycleEngine(c,j,x=>{commands.Add(x);return Task.CompletedTask;});e.Bind(Hello(c));e.Armed=true;e.Account=new(42,c.Server,2000,2000,1900,0,true,0,Clock.UtcNs);e.Quote=new(6000,6000.25,Clock.UtcNs,Clock.UtcNs);
    await e.AcceptAsync(Intent(c),Clock.UtcNs);var cycle=e.Cycles.Single();await e.ApplyAsync(Report("fill",commands[0].Id,cycle.Id));await e.ApplyAsync(Report("close",commands[0].Id,cycle.Id,deal:2,pnl:-4));await e.ApplyAsync(Report("protected",commands[1].Id,cycle.Id,sl:5980,tp:6023.2));Check(commands.Count==2&&!e.Reconciled);
});
await Test("recovery preserves survivor and deduplicates historic deals",async()=>
{
    var c=Config();string path=Path.Combine(temp,"recovery.bin");var cycle=new Cycle{Id=Guid.NewGuid(),SignalId=Guid.NewGuid(),Profile=c.Id,ConfigHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Json.Bytes(c))),Phase="Survivor",Hedge=true,Risk=20,TargetR=1.16,Lots=.2,OpenedNs=now,SeenDeals=[1,2,3],Legs=[new(){Side="LONG",CommandId=Guid.NewGuid(),Position=1,Identifier=1,Volume=.2,ExitVolume=.2,Entry=6000,Sl=5980,Tp=6023.2,Closed=true,Protected=true,Pnl=-4},new(){Side="SHORT",CommandId=Guid.NewGuid(),Position=2,Identifier=2,Volume=.2,Entry=6000,Sl=6020,Tp=5976.8,Protected=true}]};
    await using(var j=new DurableJournal(path))await j.AppendAsync(cycle);
    await using(var j=new DurableJournal(path))
    {
        var sent=new List<BrokerCommand>();var e=new CycleEngine(c,j,x=>{sent.Add(x);return Task.CompletedTask;});e.Bind(Hello(c));var commands=e.ReconciliationCommands().ToArray();Check(commands.Length==2);
        await e.ApplyAsync(Report("history_close",cycle.Legs[0].CommandId,cycle.Id,deal:3,pnl:-4));await e.ApplyAsync(Report("leg_reconciled",cycle.Legs[0].CommandId,cycle.Id,volume:0));await e.ReconcileAsync([],true);Check(!e.Reconciled);
        await e.ApplyAsync(Report("leg_reconciled",cycle.Legs[1].CommandId,cycle.Id,pos:2,volume:.2,sl:6020,tp:5976.8) with {Identifier=2});await e.ReconcileAsync([],true);
        Check(e.Reconciled&&e.Cycles.Single().Phase=="Survivor"&&e.Cycles.Single().Net==-4&&sent.Count==0);
    }
});
await Test("uncertain absent entry is never retried or declared successful",async()=>
{
    await using var j=new DurableJournal(Path.Combine(temp,"absent.bin"));var c=Config();var sent=new List<BrokerCommand>();var e=new CycleEngine(c,j,x=>{sent.Add(x);return Task.CompletedTask;});e.Bind(Hello(c));e.Armed=true;e.Account=new(42,c.Server,2000,2000,1900,0,true,0,Clock.UtcNs);e.Quote=new(6000,6000.25,Clock.UtcNs,Clock.UtcNs);
    await e.AcceptAsync(Intent(c),Clock.UtcNs);var cycle=e.Cycles.Single();e.ReconciliationCommands().ToArray();await e.ApplyAsync(Report("leg_reconciled",sent[0].Id,cycle.Id,volume:0));await e.ReconcileAsync([],true);Check(!e.Reconciled&&sent.Count==1&&cycle.Phase=="ReconcileRequired");
});
await Test("directional fade requires depth, tape and one-second persistence",()=>Sync(()=>
{
    var fade=new FadeDetector();Guid cycle=Guid.NewGuid();var f=new FeatureSnapshot {AvailableNs=now,DepthReady=true,BrokerReady=true,Tape5=-.3,BidDepth5=40,PriorBidMedian5=100};
    Check(!fade.Evaluate(cycle,Side.Buy,f));Check(!fade.Evaluate(cycle,Side.Buy,f with {AvailableNs=now+900_000_000}));Check(fade.Evaluate(cycle,Side.Buy,f with {AvailableNs=now+1_000_000_000}));
    Check(!fade.Evaluate(cycle,Side.Buy,f with {AvailableNs=now+1_100_000_000,DepthReady=false}));Check(!fade.Evaluate(cycle,Side.Buy,f with {AvailableNs=now+1_200_000_000}));
}));
await Test("A0 ignores the directional fade exit",()=>Sync(()=>
{
    var c=Config();var p=new ShadowPortfolio(c);Check(p.Enter(Intent(c),new(6000,6000.2,now,now),now));var f=new FeatureSnapshot {AvailableNs=now+2_000_000_000,DepthReady=true,BrokerReady=true,Tape5=-1,BidDepth5=1,PriorBidMedian5=100};
    p.FadeExit(f,new(6000,6000.2,f.AvailableNs,f.AvailableNs),f.AvailableNs);Check(p.Active==1&&p.Cycles.Single().Legs.All(x=>!x.Closed));
}));
await Test("depth reference excludes the current observation",()=>Sync(()=>
{
    var book=new DepthBook();var features=new FeatureWindow();FeatureSnapshot f=default;
    for(int i=0;i<51;i++){long ns=now+i*100_000_000L;var d=Ev(i+1,ns:ns,kind:EventKind.Depth) with {Quality=Quality.Genuine|Quality.DepthEntitled,Side=1,Position=0,Operation=i==0?1:2,Quantity=100};book.Apply(d);book.Apply(d with {Side=-1,Price=600025000000});f=features.Snapshot(ns,book);}
    long at=now+5_000_000_000;book.Apply(Ev(100,ns:at,kind:EventKind.Depth) with {Quality=Quality.Genuine|Quality.DepthEntitled,Side=1,Operation=2,Quantity=1});f=features.Snapshot(at,book);Check(f.PriorBidMedian5==100&&f.BidDepth5==1);
}));
await Test("unknown source timestamp cannot label an executed print",()=>Sync(()=>{var q=new FeedQuality();Check(!q.Accept(Ev() with {Quality=Quality.Genuine|Quality.SourceTimestampUnknown},now,out _));}));
await Test("shadow restart restores cash once and retains active inventory",async()=>
{
    var c=Config() with {AccountType="raw"};var p=new ShadowPortfolio(c);var path=Path.Combine(temp,"shadow.bin");Check(p.Enter(Intent(c),new(6000,6000.2,now,now),now));
    await using(var j=new DurableJournal(path)){await j.AppendAsync(p.Cycles.Single());p.Mark(new(6019.8,6020,now,now),now);await j.AppendAsync(p.Cycles.Single());}
    await using(var j=new DurableJournal(path)){var restored=new ShadowPortfolio(c,2000,j.Recover());Check(restored.Active==1&&Math.Abs(restored.Balance-p.Balance)<1e-8&&restored.Cycles.Count==1);}
});
await Test("cached depth snapshot does not manufacture live freshness",()=>Sync(()=>
{
    var book=new DepthBook();var d=Ev(kind:EventKind.Depth) with {Quality=Quality.Genuine|Quality.DepthEntitled|Quality.Snapshot|Quality.SourceTimestampUnknown,Side=1,Operation=1,Quantity=100};
    book.Apply(d);book.Apply(d with {Side=-1,Price=600025000000});Check(book.Ready&&book.LastNs==0);
    book.Apply(d with {Quality=Quality.Genuine|Quality.DepthEntitled,Operation=2});Check(book.LastNs==now);
}));
await Test("compressed recording preserves every complete frame",async()=>
{
    string dir=Path.Combine(temp,"compressed");await using(var r=new EventRecorder(dir,compress:true))for(int i=0;i<1000;i++)Check(r.Submit(MessageType.Market,Wire.EncodeMarket(Ev(i+1))));
    var path=Directory.GetFiles(dir,"*.bin.gz",SearchOption.AllDirectories).Single();await using var file=File.OpenRead(path);using var zip=new System.IO.Compression.GZipStream(file,System.IO.Compression.CompressionMode.Decompress);int count=0;
    while(await Wire.ReadAsync(zip) is {} f){Check(Wire.DecodeMarket(f.Payload).Sequence==++count);}Check(count==1000&&new FileInfo(path).Length<132000);
});
await Test("compressed logs flush while open and checkpoint keeps recording",async()=>
{
    string dir=Path.Combine(temp,"checkpoint");await using var recorder=new EventRecorder(dir,compress:true);
    Check(recorder.Submit(MessageType.Market,Wire.EncodeMarket(Ev(1))));
    string[] files=[];for(int i=0;i<100;i++){files=Directory.GetFiles(dir,"*.bin.gz",SearchOption.AllDirectories);if(files.Length==1&&new FileInfo(files[0]).Length>0)break;await Task.Delay(10);}
    Check(files.Length==1&&new FileInfo(files[0]).Length>0,"active compressed bytes were not flushed");
    string first=(await recorder.SealAsync())!;Check(recorder.Submit(MessageType.Market,Wire.EncodeMarket(Ev(2))));string second=(await recorder.SealAsync())!;
    Check(first!=second&&recorder.Written==2&&recorder.Dropped==0&&recorder.Error==null);
    int count=0;foreach(string path in new[]{first,second}){await using var file=File.OpenRead(path);using var zip=new System.IO.Compression.GZipStream(file,System.IO.Compression.CompressionMode.Decompress);while(await Wire.ReadAsync(zip) is {} f)Check(Wire.DecodeMarket(f.Payload).Sequence==++count);}
    Check(count==2);
});
await Test("separate profiles never share broker quotes or basis",()=>Sync(()=>
{
    var a=new BrokerAlignment();var b=new BrokerAlignment();a.Add(new(5000,5000.2,now,now),Ev(),now);b.Add(new(5100,5101,now,now),Ev(),now);
    var common=new FeatureSnapshot {AvailableNs=now,Mid=6000.125};var fa=a.Apply(common,now);var fb=b.Apply(common,now);
    Check(fa.BrokerReady&&fb.BrokerReady&&Math.Abs(fa.Spread-.2)<1e-7&&fb.Spread==1&&Math.Abs(fa.Basis-fb.Basis)>99);
    a.Reset();Check(!a.Apply(common,now).BrokerReady&&b.Apply(common,now).BrokerReady);
}));
await Test("demo execution rejects a read-only native bridge",async()=>
{
    await using var j=new DurableJournal(Path.Combine(temp,"readonly.bin"));var c=Config() with {Execution="demo"};var e=new CycleEngine(c,j,_=>Task.CompletedTask);
    try{e.Bind(Hello(c));throw new Exception("read-only adapter armed");}catch(InvalidDataException){}
    e.Bind(Hello(c) with {OrdersAllowed=true});Check(e.Identity!=null);
});

Console.WriteLine($"{passed} passed, {failures.Count} failed");
var report=new {timeUtc=DateTime.UtcNow,passed,failures};
var root=Workspace.Root(args);Workspace.Atomic(Path.Combine(root,"dotnet/runtime/test-results.json"),Json.Bytes(report));
if(failures.Count>0){Environment.ExitCode=1;return;}
var clock=new PrintClock(Asset.ES,"volume5",new(100,1_000_000,"bench",[]),Cal());var times=new double[100000];
for(int i=0;i<times.Length;i++){long start=Stopwatch.GetTimestamp();var e=Ev(i+1);var wire=Wire.EncodeMarket(e);var decoded=Wire.DecodeMarket(wire);clock.Add(decoded);times[i]=Clock.Milliseconds(Stopwatch.GetTimestamp()-start);}
Array.Sort(times);var bench=new {scope="in-process encode/decode/whole-print clock; synthetic fixtures, not broker latency",events=times.Length,p50Ms=times[times.Length/2],p95Ms=times[(int)(times.Length*.95)],p99Ms=times[(int)(times.Length*.99)],maxMs=times[^1]};
Workspace.Atomic(Path.Combine(root,"dotnet/runtime/kernel-benchmark.json"),Json.Bytes(bench));Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(bench));

sealed class FragmentStream(Stream inner):Stream
{
    public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>inner.Length;public override long Position {get=>inner.Position;set=>throw new NotSupportedException();}
    public override int Read(byte[] b,int o,int n)=>inner.Read(b,o,Math.Min(1,n));
    public override ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default)=>inner.ReadAsync(b[..Math.Min(1,b.Length)],ct);
    public override void Flush()=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
}
