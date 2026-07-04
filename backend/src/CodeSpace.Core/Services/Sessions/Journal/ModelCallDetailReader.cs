using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>Reads one model call's full detail (prompt · result · usage · trace) on demand for the journal drawer.</summary>
public interface IModelCallDetailReader
{
    /// <summary>The model call at <paramref name="sequence"/> in <paramref name="runId"/> (the completed interaction record), team-scoped. Null when the run isn't the team's, or the sequence isn't an interaction completion.</summary>
    Task<ModelCallDetail?> ReadAsync(Guid runId, long sequence, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IModelCallDetailReader"/> — reads the completed interaction record (by run + sequence) + its
/// paired start (by correlation id), and resolves any OFFLOADED prompt / result (a large field moved to a
/// content-addressed <c>$artifact_id</c> artifact by <c>RecordingLLMClientDecorator</c>) back to text via the team-scoped
/// <see cref="IArtifactStore"/>, so the drawer reads them whole. Team-checked up front (a foreign run → null, an
/// indistinguishable not-found). READ-ONLY.
/// </summary>
public sealed class ModelCallDetailReader : IModelCallDetailReader, IScopedDependency
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactStore _artifacts;

    public ModelCallDetailReader(CodeSpaceDbContext db, IArtifactStore artifacts)
    {
        _db = db;
        _artifacts = artifacts;
    }

    public async Task<ModelCallDetail?> ReadAsync(Guid runId, long sequence, Guid teamId, CancellationToken cancellationToken)
    {
        var owned = await _db.WorkflowRun.AsNoTracking().AnyAsync(r => r.Id == runId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        if (!owned) return null;

        var completed = await _db.WorkflowRunRecord.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RunId == runId && r.Sequence == sequence, cancellationToken).ConfigureAwait(false);

        if (completed is null || completed.RecordType is not (WorkflowRunRecordTypes.InteractionCompleted or WorkflowRunRecordTypes.InteractionFailed))
            return null;

        var started = completed.CorrelationId is not { } cid ? null : await _db.WorkflowRunRecord.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RunId == runId && r.CorrelationId == cid && r.RecordType == WorkflowRunRecordTypes.InteractionStarted, cancellationToken).ConfigureAwait(false);

        return new ModelCallDetail
        {
            Prompt = started is null ? null : await ResolvePromptAsync(started.PayloadJson, teamId, cancellationToken).ConfigureAwait(false),
            Result = await ResolveFieldAsync(completed.PayloadJson, "output", teamId, cancellationToken).ConfigureAwait(false),
            Usage = PrettyField(completed.PayloadJson, "usage"),
            Trace = BuildTrace(started, completed),
        };
    }

    /// <summary>The prompt as readable text — the caller stamps it as either a bare string, an offload ref, or a <c>{ system, user }</c> object (each part inline or offloaded). Resolves each part and labels the two sections.</summary>
    private async Task<string?> ResolvePromptAsync(string payloadJson, Guid teamId, CancellationToken cancellationToken)
    {
        if (!TryGetField(payloadJson, "prompt", out var prompt)) return null;

        if (prompt.ValueKind == JsonValueKind.Object && (prompt.TryGetProperty("system", out _) || prompt.TryGetProperty("user", out _)))
        {
            var system = prompt.TryGetProperty("system", out var sys) ? await ResolveElementAsync(sys, teamId, cancellationToken).ConfigureAwait(false) : null;
            var user = prompt.TryGetProperty("user", out var usr) ? await ResolveElementAsync(usr, teamId, cancellationToken).ConfigureAwait(false) : null;

            return JoinSections(("SYSTEM", system), ("USER", user));
        }

        return await ResolveElementAsync(prompt, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One payload field (e.g. the completion's <c>output</c>) resolved to text — inline string as-is, an offload ref fetched, else pretty JSON. Null when the field is absent / the payload is malformed.</summary>
    private async Task<string?> ResolveFieldAsync(string payloadJson, string field, Guid teamId, CancellationToken cancellationToken)
    {
        if (!TryGetField(payloadJson, field, out var el)) return null;

        return await ResolveElementAsync(el, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An element to text: a string is itself; an offload ref (<c>{ "$artifact_id": … }</c>) is fetched from the team-scoped store; anything else is pretty JSON. A missing artifact reads null (never a throw).</summary>
    private async Task<string?> ResolveElementAsync(JsonElement el, Guid teamId, CancellationToken cancellationToken)
    {
        if (el.ValueKind == JsonValueKind.String) return el.GetString();

        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("$artifact_id", out var idEl)
            && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var artifactId))
        {
            var bytes = await _artifacts.GetBytesAsync(teamId, artifactId, cancellationToken).ConfigureAwait(false);

            return bytes is null ? null : Encoding.UTF8.GetString(bytes.Bytes);
        }

        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;

        return JsonSerializer.Serialize(el, Pretty);
    }

    private static string? PrettyField(string payloadJson, string field) =>
        TryGetField(payloadJson, field, out var el) && el.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? JsonSerializer.Serialize(el, Pretty) : null;

    private static bool TryGetField(string payloadJson, string field, out JsonElement value)
    {
        value = default;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);

            if (!doc.RootElement.TryGetProperty(field, out var el)) return false;

            value = el.Clone();   // clone so it outlives the disposed document
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The raw ledger records (started + completed payloads) as pretty JSON — the audit trace, offload refs left visible.</summary>
    private static string BuildTrace(WorkflowRunRecord? started, WorkflowRunRecord completed)
    {
        var trace = new StringBuilder();

        if (started is not null) trace.Append("── interaction.started ──\n").Append(PrettyOrRaw(started.PayloadJson)).Append("\n\n");

        trace.Append($"── {completed.RecordType} ──\n").Append(PrettyOrRaw(completed.PayloadJson));

        return trace.ToString();
    }

    private static string PrettyOrRaw(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, Pretty);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string? JoinSections(params (string Label, string? Text)[] sections)
    {
        var present = sections.Where(s => !string.IsNullOrWhiteSpace(s.Text)).Select(s => $"{s.Label}\n{s.Text!.Trim()}").ToList();

        return present.Count == 0 ? null : string.Join("\n\n", present);
    }
}
