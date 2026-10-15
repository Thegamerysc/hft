using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using TraderProMax.Contracts;
using TraderProMax.Core;
using TraderProMax.Execution;
using TraderProMax.Recorder;
using TraderProMax.Transport;

var root=Workspace.Root(args);var bridge=Workspace.Bridge(root);string id=Workspace.Get(args,"--profile","a0-volume-1");
var cfg=Json.Read<ProfileConfig[]>(File.ReadAllBytes(Path.Combine(Workspace.Runtime(root),"profiles.json"))).Single(x=>x.Id==id);cfg.Validate();
string runDir=Path.Combine(root,"data/v2/execution",id,$"{cfg.Asset}-{cfg.Login}");
Directory.CreateDirectory(runDir);
RunManifest.Save(root,runDir,"ExecutionWorker",cfg);
using var ownership=new FileStream(Path.Combine(runDir,"owner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
await using var recorder=new EventRecorder(Path.Combine(root,"data/v2/decisions",id),compress:true);
await using var journal=new DurableJournal(Path.Combine(runDir,"journal.bin"));
await using var shadowJournal=new DurableJournal(Path.Combine(runDir,"shadow-journal.bin"));
var worker=new Worker(root,bridge,cfg,recorder,journal,shadowJournal);
Console.WriteLine($"C# execution worker {id} 127.0.0.1:{cfg.WorkerPort} | {cfg.Execution} | demo-only");
using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};await worker.Run(stop.Token);

sealed record Input(string Source,Frame Frame,TaskCompletionSource<bool>? Bound=null);
sealed class Worker
{
    readonly string root;readonly BridgeConfig bridge;readonly ProfileConfig cfg;readonly EventRecorder recorder;readonly CycleEngine engine;readonly ShadowPortfolio paper;
    readonly Channel<Input> inbox=Channel.CreateBounded<Input>(new BoundedChannelOptions(2048){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    readonly Channel<Frame> toHost=Channel.CreateBounded<Frame>(new BoundedChannelOptions(4096){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    readonly Channel<Frame> toBroker=Channel.CreateBounded<Frame>(new BoundedChannelOptions(256){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    bool armed;volatile bool brokerConnected,hostConnected,brokerOwner;long brokerSeen,lastReconcile;BrokerQuote? quote;AccountSnapshot? account;string reason="Awaiting dedicated demo terminal";
    readonly Dictionary<Guid,string> recordedCycles=[];
    readonly DurableJournal shadowJournal;
    public Worker(string r,BridgeConfig b,ProfileConfig p,EventRecorder rec,DurableJournal journal,DurableJournal shadow)
    {
        root=r;bridge=b;cfg=p;recorder=rec;shadowJournal=shadow;paper=new(p,2000,shadow.Recover());
        foreach(var c in paper.Cycles)recordedCycles[c.Id]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Json.Bytes(c)));
        engine=new(p,journal,command=>
        {
            if(cfg.Execution!="demo")throw new InvalidOperationException("Shadow worker cannot submit broker orders");
            if(!brokerConnected||!hostConnected)throw new IOException("Execution connection unavailable");
            if(!toBroker.Writer.TryWrite(new(MessageType.Command,Json.Bytes(command))))throw new IOException("Broker command queue full");
            recorder.Submit(MessageType.Research,Json.Bytes(new {kind="command",command}));return Task.CompletedTask;
        });
    }
    public async Task Run(CancellationToken ct)
    {
        var listener=new TcpListener(IPAddress.Loopback,cfg.WorkerPort);listener.Start();
        var accept=Task.Run(async()=>{try{while(!ct.IsCancellationRequested){var client=await listener.AcceptTcpClientAsync(ct);client.NoDelay=true;_=Broker(client,ct);}}catch(OperationCanceledException){}},ct);
        var host=Task.Run(()=>Host(ct),ct);
        var pulse=Task.Run(async()=>{using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(200));try{while(await timer.WaitForNextTickAsync(ct))await inbox.Writer.WriteAsync(new("pulse",new(MessageType.Heartbeat,[])),ct);}catch(OperationCanceledException){}},ct);
        try{await foreach(var message in inbox.Reader.ReadAllAsync(ct))try{await Handle(message);}catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException){armed=false;engine.Armed=false;reason="Reconciliation required: "+ex.Message;recorder.Submit(MessageType.Research,Json.Bytes(new {kind="worker_error",timeNs=Clock.UtcNs,reason}));}}
        catch(OperationCanceledException){}
        finally{listener.Stop();await Task.WhenAll(accept,host,pulse);}
    }
    async Task Host(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var c=Wire.Client();await c.ConnectAsync(IPAddress.Loopback,bridge.Port,ct);var s=c.GetStream();
                await Wire.WriteAsync(s,MessageType.Hello,Json.Bytes(new FeedHello(bridge.Token,"worker",cfg.Id)),ct);
                var ack=await Wire.ReadAsync(s,ct);if(ack?.Type!=MessageType.Ack)throw new IOException("Market handshake failed");hostConnected=true;
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
                var writes=Task.Run(async()=>{try{await foreach(var f in toHost.Reader.ReadAllAsync(linked.Token))await Wire.WriteAsync(s,f.Type,f.Payload,linked.Token);}catch(IOException){c.Close();}},ct);
                try{while(await Wire.ReadAsync(s,ct) is {} f)await inbox.Writer.WriteAsync(new("host",f),ct);}
                finally{hostConnected=false;linked.Cancel();try{await writes;}catch(OperationCanceledException){}}
            }
            catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException){hostConnected=false;}
            if(!ct.IsCancellationRequested)await Task.Delay(1000,ct);
        }
    }
    async Task Broker(TcpClient c,CancellationToken ct)
    {
        bool owns=false;
        try
        {
            using(c)
            {
                var s=c.GetStream();using var handshake=CancellationTokenSource.CreateLinkedTokenSource(ct);handshake.CancelAfter(5000);
                var f=await Wire.ReadAsync(s,handshake.Token);if(f?.Type!=MessageType.Hello)return;
                var h=Json.Read<BrokerHello>(f.Value.Payload);if(!Workspace.TokenEquals(bridge.Token,h.Token))return;
                lock(inbox){if(brokerOwner)throw new InvalidDataException("Demo terminal already connected");brokerOwner=true;owns=true;}
                var bound=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await inbox.Writer.WriteAsync(new("bind",f.Value,bound),ct);bool valid=await bound.Task.WaitAsync(TimeSpan.FromSeconds(5),ct);
                await Wire.WriteAsync(s,MessageType.Ack,Json.Bytes(new {accepted=valid,orders=valid&&cfg.Execution=="demo"}),ct);if(!valid)return;
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
                var writes=Task.Run(async()=>{try{await foreach(var command in toBroker.Reader.ReadAllAsync(linked.Token))await Wire.WriteAsync(s,command.Type,command.Payload,linked.Token);}catch(IOException){c.Close();}},ct);
                try{while(await Wire.ReadAsync(s,ct) is {} row)await inbox.Writer.WriteAsync(new("broker",row),ct);}
                finally{linked.Cancel();try{await writes;}catch(OperationCanceledException){}}
            }
        }
        catch(Exception ex) when(ex is IOException or SocketException or InvalidDataException or OperationCanceledException or JsonException){reason=ex.GetType().Name;}
        finally{if(owns){brokerOwner=false;brokerConnected=false;armed=false;engine.Armed=false;}}
    }
    async Task Handle(Input input)
    {
        long now=Clock.UtcNs;
        if(input.Source=="bind")
        {
            try
            {
                engine.Bind(Json.Read<BrokerHello>(input.Frame.Payload));brokerConnected=true;brokerSeen=now;reason="Demo terminal verified";
                var h=engine.Identity!;recorder.Submit(MessageType.Research,Json.Bytes(new {kind="broker_manifest",timeNs=now,h.Login,h.Server,h.Currency,h.Demo,h.Hedging,h.Symbol,h.ContractSize,h.TickSize,h.MinLot,h.LotStep,h.Magic,h.StopDistance,mode=cfg.Execution}));
                while(toBroker.Reader.TryRead(out _)){} // Unconfirmed opens are reconciled, never replayed on reconnect.
                if(cfg.Execution=="demo")RequestReconciliation(now);input.Bound?.TrySetResult(true);
            }
            catch(InvalidDataException ex){brokerConnected=false;reason=ex.Message;input.Bound?.TrySetResult(false);}
        }
        else if(input.Source=="host"&&input.Frame.Type==MessageType.Control)
        {
            var c=Json.Read<ControlRequest>(input.Frame.Payload);armed=c.Action=="start";engine.Armed=armed&&cfg.Execution=="demo";reason=armed?"Awaiting eligible signal":"Stopped";
        }
        else if(input.Source=="host"&&input.Frame.Type==MessageType.Signal)
        {
            var intent=Json.Read<SignalIntent>(input.Frame.Payload);bool accepted=false;
            if(armed&&brokerConnected&&recorder.Healthy&&hostConnected)
            {
                if(cfg.Execution=="shadow"&&quote!=null)accepted=paper.Enter(intent,quote,now);
                else accepted=await engine.AcceptAsync(intent,now);
            }
            recorder.Submit(MessageType.Research,Json.Bytes(new {kind="opportunity",intent,accepted,reason=accepted?"accepted":engine.Reason,mode=cfg.Execution}));
            reason=accepted?"Cycle opened":engine.Reason;
        }
        else if(input.Source=="host"&&input.Frame.Type==MessageType.Research&&cfg.Family=="JD1")
        {
            var f=Json.Read<FeatureSnapshot>(input.Frame.Payload);
            if(now-f.AvailableNs<=250_000_000&&f.BrokerReady)
            {if(cfg.Execution=="demo"&&brokerConnected)await engine.FadeExit(f,now);else if(cfg.Execution=="shadow"&&quote!=null)paper.FadeExit(f,quote,now);}
        }
        else if(input.Source=="broker"&&input.Frame.Type==MessageType.Broker)
        {
            brokerSeen=now;if(!brokerConnected)return;using var doc=JsonDocument.Parse(input.Frame.Payload);string kind=doc.RootElement.GetProperty("kind").GetString()??"";
            if(kind=="quote")
            {
                var received=Json.Read<BrokerQuote>(input.Frame.Payload);quote=received with {ReceivedNs=now};engine.Quote=quote;
                recorder.Submit(MessageType.Broker,Json.Bytes(new {kind="quote",profile=cfg.Id,quote.Bid,quote.Ask,quote.SourceNs,quote.ReceivedNs}));
                if(cfg.Execution=="shadow")paper.Mark(quote,now);
                toHost.Writer.TryWrite(new(MessageType.Broker,Json.Bytes(new WorkerBroker(cfg.Id,quote))));
            }
            else if(kind=="account")
            {account=Json.Read<AccountSnapshot>(input.Frame.Payload) with {TimeNs=now};engine.Account=account;recorder.Submit(MessageType.Research,Json.Bytes(new {kind="account",timeNs=now,profile=cfg.Id,account}));}
            else if(kind=="reconcile_complete")await engine.ReconcileAsync([],true);
            else
            {var report=Json.Read<BrokerReport>(input.Frame.Payload);if(cfg.Execution=="demo"){await engine.ApplyAsync(report);if(kind=="leg_reconciled")await engine.ReconcileAsync([],true);}recorder.Submit(MessageType.Broker,input.Frame.Payload);}
        }
        if(input.Source=="pulse")
        {
            if(brokerConnected&&now-brokerSeen>3_000_000_000){brokerConnected=false;armed=false;engine.Armed=false;reason="Broker heartbeat stale";}
            if(!hostConnected)engine.Armed=false;
            if(cfg.Execution=="demo"&&brokerConnected)
            {await engine.CheckTimeouts(now);if(!engine.Reconciled&&now-lastReconcile>=5_000_000_000)RequestReconciliation(now);await engine.TimeExits(now);}
            var inventory=cfg.Execution=="shadow"?paper.Cycles:engine.Cycles;
            foreach(var c in inventory)
            {
                string fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Json.Bytes(c)));
                if(recordedCycles.GetValueOrDefault(c.Id)==fingerprint)continue;
                if(cfg.Execution=="shadow"){await shadowJournal.AppendAsync(c);fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Json.Bytes(c)));}
                recordedCycles[c.Id]=fingerprint;recorder.Submit(MessageType.Research,Json.Bytes(new {kind="cycle_state",timeNs=now,mode=cfg.Execution,config=cfg,cycle=c}));
            }
            var cycles=inventory.Select(c=>new CycleView(c.Id,c.Phase,c.Net,c.OpenedNs,c.ClosedNs,c.Aborted,c.Reason)).ToArray();
            var a=account;
            if(cfg.Execution=="shadow"&&a!=null)a=a with {Balance=paper.Balance,Equity=paper.Equity(quote),Positions=paper.Active*(cfg.Family=="JD1"?1:2)};
            toHost.Writer.TryWrite(new(MessageType.Status,Json.Bytes(new WorkerView(cfg.Id,brokerConnected,armed,engine.Reconciled,reason,a,quote,cycles))));
        }
    }
    void RequestReconciliation(long now)
    {
        lastReconcile=now;
        foreach(var cmd in engine.ReconciliationCommands())if(!toBroker.Writer.TryWrite(new(MessageType.Command,Json.Bytes(cmd))))throw new IOException("Reconciliation queue full");
    }
}
