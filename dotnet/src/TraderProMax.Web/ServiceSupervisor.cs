using System.Diagnostics;
using TraderProMax.Contracts;
using TraderProMax.Transport;

namespace TraderProMax.Web;
public sealed class ServiceSupervisor(string root)
{
    readonly Dictionary<string,Process> workers=[];readonly object gate=new();
    public async Task Worker(string id,bool restart=false)
    {
        var view=Json.Read<TraderProMax.Core.ProfileConfig[]>(File.ReadAllBytes(Path.Combine(Workspace.Runtime(root),"profiles.json")));
        if(!view.Any(p=>p.Id==id))throw new InvalidDataException("Unknown worker");
        Process? old;
        lock(gate)workers.TryGetValue(id,out old);
        if(old is {HasExited:false})
        {if(!restart)return;old.Kill();await old.WaitForExitAsync();old.Dispose();}
        var dll=Workspace.ServiceDll(root,"ExecutionWorker");
        var start=new ProcessStartInfo(Workspace.DotNetHost(root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(dll);start.ArgumentList.Add("--root");start.ArgumentList.Add(root);start.ArgumentList.Add("--profile");start.ArgumentList.Add(id);
        var p=Process.Start(start)??throw new IOException("Worker did not launch");lock(gate)workers[id]=p;
        string log=Path.Combine(Workspace.Runtime(root),id+".log");
        _=Task.Run(async()=>{await using var file=new StreamWriter(log,true);while(await p.StandardOutput.ReadLineAsync() is {} line){await file.WriteLineAsync(line);await file.FlushAsync();}});
        _=Task.Run(async()=>{string err=await p.StandardError.ReadToEndAsync();if(err.Length>0)await File.AppendAllTextAsync(log+".errors",err);});
    }
    public TraderProMax.Core.ProfileConfig Config(string id)=>Json.Read<TraderProMax.Core.ProfileConfig[]>(File.ReadAllBytes(Path.Combine(Workspace.Runtime(root),"profiles.json"))).Single(p=>p.Id==id);
    public void Settings(TraderProMax.Core.ProfileConfig p)
    {
        var bridge=Workspace.Bridge(root);string path=Path.Combine(Workspace.Runtime(root),"bindings",p.Id+".set");
        var text=$"BridgeToken={bridge.Token}\r\nBridgePort={p.WorkerPort}\r\nExpectedLogin={p.Login}\r\nExpectedServer={p.Server}\r\nTradingSymbol={p.Symbol}\r\nStrategyMagic={p.Magic}\r\nAllowDemoOrders={(p.Execution=="demo"?"true":"false")}\r\n";
        Workspace.Atomic(path,[..System.Text.Encoding.Unicode.GetPreamble(),..System.Text.Encoding.Unicode.GetBytes(text)]);
    }
}
