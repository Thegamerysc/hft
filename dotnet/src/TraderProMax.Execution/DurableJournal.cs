using System.Buffers.Binary;
using System.Security.Cryptography;
using TraderProMax.Contracts;

namespace TraderProMax.Execution;

public sealed class DurableJournal : IAsyncDisposable
{
    readonly FileStream file;readonly SemaphoreSlim gate=new(1,1);
    public DurableJournal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        file=new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read,4096,FileOptions.Asynchronous);
    }
    public IReadOnlyList<Cycle> Recover()
    {
        var rows=new List<Cycle>();file.Position=0;long valid=0;
        while(file.Position<file.Length)
        {
            byte[] h=new byte[4];if(file.Read(h)!=4)break;int n=BinaryPrimitives.ReadInt32LittleEndian(h);
            if(n<=0||n>65536)throw new InvalidDataException("Journal length corruption");
            if(file.Length-file.Position<n+32)break;
            byte[] bytes=new byte[n];file.ReadExactly(bytes);byte[] digest=new byte[32];file.ReadExactly(digest);
            if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),digest))throw new InvalidDataException("Journal checksum corruption");
            rows.Add(Json.Read<Cycle>(bytes));valid=file.Position;
        }
        if(valid<file.Length){file.SetLength(valid);file.Flush(true);}file.Position=valid;return rows;
    }
    public async Task AppendAsync(Cycle cycle,CancellationToken ct=default)
    {
        cycle.UpdatedNs=Clock.UtcNs;
        var bytes=Json.Bytes(cycle);byte[] h=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(h,bytes.Length);
        await gate.WaitAsync(ct);
        try{await file.WriteAsync(h,ct);await file.WriteAsync(bytes,ct);await file.WriteAsync(SHA256.HashData(bytes),ct);await Task.Run(()=>file.Flush(true),ct);}
        finally{gate.Release();}
    }
    public async ValueTask DisposeAsync(){await file.DisposeAsync();gate.Dispose();}
}
