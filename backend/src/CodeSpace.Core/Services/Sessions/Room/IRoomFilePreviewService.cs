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
    Task<RoomFilePreview?> PreviewAsync(Guid runId, string path, Guid teamId, Guid? agentRunId, CancellationToken cancellationToken);
}
