using System.Net.Http;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

public interface ILegacyPlacementAdoptionRuntime : IScopedDependency
{
    IStorageProviderModuleCatalog Modules { get; }
    IStorageRuntimeDriverBroker Broker { get; }
    TimeProvider Clock { get; }
    TimeSpan ClaimTtl { get; }
    TimeSpan ClaimRenewalInterval { get; }
    TimeSpan ProviderOperationTimeout { get; }
}

public sealed class LegacyPlacementAdoptionRuntime : ILegacyPlacementAdoptionRuntime
{
    public LegacyPlacementAdoptionRuntime(IStorageProviderModuleCatalog modules, IStorageRuntimeDriverBroker broker, TimeProvider clock)
    {
        Modules = modules;
        Broker = broker;
        Clock = clock;
    }

    public IStorageProviderModuleCatalog Modules { get; }
    public IStorageRuntimeDriverBroker Broker { get; }
    public TimeProvider Clock { get; }
    public TimeSpan ClaimTtl { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ClaimRenewalInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ProviderOperationTimeout { get; init; } = TimeSpan.FromSeconds(45);
}

internal enum LegacyProviderExceptionDisposition
{
    Missing,
    Corrupt,
    Retryable,
    Rejected,
    ProgrammingFault,
}

internal static class LegacyProviderExceptionClassifier
{
    public static LegacyProviderExceptionDisposition Classify(ArtifactStorageError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Code is ArtifactStorageErrorCode.InvalidRequest or ArtifactStorageErrorCode.Unauthorized
            or ArtifactStorageErrorCode.Forbidden or ArtifactStorageErrorCode.Unsupported)
            return LegacyProviderExceptionDisposition.Rejected;
        if (error.IsRetryable) return LegacyProviderExceptionDisposition.Retryable;
        return error.Code switch
        {
            ArtifactStorageErrorCode.Missing => LegacyProviderExceptionDisposition.Missing,
            ArtifactStorageErrorCode.IntegrityMismatch or ArtifactStorageErrorCode.Corrupt => LegacyProviderExceptionDisposition.Corrupt,
            _ => LegacyProviderExceptionDisposition.Retryable,
        };
    }

    public static LegacyProviderExceptionDisposition Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is IArtifactStorageOperationalException classified)
            return Classify(new ArtifactStorageError(classified.Code, "Provider operational failure.", classified.IsRetryable));
        if (exception is UnauthorizedAccessException or NotSupportedException)
            return LegacyProviderExceptionDisposition.Rejected;
        if (exception is InvalidDataException)
            return LegacyProviderExceptionDisposition.ProgrammingFault;
        if (exception is OperationCanceledException or IOException or TimeoutException or HttpRequestException)
            return LegacyProviderExceptionDisposition.Retryable;
        return LegacyProviderExceptionDisposition.ProgrammingFault;
    }
}

internal sealed class LegacyPlacementPassBudget
{
    private readonly TimeProvider _clock;
    private readonly long _startedAt;
    private readonly TimeSpan _timeBudget;
    private readonly long _byteBudget;
    private bool _started;

    public LegacyPlacementPassBudget(long byteBudget, TimeSpan timeBudget, TimeProvider clock)
    {
        _byteBudget = Math.Clamp(byteBudget, 1, LegacyPlacementAdoptionLimits.MaxBytesPerPass);
        _timeBudget = timeBudget <= TimeSpan.Zero ? TimeSpan.FromSeconds(1)
            : timeBudget > LegacyPlacementAdoptionLimits.MaxTimePerPass ? LegacyPlacementAdoptionLimits.MaxTimePerPass : timeBudget;
        _clock = clock;
        _startedAt = clock.GetTimestamp();
    }

    public long ReadBytes { get; private set; }
    public int ProcessedRows { get; private set; }
    public bool OversizedItem { get; private set; }
    public LegacyPlacementAdoptionYieldReason YieldReason { get; private set; }

    public bool TryStart(long expectedBytes, bool readsPayload = true)
    {
        var cost = !readsPayload ? 0 : expectedBytes == long.MaxValue ? long.MaxValue : checked(expectedBytes + 1);
        if (!_started)
        {
            _started = true;
            OversizedItem = cost > _byteBudget - Math.Min(ReadBytes, _byteBudget);
            ProcessedRows++;
            return true;
        }
        if (OversizedItem || cost > _byteBudget - Math.Min(ReadBytes, _byteBudget))
        {
            YieldReason = LegacyPlacementAdoptionYieldReason.ByteBudget;
            return false;
        }
        if (_clock.GetElapsedTime(_startedAt) >= _timeBudget)
        {
            YieldReason = LegacyPlacementAdoptionYieldReason.TimeBudget;
            return false;
        }
        ProcessedRows++;
        return true;
    }

    public void AddReadBytes(int count) => ReadBytes = checked(ReadBytes + count);

    public void Finish(bool sourceHasMore)
    {
        if (YieldReason != LegacyPlacementAdoptionYieldReason.None) return;
        if (sourceHasMore) YieldReason = LegacyPlacementAdoptionYieldReason.RowLimit;
    }
}
