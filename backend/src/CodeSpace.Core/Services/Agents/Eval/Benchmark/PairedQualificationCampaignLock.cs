using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.OAuth;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

public interface IPairedQualificationCampaignLock
{
    Task<IAsyncDisposable> AcquireAsync(Guid observationGroupId, CancellationToken cancellationToken);
}

/// <summary>Serializes every initial or recovery writer for one paired campaign; a lost process releases the PostgreSQL session lock.</summary>
public sealed class PairedQualificationCampaignLock : IPairedQualificationCampaignLock, IScopedDependency
{
    private readonly ICrossProcessLock _locks;
    public PairedQualificationCampaignLock(ICrossProcessLock locks) => _locks = locks;

    public Task<IAsyncDisposable> AcquireAsync(Guid observationGroupId, CancellationToken cancellationToken)
    {
        if (observationGroupId == Guid.Empty) throw new ArgumentException("A paired campaign identity is required.", nameof(observationGroupId));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"codespace.paired-qualification\u001f{observationGroupId:D}"));
        return _locks.AcquireAsync(BinaryPrimitives.ReadInt64BigEndian(bytes), cancellationToken);
    }
}
