using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Context;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// The first-party READ-ONLY <c>get_context</c> tool — the agent's PULL handle for prior work context, complementing
/// the digest it is PUSHED at launch. It is a thin dispatcher over the <see cref="IContextSourceRegistry"/>: it resolves
/// the calling run's scope (its team from the trusted <see cref="AgentToolCall.TeamId"/>, its work thread via
/// <c>AgentRun → WorkflowRun.SessionId</c>) ONCE, then forwards a source-agnostic <see cref="AgentContextQuery"/> to the
/// named source (or every source when none is named). New retrievable context plugs in as an <see cref="IContextSource"/>
/// with ZERO edit here (Rule 18.3, the variant axis).
///
/// <para>Read-only ⇒ it never touches the ledger / approval gate and runs at EVERY autonomy tier (a safe read is always
/// allowed). It owns no scoped state: the sources read per-request DB state, so it mints a FRESH DI scope per call
/// (<see cref="IServiceScopeFactory"/>) rather than capturing the run's shared MCP scope — the same captive-dependency
/// avoidance the in-process LLM plane uses, and the reason concurrent connections can't race a shared DbContext.</para>
/// </summary>
public sealed class GetContextTool : IAgentTool
{
    /// <summary>The reserved tool name the model calls and the registry keys on.</summary>
    public const string ToolKind = "get_context";

    private readonly IServiceScopeFactory _scopeFactory;

    public GetContextTool(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Kind => ToolKind;

    public string Description =>
        "Retrieve prior context for THIS work thread on demand — the full version of what was summarized into your " +
        "launch briefing. Call with no arguments to pull every available source; or name one 'source'. Optional 'query' " +
        "refines a source (e.g. filters prior turns to those mentioning it).";

    public JsonElement InputSchema { get; } = BuildInputSchema();

    public JsonElement OutputSchema { get; } = BuildOutputSchema();

    // A pure read: safe to repeat, safe to run concurrently, never a side effect, never gated.
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;
    public bool IsDestructive => false;
    public bool RequiresApproval => false;

    public AgentToolValidation ValidateInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return AgentToolValidation.Invalid("get_context input must be a JSON object.");

        if (input.TryGetProperty("source", out var s) && s.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            return AgentToolValidation.Invalid("'source' must be a string (a context source kind) when provided.");

        if (input.TryGetProperty("query", out var q) && q.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            return AgentToolValidation.Invalid("'query' must be a string when provided.");

        return AgentToolValidation.Valid;
    }

    public async Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken)
    {
        // No trusted run/team identity → nothing to scope a retrieval to. A clean miss (found:false), not an error.
        if (call.RunId is not { } runId || runId == Guid.Empty || call.TeamId is not { } teamId)
            return Ok(found: false, source: "none", text: "No run context is available, so there is nothing to retrieve.");

        // Fresh per-call scope: the sources read per-request DB state, and the tool instance is shared across the run's
        // concurrent MCP connections — a captured DbContext would race. Resolve the registry + the run→session lookup
        // inside it (same posture as the in-process LLM plane).
        using var scope = _scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IContextSourceRegistry>();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var sessionId = await ResolveSessionIdAsync(db, runId, teamId, cancellationToken).ConfigureAwait(false);

        var query = new AgentContextQuery { TeamId = teamId, RunId = runId, SessionId = sessionId, Query = ReadString(call.Input, "query") };

        var requested = ReadString(call.Input, "source");

        return requested is null
            ? await RetrieveAllAsync(registry, query, cancellationToken).ConfigureAwait(false)
            : await RetrieveOneAsync(registry, requested, query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolve the calling agent run to its work thread: <c>AgentRun(runId, teamId) → WorkflowRun.SessionId</c>, both legs team-scoped. Null when the run is unknown, session-less, or cross-team (fail-closed).</summary>
    private static async Task<Guid?> ResolveSessionIdAsync(CodeSpaceDbContext db, Guid runId, Guid teamId, CancellationToken cancellationToken) =>
        await db.AgentRun.AsNoTracking()
            .Where(a => a.Id == runId && a.TeamId == teamId && a.WorkflowRunId != null)
            .Join(db.WorkflowRun.AsNoTracking(), a => a.WorkflowRunId, w => w.Id, (a, w) => new { w.SessionId, WorkflowTeamId = w.TeamId })
            .Where(x => x.WorkflowTeamId == teamId)
            .Select(x => x.SessionId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Dispatch to ONE named source; an unknown name is a teachable error listing what IS available.</summary>
    private static async Task<AgentToolResult> RetrieveOneAsync(IContextSourceRegistry registry, string requested, AgentContextQuery query, CancellationToken cancellationToken)
    {
        if (!registry.TryResolve(requested, out var source))
            return AgentToolResult.Fail($"Unknown context source '{requested}'. Available: {AvailableKinds(registry)}.");

        var result = await source.RetrieveAsync(query, cancellationToken).ConfigureAwait(false);

        return result.Found
            ? Ok(found: true, source.Kind, result.Text)
            : Ok(found: false, source.Kind, $"No '{source.Kind}' context is available for this run yet.");
    }

    /// <summary>Pull EVERY source, concatenate the ones that returned content (each carries its own heading). A first call with no arguments is also how the model discovers which sources exist.</summary>
    private static async Task<AgentToolResult> RetrieveAllAsync(IContextSourceRegistry registry, AgentContextQuery query, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var found = false;

        foreach (var source in registry.All)
        {
            var result = await source.RetrieveAsync(query, cancellationToken).ConfigureAwait(false);

            if (!result.Found) continue;

            if (found) sb.AppendLine().AppendLine();

            sb.Append(result.Text);
            found = true;
        }

        return found
            ? Ok(found: true, source: "all", sb.ToString())
            : Ok(found: false, source: "all", $"No prior context is available for this run yet. Sources checked: {AvailableKinds(registry)}.");
    }

    private static string AvailableKinds(IContextSourceRegistry registry) => string.Join(", ", registry.All.Select(s => s.Kind));

    private static AgentToolResult Ok(bool found, string source, string text)
    {
        var json = JsonSerializer.SerializeToElement(new { found, source, text }, AgentJson.Options);

        return AgentToolResult.Ok(json, json.GetRawText().Length);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    private static JsonElement BuildInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            source = new { type = "string", description = "Which context source to read (e.g. 'session.turns', 'session.summary'). Omit to pull every available source — also how to discover what exists." },
            query = new { type = "string", description = "Optional refinement the source interprets (session.turns filters to prior turns mentioning it)." },
        },
    }, AgentJson.Options);

    private static JsonElement BuildOutputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "found", "source", "text" },
        properties = new
        {
            found = new { type = "boolean", description = "True when context was returned; false is a clean 'nothing here', not an error." },
            source = new { type = "string", description = "Which source produced this ('all' when every source was pulled, 'none' when the run had no context to scope to)." },
            text = new { type = "string", description = "The retrieved context." },
        },
    }, AgentJson.Options);
}
