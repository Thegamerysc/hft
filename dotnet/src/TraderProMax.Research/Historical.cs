using System.Globalization;
using Parquet;
using TraderProMax.Contracts;

namespace TraderProMax.Research;
public sealed record HistoricalRow(string Id,string Day,long Ns,long ExitNs,double Net,Dictionary<string,double> Values,long LongExitNs,long ShortExitNs,double LongPnl,double ShortPnl);
public sealed record DailyResult(string Day,double Opening,double Pnl,double Closing,int Completed);
public sealed record PortfolioResult(int Completed,double Net,double WinRate,double Drawdown,IReadOnlyList<DailyResult> Daily,int Opened,int Unresolved,int BothTp=0,double IdealBinaryNet=0,
    int BothSl=0,int OneTpNetLoss=0,double BarrierSuccessRate=0,double MeanWin=0,double MeanLoss=0);
public static class Historical
{
    public static long Ns(string day)=>(DateTime.Parse(day,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal).Ticks-DateTime.UnixEpoch.Ticks)*100;
    public static string[] Features(string family)
    {
        string[] price=["tick_ret_30s","tick_rv_30s","spread_now_r","rv_ratio_1_30","eff_5"];
        string[] tape=["es_ti5","es_ti30","es_trade_volume30","es_trade_per_move30"];
        string[] depth=["es_obi5","es_ofi5","es_replenish_bid5","es_replenish_ask5","es_deplete_bid5","es_deplete_ask5"];
        return family=="price"?price:family=="price_tape"?[..price,..tape]:[..price,..tape,..depth];
    }
    static async Task<Dictionary<string,Array>> Read(string path,IEnumerable<string> names)
    {
        await using var s=File.OpenRead(path);await using var reader=await ParquetReader.CreateAsync(s);
        var fields=reader.Schema.GetDataFields().Where(x=>names.Contains(x.Name)).ToArray();
        var collected=fields.ToDictionary(x=>x.Name,_=>new List<Array>());
        for(int group=0;group<reader.RowGroupCount;group++)
        {
            using var g=reader.OpenRowGroupReader(group);
            foreach(var field in fields)
            {
                int n=checked((int)g.RowCount);Array data;
                if(field.Name is "day" or "sample_id"){var col=new string?[n];await g.ReadAsync(field,col.AsMemory());data=col;}
                else if(field.ClrType==typeof(long)){var col=new long?[n];await g.ReadAsync<long>(field,col.AsMemory());data=col;}
                else{var col=new double?[n];await g.ReadAsync<double>(field,col.AsMemory());data=col;}
                collected[field.Name].Add(data);
            }
        }
        var result=new Dictionary<string,Array>();foreach(var (name,arrays) in collected)
        {var type=arrays[0].GetType().GetElementType()!;int n=arrays.Sum(a=>a.Length);var all=Array.CreateInstance(type,n);int at=0;foreach(var a in arrays){Array.Copy(a,0,all,at,a.Length);at+=a.Length;}result[name]=all;}
        return result;
    }
    static double D(Dictionary<string,Array> d,string k,int i)=>d[k].GetValue(i) is {} v?Convert.ToDouble(v,CultureInfo.InvariantCulture):double.NaN;
    static string S(Dictionary<string,Array> d,string k,int i)=>Convert.ToString(d[k].GetValue(i),CultureInfo.InvariantCulture)??"";
    public static async Task<List<HistoricalRow>> Load(string root,string clock,string[]? months=null)
    {
        var all=new List<HistoricalRow>();var featureNames=Features("price_tape_depth");
        foreach(string month in months??["2026-04","2026-05","2026-06","2026-07","2026-08","2026-09"])
        {
            string path=Path.Combine(root,"research/us500_flow/datasets",clock,month);if(!File.Exists(Path.Combine(path,"observations.parquet")))continue;
            var obs=await Read(Path.Combine(path,"observations.parquet"),[..featureNames,"sample_id","day","ts","risk_points"]);
            var lab=await Read(Path.Combine(path,"outcomes.parquet"),["sample_id","risk_points","h_exit_ns","h_long_entry","h_short_entry","h_long_exit","h_short_exit","h_legs_filled","h_invalid_path","h_long_exit_ns","h_short_exit_ns"]);
            var labels=new Dictionary<string,(long Exit,double Net,long LongNs,long ShortNs,double LongPnl,double ShortPnl)>();
            for(int i=0;i<lab["sample_id"].Length;i++)
            {
                if(D(lab,"risk_points",i)!=20||D(lab,"h_legs_filled",i)!=2||D(lab,"h_invalid_path",i)!=0)continue;
                double exit=D(lab,"h_exit_ns",i),lp=(D(lab,"h_long_exit",i)-D(lab,"h_long_entry",i))*.2,sp=(D(lab,"h_short_entry",i)-D(lab,"h_short_exit",i))*.2;
                if(!double.IsFinite(exit+lp+sp)||exit<=0)continue;
                var longPnl=Math.Round(lp,2,MidpointRounding.ToEven);var shortPnl=Math.Round(sp,2,MidpointRounding.ToEven);
                labels[S(lab,"sample_id",i)]=((long)exit,longPnl+shortPnl,(long)D(lab,"h_long_exit_ns",i),(long)D(lab,"h_short_exit_ns",i),longPnl,shortPnl);
            }
            for(int i=0;i<obs["sample_id"].Length;i++)
            {
                if(D(obs,"risk_points",i)!=20)continue;string id=S(obs,"sample_id",i);if(!labels.TryGetValue(id,out var outcome))continue;
                var values=featureNames.ToDictionary(n=>n,n=>D(obs,n,i));
                all.Add(new(id,S(obs,"day",i),Convert.ToInt64(obs["ts"].GetValue(i)),outcome.Exit,outcome.Net,values,outcome.LongNs,outcome.ShortNs,outcome.LongPnl,outcome.ShortPnl));
            }
            Console.WriteLine($"{month}: joined {labels.Count:N0} completed valid R20 labels");
        }
        return all.OrderBy(x=>x.Ns).ToList();
    }
    public static PortfolioResult Portfolio(IEnumerable<HistoricalRow> rows,Func<HistoricalRow,bool> filter,int cap,double fee)
    {
        var source=rows.OrderBy(r=>r.Ns).ToArray();if(source.Length==0)return new(0,0,0,0,[],0,0);
        long limit=Ns(source.Max(r=>r.Day)!)+86_400_000_000_000;
        var active=new List<long>();var picked=new List<HistoricalRow>();var pending=new PriorityQueue<(long Ns,double Pnl),(long Ns,long Serial)>();var settled=new List<(long Ns,double Net)>();long serial=0;
        double cash=2000,peak=2000,dd=0;
        void Settle(long at){while(pending.TryPeek(out var evt,out var key)&&key.Ns<=at){pending.Dequeue();cash+=evt.Pnl;peak=Math.Max(peak,cash);dd=Math.Max(dd,peak-cash);settled.Add((evt.Ns,evt.Pnl));}}
        foreach(var r in source)
        {
            Settle(r.Ns);active.RemoveAll(t=>t<=r.Ns);
            if(active.Count>=cap||cash<8||!filter(r))continue;active.Add(r.ExitNs);picked.Add(r);
            foreach(var leg in new[]{(r.LongExitNs,r.LongPnl,r.Ns+200_000_000),(r.ShortExitNs,r.ShortPnl,r.Ns+400_000_000)})
            {
                pending.Enqueue((leg.Item3,-fee/4),(leg.Item3,serial++));
                if(leg.Item1<limit)pending.Enqueue((leg.Item1,leg.Item2-fee/4),(leg.Item1,serial++));
            }
        }
        Settle(limit-1);var completed=picked.Where(r=>r.ExitNs<limit).ToArray();var daily=new List<DailyResult>();double balance=2000;
        foreach(string day in source.Select(r=>r.Day).Distinct().Order())
        {
            double pnl=settled.Where(e=>DateTimeOffset.FromUnixTimeMilliseconds(e.Ns/1_000_000).ToString("yyyy-MM-dd")==day).Sum(x=>x.Net);double opening=balance;balance+=pnl;
            int n=completed.Count(r=>DateTimeOffset.FromUnixTimeMilliseconds(r.ExitNs/1_000_000).ToString("yyyy-MM-dd")==day);daily.Add(new(day,opening,pnl,balance,n));
        }
        int wins=completed.Count(x=>x.Net-fee>0);
        return new(completed.Length,settled.Sum(x=>x.Net),completed.Length==0?0:wins/(double)completed.Length,dd,daily,picked.Count,picked.Count-completed.Length,
            completed.Count(x=>x.LongPnl>0&&x.ShortPnl>0),wins*.64-(completed.Length-wins)*8-completed.Length*fee,
            completed.Count(x=>x.LongPnl<0&&x.ShortPnl<0),completed.Count(x=>x.Net-fee<0&&(x.LongPnl>0||x.ShortPnl>0)),
            completed.Length==0?0:completed.Count(x=>x.LongPnl>0||x.ShortPnl>0)/(double)completed.Length,
            completed.Where(x=>x.Net-fee>0).Select(x=>x.Net-fee).DefaultIfEmpty().Average(),completed.Where(x=>x.Net-fee<0).Select(x=>x.Net-fee).DefaultIfEmpty().Average());
    }
}
public sealed class HistoricalModel
{
    public string[] Names {get;init;}=[];public double[] Means {get;init;}=[];public double[] Scales {get;init;}=[];public double[] Weights {get;init;}=[];public double Intercept {get;set;}
    public double Predict(HistoricalRow r)
    {
        double score=Intercept;for(int i=0;i<Names.Length;i++){double v=r.Values[Names[i]];if(!double.IsFinite(v))return double.NaN;score+=Math.Clamp((v-Means[i])/Scales[i],-8,8)*Weights[i];}return 1/(1+Math.Exp(-Math.Clamp(score,-40,40)));
    }
    public object Diagnostic(IEnumerable<HistoricalRow> rows)
    {
        var scored=rows.Select(r=>(Prediction:Predict(r),Won:r.Net>0)).Where(x=>double.IsFinite(x.Prediction)).OrderBy(x=>x.Prediction).ToArray();
        if(scored.Length==0)return new {completeRows=0};
        return new {completeRows=scored.Length,observedSuccess=scored.Count(x=>x.Won)/(double)scored.Length,meanProbability=scored.Average(x=>x.Prediction),
            p50=scored[scored.Length/2].Prediction,p95=scored[(int)(scored.Length*.95)].Prediction,max=scored[^1].Prediction,
            brier=scored.Average(x=>Math.Pow(x.Prediction-(x.Won?1:0),2)),logLoss=scored.Average(x=>-(x.Won?Math.Log(Math.Max(1e-12,x.Prediction)):Math.Log(Math.Max(1e-12,1-x.Prediction))))};
    }
    public static HistoricalModel Fit(IEnumerable<HistoricalRow> input,string[] names)
    {
        var rows=input.Where(r=>names.All(n=>double.IsFinite(r.Values[n]))).ToArray();if(rows.Length<200)throw new InvalidDataException("Insufficient complete training observations");
        var model=new HistoricalModel {Names=names,Means=names.Select(n=>rows.Average(r=>r.Values[n])).ToArray(),Scales=new double[names.Length],Weights=new double[names.Length]};
        for(int j=0;j<names.Length;j++)model.Scales[j]=Math.Max(1e-6,Math.Sqrt(rows.Average(r=>Math.Pow(r.Values[names[j]]-model.Means[j],2))));
        double prior=Math.Clamp(rows.Count(r=>r.Net>0)/(double)rows.Length,.01,.99);model.Intercept=Math.Log(prior/(1-prior));
        var x=rows.Select(r=>names.Select((n,j)=>Math.Clamp((r.Values[n]-model.Means[j])/model.Scales[j],-8,8)).ToArray()).ToArray();
        for(int epoch=0;epoch<80;epoch++)
        {
            var gradient=new double[names.Length];double interceptGrad=0;
            for(int k=0;k<rows.Length;k++)
            {double score=model.Intercept;for(int j=0;j<names.Length;j++)score+=x[k][j]*model.Weights[j];double error=1/(1+Math.Exp(-Math.Clamp(score,-40,40)))-(rows[k].Net>0?1:0);interceptGrad+=error;for(int j=0;j<names.Length;j++)gradient[j]+=error*x[k][j];}
            model.Intercept-=.1*interceptGrad/rows.Length;for(int j=0;j<names.Length;j++)model.Weights[j]-=.1*(gradient[j]/rows.Length+.05*model.Weights[j]);
        }
        return model;
    }
}
