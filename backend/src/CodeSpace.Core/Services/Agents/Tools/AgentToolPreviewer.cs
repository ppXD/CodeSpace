using System.Text.Json;
using Autofac;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// Resolves an agent's call to a node tool into the <see cref="ToolCallPreview"/> its approval card shows, from the node's
/// manifest alone: every declared input the call names, the repository input by the repository's path and how the run is
/// bound to it, and — for a node that names a pull request (<see cref="RepositoryInputSpec.PullRequestInputKey"/>) — the
/// pull request read from the provider: its title, its head (repository, branch, commit) and its base. A head in a
/// repository the run is not bound to (a fork) is flagged. A node that pins the head
/// (<see cref="RepositoryInputSpec.HeadShaInputKey"/>) or the base (<see cref="RepositoryInputSpec.BaseBranchInputKey"/>)
/// gets the one shown as its pin, so the approved call runs only at the commit, and into the branch, the reviewer saw —
/// and its target is judged with those pins, so new commits are a new request.
/// </summary>
public interface IAgentToolPreviewer
{
    /// <summary>The preview of <paramref name="call"/> to a node with <paramref name="manifest"/>, whose inputs as the node reads them are <paramref name="inputs"/>. Throws <see cref="ToolCallPreviewException"/> when the pull request it names cannot be read or its head is not the one the call names.</summary>
    Task<ToolCallPreview> PreviewAsync(NodeManifest manifest, AgentToolCall call, IReadOnlyDictionary<string, JsonElement> inputs, CancellationToken cancellationToken);
}

/// <summary>
/// Reads in a child of the owning scope, because one run's tool catalog serves concurrent calls and must not share a
/// DbContext between them (as <see cref="AgentRepositoryPolicy"/> does). The pull request is read with the repository's
/// connection credential, the one the call itself would act with.
/// </summary>
public sealed class AgentToolPreviewer(ILifetimeScope owner) : IAgentToolPreviewer, IScopedDependency
{
    public async Task<ToolCallPreview> PreviewAsync(NodeManifest manifest, AgentToolCall call, IReadOnlyDictionary<string, JsonElement> inputs, CancellationToken cancellationToken)
    {
        await using var scope = owner.BeginLifetimeScope();

        var paths = await LoadRepositoryPathsAsync(scope.Resolve<CodeSpaceDbContext>(), manifest, call, inputs, cancellationToken).ConfigureAwait(false);
        var pullRequest = await ReadPullRequestAsync(scope.Resolve<IPullRequestService>(), manifest, call, inputs, cancellationToken).ConfigureAwait(false);

        return Compose(manifest, call, inputs, paths, pullRequest);
    }

    /// <summary>The preview from what was read: the repository paths (the named one and the run's bound ones) and the pull request, if the node names one. Its target is judged with its pins written over the inputs: what the approved call would run with. Pure — pinned against the production manifests.</summary>
    internal static ToolCallPreview Compose(NodeManifest manifest, AgentToolCall call, IReadOnlyDictionary<string, JsonElement> inputs, IReadOnlyDictionary<Guid, string> paths, RemotePullRequest? pullRequest)
    {
        var pins = Pins(manifest, inputs, pullRequest);

        return new ToolCallPreview
        {
            Target = AgentToolInputs.Target(manifest, AgentToolInputs.WithPins(inputs, pins)),
            Lines = [.. InputLines(manifest, call, inputs, paths), .. PullRequestLines(manifest, call, inputs, pullRequest, paths)],
            Pins = pins,
        };
    }

    // ── Inputs ───────────────────────────────────────────────────────────────

    /// <summary>Each declared input the call names, in the schema's order. A pinned input is shown with the pull request, as what it pins.</summary>
    private static IEnumerable<ToolCallPreviewLine> InputLines(NodeManifest manifest, AgentToolCall call, IReadOnlyDictionary<string, JsonElement> inputs, IReadOnlyDictionary<Guid, string> paths)
    {
        foreach (var key in AgentToolInputs.Declared(manifest.InputSchema))
        {
            if (IsPinKey(manifest, key) || !inputs.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null) continue;

            yield return key == manifest.RepositoryInput?.InputKey ? RepositoryLine(call, value, paths) : ToolCallPreviews.Line(key, value);
        }
    }

