using System.Diagnostics;
using System.Text.Json;

namespace TraderProMax.Contracts;

public enum MessageType : ushort { Hello=1, Market=2, Heartbeat=3, Signal=4, Status=5, Control=6, Ack=7, Broker=8, Command=9, Research=10 }
public enum Asset { ES=1, NQ=2 }
public enum EventKind { Quote=1, Trade=2, Depth=3, Reset=4, Health=5 }
[Flags] public enum Quality { None=0, Genuine=1, DepthEntitled=2, SequenceGap=4, Dropped=8, QuoteInvalid=16, AggressorKnown=32, Snapshot=64, SourceTimestampUnknown=128, Rejected=256 }
public enum Side { None=0, Buy=1, Sell=-1 }
public enum DepthOp { Insert=1, Update=2, Remove=3, Reset=4 }

// Prices are integer index points * 1e8; executed quantity is whole contracts.
public readonly record struct MarketEvent(Guid Epoch,long Sequence,long SourceNs,long CaptureTicks,
    long CaptureFrequency,Asset Asset,EventKind Kind,Quality Quality,long Price,long Quantity,
    long Bid,long Ask,int Side,int Position,int Operation,long ReceivedNs=0,long ReceivedTicks=0)
{
    public const long Scale=100_000_000;
    public double Mid => (Bid+Ask)/(2.0*Scale);
    public bool QuoteValid => Bid>0 && Ask>Bid;
    public bool Genuine => Quality.HasFlag(Quality.Genuine);
}

public readonly record struct CompletedBar(Asset Asset,string Clock,Guid Epoch,long Sequence,
    long SourceNs,long AvailableNs,long Volume,decimal Notional,double Quantity,double Threshold,string HistoryHash);
public sealed record SessionTotal(string Root,string Day,long Volume,decimal Dollars,bool Complete);
public sealed record ClockLimits(double Volume5,double Dollar30,string Hash,string[] Days);
public sealed record FeedHello(string Token,string Role,string Producer,string[]? Contracts=null,bool Depth=false);
public sealed record BrokerHello(string Token,long Login,string Server,string Currency,bool Demo,bool Hedging,
    string Symbol,double ContractSize,double TickSize,double MinLot,double LotStep,long Magic,double StopDistance=0,
    string TerminalPath="",bool OrdersAllowed=false);
public sealed record SignalIntent(Guid Id,string ProfileId,string RunId,Asset Asset,long Opportunity,long CreatedNs,
    long ExpiresNs,double Risk,double TargetR,double Lots,Side Side,bool Hedge,int MaxActive,string FeatureHash);
public sealed record BrokerCommand(Guid Id,Guid CycleId,string Action,string Symbol,string Side,double Lots,
    double Risk,double TargetR,long ExpiresNs,long Magic,ulong Position=0,double Sl=0,double Tp=0,long DeadlineBootMs=0,
    Guid ReferenceCommandId=default,long FromNs=0,bool Hedge=false,int ReserveLegs=1,ulong Identifier=0);
public sealed record BrokerReport(string Kind,Guid CommandId,Guid CycleId,long RequestId,ulong Order,ulong Deal,
    ulong Position,string Side,double Volume,double Price,double Sl,double Tp,double Pnl,double Commission,
    double Swap,double Fee,long TimeNs,int Retcode,string Reason,ulong Identifier=0);
public sealed record BrokerQuote(double Bid,double Ask,long SourceNs,long ReceivedNs);
public sealed record AccountSnapshot(long Login,string Server,double Balance,double Equity,double FreeMargin,
    double Margin,bool Connected,int Positions,long TimeNs,int Orders=0,int ForeignPositions=0);
public sealed record ControlRequest(Guid Id,string Action,string ProfileId,JsonElement? Value=null);
public sealed record ControlReply(Guid Id,bool Success,string Message);
public static class Clock
{
    public static long UtcNs => (DateTime.UtcNow.Ticks-DateTime.UnixEpoch.Ticks)*100;
    public static long Mono => Stopwatch.GetTimestamp();
    public static double Milliseconds(long ticks) => ticks*1000.0/Stopwatch.Frequency;
}
public static class Json
{
    public static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web) { WriteIndented=false, NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,Options);
    public static T Read<T>(ReadOnlySpan<byte> bytes)=>JsonSerializer.Deserialize<T>(bytes,Options) ?? throw new InvalidDataException("Empty message");
}
