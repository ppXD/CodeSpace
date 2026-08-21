using CodeSpace.Messages.Dtos.Sessions.Room;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// Resolves a GENERIC preview of one file a turn produced — from the producing agent's captured diff (durable,
/// offline, any repo, single- or multi-repo, supervisor or plain-agent turn). Team-scoped: returns null for a foreign
/// / missing run (indistinguishable not-found), and a graceful <c>unavailable</c> preview — never throws — for a file
/// that isn't reconstructable.
/// </summary>
public interface IRoomFilePreviewService
{
    /// <summary>Legacy single-repo/path-only read. Preserved byte-for-byte when the path identifies one repository; ambiguous multi-repo paths return a typed unavailable preview.</summary>
    Task<RoomFilePreview?> PreviewAsync(Guid runId, string path, Guid teamId, Guid? agentRunId, CancellationToken cancellationToken);

    /// <summary>Identity-aware read used by multi-repo room rows.</summary>
    Task<RoomFilePreview?> PreviewAsync(Guid runId, RoomFileIdentity identity, Guid teamId, CancellationToken cancellationToken);
}
