using System.Security.Cryptography;
using TraderProMax.Contracts;

namespace TraderProMax.Transport;
public sealed record BridgeConfig(string Token,int Port,string[] Contracts,int DashboardPort=8765);
public static class Workspace
{
    public static string DotNetHost(string root)
    {
        string local=Path.Combine(root,"tools/dotnet10/dotnet.exe");if(File.Exists(local))return local;
        string? current=Environment.ProcessPath;if(current!=null&&string.Equals(Path.GetFileName(current),"dotnet.exe",StringComparison.OrdinalIgnoreCase))return current;
        foreach(string folder in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))
        {string path=Path.Combine(folder.Trim('"'),"dotnet.exe");if(File.Exists(path))return path;}
        throw new FileNotFoundException("Install the .NET 10 SDK or use the Windows release ZIP with its bundled runtime");
    }
    public static string ServiceDll(string root,string service)
    {
        string active=Path.Combine(Runtime(root),"active-release.json");
        if(File.Exists(active))
        {
            using var d=System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(active));
            string build=Path.GetFullPath(d.RootElement.GetProperty("directory").GetString()!);
            string allowed=Path.GetFullPath(Path.Combine(root,"dotnet/builds"))+Path.DirectorySeparatorChar;
            if(!build.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Release directory outside workspace builds");
            string dll=Path.Combine(build,"bin","TraderProMax."+service,"release","TraderProMax."+service+".dll");
            if(File.Exists(dll))return dll;
            throw new FileNotFoundException("Build the selected C# release",dll);
        }
        return Path.Combine(root,"dotnet/src","TraderProMax."+service,"bin/Release/net10.0","TraderProMax."+service+".dll");
    }
    public static string Root(string[] args)
    {
        int i=Array.IndexOf(args,"--root");if(i>=0&&i+1<args.Length)return Path.GetFullPath(args[i+1]);
        var d=new DirectoryInfo(Environment.CurrentDirectory);
        while(d!=null){if(File.Exists(Path.Combine(d.FullName,".traderpromax"))||File.Exists(Path.Combine(d.FullName,"docs/architecture/CSHARP_REWRITE_PLAN.md")))return d.FullName;d=d.Parent;}
        throw new DirectoryNotFoundException("Provide --root with TraderProMax workspace");
    }
    public static string Runtime(string root)=>Path.Combine(root,"dotnet/runtime");
    public static BridgeConfig Bridge(string root)
    {
        string path=Path.Combine(Runtime(root),"bridge.json");Directory.CreateDirectory(Runtime(root));
        if(File.Exists(path))return Json.Read<BridgeConfig>(File.ReadAllBytes(path));
        var c=new BridgeConfig(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),8876,["ES 12-26","NQ 12-26"]);
        Atomic(path,Json.Bytes(c));return c;
    }
    public static bool TokenEquals(string expected,string actual)
        =>CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected),System.Text.Encoding.UTF8.GetBytes(actual));
    public static void Atomic(string path,byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);
    }
    public static string Get(string[] args,string flag,string fallback){int i=Array.IndexOf(args,flag);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
}
