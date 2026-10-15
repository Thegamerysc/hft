using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TraderProMax.Contracts;

namespace TraderProMax.Core;

public sealed record CashSession(string Day,long OpenNs,long CloseNs);
public sealed class TradingCalendar(IEnumerable<CashSession> sessions)
{
    readonly SortedDictionary<string,CashSession> days=new(sessions.ToDictionary(x=>x.Day),StringComparer.Ordinal);
    readonly CashSession[] ordered=sessions.OrderBy(x=>x.OpenNs).ToArray();
    public CashSession? At(long ns)
    {
        int lo=0,hi=ordered.Length-1;
        while(lo<=hi){int m=(lo+hi)/2;if(ns<ordered[m].OpenNs)hi=m-1;else if(ns>=ordered[m].CloseNs)lo=m+1;else return ordered[m];}
        return null;
    }
    public bool Entries(long ns)=>At(ns) is {} s&&ns>=s.OpenNs+300_000_000_000&&ns<s.CloseNs-900_000_000_000;
    public string Previous(string day)=>days.Keys.LastOrDefault(x=>string.CompareOrdinal(x,day)<0) ?? throw new InvalidDataException("Calendar history missing");
    public string Next(string day)=>days.Keys.FirstOrDefault(x=>string.CompareOrdinal(x,day)>0) ?? throw new InvalidDataException("Calendar future missing");
    public bool FullSession(string day)=>days.TryGetValue(day,out var s)&&s.CloseNs-s.OpenNs==23_400_000_000_000;
    public CashSession[] Prior(string day,int count)=>ordered.Where(x=>string.CompareOrdinal(x.Day,day)<0).TakeLast(count).ToArray();
    public static TradingCalendar Load(string path)=>new(Json.Read<CashSession[]>(File.ReadAllBytes(path)));
}

public static class Thresholds
{
    public static ClockLimits Calculate(IEnumerable<SessionTotal> history,Asset asset,string day,string previous)
    {
        var rows=history.Where(x=>x.Root==asset.ToString()&&x.Complete&&x.Volume>0&&x.Dollars>0&&string.CompareOrdinal(x.Day,day)<0)
            .OrderBy(x=>x.Day,StringComparer.Ordinal).ToArray();
        if(rows.GroupBy(x=>x.Day).Any(g=>g.Count()!=1))throw new InvalidDataException("Duplicate session totals");
        if(rows.Length<20||rows[^1].Day!=previous)throw new InvalidDataException("20 complete prior sessions including latest required");
        var last=rows[^20..];var v=last.Select(x=>(double)x.Volume).Order().ToArray();var d=last.Select(x=>(double)x.Dollars).Order().ToArray();
        var canonical=string.Join("|",last.Select(x=>$"{x.Root}:{x.Day}:{x.Volume}:{x.Dollars.ToString(CultureInfo.InvariantCulture)}"));
        return new((v[9]+v[10])*.5*5/23400,(d[9]+d[10])*.5*30/23400,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),last.Select(x=>x.Day).ToArray());
    }
}

public sealed class PrintClock(Asset asset,string kind,ClockLimits limits,TradingCalendar calendar)
{
    double quantity;long volume,lastSource;decimal notional;Guid epoch;
    public int ResetCount {get;private set;}
    public void Reset(){quantity=0;volume=0;notional=0;lastSource=0;epoch=Guid.Empty;ResetCount++;}
    public CompletedBar? Add(in MarketEvent e)
    {
        if(e.Asset!=asset)return null;
        if(e.Kind==EventKind.Reset||(e.Quality&(Quality.SequenceGap|Quality.Dropped))!=0){Reset();return null;}
        if(e.Kind!=EventKind.Trade||!e.Genuine||e.Quantity<=0||e.Price<=0||calendar.At(e.SourceNs)==null)return null;
        if(lastSource>0&&(e.SourceNs<lastSource||e.SourceNs-lastSource>5_000_000_000||e.Epoch!=epoch||calendar.At(lastSource)?.Day!=calendar.At(e.SourceNs)?.Day))Reset();
        lastSource=e.SourceNs;epoch=e.Epoch;volume=checked(volume+e.Quantity);
        var dollars=(decimal)e.Price/MarketEvent.Scale*e.Quantity*(asset==Asset.ES?50:20);notional+=dollars;
        quantity+=kind=="dollar30"?(double)dollars:e.Quantity;
        var limit=kind=="dollar30"?limits.Dollar30:limits.Volume5;if(quantity<limit)return null;
        var bar=new CompletedBar(asset,kind,e.Epoch,e.Sequence,e.SourceNs,e.ReceivedNs,volume,notional,quantity,limit,limits.Hash);
        quantity=0;volume=0;notional=0;return bar;
    }
}

