using TraderProMax.Core;
using TraderProMax.Contracts;
using TraderProMax.Transport;

namespace TraderProMax.Web;
public sealed class ShadowBootstrap(string root,ServiceSupervisor supervisor,MarketLink market):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(market.View.TimeNs==0)await Task.Delay(200,ct);
        var profiles=Json.Read<ProfileConfig[]>(File.ReadAllBytes(Path.Combine(Workspace.Runtime(root),"profiles.json")));
        foreach(var cfg in profiles.Where(p=>p.Execution=="shadow"))
        {
            try{await supervisor.Worker(cfg.Id);await market.Control("start",cfg.Id);}
            catch(Exception ex) when(ex is IOException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException){Console.WriteLine($"Shadow startup {cfg.Id}: {ex.Message}. Use Start after the feed warms.");}
        }
    }
}
