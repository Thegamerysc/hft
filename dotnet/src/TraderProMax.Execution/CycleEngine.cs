using TraderProMax.Contracts;
using TraderProMax.Core;
using System.Security.Cryptography;

namespace TraderProMax.Execution;

public sealed class Leg
{
    public string Side {get;set;}="";public Guid CommandId {get;set;}public ulong Position {get;set;}public ulong Deal {get;set;}
    public double Volume {get;set;}public double Entry {get;set;}public double Sl {get;set;}public double Tp {get;set;}
    public bool Protected {get;set;}public bool Closed {get;set;}public double Pnl {get;set;}public double Commission {get;set;}
    public ulong Identifier {get;set;}
    public double ExitVolume {get;set;}public bool Rejected {get;set;}public bool ReconcileSeen {get;set;}
    public long EntryNs {get;set;}public long ExitNs {get;set;}
    public double Swap {get;set;}public double Fee {get;set;}public double Net=>Pnl+Commission+Swap+Fee;
}
public sealed class Cycle
{
    public Guid Id {get;set;}public Guid SignalId {get;set;}public string Profile {get;set;}="";
    public string Phase {get;set;}="Intent";public string Reason {get;set;}="";public long OpenedNs {get;set;}public long ClosedNs {get;set;}
    public double Risk {get;set;}public double TargetR {get;set;}public double Lots {get;set;}public bool Hedge {get;set;}
    public long UpdatedNs {get;set;}public bool Aborted {get;set;}
    public string ConfigHash {get;set;}="";
    public string RunId {get;set;}="";
    public List<Leg> Legs {get;set;}=[];public List<ulong> SeenDeals {get;set;}=[];public List<Guid> AppliedCommands {get;set;}=[];
    public double Net=>Legs.Sum(x=>x.Net);
}

