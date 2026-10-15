using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Parquet;
using Parquet.Schema;
using TraderProMax.Contracts;
using TraderProMax.Transport;

namespace TraderProMax.Research;
public static class ArchiveExport
{
    public static async Task Export(string input,string output)
    {
        if(!File.Exists(input))throw new FileNotFoundException("Recorded events.bin required",input);
        if(Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any())throw new IOException("Use a new empty export directory");
        Directory.CreateDirectory(output);
        using var file=new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        long length=file.Length,completeBytes=0,decodedBytes=0,marketRows=0,decisionRows=0;bool compressed=input.EndsWith(".gz",StringComparison.OrdinalIgnoreCase);
        using var snapshot=new PrefixStream(file,length); // A growing recording contributes only its captured prefix.
        using var inflated=compressed?new GZipStream(snapshot,CompressionMode.Decompress,true):null;
        Stream frames=inflated is null?snapshot:inflated;
        string[] names=["sequence","source_ns","capture_ticks","capture_frequency","asset","kind","quality","price_e8","quantity","bid_e8","ask_e8","side","position","operation","received_ns"];
        var fields=names.Select(n=>(DataField)new DataField<long>(n,false)).ToArray();var epoch=new DataField<string>("epoch",false);
        var schema=new ParquetSchema([epoch,..fields]);
        await using var parquetFile=File.Create(Path.Combine(output,"market.parquet"));
        await using var writer=await ParquetWriter.CreateAsync(schema,parquetFile);
        await using var decisionFile=File.Create(Path.Combine(output,"decisions.jsonl.gz"));
        await using var gzip=new GZipStream(decisionFile,CompressionLevel.Fastest,true);
        await using var decisions=new StreamWriter(gzip,new UTF8Encoding(false));
        var batch=new List<MarketEvent>(50000);
        async Task Flush()
        {
            if(batch.Count==0)return;
            using var group=writer.CreateRowGroup();await group.WriteAsync(epoch,batch.Select(e=>e.Epoch.ToString()).ToArray());
            for(int column=0;column<fields.Length;column++)
            {
                int k=column;long Value(MarketEvent e)=>k switch {0=>e.Sequence,1=>e.SourceNs,2=>e.CaptureTicks,3=>e.CaptureFrequency,4=>(long)e.Asset,5=>(long)e.Kind,6=>(long)e.Quality,7=>e.Price,8=>e.Quantity,9=>e.Bid,10=>e.Ask,11=>e.Side,12=>e.Position,13=>e.Operation,14=>e.ReceivedNs,_=>throw new InvalidDataException("column")};
                await group.WriteAsync<long>(fields[column],batch.Select(Value).ToArray().AsMemory());
            }
            batch.Clear();
        }
        bool tail=false;
        while(true)
        {
            Frame? row;try{row=await Wire.ReadAsync(frames);}catch(EndOfStreamException){tail=true;break;}
            if(row==null)break;completeBytes=file.Position;decodedBytes+=12+row.Value.Payload.Length;
            if(row.Value.Type==MessageType.Market){batch.Add(Wire.DecodeMarket(row.Value.Payload));marketRows++;if(batch.Count==50000)await Flush();}
            else if(row.Value.Type is MessageType.Research or MessageType.Broker){await decisions.WriteLineAsync(Encoding.UTF8.GetString(row.Value.Payload));decisionRows++;}
        }
        await Flush();await decisions.FlushAsync();
        long hashedBytes=compressed?length:completeBytes;file.Position=0;using var prefix=new PrefixStream(file,hashedBytes);string hash=Convert.ToHexString(await SHA256.HashDataAsync(prefix));
        Workspace.Atomic(Path.Combine(output,"manifest.json"),Json.Bytes(new {schema="native-archive-v1",source=Path.GetFullPath(input),capturedBytes=length,completeBytes,decodedCompleteBytes=decodedBytes,compression=compressed?"gzip":"none",hashedBytes,truncatedTailExcluded=tail,sha256Prefix=hash,marketRows,decisionRows,
            priceScale=MarketEvent.Scale,quantities="executed contracts for trade rows; displayed size for depth and quote rows; legacy quote rows have zero side/price/size",precision="UTC ns representation, source precision unchanged",qualityFlags="Genuine=1 DepthEntitled=2 Gap=4 Dropped=8 InvalidQuote=16 AggressorKnown=32 Snapshot=64 UnknownSourceTime=128 Rejected=256",
            eligibility="Rejected/gap/dropped rows must not produce training labels; simulated cycles remain separate",exportedUtc=DateTime.UtcNow}));
        Console.WriteLine($"Exported {marketRows:N0} market rows and {decisionRows:N0} decision/broker rows to {output}");
    }
    sealed class PrefixStream(Stream inner,long limit):Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>limit;public override long Position{get=>inner.Position;set=>throw new NotSupportedException();}
        public override int Read(byte[] bytes,int offset,int count)=>inner.Read(bytes,offset,(int)Math.Min(count,Math.Max(0,limit-inner.Position)));
        public override ValueTask<int> ReadAsync(Memory<byte> bytes,CancellationToken ct=default)=>inner.ReadAsync(bytes[..(int)Math.Min(bytes.Length,Math.Max(0,limit-inner.Position))],ct);
        public override void Flush()=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    }
}
