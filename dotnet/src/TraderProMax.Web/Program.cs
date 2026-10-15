using TraderProMax.Transport;
using TraderProMax.Web;
using TraderProMax.Web.Components;

var root=Workspace.Root(args);
string contentRoot=Directory.Exists(Path.Combine(AppContext.BaseDirectory,"wwwroot"))?AppContext.BaseDirectory:Path.Combine(root,"dotnet/src/TraderProMax.Web");
var builder=WebApplication.CreateBuilder(new WebApplicationOptions {Args=args.Where(x=>x!="--shadow").ToArray(),ContentRootPath=contentRoot});
builder.WebHost.UseUrls($"http://127.0.0.1:{Workspace.Bridge(root).DashboardPort}").UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();builder.Services.AddSingleton(new MarketLink(root));
builder.Services.AddHostedService(sp=>sp.GetRequiredService<MarketLink>());builder.Services.AddSingleton(new ServiceSupervisor(root));
if(args.Contains("--shadow"))builder.Services.AddHostedService(sp=>new ShadowBootstrap(root,sp.GetRequiredService<ServiceSupervisor>(),sp.GetRequiredService<MarketLink>()));
var app=builder.Build();app.UseStaticFiles();app.UseAntiforgery();app.MapStaticAssets();
app.MapGet("/health",()=>new {ok=true,version="2.0-csharp",demoOnly=true});
app.MapGet("/api/state",(MarketLink link)=>link.View);
app.MapGet("/api/export",(MarketLink link)=>Results.File(link.Export(),"text/csv","traderpromax-cycles.csv"));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();app.Run();
