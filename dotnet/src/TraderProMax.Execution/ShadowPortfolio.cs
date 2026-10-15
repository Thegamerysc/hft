using TraderProMax.Contracts;
using TraderProMax.Core;

namespace TraderProMax.Execution;

public sealed class ShadowPortfolio
{
    readonly Dictionary<Guid,Cycle> cycles=[];readonly HashSet<Guid> signals=[];double balance;readonly ProfileConfig config;readonly string configHash;
    readonly FadeDetector fade=new();
    public ShadowPortfolio(ProfileConfig config,double initial=2000,IEnumerable<Cycle>? restored=null)
    {
        this.config=config;configHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Json.Bytes(config)));balance=initial;
        foreach(var c in (restored??[]).GroupBy(x=>x.Id).Select(x=>x.Last()))
        {
            if(c.Phase!="Closed"&&c.ConfigHash!=configHash)throw new InvalidDataException("Active shadow portfolio belongs to another configuration");
            cycles[c.Id]=c;signals.Add(c.SignalId);balance+=c.Net;
        }
    }
    public IReadOnlyCollection<Cycle> Cycles=>cycles.Values;public double Balance=>balance;public int Active=>cycles.Values.Count(c=>c.Phase!="Closed");
    double Commission=>config.AccountType=="raw"?(config.Asset==Asset.ES?.125:.3125):0;
    public bool Enter(SignalIntent s,BrokerQuote quote,long now)
    {
        if(s.ProfileId!=config.Id||s.Asset!=config.Asset||s.Risk!=config.Risk||s.TargetR!=config.TargetR||s.Lots!=config.Lots||s.MaxActive!=config.MaxActive||s.Hedge!=(config.Family!="JD1")||!s.Hedge&&s.Side==Side.None||
           s.ExpiresNs<now||Active>=config.MaxActive||signals.Contains(s.Id)||quote.Ask<=quote.Bid||quote.Bid<=0||now-quote.ReceivedNs>2_000_000_000||now-quote.SourceNs>2_000_000_000||quote.SourceNs>now+250_000_000)return false;
        signals.Add(s.Id);var c=new Cycle {Id=Guid.NewGuid(),SignalId=s.Id,Profile=config.Id,Risk=s.Risk,TargetR=s.TargetR,Lots=s.Lots,Hedge=s.Hedge,OpenedNs=now,Phase="Active",ConfigHash=configHash,RunId=s.RunId};
        if(s.Hedge||s.Side==Side.Buy)c.Legs.Add(Create("LONG",quote.Ask,s));
        if(s.Hedge||s.Side==Side.Sell)c.Legs.Add(Create("SHORT",quote.Bid,s));
        cycles[c.Id]=c;balance+=c.Legs.Sum(l=>l.Commission);return true;
    }
    Leg Create(string side,double entry,SignalIntent s)
    {
        int sign=side=="LONG"?1:-1;
        return new Leg {Side=side,Entry=entry,Volume=s.Lots,Sl=entry-sign*s.Risk,Tp=entry+sign*s.Risk*s.TargetR,Protected=true,Commission=-s.Lots*Commission};
    }
    public void Mark(BrokerQuote q,long now)
    {
        if(q.Bid<=0||q.Ask<=q.Bid)return;
        foreach(var c in cycles.Values.Where(c=>c.Phase!="Closed"))
        {
            foreach(var l in c.Legs.Where(l=>!l.Closed))
            {
                bool buy=l.Side=="LONG";double price=buy?q.Bid:q.Ask;
                bool sl=buy?price<=l.Sl:price>=l.Sl;bool tp=buy?price>=l.Tp:price<=l.Tp;
                bool time=config.Family=="JD1"&&now-c.OpenedNs>=300_000_000_000;
                if(sl||tp||time){double exit=tp?l.Tp:price;Settle(l,exit);}
            }
            if(c.Legs.All(l=>l.Closed)){c.Phase="Closed";c.ClosedNs=now;}
            else if(c.Legs.Any(l=>l.Closed))c.Phase="Survivor";
        }
    }
    void Settle(Leg l,double exit)
    {l.Pnl=(exit-l.Entry)*(l.Side=="LONG"?1:-1)*l.Volume;l.Commission-=l.Volume*Commission;l.Closed=true;l.ExitVolume=l.Volume;balance+=l.Pnl-l.Volume*Commission;}
    public void FadeExit(FeatureSnapshot f,BrokerQuote q,long now)
    {
        if(config.Family!="JD1"||!config.FadeExit||now-f.AvailableNs>250_000_000||now-q.ReceivedNs>2_000_000_000)return;
        foreach(var c in cycles.Values.Where(x=>x.Phase=="Active"&&now-x.OpenedNs>=500_000_000))
        {var l=c.Legs.Single();if(!fade.Evaluate(c.Id,l.Side=="LONG"?Side.Buy:Side.Sell,f))continue;Settle(l,l.Side=="LONG"?q.Bid:q.Ask);c.Phase="Closed";c.ClosedNs=now;c.Reason="Directional flow fade";}
    }
    public double Equity(BrokerQuote? q)=>q is null?balance:balance+cycles.Values.SelectMany(c=>c.Legs).Where(l=>!l.Closed).Sum(l=>((l.Side=="LONG"?q.Bid:q.Ask)-l.Entry)*(l.Side=="LONG"?1:-1)*l.Volume);
}
