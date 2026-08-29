using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Bounded window reads. The provider is asked for the window itself, so paging an object costs the bytes shown rather
/// than every byte that precedes them — the whole-object stream is forward-only, and slicing it would re-read the
/// object from zero on every page.
/// </summary>
public sealed partial class ArtifactCasRuntimeCoordinator
{
    public async Task<ArtifactCasRangeResult> ReadRangeAsync(ArtifactCasRangeRequest request, CancellationToken cancellationToken)
    {
        var timeout = Validate(request);
        var resolved = await ResolveProfileRevisionAsync(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, StorageProfileEligibility.Read, cancellationToken).ConfigureAwait(false);
        if (resolved.Problem != null) return new ArtifactCasRangeResult.Unavailable(resolved.Problem);

        var stored = await StoredLocationAsync(request.TeamId, request.ArtifactObjectId, resolved.ProfileRevisionId!.Value, cancellationToken).ConfigureAwait(false);
        if (stored == null) return new ArtifactCasRangeResult.Unavailable(Problem(ArtifactCasProblemCode.ArtifactMissing));

        // The caller bounds the offset against the row it holds; disagreeing with the ledger is a corruption signal.
        if (request.Offset > stored.Size) return new ArtifactCasRangeResult.Unavailable(Problem(ArtifactCasProblemCode.TargetCorrupt));

        var activation = new DriverActivationRequest(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, StorageProfileEligibility.Read, timeout, StorageProviderCapabilities.StreamingRead | StorageProviderCapabilities.RangeRead);
        var create = await OpenDriverAsync(activation, cancellationToken).ConfigureAwait(false);
        if (create.Problem != null) return new ArtifactCasRangeResult.Unavailable(create.Problem);

        var drive = new RangeDrive(stored, request.Offset, Math.Min(request.Length, stored.Size - request.Offset), create.Lease!, timeout);
        try
        {
            return await DriveRangeAsync(drive, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisposeLeaseQuietlyAsync(create.Lease!).ConfigureAwait(false);
        }
    }

    private static async Task<ArtifactCasRangeResult> DriveRangeAsync(RangeDrive drive, CancellationToken cancellationToken)
    {
        var head = await HeadForRangeAsync(drive, cancellationToken).ConfigureAwait(false);
        if (head.Problem != null) return new ArtifactCasRangeResult.Unavailable(head.Problem);

        var opened = await OpenWindowAsync(drive, head.Value!, cancellationToken).ConfigureAwait(false);
        if (opened.Problem != null) return new ArtifactCasRangeResult.Unavailable(opened.Problem);

        return await WindowBytesAsync(drive, opened.Value!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Confirms the provider still holds the object the ledger recorded before any window is requested.</summary>
    private static async Task<Invocation<ArtifactStorageObjectMetadata>> HeadForRangeAsync(RangeDrive drive, CancellationToken cancellationToken)
    {
        var driver = drive.Lease.Driver;
        var head = await InvokeAsync(token => driver.HeadAsync(new ArtifactStorageHeadRequest(drive.Stored.ObjectKey), token), drive.Timeout, cancellationToken, drive.Lease).ConfigureAwait(false);

        if (head.Problem != null) return new Invocation<ArtifactStorageObjectMetadata>(null, false, head.Problem);
        if (head.Timeout) return new Invocation<ArtifactStorageObjectMetadata>(null, true, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        if (head.Value?.Error != null) return new Invocation<ArtifactStorageObjectMetadata>(null, false, Map(head.Value.Error, readMissing: true));
        if (!MetadataMatches(drive.Stored, head.Value!.Metadata!, drive.Lease.Driver.Capabilities)) return new Invocation<ArtifactStorageObjectMetadata>(null, false, Problem(ArtifactCasProblemCode.TargetCorrupt));

        return new Invocation<ArtifactStorageObjectMetadata>(head.Value.Metadata!, false, null);
    }

    private static async Task<Invocation<Stream>> OpenWindowAsync(RangeDrive drive, ArtifactStorageObjectMetadata head, CancellationToken cancellationToken)
    {
        var driver = drive.Lease.Driver;
        var opened = await InvokeAsync(token => driver.OpenReadAsync(new ArtifactStorageReadRequest(drive.Stored.ObjectKey)
        {
            ExpectedETag = DurableETag(drive.Stored.ProviderETag, driver.Capabilities),
            ExpectedVersion = drive.Stored.ProviderObjectVersion,
            Range = new ArtifactStorageByteRange(drive.Offset, drive.Window),
        }, token), drive.Timeout, cancellationToken, drive.Lease).ConfigureAwait(false);

        if (opened.Problem != null) return new Invocation<Stream>(null, false, opened.Problem);
        if (opened.Timeout) return new Invocation<Stream>(null, true, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        if (opened.Value?.Error != null) return new Invocation<Stream>(null, false, Map(opened.Value.Error, readMissing: true));

        if (opened.Value!.TotalLength != drive.Stored.Size || opened.Value.ContentLength != drive.Window
            || !MetadataAgrees(head, opened.Value.Metadata!, drive.Stored.ObjectKey))
        {
            await opened.Value.Content!.DisposeAsync().ConfigureAwait(false);
            return new Invocation<Stream>(null, false, Problem(ArtifactCasProblemCode.TargetCorrupt));
        }

        return new Invocation<Stream>(opened.Value.Content!, false, null);
    }

    /// <summary>
    /// Drains exactly the window. A window carries no digest guarantee — the recorded digest covers the whole object —
    /// so the only integrity claim made here is that the provider delivered the length it agreed to.
    /// </summary>
    private static async Task<ArtifactCasRangeResult> WindowBytesAsync(RangeDrive drive, Stream content, CancellationToken cancellationToken)
    {
        await using var window = content;
        var bytes = new byte[drive.Window];

        try
        {
            await window.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return new ArtifactCasRangeResult.Unavailable(Problem(ArtifactCasProblemCode.TargetCorrupt));
        }
        catch (IOException)
        {
            return new ArtifactCasRangeResult.Unavailable(Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true));
        }

        return new ArtifactCasRangeResult.Available(bytes, drive.Stored.Size);
    }

    /// <summary>The freshest durably Available placement of one object under one profile revision.</summary>
    private async Task<ReadLocation?> StoredLocationAsync(Guid teamId, Guid artifactObjectId, Guid profileRevisionId, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        return await (from location in db.ArtifactLocation.AsNoTracking()
                      join artifact in db.ArtifactObject.AsNoTracking()
                          on new { location.TeamId, Id = location.ArtifactObjectId } equals new { artifact.TeamId, artifact.Id }
                      where location.TeamId == teamId && location.ArtifactObjectId == artifactObjectId
                          && location.StorageProfileRevisionId == profileRevisionId && location.State == ArtifactLocationState.Available
                      orderby location.VerifiedAt descending, location.Id
                      select new ReadLocation(location.ObjectKey, location.ProviderETag, location.ProviderObjectVersion, artifact.SizeBytes, artifact.Digest))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan Validate(ArtifactCasRangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty || request.ArtifactObjectId == Guid.Empty || request.StorageProfileId == Guid.Empty)
            throw new ArgumentException("Team, artifact object and storage profile ids are required.", nameof(request));
        if (request.StorageProfileRevision <= 0) throw new ArgumentOutOfRangeException(nameof(request), "A positive profile revision is required.");
        if (request.Offset < 0) throw new ArgumentOutOfRangeException(nameof(request), "A byte window cannot start before the object.");
        if (request.Length <= 0) throw new ArgumentOutOfRangeException(nameof(request), "A byte window must request at least one byte.");

        return ValidateTimeout(request.OperationTimeout);
    }

    private sealed record RangeDrive(ReadLocation Stored, long Offset, long Window, StorageRuntimeDriverLease Lease, TimeSpan Timeout);
}