public sealed class DepthBook
{
    public readonly record struct Level(long Price,long Size);
    readonly List<Level> bids=[];readonly List<Level> asks=[];
    public bool Ready {get;private set;}
    public int Depth=>Math.Min(bids.Count,asks.Count);
    public long LastNs {get;private set;}
    public int Replenishments {get;private set;}
    public void Reset(){bids.Clear();asks.Clear();Ready=false;LastNs=0;Replenishments=0;}
    public bool Apply(in MarketEvent e)
    {
        if(e.Kind==EventKind.Reset||(e.Quality&(Quality.SequenceGap|Quality.Dropped))!=0){Reset();return false;}
        if(e.Kind!=EventKind.Depth)return false;
        if(!e.Genuine||!e.Quality.HasFlag(Quality.DepthEntitled)){Reset();return false;}
        var levels=e.Side==(int)Side.Buy?bids:asks;var pos=e.Position;
        if(pos<0||pos>1000){Reset();return false;}
        switch((DepthOp)e.Operation)
        {
            case DepthOp.Reset:Reset();return false;
            case DepthOp.Insert:if(pos>levels.Count){Reset();return false;}levels.Insert(pos,new(e.Price,e.Quantity));break;
            case DepthOp.Update:if(pos>=levels.Count){Reset();return false;}if(levels[pos].Price==e.Price&&e.Quantity>levels[pos].Size)Replenishments++;levels[pos]=new(e.Price,e.Quantity);break;
            case DepthOp.Remove:if(pos>=levels.Count){Reset();return false;}levels.RemoveAt(pos);break;
            default:Reset();return false;
        }
        if(!e.Quality.HasFlag(Quality.Snapshot))LastNs=e.ReceivedNs;
        Ready=bids.Count>0&&asks.Count>0&&bids[0].Price>0&&bids[0].Price<asks[0].Price&&
            bids.Zip(bids.Skip(1)).All(x=>x.First.Price>x.Second.Price)&&asks.Zip(asks.Skip(1)).All(x=>x.First.Price<x.Second.Price);
        if(!Ready&&bids.Count>0&&asks.Count>0&&bids[0].Price>=asks[0].Price)return false;
        return Ready;
    }
    public Level? Best(Side side)=>(side==Side.Buy?bids:asks).FirstOrDefault() is var l&&l.Price>0?l:null;
    public long Size(Side side,int n)=>(side==Side.Buy?bids:asks).Take(n).Sum(x=>x.Size);
    public double Imbalance(int n){double b=Size(Side.Buy,n),a=Size(Side.Sell,n);return b+a>0?(b-a)/(b+a):double.NaN;}
}

public sealed class FeedQuality
{
    readonly Dictionary<Guid,long> sequence=[];
    public long Gaps {get;private set;}
    public long Rejected {get;private set;}
    public string Reason {get;private set;}="";
    public double? RejectedSourceAge {get;private set;}
    public bool Accept(in MarketEvent e,long now,out MarketEvent normalized)
    {
        normalized=e;
        string problem=e.Sequence<=0||e.Epoch==Guid.Empty?"Invalid sequence/epoch":!e.Genuine?"Artificial/unverified feed":
            e.SourceNs<=0||e.ReceivedNs<=0?"Missing timestamp":e.SourceNs>now+250_000_000?"Source timestamp in future":
            now-e.SourceNs>2_000_000_000?"Source timestamp stale":e.Kind==EventKind.Trade&&(e.Quantity<=0||e.Price<=0||e.Quality.HasFlag(Quality.SourceTimestampUnknown))?"Invalid executed print":"";
        if(problem.Length>0){Rejected++;Reason=problem+" · "+e.Kind;RejectedSourceAge=(now-e.SourceNs)/1e9;return false;}
        if(sequence.TryGetValue(e.Epoch,out var prev))
        {
            if(e.Sequence<=prev){Rejected++;Reason="Duplicate/out-of-order sequence";RejectedSourceAge=(now-e.SourceNs)/1e9;return false;}
            if(e.Sequence!=prev+1){Gaps++;normalized=e with {Quality=e.Quality|Quality.SequenceGap};}
        }
        sequence[e.Epoch]=e.Sequence;
        if(sequence.Count>32){var latest=sequence[e.Epoch];sequence.Clear();sequence[e.Epoch]=latest;}
        return true;
    }
}
