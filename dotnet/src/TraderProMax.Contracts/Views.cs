namespace TraderProMax.Contracts;
public sealed record CycleView(Guid Id,string Phase,double Net,long OpenedNs,long ClosedNs,bool Aborted=false,string Reason="");
public sealed record ProfileView(string Id,string Name,string Family,string Market,string Symbol,string Mode,
    bool Running,string State,string Reason,bool Bound,bool BrokerConnected,bool ClockReady,int Cap,int Active,
    int Completed,int Wins,int Losses,double? Balance,double? Equity,double? Floating,double Net,double Risk,
    double TargetR,double Lots,string Server,long Login,int Port,IReadOnlyList<CycleView> Cycles);
public sealed record MarketView(string Root,long Events,long Trades,long Volume,bool Connected,bool DepthReady,
    int Depth,double? Bid,double? Ask,double? QuoteAge,double? LocalMs,long Gaps,long Rejected,string HistoryReason,
    string QualityReason="",double? RejectedSourceAge=null);
public sealed record HostView(string Version,long TimeNs,IReadOnlyList<ProfileView> Profiles,IReadOnlyList<MarketView> Markets,
    long Recorded,long RecordingDrops,string? RecordingError,IReadOnlyList<ActivityView> Activity,bool OrdersEnabled);
public sealed record ActivityView(long TimeNs,string Profile,string Kind,string Message);
public sealed record WorkerView(string Profile,bool Connected,bool Armed,bool Reconciled,string Reason,
    AccountSnapshot? Account,BrokerQuote? Quote,IReadOnlyList<CycleView> Cycles);
public sealed record WorkerBroker(string Profile,BrokerQuote? Quote=null,AccountSnapshot? Account=null);
