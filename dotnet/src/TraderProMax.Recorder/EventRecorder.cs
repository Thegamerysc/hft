using System.Threading.Channels;
using System.IO.Compression;
using TraderProMax.Contracts;
using TraderProMax.Transport;

namespace TraderProMax.Recorder;

public sealed class EventRecorder : IAsyncDisposable
{
    sealed record Work(Frame? Frame,TaskCompletionSource<string?>? Seal=null);
    readonly Channel<Work> queue=Channel.CreateBounded<Work>(new BoundedChannelOptions(32768){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    readonly Task writer;readonly string root;readonly bool compressed;long dropped,written;string? error;
    public long Dropped=>Interlocked.Read(ref dropped);public long Written=>Interlocked.Read(ref written);public string? Error=>error;
    public bool Healthy=>Dropped==0&&Error==null;
    public EventRecorder(string directory,bool compress=false){root=directory;compressed=compress;Directory.CreateDirectory(root);writer=Task.Run(WriteLoop);}
    public bool Submit(MessageType type,byte[] bytes)
    {
        if(queue.Writer.TryWrite(new(new Frame(type,bytes))))return true;Interlocked.Increment(ref dropped);return false;
    }
    // A barrier is processed by the sole writer after previously accepted events.
    public async Task<string?> SealAsync(CancellationToken ct=default)
    {
        var done=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new(null,done),ct);return await done.Task.WaitAsync(ct);
    }
    async Task WriteLoop()
    {
        Stream? stream=null;FileStream? file=null;string? day=null,pathWritten=null;long rotateNs=0;int part=0;string run=DateTime.UtcNow.ToString("HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];
        TaskCompletionSource<string?>? activeSeal=null;
        try
        {
            await foreach(var work in queue.Reader.ReadAllAsync())
            {
                if(work.Seal is {} seal)
                {
                    activeSeal=seal;if(stream!=null)await stream.DisposeAsync();stream=null;file=null;day=null;
                    seal.TrySetResult(pathWritten);activeSeal=null;continue;
                }
                var row=work.Frame!.Value;
                string d=DateTime.UtcNow.ToString("yyyy-MM-dd");
                if(d!=day||compressed&&(Clock.UtcNs>=rotateNs||file?.Position>=8_388_608))
                {
                    if(stream!=null)await stream.DisposeAsync();day=d;var path=Path.Combine(root,d);Directory.CreateDirectory(path);
                    string name=compressed?$"events-{run}-{part++:D4}.bin.gz":"events.bin";
                    pathWritten=Path.Combine(path,name);
                    file=new(pathWritten,compressed?FileMode.CreateNew:FileMode.Append,FileAccess.Write,FileShare.Read,65536,FileOptions.Asynchronous);
                    stream=compressed?new GZipStream(file,CompressionLevel.Fastest):file;rotateNs=Clock.UtcNs+300_000_000_000;
                }
                await Wire.WriteAsync(stream!,row.Type,row.Payload);Interlocked.Increment(ref written);
                if(queue.Reader.Count==0){await stream!.FlushAsync();if(compressed)await file!.FlushAsync();}
            }
        }
        catch(Exception ex){error=ex.Message;queue.Writer.TryComplete(ex);activeSeal?.TrySetException(ex);while(queue.Reader.TryRead(out var pending))pending.Seal?.TrySetException(ex);}
        finally{if(stream!=null)await stream.DisposeAsync();}
    }
    public async ValueTask DisposeAsync(){queue.Writer.TryComplete();await writer;}
}
