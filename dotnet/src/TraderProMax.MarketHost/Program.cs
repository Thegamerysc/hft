using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using TraderProMax.Contracts;
using TraderProMax.Core;
using TraderProMax.Recorder;
using TraderProMax.Transport;

var root=Workspace.Root(args);var bridge=Workspace.Bridge(root);
using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
await using var recorder=new EventRecorder(Path.Combine(root,"data/v2/raw/ninjatrader"),compress:true);
var host=new MarketHost(root,bridge,recorder);
Console.WriteLine($"C# market host 127.0.0.1:{bridge.Port} | version 2 | demo-only, profiles initially stopped");
await host.Run(stop.Token);

sealed record Input(string Role,Frame Frame,TcpClient? Client=null,TaskCompletionSource<ControlReply>? Reply=null,string Producer="");
sealed class MarketState(Asset asset)
{
    public Asset Asset=asset;public readonly DepthBook Book=new();public readonly FeatureWindow Features=new();
    public PrintClock? Volume,Dollar;public string Day="",AttemptedDay="",HistoryReason="20 prior sessions required",QualityReason="";public double? RejectedSourceAge;public long LastRejectedRecord,LastFeaturePush;
    public SessionTotal[] History=[];
    public long LastRecoveryNs;
    public Guid Epoch;public long Events,Trades,VolumeCount;public MarketEvent? Latest;public long LastSeen;public double? LocalMs;
}
sealed class ProfileState(ProfileConfig config)
{
    public ProfileConfig Config=config;public bool Running;public string Reason="Stopped";
    public JointModel? Model;public Policy Policy=new(config,null);public JointSetup Setup=new();
    public BrokerAlignment Alignment=new();
    public WorkerView? Worker;public long WorkerSeen;public readonly SemaphoreSlim Writer=new(1,1);public NetworkStream? Route;
    public readonly Channel<Frame> Outbound=Channel.CreateBounded<Frame>(new BoundedChannelOptions(256){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
}
sealed class MarketHost
{
    readonly string root;readonly BridgeConfig bridge;readonly EventRecorder recorder;readonly TradingCalendar calendar;
    readonly Dictionary<Asset,MarketState> markets=new(){{Asset.ES,new(Asset.ES)},{Asset.NQ,new(Asset.NQ)}};
    readonly Dictionary<string,ProfileState> profiles=[];readonly Queue<ActivityView> activity=[];
    readonly FeedQuality quality=new();readonly Channel<Input> inbox=Channel.CreateBounded<Input>(new BoundedChannelOptions(32768){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    volatile byte[] snapshot=Json.Bytes(new HostView("2.0",Clock.UtcNs,[],[],0,0,null,[],false));
    readonly string profilePath;long lastSnapshot;bool feedOwner;
    readonly string runId;
    readonly Channel<Frame> feedOutbound=Channel.CreateBounded<Frame>(16);
    readonly Dictionary<Asset,Dictionary<string,SessionTotal>> seeded=[];
    readonly Dictionary<Asset,Guid> seedRequests=[];
    public MarketHost(string r,BridgeConfig b,EventRecorder rec)
    {
        root=r;bridge=b;recorder=rec;calendar=TradingCalendar.Load(Path.Combine(root,"dotnet/configs/calendar.json"));
        runId=RunManifest.Save(root,Path.Combine(root,"data/v2/raw/ninjatrader"),"MarketHost");
        foreach(var m in markets.Values)
        {
            string native=Path.Combine(root,"data/v2/history",m.Asset+".NinjaTrader.json");
            string owned=Path.Combine(root,"market_feed/history",m.Asset+".json");
            try{m.History=Json.Read<SessionTotal[]>(File.ReadAllBytes(File.Exists(native)?native:owned));}catch(IOException){}catch(JsonException){}
        }
        profilePath=Path.Combine(Workspace.Runtime(root),"profiles.json");
        if(!File.Exists(profilePath))Workspace.Atomic(profilePath,File.ReadAllBytes(Path.Combine(root,"dotnet/configs/profiles.json")));
        foreach(var c in Json.Read<ProfileConfig[]>(File.ReadAllBytes(profilePath)))
        {
            c.Validate();var p=new ProfileState(c);
            if(c.ModelPath.Length>0&&File.Exists(Path.Combine(root,c.ModelPath))){p.Model=Json.Read<JointModel>(File.ReadAllBytes(Path.Combine(root,c.ModelPath)));p.Model.Validate();}
            p.Policy=new(c,p.Model);profiles[c.Id]=p;
        }
    }
    public async Task Run(CancellationToken ct)
    {
        var listener=new TcpListener(IPAddress.Loopback,bridge.Port);listener.Start();
        var accept=Task.Run(async()=>{try{while(!ct.IsCancellationRequested){var c=await listener.AcceptTcpClientAsync(ct);c.NoDelay=true;_=Serve(c,ct);}}catch(OperationCanceledException){}},ct);
        var pulse=Task.Run(async()=>{using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(100));try{while(await timer.WaitForNextTickAsync(ct))await inbox.Writer.WriteAsync(new("pulse",new(MessageType.Heartbeat,[])),ct);}catch(OperationCanceledException){}},ct);
        try{await foreach(var input in inbox.Reader.ReadAllAsync(ct))await Handle(input,ct);}
        catch(OperationCanceledException){}
        finally{listener.Stop();await Task.WhenAll(accept,pulse);}
    }
    async Task Serve(TcpClient client,CancellationToken ct)
    {
        string role="";ProfileState? route=null;bool ownsFeed=false,ownsRoute=false;
        try
        {
            using(client)
            {
                var s=client.GetStream();using var helloTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);helloTimeout.CancelAfter(5000);
                var hello=await Wire.ReadAsync(s,helloTimeout.Token);if(hello?.Type!=MessageType.Hello)return;
                var h=Json.Read<FeedHello>(hello.Value.Payload);if(!Workspace.TokenEquals(bridge.Token,h.Token))return;role=h.Role;
                if(role=="feed")
                {
                    lock(markets){if(feedOwner)throw new InvalidDataException("Feed owner already connected");feedOwner=true;ownsFeed=true;}
                }
                else if(role=="worker")
                {
                    if(!profiles.TryGetValue(h.Producer,out route))return;
                    await route.Writer.WaitAsync(ct);try{if(route.Route!=null)throw new InvalidDataException("Worker route already owned");route.Route=s;ownsRoute=true;}finally{route.Writer.Release();}
                }
                else if(role is not ("observer" or "control"))return;
                await Wire.WriteAsync(s,MessageType.Ack,Json.Bytes(new {accepted=true}),ct);
                if(route!=null)route.Outbound.Writer.TryWrite(new(MessageType.Control,Json.Bytes(new ControlRequest(Guid.NewGuid(),route.Running?"start":"stop",route.Config.Id))));
                if(role=="observer")
                {using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(200));while(await timer.WaitForNextTickAsync(ct))await Wire.WriteAsync(s,MessageType.Status,snapshot,ct);return;}
                using var linkStop=CancellationTokenSource.CreateLinkedTokenSource(ct);
                var outbound=role=="feed"?feedOutbound.Reader:route?.Outbound.Reader;
                Task? outgoing=outbound==null?null:Task.Run(async()=>
                {
                    try{await foreach(var f in outbound.ReadAllAsync(linkStop.Token))await Wire.WriteAsync(s,f.Type,f.Payload,linkStop.Token);}
                    catch(Exception ex) when(ex is IOException or OperationCanceledException){client.Close();}
                },ct);
                try{while(await Wire.ReadAsync(s,ct) is {} f)
                {
                    if(role=="control")
                    {
                        if(f.Type!=MessageType.Control)return;var reply=new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await inbox.Writer.WriteAsync(new(role,f,client,reply),ct);var response=await reply.Task.WaitAsync(ct);await Wire.WriteAsync(s,MessageType.Ack,Json.Bytes(response),ct);
                    }
                    else await inbox.Writer.WriteAsync(new(role,f,client,Producer:h.Producer),ct);
                }}finally{linkStop.Cancel();if(outgoing!=null)await outgoing;}
            }
        }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException){Console.WriteLine($"{role} connection ended: {ex.GetType().Name}");}
        finally
        {
            if(ownsFeed){lock(markets)feedOwner=false;}
            if(route!=null&&ownsRoute){await route.Writer.WaitAsync();try{route.Route=null;}finally{route.Writer.Release();}}
        }
    }
    async Task Handle(Input input,CancellationToken ct)
    {
        long now=Clock.UtcNs;
        if(input.Role=="control")
        {ControlReply reply;try{reply=await Control(Json.Read<ControlRequest>(input.Frame.Payload),ct);}catch(Exception ex){reply=new(Guid.Empty,false,ex.Message);}input.Reply?.TrySetResult(reply);}
        else if(input.Role=="worker"&&input.Frame.Type==MessageType.Status)
        {var w=Json.Read<WorkerView>(input.Frame.Payload);if(w.Profile==input.Producer&&profiles.TryGetValue(w.Profile,out var p)){p.Worker=w;p.WorkerSeen=now;}}
        else if(input.Role=="worker"&&input.Frame.Type==MessageType.Broker)
        {var q=Json.Read<WorkerBroker>(input.Frame.Payload);if(q.Profile==input.Producer&&profiles.TryGetValue(q.Profile,out var p)&&q.Quote is {} b)p.Alignment.Add(b,markets[p.Config.Asset].Latest,now);}
        else if(input.Role=="feed"&&input.Frame.Type==MessageType.Research)
        {
            recorder.Submit(MessageType.Research,input.Frame.Payload);
            using var doc=JsonDocument.Parse(input.Frame.Payload);var value=doc.RootElement;
            if(value.TryGetProperty("kind",out var kind)&&kind.GetString()=="history")
            {
                var asset=Enum.Parse<Asset>(value.GetProperty("root").GetString()!);var req=value.GetProperty("requestId").GetGuid();
                if(!seedRequests.TryGetValue(asset,out var expected)||req!=expected)return;
                var row=value.Deserialize<SessionTotal>(Json.Options)!;
                if(!row.Complete||row.Volume<=0||row.Dollars<=0)
                {
                    string detail="";foreach(string name in new[]{"error","message","bars"})if(value.TryGetProperty(name,out var v))detail+=" "+name+"="+v.ToString();
                    markets[asset].HistoryReason=asset+" history incomplete "+row.Day+detail;
                    Note("", "history",markets[asset].HistoryReason);return;
                }
                seeded[asset][row.Day]=row;
                markets[asset].HistoryReason=$"{asset} historical seed {seeded[asset].Count}/20";
                if(seeded[asset].Count==20)
                {
                    var totals=seeded[asset].Values.OrderBy(x=>x.Day).ToArray();markets[asset].History=totals;
                    _=Task.Run(()=>Workspace.Atomic(Path.Combine(root,"data/v2/history",asset+".NinjaTrader.json"),Json.Bytes(totals)));
                    markets[asset].Day="";markets[asset].AttemptedDay="";
                    Note("", "history",asset+" 20 prior sessions received; native-source clocks ready to warm");
                }
            }
        }
        else if(input.Role=="feed"&&input.Frame.Type==MessageType.Market)
        {
            var raw=Wire.DecodeMarket(input.Frame.Payload,now,Clock.Mono);
            if(!markets.TryGetValue(raw.Asset,out var m))return;
            if(!quality.Accept(raw,now,out var e))
            {
                m.QualityReason=quality.Reason;m.RejectedSourceAge=quality.RejectedSourceAge;
                if(now-m.LastRejectedRecord>=1_000_000_000){m.LastRejectedRecord=now;recorder.Submit(MessageType.Market,Wire.EncodeMarket(raw with {Quality=raw.Quality|Quality.Rejected}));recorder.Submit(MessageType.Research,Json.Bytes(new {kind="quality_rejection_sample",timeNs=now,asset=raw.Asset,raw.Sequence,reason=m.QualityReason,age=m.RejectedSourceAge}));}
                return;
            }
            m.LastSeen=now;m.Events++;
            if(m.Epoch!=e.Epoch){m.Epoch=e.Epoch;m.Book.Reset();m.Features.Reset();m.Volume?.Reset();m.Dollar?.Reset();foreach(var p in profiles.Values.Where(p=>p.Config.Asset==e.Asset))p.Alignment.Reset();}
            if(e.Quality.HasFlag(Quality.SequenceGap)){foreach(var x in markets.Values){x.Book.Reset();x.Features.Reset();x.Volume?.Reset();x.Dollar?.Reset();}foreach(var p in profiles.Values)p.Alignment.Reset();}
            if(e.QuoteValid)m.Latest=e;
            if(e.CaptureFrequency==System.Diagnostics.Stopwatch.Frequency)m.LocalMs=Math.Max(0,(e.ReceivedTicks-e.CaptureTicks)*1000.0/e.CaptureFrequency);
            recorder.Submit(MessageType.Market,Wire.EncodeMarket(e));m.Book.Apply(e);m.Features.Add(e);
            if(e.Kind==EventKind.Depth&&!m.Book.Ready&&!e.Quality.HasFlag(Quality.Snapshot)&&now-m.LastRecoveryNs>=5_000_000_000)
            {m.LastRecoveryNs=now;feedOutbound.Writer.TryWrite(new(MessageType.Control,Json.Bytes(new {action="snapshot",contract=bridge.Contracts[(int)e.Asset-1]})));}
            if(e.Kind==EventKind.Trade){m.Trades++;m.VolumeCount+=e.Quantity;}
            var session=calendar.At(e.SourceNs);
            if(session!=null&&m.Day!=session.Day&&m.AttemptedDay!=session.Day)
            {
                m.AttemptedDay=session.Day;
                try
                {
                    var limits=Thresholds.Calculate(m.History,e.Asset,session.Day,calendar.Previous(session.Day));
                    m.Volume=new(e.Asset,"volume5",limits,calendar);m.Dollar=new(e.Asset,"dollar30",limits,calendar);m.Day=session.Day;m.HistoryReason="Ready";
                }
                catch(InvalidDataException ex){m.HistoryReason=ex.Message;}
            }
            var volume=m.Volume?.Add(e);var dollar=m.Dollar?.Add(e);var common=m.Features.Snapshot(now,m.Book);
            bool pushFeatures=now-m.LastFeaturePush>=100_000_000;if(pushFeatures)m.LastFeaturePush=now;
            foreach(var p in profiles.Values.Where(x=>x.Config.Asset==e.Asset))
            {
                var features=p.Alignment.Apply(common,now);
                SignalIntent? intent=null;
                if(p.Config.Family=="JD1")
                {
                    if(pushFeatures&&p.Route!=null)p.Outbound.Writer.TryWrite(new(MessageType.Research,Json.Bytes(features)));
                    var side=p.Setup.Evaluate(e,features,m.Book,p.Model);p.Reason=p.Setup.State;
                    if(side!=Side.None&&p.Running&&calendar.Entries(now)&&(p.Worker?.Cycles.Count(x=>x.Phase!="Closed")??0)<p.Config.MaxActive)
                        intent=new(Guid.NewGuid(),p.Config.Id,"",e.Asset,e.Sequence,now,now+250_000_000,p.Config.Risk,p.Config.TargetR,p.Config.Lots,side,false,p.Config.MaxActive,p.Model?.Hash??"");
                }
                else
                {
                    var bar=p.Config.Clock=="dollar30"?dollar:volume;
                    if(bar is {} b)
                    {
                        intent=p.Policy.OnBar(b,features,p.Running,calendar.Entries(now),p.Worker?.Cycles.Count(x=>x.Phase!="Closed")??0);p.Reason=p.Policy.Reason;
                        recorder.Submit(MessageType.Research,Json.Bytes(new {kind="bar_opportunity",timeNs=now,profile=p.Config.Id,config=p.Config,bar=b,features,action=intent==null?"skip":"entry_intent",reason=p.Reason,policy="deterministic",propensityKnown=true,propensity=1.0,modelHash=p.Model?.Hash}));
                    }
                    else if(p.Running)p.Reason=m.Day==session?.Day?"Awaiting completed bar":m.HistoryReason;
                }
                if(intent!=null)
                {
                    intent=intent with {RunId=runId};
                    if(!recorder.Healthy){p.Reason="Research recorder unavailable";continue;}
                    if(p.Route==null||now-p.WorkerSeen>2_000_000_000){p.Reason="Execution worker unavailable";continue;}
                    recorder.Submit(MessageType.Research,Json.Bytes(new {kind="decision",intent,features}));
                    if(!p.Outbound.Writer.TryWrite(new(MessageType.Signal,Json.Bytes(intent)))){p.Reason="Execution queue full; signal skipped";p.Running=false;}
                    Note(p.Config.Id,"decision",p.Reason);
                }
            }
        }
        if(now-lastSnapshot>=200_000_000){lastSnapshot=now;Publish(now);}
    }
    void Note(string profile,string kind,string message)
    {var a=new ActivityView(Clock.UtcNs,profile,kind,message);activity.Enqueue(a);while(activity.Count>60)activity.Dequeue();recorder.Submit(MessageType.Research,Json.Bytes(a));}
    async Task<ControlReply> Control(ControlRequest c,CancellationToken ct)
    {
        if(c.Action=="checkpoint")
        {
            var path=await recorder.SealAsync(ct);return new(c.Id,true,path??"No open recording segment");
        }
        return ControlNow(c);
    }
    ControlReply ControlNow(ControlRequest c)
    {
        if(!profiles.TryGetValue(c.ProfileId,out var p))return new(c.Id,false,"Unknown profile");
        if(c.Action=="history")
        {
            if(!feedOwner)return new(c.Id,false,"Connect NinjaTrader native feed first");
            string day=DateTimeOffset.FromUnixTimeMilliseconds(Clock.UtcNs/1_000_000).ToString("yyyy-MM-dd");var sessions=calendar.Prior(day,20);
            var current=calendar.Prior(calendar.Next(day),1).SingleOrDefault();
            if(current!=null&&current.Day==day&&Clock.UtcNs>=current.CloseNs) sessions=calendar.Prior(calendar.Next(day),20);
            var request=Guid.NewGuid();seedRequests[p.Config.Asset]=request;seeded[p.Config.Asset]=[];
            var frame=new Frame(MessageType.Control,Json.Bytes(new {action="history",requestId=request,root=p.Config.Asset.ToString(),contract=bridge.Contracts[(int)p.Config.Asset-1],sessions}));
            if(!feedOutbound.Writer.TryWrite(frame))return new(c.Id,false,"History queue full");
            return new(c.Id,true,"Requesting only 20 prior session totals; no paid purchase");
        }
        if(c.Action is "start" or "stop" or "restart")
        {
            bool start=c.Action!="stop";
            if(start&&p.Config.Execution=="demo"&&(p.Config.Login<=0||p.Worker is not {Connected:true,Reconciled:true}))return new(c.Id,false,"Bind and reconcile dedicated demo terminal first");
            p.Running=start;p.Reason=start?"Warming inputs":"Stopped; existing protection retained";
            if(p.Route!=null&&!p.Outbound.Writer.TryWrite(new(MessageType.Control,Json.Bytes(new ControlRequest(c.Id,start?"start":"stop",p.Config.Id)))))return new(c.Id,false,"Worker command queue full");
            Note(c.ProfileId,c.Action,p.Reason);return new(c.Id,true,p.Reason);
        }
        if(c.Action=="bind"&&c.Value is {} value)
        {
            if(p.Running||(p.Worker?.Cycles.Any(x=>x.Phase!="Closed")??false))return new(c.Id,false,"Stop and reconcile active inventory before rebinding");
            var cfg=value.Deserialize<ProfileConfig>(Json.Options)??throw new InvalidDataException("Binding missing");cfg.Validate();if(cfg.Id!=p.Config.Id)throw new InvalidDataException("Profile identity differs");
            if(profiles.Values.Any(x=>x.Config.Id!=cfg.Id&&cfg.Login>0&&x.Config.Login==cfg.Login))throw new InvalidDataException("Account already bound to another profile");
            if(profiles.Values.Any(x=>x.Config.Id!=cfg.Id&&cfg.TerminalPath.Length>0&&string.Equals(x.Config.TerminalPath,cfg.TerminalPath,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Terminal already bound to another profile");
            if(cfg.Execution=="demo"&&(cfg.Login<=0||!cfg.Server.Contains("Exness",StringComparison.OrdinalIgnoreCase)||!File.Exists(cfg.TerminalPath)||Path.GetFileName(cfg.TerminalPath)!="terminal64.exe"))throw new InvalidDataException("Dedicated MT5 terminal/login/server required");
            p.Config=cfg;p.Policy=new(cfg,p.Model);p.Setup.Reset();p.Alignment.Reset();Workspace.Atomic(profilePath,Json.Bytes(profiles.Values.Select(x=>x.Config).ToArray()));
            Note(cfg.Id,"bind","Binding saved; restart the profile worker to load it");return new(c.Id,true,"Binding saved; worker restart required");
        }
        return new(c.Id,false,"Unknown control action");
    }
    void Publish(long now)
    {
        var pv=profiles.Values.Select(p=>
        {
            var w=now-p.WorkerSeen<=2_000_000_000?p.Worker:null;var all=w?.Cycles??[];var done=all.Where(x=>x.Phase=="Closed").ToArray();var a=w?.Account;
            return new ProfileView(p.Config.Id,p.Config.Name,p.Config.Family,p.Config.Asset==Asset.ES?"US500":"US100",p.Config.Symbol,p.Config.Execution,p.Running,
                !p.Running?"Stopped":w is not {Connected:true}?"Waiting for terminal":!w.Reconciled?"Reconciliation":"Running",p.Reason,p.Config.Login>0,w?.Connected??false,
                p.Config.Clock=="event"?markets[p.Config.Asset].Book.Ready:markets[p.Config.Asset].Day==calendar.At(now)?.Day,
                p.Config.MaxActive,all.Count(x=>x.Phase!="Closed"),done.Length,done.Count(x=>x.Net>0),done.Count(x=>x.Net<0),a?.Balance,a?.Equity,a is null?null:a.Equity-a.Balance,done.Sum(x=>x.Net),
                p.Config.Risk,p.Config.TargetR,p.Config.Lots,p.Config.Server,p.Config.Login,p.Config.WorkerPort,all);
        }).ToArray();
        var mv=markets.Values.Select(m=>new MarketView(m.Asset.ToString(),m.Events,m.Trades,m.VolumeCount,now-m.LastSeen<=2_000_000_000,m.Book.Ready&&now-m.Book.LastNs<=2_000_000_000,
            m.Book.Depth,m.Latest is {} q?q.Bid/(double)MarketEvent.Scale:null,m.Latest is {} q2?q2.Ask/(double)MarketEvent.Scale:null,m.Latest is {} q3?(now-q3.SourceNs)/1e9:null,
            m.LocalMs,quality.Gaps,quality.Rejected,m.HistoryReason,m.QualityReason,m.RejectedSourceAge)).ToArray();
        snapshot=Json.Bytes(new HostView("2.0 C#",now,pv,mv,recorder.Written,recorder.Dropped,recorder.Error,activity.Reverse().ToArray(),pv.Any(x=>x.Mode=="demo"&&x.Running)));
    }
}
