using TraderProMax.Contracts;

namespace TraderProMax.Core;

public readonly record struct FeatureSnapshot(long AvailableNs,double Mid,double Tape1,double Tape5,double Tape20,
    double Volume1,double Volume5,double Volume20,double Intensity,double VarianceRatio,double Obi5,double BidDepth5,
    double AskDepth5,double Basis,double BasisResidual,bool DepthReady,bool BrokerReady,double Spread,double QuoteAge,
    int Replenishments,double PriceProgress,int UnknownTrades,double PriorBidMedian5=double.NaN,double PriorAskMedian5=double.NaN)
{
    public double[] Vector()=>[Tape1,Tape5,Tape20,Volume1,Volume5,Volume20,Intensity,VarianceRatio,Obi5,BasisResidual,Spread,PriceProgress];
    public static readonly string[] Names=["tape1","tape5","tape20","volume1","volume5","volume20","intensity","varianceRatio","obi5","basisResidual","spread","priceProgress"];
}
public sealed class FeatureWindow
{
    struct Bin {public long Slot;public double Volume,Signed,SquareReturn;public int Count,Unknown;}
    readonly Bin[] bins=new Bin[220];
    readonly (long Slot,double Bid,double Ask)[] depths=new (long,double,double)[51];
    long medianSlot;double bidReference=double.NaN,askReference=double.NaN;
    MarketEvent? quote;BrokerQuote? broker;double lastTrade,basis;bool basisReady;int lastReplenishments;
    public void Reset(){Array.Clear(bins);Array.Clear(depths);quote=null;lastTrade=0;basisReady=false;lastReplenishments=0;medianSlot=0;bidReference=double.NaN;askReference=double.NaN;}
    public void Broker(BrokerQuote q,long now)
    {
        if(q.Bid<=0||q.Ask<=q.Bid||q.SourceNs>q.ReceivedNs+250_000_000)return;
        broker=q;
        if(quote is {} f&&now-f.ReceivedNs<=2_000_000_000&&now-q.ReceivedNs<=2_000_000_000)
        {double b=f.Mid-(q.Bid+q.Ask)/2;basis=basisReady?basis*.995+b*.005:b;basisReady=true;}
    }
    public void Add(in MarketEvent e)
    {
        if(e.Kind==EventKind.Reset||(e.Quality&(Quality.SequenceGap|Quality.Dropped))!=0){Reset();return;}
        if(e.QuoteValid)quote=e;
        if(e.Kind!=EventKind.Trade)return;
        long slot=e.ReceivedNs/100_000_000;int i=(int)(slot%bins.Length);ref var b=ref bins[i];if(b.Slot!=slot)b=new Bin {Slot=slot};
        double p=e.Price/(double)MarketEvent.Scale;int side=e.Side;
        if(!e.Quality.HasFlag(Quality.AggressorKnown))side=e.QuoteValid?(e.Price>=e.Ask?1:e.Price<=e.Bid?-1:0):0;
        b.Volume+=e.Quantity;b.Signed+=side*e.Quantity;b.Count++;if(side==0)b.Unknown++;
        if(lastTrade>0)b.SquareReturn+=Math.Pow(p-lastTrade,2);lastTrade=p;
    }
    public FeatureSnapshot Snapshot(long now,DepthBook book)
    {
        double v1=0,v5=0,v20=0,s1=0,s5=0,s20=0,rv1=0,rv20=0;int count=0,unknown=0;long slot=now/100_000_000;
        foreach(var b in bins)
        {
            long age=slot-b.Slot;if(age<0||age>=200)continue;v20+=b.Volume;s20+=b.Signed;rv20+=b.SquareReturn;unknown+=b.Unknown;
            if(age<50){v5+=b.Volume;s5+=b.Signed;count+=b.Count;}if(age<10){v1+=b.Volume;s1+=b.Signed;rv1+=b.SquareReturn;}
        }
        var q=quote;var br=broker;bool brReady=br is {} bq&&now-bq.ReceivedNs<=2_000_000_000&&now-bq.SourceNs<=2_000_000_000;
        double mid=q?.Mid??0,spread=br is {} spreadQuote?(spreadQuote.Ask-spreadQuote.Bid):double.NaN,ageQ=br is {} b2?(now-b2.SourceNs)/1e9:double.NaN;
        double residual=basisReady&&br is {} b3?mid-(b3.Bid+b3.Ask)/2-basis:double.NaN;
        bool depth=book.Ready&&now-book.LastNs<=2_000_000_000;
        if(slot!=medianSlot)
        {
            medianSlot=slot;var previous=depths.Where(d=>slot-d.Slot is >=1 and <=50).ToArray();
            double Median(IEnumerable<double> values){var a=values.Order().ToArray();return a.Length<40?double.NaN:(a[(a.Length-1)/2]+a[a.Length/2])/2;}
            bidReference=Median(previous.Select(x=>x.Bid));askReference=Median(previous.Select(x=>x.Ask));
        }
        if(depth)depths[(int)(slot%depths.Length)]=(slot,book.Size(Side.Buy,5),book.Size(Side.Sell,5));
        int replenish=book.Replenishments-lastReplenishments;lastReplenishments=book.Replenishments;
        return new(now,mid,v1>0?s1/v1:0,v5>0?s5/v5:0,v20>0?s20/v20:0,v1,v5,v20,count/5.0,
            rv20>0?rv1/(rv20/20):0,depth?book.Imbalance(5):double.NaN,book.Size(Side.Buy,5),book.Size(Side.Sell,5),
            basisReady?basis:double.NaN,residual,depth,brReady,spread,ageQ,replenish,lastTrade-mid,unknown,bidReference,askReference);
    }
}

