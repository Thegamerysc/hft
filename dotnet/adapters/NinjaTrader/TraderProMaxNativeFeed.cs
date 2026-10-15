// Native data-only adapter. No accounts, broker orders, UI work or disk logging in callbacks.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.AddOns
{
    public class TraderProMaxNativeFeed : AddOnBase
    {
        static string ConfigPath()
        {
            string path=Environment.GetEnvironmentVariable("TRADERPROMAX_BRIDGE_CONFIG");
            if(string.IsNullOrWhiteSpace(path))path=File.ReadAllText(Path.Combine(NinjaTrader.Core.Globals.UserDataDir,"TraderProMax","bridge-location.txt")).Trim();
            if(!Path.IsPathRooted(path))throw new IOException("TraderProMax bridge location must be an absolute path");
            return path;
        }
        struct Packet
        {
            public Guid Epoch;public long Seq,Ns,Ticks;public int Asset,Kind,Flags,Side,Position,Operation;
            public long Price,Size,Bid,Ask;
        }
        readonly ConcurrentDictionary<string,Instrument> instruments=new ConcurrentDictionary<string,Instrument>();
        BlockingCollection<Packet> queue;Thread writer;Timer timer;volatile bool stopping,valid;
        Dictionary<string,object> config;Guid epoch=Guid.NewGuid();long seq,drops;string provider="";
        readonly BlockingCollection<byte[]> research=new BlockingCollection<byte[]>(100);
        protected override void OnStateChange()
        {
            if(State==State.SetDefaults){Name="TraderProMaxNativeFeed";Description="Binary ES/NQ tape and depth to C# host; data only";}
            else if(State==State.Active)
            {
                try
                {
                    config=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(ConfigPath()));
                    queue=new BlockingCollection<Packet>(65536);writer=new Thread(WriteLoop){IsBackground=true,Name="TraderProMax binary feed"};writer.Start();
                    timer=new Timer(Check,null,0,1000);
                }
                catch(Exception ex){Print("TraderProMax native feed unavailable: "+ex.Message);}
            }
            else if(State==State.Terminated)
            {
                stopping=true;valid=false;if(timer!=null)timer.Dispose();
                foreach(var instrument in instruments.Values)instrument.Dispatcher.InvokeAsync(()=>{instrument.MarketData.Update-=OnData;instrument.MarketDepth.Update-=OnDepth;});
                if(queue!=null)queue.CompleteAdding();if(writer!=null)writer.Join(2000);
            }
        }
        bool Genuine()
        {
            if(Connection.PlaybackConnection!=null)return false;
            var connected=Connection.Connections.Where(c=>c.PriceStatus==ConnectionStatus.Connected).ToArray();
            var candidates=connected.Where(c=>c.Options.Name=="Simulation").ToArray();if(candidates.Length!=1)return false;
            provider=candidates[0].Options.Provider.ToString();
            if(provider.IndexOf("simulat",StringComparison.OrdinalIgnoreCase)>=0||provider.IndexOf("playback",StringComparison.OrdinalIgnoreCase)>=0||provider.IndexOf("external",StringComparison.OrdinalIgnoreCase)>=0)return false;
            return !connected.Any(c=>c!=candidates[0]&&c!=Connection.ClientConnection);
        }
        void Check(object state)
        {
            if(stopping||queue==null)return;
            try
            {
                bool good=Genuine();if(good!=valid){valid=good;lock(queue){epoch=Guid.NewGuid();seq=0;Packet ignored;while(queue.TryTake(out ignored)){}drops=0;}}
                if(!good)return;
                foreach(object name in (System.Collections.IEnumerable)config["contracts"])
                {
                    string full=(string)name;if(instruments.ContainsKey(full))continue;var instrument=Instrument.GetInstrument(full);if(instrument==null)continue;
                    if(instruments.TryAdd(full,instrument))instrument.Dispatcher.InvokeAsync(()=>{instrument.MarketData.Update+=OnData;instrument.MarketDepth.Update+=OnDepth;});
                }
                foreach(var instrument in instruments.Values){var health=Base(instrument,5,DateTime.UtcNow);health.Flags|=128;Enqueue(health);}
            }
            catch(Exception ex){valid=false;Print("TraderProMax connection check: "+ex.GetType().Name);}
        }
        static long Ns(DateTime t){return(t.Ticks-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).Ticks)*100;}
        static long Price(double p){return checked((long)Math.Round(p*100000000));}
        Packet Base(Instrument i,int kind,DateTime t)
        {
            return new Packet{Ticks=Stopwatch.GetTimestamp(),Ns=Ns(t),Asset=i.MasterInstrument.Name=="ES"?1:2,Kind=kind,Flags=valid?1:0};
        }
        void OnData(object sender,MarketDataEventArgs e)
        {
            if(stopping||!valid||e.MarketDataType!=MarketDataType.Last&&e.MarketDataType!=MarketDataType.Bid&&e.MarketDataType!=MarketDataType.Ask)return;
            DateTime utc=e.Time.Kind==DateTimeKind.Utc?e.Time:TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(e.Time,DateTimeKind.Unspecified),NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo);
            var p=Base(e.Instrument,e.MarketDataType==MarketDataType.Last?2:1,utc);
            p.Bid=e.Instrument.MarketData.Bid==null?0:Price(e.Instrument.MarketData.Bid.Price);p.Ask=e.Instrument.MarketData.Ask==null?0:Price(e.Instrument.MarketData.Ask.Price);
            if(e.MarketDataType==MarketDataType.Last){p.Price=Price(e.Price);p.Size=e.Volume;if(e.Bid>0&&e.Ask>e.Bid){p.Bid=Price(e.Bid);p.Ask=Price(e.Ask);}p.Side=p.Price>=p.Ask&&p.Ask>p.Bid?1:p.Price<=p.Bid&&p.Ask>p.Bid?-1:0;}
            else{p.Price=Price(e.Price);p.Size=e.Volume;p.Side=e.MarketDataType==MarketDataType.Bid?1:-1;}
            if(p.Bid<=0||p.Ask<=p.Bid)p.Flags|=16;Enqueue(p);
        }
        void OnDepth(object sender,MarketDepthEventArgs e)
        {
            if(stopping||!valid)return;
            DateTime utc=e.Time==default(DateTime)?DateTime.UtcNow:e.Time.Kind==DateTimeKind.Utc?e.Time:TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(e.Time,DateTimeKind.Unspecified),NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo);
            var p=Base(e.Instrument,e.IsReset?4:3,utc);p.Flags|=2;
            if(e.Time==default(DateTime))p.Flags|=128;
            p.Price=Price(e.Price);p.Size=e.Volume;p.Side=e.MarketDataType==MarketDataType.Bid?1:-1;p.Position=e.Position;
            p.Operation=e.Operation==Operation.Add?1:e.Operation==Operation.Update?2:e.Operation==Operation.Remove?3:4;
            Enqueue(p);
        }
        void Enqueue(Packet p)
        {
            lock(queue)
            {if(stopping||!valid)return;p.Epoch=epoch;p.Seq=++seq;if(drops>0)p.Flags|=8;if(!queue.TryAdd(p))drops++;}
        }
        static void I32(byte[] b,int o,int v){for(int i=0;i<4;i++)b[o+i]=(byte)(v>>(8*i));}
        static void I64(byte[] b,int o,long v){for(int i=0;i<8;i++)b[o+i]=(byte)(v>>(8*i));}
        static byte[] Frame(int type,byte[] payload)
        {
            var b=new byte[12+payload.Length];I32(b,0,payload.Length);I32(b,4,0x32504D54);b[8]=1;b[10]=(byte)type;b[11]=(byte)(type>>8);Buffer.BlockCopy(payload,0,b,12,payload.Length);return b;
        }
        static byte[] Encode(Packet p)
        {
            var b=new byte[120];Buffer.BlockCopy(p.Epoch.ToByteArray(),0,b,0,16);I64(b,16,p.Seq);I64(b,24,p.Ns);I64(b,32,p.Ticks);I64(b,40,Stopwatch.Frequency);
            I32(b,48,p.Asset);I32(b,52,p.Kind);I32(b,56,p.Flags);I64(b,64,p.Price);I64(b,72,p.Size);I64(b,80,p.Bid);I64(b,88,p.Ask);I32(b,96,p.Side);I32(b,100,p.Position);I32(b,104,p.Operation);return Frame(2,b);
        }
        void Snapshot(Instrument instrument)
        {
            instrument.Dispatcher.InvokeAsync(()=>
            {
                lock(queue)
                {
                    var reset=Base(instrument,4,DateTime.UtcNow);reset.Flags|=192;Enqueue(reset);
                    int n=0;foreach(var level in instrument.MarketDepth.Bids){var p=Base(instrument,3,DateTime.UtcNow);p.Flags|=194;p.Side=1;p.Position=n++;p.Operation=1;p.Price=Price(level.Price);p.Size=level.Volume;Enqueue(p);}
                    n=0;foreach(var level in instrument.MarketDepth.Asks){var p=Base(instrument,3,DateTime.UtcNow);p.Flags|=194;p.Side=-1;p.Position=n++;p.Operation=1;p.Price=Price(level.Price);p.Size=level.Volume;Enqueue(p);}
                }
            });
        }
        void Commands(Stream stream)
        {
            try
            {
                while(!stopping)
                {
                    byte[] h=new byte[12];ReadAll(stream,h);int n=BitConverter.ToInt32(h,0);if(n<=0||n>65536)throw new IOException("Command frame size");
                    byte[] b=new byte[n];ReadAll(stream,b);if(h[10]!=6)continue;
                    var json=new JavaScriptSerializer();var cmd=json.Deserialize<Dictionary<string,object>>(System.Text.Encoding.UTF8.GetString(b));
                    if((string)cmd["action"]=="history")Task.Run(()=>History(cmd));
                    else if((string)cmd["action"]=="snapshot"){var instrument=Instrument.GetInstrument((string)cmd["contract"]);if(instrument!=null)Snapshot(instrument);}
                }
            }
            catch(Exception){ }
        }
        readonly SemaphoreSlim historyLock=new SemaphoreSlim(1,1);
        void History(Dictionary<string,object> command)
        {
            historyLock.Wait();
            try
            {
                string root=(string)command["root"],contract=(string)command["contract"],requestId=(string)command["requestId"];
                var instrument=Instrument.GetInstrument(contract);if(instrument==null)return;
                foreach(object obj in (System.Collections.IEnumerable)command["sessions"])
                {
                    if(stopping||!valid)return;var row=(Dictionary<string,object>)obj;
                    string day=(string)row["day"];long open=Convert.ToInt64(row["openNs"]),close=Convert.ToInt64(row["closeNs"]);
                    var utc0=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddTicks(open/100);var utc1=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddTicks(close/100);
                    var done=new ManualResetEventSlim();
                    // The date-based API normalizes to midnight; the following day must bound the RTH session.
                    using(var request=new BarsRequest(instrument,TimeZoneInfo.ConvertTimeFromUtc(utc0,NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo).Date,TimeZoneInfo.ConvertTimeFromUtc(utc1,NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo).Date.AddDays(1)))
                    {
                        request.BarsPeriod=new BarsPeriod{BarsPeriodType=BarsPeriodType.Tick,Value=1};request.TradingHours=TradingHours.Get("Default 24 x 7");request.MergePolicy=MergePolicy.DoNotMerge;
                        request.Request((r,error,message)=>
                        {
                            Task.Run(()=>
                            {
                            long volume=0;decimal dollars=0;bool complete=error==ErrorCode.NoError&&r.Bars!=null;
                            if(complete)
                            {
                                for(int i=0;i<r.Bars.Count;i++)
                                {
                                    DateTime t=r.Bars.GetTime(i);DateTime utc=t.Kind==DateTimeKind.Utc?t:TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(t,DateTimeKind.Unspecified),NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo);
                                    long ns=Ns(utc);if(ns<open||ns>=close)continue;long size=r.Bars.GetVolume(i);volume+=size;dollars+=(decimal)r.Bars.GetClose(i)*size*(root=="ES"?50:20);
                                }
                            }
                            var body=new Dictionary<string,object>{{"kind","history"},{"requestId",requestId},{"root",root},{"day",day},{"volume",volume},{"dollars",dollars},{"complete",complete&&volume>0},{"source","NinjaTrader historical Tick1 RTH; not Databento event parity"},{"error",error.ToString()},{"message",message??""},{"bars",r.Bars==null?0:r.Bars.Count},{"firstBar",r.Bars==null||r.Bars.Count==0?"":r.Bars.GetTime(0).ToString("o")},{"lastBar",r.Bars==null||r.Bars.Count==0?"":r.Bars.GetTime(r.Bars.Count-1).ToString("o")},{"timeZone",NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo.Id},{"requestedOpenUtc",utc0.ToString("o")},{"requestedCloseUtc",utc1.ToString("o")}};
                            research.TryAdd(Frame(10,System.Text.Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body))));done.Set();
                            });
                        });
                        if(!done.Wait(120000)){Print("TraderProMax history wait timed out for "+day);return;}
                        done.Dispose();
                    }
                }
            }
            catch(Exception ex){Print("TraderProMax history incomplete: "+ex.GetType().Name);}
            finally{historyLock.Release();}
        }
        void WriteLoop()
        {
            while(!stopping)
            {
                try
                {
                    using(var client=new TcpClient())
                    {
                        client.NoDelay=true;client.SendTimeout=2000;client.ReceiveTimeout=5000;client.Connect("127.0.0.1",Convert.ToInt32(config["port"]));
                        var stream=client.GetStream();var json=new JavaScriptSerializer();
                        var hello=System.Text.Encoding.UTF8.GetBytes(json.Serialize(new Dictionary<string,object>{{"token",config["token"]},{"role","feed"},{"producer","NinjaTraderDesktop"},{"contracts",config["contracts"]},{"depth",true}}));
                        var frame=Frame(1,hello);stream.Write(frame,0,frame.Length);
                        byte[] header=new byte[12];ReadAll(stream,header);int length=BitConverter.ToInt32(header,0);if(length<1||length>4096)throw new IOException("Handshake length");
                        byte[] ack=new byte[length];ReadAll(stream,ack);var accepted=json.Deserialize<Dictionary<string,object>>(System.Text.Encoding.UTF8.GetString(ack));if(!accepted.ContainsKey("accepted")||!(bool)accepted["accepted"])throw new IOException("Handshake rejected");
                        client.ReceiveTimeout=0;new Thread(()=>Commands(stream)){IsBackground=true,Name="TraderProMax history command receiver"}.Start();
                        lock(queue){Packet old;while(queue.TryTake(out old)){}epoch=Guid.NewGuid();seq=0;drops=0;}
                        research.TryAdd(Frame(10,System.Text.Encoding.UTF8.GetBytes(json.Serialize(new {kind="feed_manifest",epoch=epoch.ToString(),provider=provider,connection="Simulation",genuine=valid,timeZone=NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo.Id,contracts=config["contracts"],timestamps="provider callback time; local snapshot/health explicitly flagged 128"}))));
                        foreach(var instrument in instruments.Values)Snapshot(instrument);
                        byte[] batch=new byte[132*256];
                        while(!stopping)
                        {
                            byte[] saved;if(research.TryTake(out saved))stream.Write(saved,0,saved.Length);
                            Packet p;if(!queue.TryTake(out p,20))continue;int count=0;
                            do{var packet=Encode(p);Buffer.BlockCopy(packet,0,batch,count,packet.Length);count+=packet.Length;}while(count<batch.Length&&queue.TryTake(out p));
                            stream.Write(batch,0,count);
                        }
                    }
                }
                catch(Exception){if(!stopping)Thread.Sleep(1000);}
            }
        }
        static void ReadAll(Stream s,byte[] b){int n=0;while(n<b.Length){int r=s.Read(b,n,b.Length-n);if(r==0)throw new EndOfStreamException();n+=r;}}
    }
}
