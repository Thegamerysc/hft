using TraderProMax.Contracts;

namespace TraderProMax.Core;
// Broker prices/basis belong to one account/profile, even when futures features are shared.
public sealed class BrokerAlignment
{
    BrokerQuote? quote;double basis;bool ready;
    public void Reset(){quote=null;basis=0;ready=false;}
    public void Add(BrokerQuote q,MarketEvent? futures,long now)
    {
        if(q.Bid<=0||q.Ask<=q.Bid||q.SourceNs>now+250_000_000||now-q.SourceNs>2_000_000_000||now-q.ReceivedNs>2_000_000_000)return;
        quote=q;
        if(futures is {} f&&f.QuoteValid&&now-f.ReceivedNs<=2_000_000_000&&now-f.SourceNs<=2_000_000_000)
        {double b=f.Mid-(q.Bid+q.Ask)/2;basis=ready?basis*.995+b*.005:b;ready=true;}
    }
    public FeatureSnapshot Apply(in FeatureSnapshot common,long now)
    {
        bool valid=quote is {} q&&now-q.ReceivedNs<=2_000_000_000&&now-q.SourceNs<=2_000_000_000;
        return common with {BrokerReady=valid,Spread=quote is {} s?s.Ask-s.Bid:double.NaN,QuoteAge=quote is {} t?(now-t.SourceNs)/1e9:double.NaN,
            Basis=ready?basis:double.NaN,BasisResidual=ready&&quote is {} b?common.Mid-(b.Bid+b.Ask)/2-basis:double.NaN};
    }
}
