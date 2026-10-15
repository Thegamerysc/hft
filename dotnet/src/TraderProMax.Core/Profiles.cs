using TraderProMax.Contracts;

namespace TraderProMax.Core;

public sealed record ProfileConfig
{
    public string Id {get;init;}="";public string Name {get;init;}="";public Asset Asset {get;init;}=Asset.ES;
    public string Clock {get;init;}="volume5";public int MaxActive {get;init;}=1;public double Risk {get;init;}=20;
    public double TargetR {get;init;}=1.16;public double Lots {get;init;}=.2;public long Magic {get;init;}
    public int WorkerPort {get;init;}=8901;public string Family {get;init;}="A0";public long Login {get;init;}
    public string Server {get;init;}="";public string TerminalPath {get;init;}="";public string Symbol {get;init;}="US500m";
    public string Execution {get;init;}="shadow";public bool FadeExit {get;init;}
    public string AccountType {get;init;}="standard";
    public string ModelPath {get;init;}="";
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(Id)||!Enum.IsDefined(Asset)||MaxActive<1||MaxActive>3||Risk!=20||TargetR!=1.16||Lots!=.2||
            Clock is not ("volume5" or "dollar30" or "event")||Family is not ("A0" or "JH1" or "JD1")||Execution is not ("shadow" or "demo")||AccountType is not ("standard" or "raw")||WorkerPort is <1024 or >65535)
            throw new InvalidDataException("Profile definition differs from registered experiment");
        if(Family!="JD1"&&FadeExit)throw new InvalidDataException("Hedge profiles have no early exit");
        var expected=Id switch {"a0-volume-3"=>("A0","volume5",3),"a0-dollar-1"=>("A0","dollar30",1),"a0-volume-1"=>("A0","volume5",1),"joint-hedge-jh1"=>("JH1","volume5",1),"joint-directional-jd1"=>("JD1","event",1),_=>throw new InvalidDataException("Unknown registered profile")};
        if(Family!=expected.Item1||Clock!=expected.Item2||MaxActive!=expected.Item3)throw new InvalidDataException("Profile family/clock/cap is frozen");
        if(Asset==Asset.ES&&Symbol is not ("US500m" or "US500" or "US500r")||Asset==Asset.NQ&&Symbol is not ("USTECm" or "USTECr" or "USTEC"))throw new InvalidDataException("Market/symbol mismatch");
    }
}

public sealed class Policy(ProfileConfig config,JointModel? model)
{
    public string Reason {get;private set;}="Stopped";
    public SignalIntent? OnBar(in CompletedBar bar,in FeatureSnapshot f,bool running,bool session,int active)
    {
        if(!running){Reason="Stopped";return null;}
        if(!session){Reason="Outside entry session";return null;}
        if(active>=config.MaxActive){Reason="Inventory cap";return null;}
        if(!f.BrokerReady){Reason="Awaiting broker quotes";return null;}
        if(config.Family=="JH1")
        {
            if(!f.DepthReady||model==null){Reason="Verified depth and frozen model required";return null;}
            if(string.CompareOrdinal(model.TrainedThrough,DateTimeOffset.FromUnixTimeMilliseconds(f.AvailableNs/1_000_000).ToString("yyyy-MM-dd"))>=0){Reason="Model training cutoff is not in the past";return null;}
            var score=model.Predict(f);if(!double.IsFinite(score)||score<model.Threshold){Reason="Joint model selected SKIP";return null;}
        }
        Reason="Eligible";
        return new(Guid.NewGuid(),config.Id,"",bar.Asset,bar.Sequence,f.AvailableNs,f.AvailableNs+250_000_000,config.Risk,config.TargetR,config.Lots,Side.None,true,config.MaxActive,model?.Hash??"fixed-a0-v1");
    }
}