public sealed class JointModel
{
    public string Schema {get;init;}="joint-v1";
    public string TrainedThrough {get;init;}="";
    public string[] Names {get;init;}=[];
    public double[] Means {get;init;}=[];
    public double[] Scales {get;init;}=[];
    public double[] Weights {get;init;}=[];
    public double Intercept {get;init;}
    public double Threshold {get;init;}=.5;
    public string Hash {get;init;}="";
    public double Activity80 {get;init;}
    public double Intensity80 {get;init;}
    public void Validate()
    {
        if(Schema!="joint-v1"||!Names.SequenceEqual(FeatureSnapshot.Names)||Means.Length!=Names.Length||Scales.Length!=Names.Length||Weights.Length!=Names.Length||
           Scales.Any(x=>!double.IsFinite(x)||x<=0)||Weights.Concat(Means).Any(x=>!double.IsFinite(x))||string.IsNullOrEmpty(TrainedThrough)||string.IsNullOrEmpty(Hash)||Threshold<0||Threshold>1)
            throw new InvalidDataException("Joint model manifest/schema invalid");
    }
    public double Predict(in FeatureSnapshot f)
    {
        var x=f.Vector();if(x.Any(v=>!double.IsFinite(v)))return double.NaN;
        double score=Intercept;for(int i=0;i<x.Length;i++)score+=(x[i]-Means[i])/Scales[i]*Weights[i];return 1/(1+Math.Exp(-Math.Clamp(score,-40,40)));
    }
}

public sealed class JointSetup
{
    long level,armedNs,confirmedNs,lastSignalLevel;Side direction;int replenishments;long quantity;
    readonly Queue<(long Ns,long Quantity,int Replenishments)> window=[];
    public string State {get;private set;}="Waiting for verified depth";
    public void Reset(){level=0;armedNs=0;confirmedNs=0;replenishments=0;quantity=0;lastSignalLevel=0;window.Clear();State="Continuity reset";}
    public Side Evaluate(in MarketEvent e,in FeatureSnapshot f,DepthBook book,JointModel? calibration)
    {
        if(!f.DepthReady||!f.BrokerReady||calibration==null||calibration.Activity80<=0||calibration.Intensity80<=0||
            string.CompareOrdinal(calibration.TrainedThrough,DateTimeOffset.FromUnixTimeMilliseconds(f.AvailableNs/1_000_000).ToString("yyyy-MM-dd"))>=0)
        {Reset();State="Depth / broker alignment / past calibration missing";return Side.None;}
        long now=e.ReceivedNs,tick=e.Asset==Asset.ES?25_000_000:25_000_000;
        if(level==0||now-armedNs>5_000_000_000)
        {
            direction=f.Tape1>=0?Side.Buy:Side.Sell;
            level=book.Best(direction==Side.Buy?Side.Sell:Side.Buy)?.Price??0;
            armedNs=now;quantity=0;replenishments=0;confirmedNs=0;window.Clear();State="Watching persistent level";
        }
        int adds=e.Kind==EventKind.Depth&&Math.Abs(e.Price-level)<=tick?Math.Max(0,f.Replenishments):0;
        long consumed=e.Kind==EventKind.Trade&&Math.Abs(e.Price-level)<=tick?e.Quantity:0;
        if(adds>0||consumed>0){window.Enqueue((now,consumed,adds));quantity+=consumed;replenishments+=adds;}
        while(window.TryPeek(out var w)&&now-w.Ns>2_000_000_000){window.Dequeue();quantity-=w.Quantity;replenishments-=w.Replenishments;}
        if(e.Kind!=EventKind.Trade)return Side.None;
        int sign=(int)direction;
        bool setup=replenishments>=3&&quantity>=calibration.Activity80&&now-armedNs>=500_000_000;
        bool breakout=(e.Price-level)*sign>=2*tick&&f.Tape1*sign>=.2&&f.Intensity>=calibration.Intensity80;
        if(!setup||!breakout){confirmedNs=0;return Side.None;}
        if(confirmedNs==0){confirmedNs=now;State="Confirming breakout";return Side.None;}
        if(now-confirmedNs<200_000_000||lastSignalLevel==level)return Side.None;
        lastSignalLevel=level;State="Confirmed";return direction;
    }
}

// Only the registered directional arm uses this exit; A0 survivors retain fixed SL/TP.
public sealed class FadeDetector
{
    Guid current;long since;
    public bool Evaluate(Guid cycle,Side direction,in FeatureSnapshot f)
    {
        if(current!=cycle){current=cycle;since=0;}
        double size=direction==Side.Buy?f.BidDepth5:f.AskDepth5;
        double reference=direction==Side.Buy?f.PriorBidMedian5:f.PriorAskMedian5;
        bool fading=f.DepthReady&&f.BrokerReady&&double.IsFinite(reference)&&reference>0&&
            f.Tape5*(int)direction<=-.2&&size<reference*.5;
        if(!fading){since=0;return false;}
        if(since==0)since=f.AvailableNs;
        return f.AvailableNs-since>=1_000_000_000;
    }
}