public sealed class CycleEngine
{
    readonly ProfileConfig config;readonly DurableJournal journal;readonly Func<BrokerCommand,Task> send;
    readonly Dictionary<Guid,Cycle> cycles=[];readonly HashSet<Guid> seenSignals=[];
    readonly string configHash;
    readonly FadeDetector fade=new();
    public BrokerHello? Identity {get;private set;}public AccountSnapshot? Account {get;set;}public BrokerQuote? Quote {get;set;}
    public bool Armed {get;set;}public bool Reconciled {get;private set;}=true;public string Reason {get;private set;}="Awaiting dedicated demo terminal";
    public IReadOnlyCollection<Cycle> Cycles=>cycles.Values;public int Active=>cycles.Values.Count(c=>c.Phase!="Closed");
    public CycleEngine(ProfileConfig cfg,DurableJournal j,Func<BrokerCommand,Task> sender)
    {
        config=cfg;journal=j;send=sender;configHash=Convert.ToHexString(SHA256.HashData(Json.Bytes(cfg)));
        foreach(var c in j.Recover()){cycles[c.Id]=c;seenSignals.Add(c.SignalId);}
        if(Active>0){Reconciled=false;Reason="Restart: reconcile broker history before arming";}
    }
    public void Bind(BrokerHello h)
    {
        if(!h.Demo||!h.Hedging||h.Currency!="USD"||!h.Server.Contains("Exness",StringComparison.OrdinalIgnoreCase)||
            config.Login<=0||h.Login!=config.Login||h.Server!=config.Server||h.Symbol!=config.Symbol||h.Magic!=config.Magic||
            h.ContractSize!=1||h.TickSize<=0||h.MinLot>config.Lots||h.LotStep<=0||Math.Abs(config.Lots/h.LotStep-Math.Round(config.Lots/h.LotStep))>1e-6)
            throw new InvalidDataException("Dedicated Exness DEMO hedging account/symbol mismatch");
        if(config.TerminalPath.Length>0&&(string.IsNullOrWhiteSpace(h.TerminalPath)||!Path.IsPathFullyQualified(h.TerminalPath)||!string.Equals(Path.GetFullPath(h.TerminalPath),Path.GetFullPath(config.TerminalPath),StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Bridge connected from a different MT5 terminal");
        if(config.Execution=="demo"&&!h.OrdersAllowed)throw new InvalidDataException("MT5 bridge is in read-only mode; load the dedicated demo settings");
        if(cycles.Values.Any(c=>c.Phase!="Closed"&&(c.ConfigHash!=configHash||c.Risk!=config.Risk||c.Lots!=config.Lots||c.TargetR!=config.TargetR)))
            throw new InvalidDataException("Active journal belongs to another configuration; explicit reconciliation required");
        Identity=h;if(Active>0)Reconciled=false;Reason=Reconciled?"Ready":"Broker reconciliation required";
    }
    public async Task<bool> AcceptAsync(SignalIntent intent,long now)
    {
        if(seenSignals.Contains(intent.Id)){Reason="Duplicate signal";return false;}
        if(!Armed||!Reconciled||Identity==null||Account==null||!Account.Connected||now-Account.TimeNs>2_000_000_000){Reason="Account not ready";return false;}
        if(intent.ProfileId!=config.Id||intent.Asset!=config.Asset||intent.Risk!=config.Risk||intent.TargetR!=config.TargetR||intent.Lots!=config.Lots||intent.MaxActive!=config.MaxActive||intent.Hedge!=(config.Family!="JD1")||!intent.Hedge&&intent.Side==Side.None||now>intent.ExpiresNs||now<intent.CreatedNs-250_000_000){Reason="Expired or mismatched intent";return false;}
        if(Active>=config.MaxActive){Reason="Inventory cap";return false;}
        if(cycles.Values.Any(c=>c.Phase is "Opening" or "ProtectionPending" or "Closing")){Reason="Establishing or closing another cycle";return false;}
        if(Account.Login!=config.Login||Account.Server!=config.Server||Account.ForeignPositions>0||Account.Orders>0||
            Account.Positions!=cycles.Values.SelectMany(c=>c.Legs).Count(l=>l.Volume-l.ExitVolume>1e-6&&!l.Closed))
        {Reason="Account inventory differs from dedicated journal";return false;}
        if(Quote is not {} q||q.Bid<=0||q.Ask<=q.Bid||now-q.ReceivedNs>2_000_000_000||now-q.SourceNs>2_000_000_000||q.SourceNs>now+250_000_000){Reason="Broker quote stale or future-dated";return false;}
        if(Account.FreeMargin<=0||Account.Equity<20){Reason="Insufficient equity/margin";return false;}
        var side=intent.Hedge||intent.Side==Side.Buy?"LONG":"SHORT";
        var c=new Cycle {Id=Guid.NewGuid(),SignalId=intent.Id,Profile=config.Id,OpenedNs=now,Risk=config.Risk,TargetR=config.TargetR,Lots=config.Lots,Hedge=intent.Hedge,ConfigHash=configHash,RunId=intent.RunId};
        var command=OpenCommand(c,side,intent.ExpiresNs);c.Legs.Add(new Leg {Side=side,CommandId=command.Id});
        c.Phase="Opening";cycles[c.Id]=c;seenSignals.Add(intent.Id);
        await journal.AppendAsync(c);
        if(Clock.UtcNs>intent.ExpiresNs&&now>Clock.UtcNs-60_000_000_000){c.Phase="Closed";c.Reason="Expired during durable reservation";await journal.AppendAsync(c);return false;}
        try{await send(command);Reason="Opening protected leg";return true;}
        catch(Exception ex){c.Phase="ReconcileRequired";c.Reason=ex.Message;Reconciled=false;await journal.AppendAsync(c);throw;}
    }
    BrokerCommand OpenCommand(Cycle c,string side,long expires)
        =>new(Guid.NewGuid(),c.Id,"open",config.Symbol,side,c.Lots,c.Risk,c.TargetR,expires,config.Magic,
            DeadlineBootMs:Environment.TickCount64+Math.Max(0,(expires-Clock.UtcNs)/1_000_000),Hedge:c.Hedge,ReserveLegs:c.Hedge&&c.Legs.Count==0?2:1);
    public async Task ApplyAsync(BrokerReport r)
    {
        if(!cycles.TryGetValue(r.CycleId,out var c))return;
        var leg=c.Legs.FirstOrDefault(x=>x.CommandId==r.CommandId||x.Position>0&&x.Position==r.Position||r.Identifier>0&&x.Identifier==r.Identifier);
        if(leg==null)return;
        if(r.Kind=="rejected"||r.Kind=="uncertain")
        {if(r.Kind=="rejected")leg.Rejected=true;c.Phase="ReconcileRequired";c.Reason=r.Reason;Reconciled=false;await journal.AppendAsync(c);return;}
        bool history=r.Kind.StartsWith("history_",StringComparison.Ordinal);
        if(r.Kind is "fill" or "history_fill")
        {
            if(c.SeenDeals.Contains(r.Deal))return;c.SeenDeals.Add(r.Deal);
            if(r.Deal==0||r.Volume<=0||!double.IsFinite(r.Price+r.Volume+r.Commission+r.Fee))throw new InvalidDataException("Invalid broker fill");
            leg.Position=r.Position;leg.Identifier=r.Identifier>0?r.Identifier:r.Position;leg.Deal=r.Deal;
            if(leg.EntryNs==0)leg.EntryNs=r.TimeNs;
            leg.Entry=(leg.Entry*leg.Volume+r.Price*r.Volume)/(leg.Volume+r.Volume);leg.Volume+=r.Volume;leg.Commission+=r.Commission;leg.Fee+=r.Fee;
            var buy=leg.Side=="LONG";var tick=Identity!.TickSize;
            leg.Sl=Math.Round((leg.Entry+(buy?-c.Risk:c.Risk))/tick)*tick;
            leg.Tp=Math.Round((leg.Entry+(buy?c.Risk*c.TargetR:-c.Risk*c.TargetR))/tick)*tick;
            if(history){await journal.AppendAsync(c);return;}
            if(Math.Abs(leg.Volume-c.Lots)>1e-6){c.Phase="ReconcileRequired";Reconciled=false;c.Reason="Partial/unexpected volume";await journal.AppendAsync(c);return;}
            if(leg.Closed||c.Aborted||c.Phase is "Closing" or "ReconcileRequired"){Reconciled=false;await journal.AppendAsync(c);return;}
            c.Phase="ProtectionPending";await journal.AppendAsync(c);
            await send(new(Guid.NewGuid(),c.Id,"protect",config.Symbol,leg.Side,c.Lots,c.Risk,c.TargetR,0,config.Magic,leg.Position,leg.Sl,leg.Tp));return;
        }
        if(r.Kind=="protected")
        {
            if(leg.Protected||leg.Closed||c.Phase=="Closed")return;
            if(Math.Abs(r.Sl-leg.Sl)>Identity!.TickSize/2||Math.Abs(r.Tp-leg.Tp)>Identity.TickSize/2)
            {c.Phase="ReconcileRequired";Reconciled=false;c.Reason="Protection not confirmed";await journal.AppendAsync(c);return;}
            leg.Protected=true;
            if(c.Hedge&&c.Legs.Count==1&&!leg.Closed&&c.Phase!="ReconcileRequired")
            {
                var expires=Clock.UtcNs+2_000_000_000;var cmd=OpenCommand(c,"SHORT",expires);c.Legs.Add(new Leg {Side="SHORT",CommandId=cmd.Id});
                c.Phase="Opening";await journal.AppendAsync(c);await send(cmd);
            }
            else if(c.Legs.Any(x=>x.Closed)||c.Phase=="ReconcileRequired"){c.Phase="ReconcileRequired";Reconciled=false;c.Reason="Position disappeared or establishment uncertain";await journal.AppendAsync(c);}
            else{c.Phase="Active";await journal.AppendAsync(c);}return;
        }
        if(r.Kind is "close" or "history_close")
        {
            if(c.SeenDeals.Contains(r.Deal))return;c.SeenDeals.Add(r.Deal);
            if(r.Deal==0||r.Volume<=0||!double.IsFinite(r.Volume+r.Pnl+r.Commission+r.Swap+r.Fee))throw new InvalidDataException("Invalid broker exit");
            leg.Pnl+=r.Pnl;leg.Commission+=r.Commission;leg.Swap+=r.Swap;leg.Fee+=r.Fee;leg.ExitVolume+=r.Volume;
            leg.ExitNs=r.TimeNs;
            leg.Closed=leg.Volume>0&&Math.Abs(leg.ExitVolume-leg.Volume)<1e-6;
            if(history){await journal.AppendAsync(c);return;}
            if(leg.ExitVolume>leg.Volume+1e-6){c.Phase="ReconcileRequired";c.Reason="Exit report arrived before complete entry history";Reconciled=false;await journal.AppendAsync(c);return;}
            if(!leg.Closed){c.Phase="ReconcileRequired";c.Reason="Partial exit; verify remaining volume";Reconciled=false;await journal.AppendAsync(c);return;}
            if(c.Legs.All(x=>x.Closed)&&(!c.Hedge||c.Legs.Count==2||c.Aborted)){c.Phase="Closed";c.ClosedNs=r.TimeNs;}
            else if(c.Phase=="Active")c.Phase="Survivor";
            else if(c.Phase is "Opening" or "ProtectionPending"){c.Phase="ReconcileRequired";c.Reason="Leg closed during opening";Reconciled=false;}
            await journal.AppendAsync(c);
        }
        if(r.Kind=="position")
        {
            if(leg==null)return;
            if(Math.Abs(r.Volume-(leg.Volume-leg.ExitVolume))>1e-6){c.Phase="ReconcileRequired";c.Reason="Unexpected remaining volume";Reconciled=false;await journal.AppendAsync(c);return;}
            leg.Position=r.Position;leg.Protected=Math.Abs(r.Sl-leg.Sl)<=Identity!.TickSize/2&&Math.Abs(r.Tp-leg.Tp)<=Identity.TickSize/2;
            if(!leg.Protected){c.Phase="ReconcileRequired";c.Reason="Broker protection differs";Reconciled=false;}
            else if(c.Legs.Count==(c.Hedge?2:1)&&c.Legs.All(x=>x.Protected||x.Closed))c.Phase=c.Legs.Any(x=>x.Closed)?"Survivor":"Active";
            await journal.AppendAsync(c);
        }
        if(r.Kind=="leg_reconciled")
        {
            leg.ReconcileSeen=r.Retcode==10009;
            if(!leg.ReconcileSeen||Math.Abs(r.Volume-(leg.Volume-leg.ExitVolume))>1e-6||r.Volume==0&&leg.Volume==0&&!leg.Rejected)
            {c.Phase="ReconcileRequired";c.Reason="Uncertain request or incomplete position/deal history; no retry";Reconciled=false;await journal.AppendAsync(c);return;}
            if(r.Volume>0)
            {
                leg.Position=r.Position;leg.Identifier=r.Identifier;
                leg.Protected=Math.Abs(r.Sl-leg.Sl)<=Identity!.TickSize/2&&Math.Abs(r.Tp-leg.Tp)<=Identity.TickSize/2;
            }
            else leg.Closed=true;
            if(c.Legs.Any(x=>!x.ReconcileSeen)){await journal.AppendAsync(c);return;}
            bool incomplete=c.Legs.Count!=(c.Hedge?2:1)||c.Legs.Any(x=>x.Rejected||Math.Abs(x.Volume-c.Lots)>1e-6);
            if(incomplete)
            {
                c.Aborted=true;c.Reason="Incomplete establishment reconciled; closing only the incomplete cycle";
                c.Phase=c.Legs.All(x=>x.Closed)?"Closed":"Closing";
                if(c.Phase=="Closed")c.ClosedNs=r.TimeNs;
                await journal.AppendAsync(c);
                foreach(var l in c.Legs.Where(x=>!x.Closed))await send(new(Guid.NewGuid(),c.Id,"close",config.Symbol,l.Side,l.Volume-l.ExitVolume,c.Risk,c.TargetR,0,config.Magic,l.Position));
                return;
            }
            if(c.Legs.Any(x=>!x.Closed&&!x.Protected)){c.Phase="ReconcileRequired";c.Reason="Reconciled position protection differs; manual review required";Reconciled=false;}
            else if(c.Legs.All(x=>x.Closed)){c.Phase="Closed";c.ClosedNs=r.TimeNs;}
            else c.Phase=c.Legs.Any(x=>x.Closed)?"Survivor":"Active";
            await journal.AppendAsync(c);
        }
    }
    public async Task ReconcileAsync(IReadOnlyList<BrokerReport> positions,bool complete)
    {
        // Empty/missing history is never evidence that an uncertain request did not fill.
        if(!complete){Reconciled=false;return;}
        foreach(var r in positions)await ApplyAsync(r);
        if(cycles.Values.Any(c=>c.Phase!="Closed"&&(c.Phase is "Opening" or "ProtectionPending" or "ReconcileRequired"||c.Legs.Any(l=>!l.ReconcileSeen)))){Reason="Uncertain orders need complete position/deal reconciliation";Reconciled=false;return;}
        Reconciled=true;Reason="Reconciled";
    }
    public async Task TimeExits(long now)
    {
        if(config.Family!="JD1"||Identity==null)return;
        foreach(var c in cycles.Values.Where(x=>x.Phase=="Active"&&now-x.OpenedNs>=300_000_000_000))
        {c.Phase="Closing";await journal.AppendAsync(c);foreach(var l in c.Legs.Where(x=>!x.Closed))await send(new(Guid.NewGuid(),c.Id,"close",config.Symbol,l.Side,l.Volume,c.Risk,c.TargetR,0,config.Magic,l.Position));}
    }
    public async Task FadeExit(FeatureSnapshot features,long now)
    {
        if(config.Family!="JD1"||!config.FadeExit||Identity==null||now-features.AvailableNs>250_000_000)return;
        foreach(var c in cycles.Values.Where(x=>x.Phase=="Active"&&now-x.OpenedNs>=500_000_000))
        {
            var l=c.Legs.Single();if(!fade.Evaluate(c.Id,l.Side=="LONG"?Side.Buy:Side.Sell,features))continue;
            c.Phase="Closing";c.Reason="Directional flow fade";await journal.AppendAsync(c);
            await send(new(Guid.NewGuid(),c.Id,"close",config.Symbol,l.Side,l.Volume-l.ExitVolume,c.Risk,c.TargetR,0,config.Magic,l.Position));
        }
    }
    public async Task CheckTimeouts(long now)
    {
        foreach(var c in cycles.Values.Where(c=>c.Phase is "Opening" or "ProtectionPending"))
            if(now-(c.UpdatedNs>0?c.UpdatedNs:c.OpenedNs)>5_000_000_000){c.Phase="ReconcileRequired";c.Reason="Execution report timeout; no entry retry";Reconciled=false;await journal.AppendAsync(c);}
    }
    public IEnumerable<BrokerCommand> ReconciliationCommands()
    {
        Reconciled=Active==0;
        foreach(var c in cycles.Values.Where(x=>x.Phase!="Closed"))foreach(var l in c.Legs)
        {
            l.ReconcileSeen=false;
            yield return new(Guid.NewGuid(),c.Id,"reconcile",config.Symbol,l.Side,l.Volume,c.Risk,c.TargetR,0,config.Magic,l.Position,l.Sl,l.Tp,
                ReferenceCommandId:l.CommandId,FromNs:c.OpenedNs,Identifier:l.Identifier);
        }
    }
}