    /// <summary>The repository by its path, labelled with how the run is bound to it; one the run is not bound to is flagged. A value that is no uuid is shown as given — the node refuses it itself.</summary>
    private static ToolCallPreviewLine RepositoryLine(AgentToolCall call, JsonElement value, IReadOnlyDictionary<Guid, string> paths)
    {
        if (!TryReadGuid(value, out var id)) return ToolCallPreviews.Line("repository", value);

        var bound = call.CallerPosture is { } caller ? AgentRepositoryBinding.Find(caller, id) : null;
        var label = bound is null ? "repository" : AgentRepositoryBinding.AllowsWrite(bound) ? "repository (bound, writable)" : "repository (bound, read-only)";

        return new ToolCallPreviewLine { Label = label, Value = paths.GetValueOrDefault(id) ?? id.ToString(), OutsideRun = call.CallerPosture is not null && bound is null };
    }

    private static bool IsPinKey(NodeManifest manifest, string key) => key == manifest.RepositoryInput?.HeadShaInputKey || key == manifest.RepositoryInput?.BaseBranchInputKey;

    // ── Pull request ─────────────────────────────────────────────────────────

    /// <summary>The pull request the call names, read with the repository's connection credential — null when the node names none or the call names none it could act on (the node then refuses the call itself).</summary>
    private static async Task<RemotePullRequest?> ReadPullRequestAsync(IPullRequestService pullRequests, NodeManifest manifest, AgentToolCall call, IReadOnlyDictionary<string, JsonElement> inputs, CancellationToken cancellationToken)
    {
        if (manifest.RepositoryInput is not { PullRequestInputKey: { } numberKey } spec || call.TeamId is not { } teamId) return null;

        if (!inputs.TryGetValue(spec.InputKey, out var repository) || !TryReadGuid(repository, out var repositoryId) || !TryReadNumber(inputs, numberKey, out var number)) return null;

        try
        {
            return await pullRequests.GetAsync(repositoryId, teamId, number, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ToolCallPreviewException($"Couldn't read pull request #{number} of repository {repositoryId} to show it for approval, so it was not put to a reviewer: {ex.Message}");
        }
    }

    /// <summary>What a reviewer needs of the pull request: its title, its head — flagged when it lives outside the run's repositories — its head commit and its base, each labelled as pinned when the node pins it.</summary>
    private static IEnumerable<ToolCallPreviewLine> PullRequestLines(NodeManifest manifest, AgentToolCall call, IReadOnlyDictionary<string, JsonElement> inputs, RemotePullRequest? pullRequest, IReadOnlyDictionary<Guid, string> paths)
    {
        if (pullRequest is null) yield break;

        var basePath = inputs.TryGetValue(manifest.RepositoryInput!.InputKey, out var repository) && TryReadGuid(repository, out var id) ? paths.GetValueOrDefault(id) : null;

        yield return new ToolCallPreviewLine { Label = "pull request", Value = $"#{pullRequest.Number} {pullRequest.Title} ({pullRequest.State})" };
        yield return new ToolCallPreviewLine { Label = "head", Value = $"{pullRequest.HeadRepositoryFullPath ?? "a repository the provider did not name"}:{pullRequest.SourceBranch}", OutsideRun = HeadOutsideRun(call, pullRequest, paths) };
        if (pullRequest.HeadSha is { } sha) yield return new ToolCallPreviewLine { Label = manifest.RepositoryInput.HeadShaInputKey is null ? "head commit" : "pinned head commit", Value = sha };
        yield return new ToolCallPreviewLine { Label = manifest.RepositoryInput.BaseBranchInputKey is null ? "base" : "pinned base", Value = $"{basePath ?? "this repository"}:{pullRequest.TargetBranch}" };
    }

    /// <summary>A head is inside the run when it lives in a repository the run is bound to. One the provider does not name is outside.</summary>
    private static bool HeadOutsideRun(AgentToolCall call, RemotePullRequest pullRequest, IReadOnlyDictionary<Guid, string> paths)
    {
        if (call.CallerPosture is not { } caller) return false;

        var boundPaths = caller.Repositories.Select(bound => paths.GetValueOrDefault(bound.RepositoryId)).OfType<string>();

        return pullRequest.HeadRepositoryFullPath is not { } head || !boundPaths.Contains(head, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The head and base the reviewer is shown, pinned on the node's head and base inputs when it declares them. A call that
    /// names one of its own must name the one shown — otherwise the reviewer would approve a commit or a branch the call
    /// never meant to act on. A pull request whose head the provider did not report cannot be pinned, so it is not put to
    /// a reviewer.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Pins(NodeManifest manifest, IReadOnlyDictionary<string, JsonElement> inputs, RemotePullRequest? pullRequest)
    {
        var pins = new Dictionary<string, string>();

        if (manifest.RepositoryInput is not { } spec || pullRequest is null) return pins;

        if (spec.HeadShaInputKey is { } headKey) pins[headKey] = PinnedHead(inputs, headKey, pullRequest);
        if (spec.BaseBranchInputKey is { } baseKey) pins[baseKey] = PinnedBase(inputs, baseKey, pullRequest);

        return pins;
    }

    private static string PinnedHead(IReadOnlyDictionary<string, JsonElement> inputs, string key, RemotePullRequest pullRequest)
    {
        if (pullRequest.HeadSha is not { Length: > 0 } head)
            throw new ToolCallPreviewException($"The provider did not report the head commit of pull request #{pullRequest.Number}, so the call cannot be pinned to what a reviewer would see and was not put to one.");

        if (Named(inputs, key) is { } given && !string.Equals(given, head, StringComparison.OrdinalIgnoreCase))
            throw new ToolCallPreviewException($"Pull request #{pullRequest.Number}'s head is {head}, not the {key} {given} this call names, so it was not put to a reviewer. Read what changed before asking again.");

        return head;
    }

    /// <summary>The base the reviewer is shown. A branch name is compared exactly, as git compares it.</summary>
    private static string PinnedBase(IReadOnlyDictionary<string, JsonElement> inputs, string key, RemotePullRequest pullRequest)
    {
        if (Named(inputs, key) is { } given && !string.Equals(given, pullRequest.TargetBranch, StringComparison.Ordinal))
            throw new ToolCallPreviewException($"Pull request #{pullRequest.Number}'s base is {pullRequest.TargetBranch}, not the {key} {given} this call names, so it was not put to a reviewer. Read what changed before asking again.");

        return pullRequest.TargetBranch;
    }

    /// <summary>The value a call names for <paramref name="key"/> as the node reads it — a JSON string, trimmed — or null for none.</summary>
    private static string? Named(IReadOnlyDictionary<string, JsonElement> inputs, string key) =>
        inputs.TryGetValue(key, out var named) && named.ValueKind == JsonValueKind.String && named.GetString()?.Trim() is { Length: > 0 } given ? given : null;

    // ── Reads ────────────────────────────────────────────────────────────────

    /// <summary>The paths of the repository the call names and of every repository the run is bound to — team-scoped, so another team's id resolves to nothing.</summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> LoadRepositoryPathsAsync(CodeSpaceDbContext db, NodeManifest manifest, AgentToolCall call, IReadOnlyDictionary<string, JsonElement> inputs, CancellationToken cancellationToken)
    {
        if (call.TeamId is not { } teamId || manifest.RepositoryInput is not { } spec) return new Dictionary<Guid, string>();

        var ids = (call.CallerPosture?.Repositories ?? []).Select(bound => bound.RepositoryId).ToList();
        if (inputs.TryGetValue(spec.InputKey, out var repository) && TryReadGuid(repository, out var named)) ids.Add(named);

        return await db.Repository.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.TeamId == teamId && r.DeletedDate == null)
            .ToDictionaryAsync(r => r.Id, r => r.FullPath, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryReadGuid(JsonElement value, out Guid id)
    {
        id = Guid.Empty;

        return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out id);
    }

    private static bool TryReadNumber(IReadOnlyDictionary<string, JsonElement> inputs, string key, out int number)
    {
        number = 0;

        return inputs.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
}
