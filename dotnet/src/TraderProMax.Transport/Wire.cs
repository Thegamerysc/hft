using System.Buffers.Binary;
using System.Net.Sockets;
using TraderProMax.Contracts;

namespace TraderProMax.Transport;

public readonly record struct Frame(MessageType Type,byte[] Payload);
public static class Wire
{
    public const uint Magic=0x32504D54;
    public const int MaxPayload=1_048_576;
    public const int MarketSize=120;
    public static byte[] EncodeMarket(in MarketEvent e)
    {
        var b=new byte[MarketSize];var s=b.AsSpan();
        e.Epoch.TryWriteBytes(s); Put64(s,16,e.Sequence);Put64(s,24,e.SourceNs);Put64(s,32,e.CaptureTicks);Put64(s,40,e.CaptureFrequency);
        Put32(s,48,(int)e.Asset);Put32(s,52,(int)e.Kind);Put32(s,56,(int)e.Quality);
        Put64(s,64,e.Price);Put64(s,72,e.Quantity);Put64(s,80,e.Bid);Put64(s,88,e.Ask);
        Put32(s,96,e.Side);Put32(s,100,e.Position);Put32(s,104,e.Operation);Put64(s,112,e.ReceivedNs);return b;
    }
    public static MarketEvent DecodeMarket(ReadOnlySpan<byte> s,long nowNs=0,long nowTicks=0)
    {
        if(s.Length!=MarketSize)throw new InvalidDataException("Market payload size");
        var asset=(Asset)Get32(s,48);var kind=(EventKind)Get32(s,52);
        if(!Enum.IsDefined(asset)||!Enum.IsDefined(kind))throw new InvalidDataException("Unknown market/event");
        return new(new Guid(s[..16]),Get64(s,16),Get64(s,24),Get64(s,32),Get64(s,40),asset,kind,(Quality)Get32(s,56),
            Get64(s,64),Get64(s,72),Get64(s,80),Get64(s,88),Get32(s,96),Get32(s,100),Get32(s,104),nowNs==0?Get64(s,112):nowNs,nowTicks);
    }
    public static async ValueTask WriteAsync(Stream stream,MessageType type,ReadOnlyMemory<byte> payload,CancellationToken ct=default)
    {
        if(payload.Length>MaxPayload)throw new InvalidDataException("Frame too large");
        var h=new byte[12];BinaryPrimitives.WriteInt32LittleEndian(h,payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4),Magic);BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8),1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10),(ushort)type);
        await stream.WriteAsync(h,ct);await stream.WriteAsync(payload,ct);
    }
    public static async ValueTask<Frame?> ReadAsync(Stream stream,CancellationToken ct=default)
    {
        var h=new byte[12];var first=await stream.ReadAsync(h.AsMemory(0,1),ct);if(first==0)return null;
        await stream.ReadExactlyAsync(h.AsMemory(1),ct);
        var length=BinaryPrimitives.ReadInt32LittleEndian(h);
        if(length<0||length>MaxPayload||BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4))!=Magic||BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(8))!=1)
            throw new InvalidDataException("Invalid frame header");
        var type=(MessageType)BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(10));if(!Enum.IsDefined(type))throw new InvalidDataException("Unknown message type");
        var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,ct);return new Frame(type,bytes);
    }
    public static TcpClient Client()=>new() { NoDelay=true };
    static void Put64(Span<byte> s,int n,long v)=>BinaryPrimitives.WriteInt64LittleEndian(s[n..],v);
    static long Get64(ReadOnlySpan<byte> s,int n)=>BinaryPrimitives.ReadInt64LittleEndian(s[n..]);
    static void Put32(Span<byte> s,int n,int v)=>BinaryPrimitives.WriteInt32LittleEndian(s[n..],v);
    static int Get32(ReadOnlySpan<byte> s,int n)=>BinaryPrimitives.ReadInt32LittleEndian(s[n..]);
}
