using System.Net;
using System.Net.Sockets;
using System.Text;
using TraderProMax.Contracts;
using TraderProMax.Transport;

namespace TraderProMax.Web;
public sealed class MarketLink(string root):BackgroundService
{
    public HostView View {get;private set;}=new("2.0 C#",0,[],[],0,0,null,[],false);
    public event Action? Changed;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var bridge=Workspace.Bridge(root);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var c=Wire.Client();await c.ConnectAsync(IPAddress.Loopback,bridge.Port,ct);var s=c.GetStream();
                await Wire.WriteAsync(s,MessageType.Hello,Json.Bytes(new FeedHello(bridge.Token,"observer","dashboard")),ct);await Wire.ReadAsync(s,ct);
                while(await Wire.ReadAsync(s,ct) is {} f)if(f.Type==MessageType.Status){View=Json.Read<HostView>(f.Payload);Changed?.Invoke();}
            }
            catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException){if(ct.IsCancellationRequested)break;}
            await Task.Delay(1000,ct);
        }
    }
    public async Task<ControlReply> Control(string action,string profile,object? value=null)
    {
        var bridge=Workspace.Bridge(root);using var timeout=new CancellationTokenSource(5000);using var c=Wire.Client();await c.ConnectAsync(IPAddress.Loopback,bridge.Port,timeout.Token);var s=c.GetStream();
        await Wire.WriteAsync(s,MessageType.Hello,Json.Bytes(new FeedHello(bridge.Token,"control","dashboard")),timeout.Token);await Wire.ReadAsync(s,timeout.Token);
        var command=new ControlRequest(Guid.NewGuid(),action,profile,value==null?null:System.Text.Json.JsonSerializer.SerializeToElement(value,Json.Options));
        await Wire.WriteAsync(s,MessageType.Control,Json.Bytes(command),timeout.Token);var f=await Wire.ReadAsync(s,timeout.Token);return f is {} reply?Json.Read<ControlReply>(reply.Payload):throw new IOException("Control disconnected");
    }
    public byte[] Export()
    {
        var sb=new StringBuilder("profile,market,mode,cycle_id,status,opened_utc,closed_utc,net_pnl_usd\r\n");
        foreach(var p in View.Profiles)foreach(var c in p.Cycles)
            sb.AppendLine($"{p.Id},{p.Market},{p.Mode},{c.Id},{c.Phase},{Stamp(c.OpenedNs)},{Stamp(c.ClosedNs)},{c.Net.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
    static string Stamp(long ns)=>ns>0?DateTimeOffset.FromUnixTimeMilliseconds(ns/1_000_000).ToString("O"):"";
}
