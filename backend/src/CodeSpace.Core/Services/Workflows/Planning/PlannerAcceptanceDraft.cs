using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Workflows.Planning;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>The versioned model wire contract. Stored plans and AgentTask keep their existing runtime acceptance vocabulary; only this explicit boundary maps typed model intent into it.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PlannerAcceptanceDraft
{
    public const int CurrentFormatVersion = 2;
    public required int FormatVersion { get; init; }
    public required BenchmarkGradingKind Kind { get; init; }
    public IReadOnlyList<string>? Argv { get; init; }
    public IReadOnlyList<string>? ArtifactPaths { get; init; }
    public IReadOnlyList<string>? OraclePaths { get; init; }
    public string? Description { get; init; }
    public AcceptanceRubric? Rubric { get; init; }
    public JsonElement? Schema { get; init; }

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    private static readonly JsonSerializerOptions ResponseReadOptions = new(PlannerSchema.Options) { Converters = { new ModelAcceptanceConverter(), new UnauthorableDropRecordConverter() } };

    /// <summary>
    /// The wire contract, checked IN ORDER. Every violation throws except one: an oracle kind chosen with no payload
    /// authored for it returns null and names itself in <paramref name="unboundPayload"/>, so the caller can keep the
    /// subtask without an oracle. The order is the reason this is one method and not a rule plus a predicate — a
    /// payload the server would have to REINTERPRET (argv on a file oracle) is rejected before the absence of the
    /// right one is ever considered, and a separate "is the payload missing?" test would not know that.
    ///
    /// <para>What that ordering decides, concretely — the boundary is narrow and the cases next to it are NOT:</para>
    /// <list type="bullet">
    /// <item><c>{"kind":"LlmJudge"}</c> with neither <c>artifactPaths</c> nor <c>rubric</c> DEGRADES: the payload gate
    /// runs first, so the subtask simply loses its oracle before the missing rubric is ever reached.</item>
    /// <item><c>{"kind":"LlmJudge","artifactPaths":["report.md"]}</c> with no rubric THROWS: the payload bound, so the
    /// reply is a judge oracle with nothing to judge against — an obligation the server would have to invent.</item>
    /// <item><c>{"kind":"TestsPass","artifactPaths":[]}</c> THROWS even though the array is empty: it is the WRONG
    /// payload for the chosen oracle, and that is reinterpretable intent, which is checked before absence.</item>
    /// </list>
    /// </summary>
    public SupervisorAcceptanceSpec? ToRuntime(out string? unboundPayload)
    {
        unboundPayload = null;
        if (FormatVersion != CurrentFormatVersion) throw new JsonException($"Unsupported planner acceptance formatVersion {FormatVersion}; expected {CurrentFormatVersion}.");
        var commandOracle = Kind == BenchmarkGradingKind.TestsPass;
        if (!commandOracle && Kind is not (BenchmarkGradingKind.ArtifactPresent or BenchmarkGradingKind.LlmJudge or BenchmarkGradingKind.CitationsResolve or BenchmarkGradingKind.ArtifactSchema))
            throw new JsonException($"Unsupported planner acceptance kind '{Kind}'.");
        if (commandOracle && ArtifactPaths is not null || !commandOracle && Argv is not null)
            throw new JsonException("Planner acceptance must supply argv for TestsPass or artifactPaths for a file oracle, never both or the other payload.");
        var payload = commandOracle ? Argv : ArtifactPaths;
        if (payload is not { Count: > 0 })
        {
            unboundPayload = $"Planner acceptance requires a non-empty {(commandOracle ? "argv" : "artifactPaths")} array.";
            return null;
        }
        if (payload.Any(value => value is null || value.Contains('\0')))
            throw new JsonException($"Planner acceptance requires a {(commandOracle ? "argv" : "artifactPaths")} array without null values or NUL characters.");
        if (commandOracle ? string.IsNullOrWhiteSpace(payload[0]) : payload.Any(string.IsNullOrWhiteSpace))
            throw new JsonException(commandOracle ? "Planner acceptance argv requires a non-blank executable; remaining arguments are preserved exactly." : "Planner acceptance artifactPaths requires non-blank file paths.");
        if (Kind != BenchmarkGradingKind.LlmJudge && Rubric is not null || Kind != BenchmarkGradingKind.ArtifactSchema && Schema is not null)
            throw new JsonException("Planner acceptance rubric/schema must belong to the selected oracle; unused requirements cannot be silently dropped.");
        if (Rubric?.Criteria?.Any(criterion => criterion is null) == true) throw new JsonException("Planner acceptance rubric criteria cannot contain null entries.");

        if (OraclePaths?.Any(value => string.IsNullOrWhiteSpace(value) || value.Contains('\0') || Path.IsPathRooted(value) || value.Split('/').Contains("..")) == true)
            throw new JsonException("Planner acceptance oraclePaths requires literal relative file paths without traversal.");
        var spec = new SupervisorAcceptanceSpec { Kind = Kind, Command = payload.ToArray(), OraclePaths = OraclePaths?.ToArray(), Description = Description, Rubric = Rubric, Schema = Schema };
        if (AgentAcceptanceContract.ValidateAuthored(spec) is { } error) throw new JsonException(error);
        return spec;
    }

    /// <summary>
    /// Fresh model replies accept only the known typed version. Historical persisted v1 plans continue to deserialize
    /// with AgentJson; they never pass through this model-response boundary.
    ///
    /// <para>Exactly ONE contract miss does not cost the plan: a subtask that chose an oracle kind and authored no
    /// payload for it binds with NO acceptance and is named in <paramref name="dropped"/>. That shape is a
    /// model-quality miss the provider's bounded re-ask has already been spent on, and the plan's other subtasks are
    /// not evidence about it. Every other violation still throws — the server never invents or repairs an argv or an
    /// artifact obligation, and it never reinterprets one payload as the other.</para>
    /// </summary>
    public static PlannedWorkflow? ReadResponse(JsonElement response, out IReadOnlyList<DroppedAcceptance> dropped)
    {
        var plan = response.Deserialize<PlannedWorkflow>(ResponseReadOptions);
        dropped = plan is null ? Array.Empty<DroppedAcceptance>() : DescribeDroppedAcceptance(response);
        return plan;
    }

    /// <summary>
    /// Name the subtasks whose acceptance the bind above dropped. The subtask id lives on the RAW subtask element —
    /// the converter only ever sees the acceptance object — so the drops are described by re-running the SAME
    /// <see cref="ToRuntime"/> over the raw drafts. One definition of the contract, read twice, rather than a second
    /// copy of the payload rule that could drift from the one the bind enforces.
    /// </summary>
    private static IReadOnlyList<DroppedAcceptance> DescribeDroppedAcceptance(JsonElement response)
    {
        if (!TryReadProperty(response, "subtasks", out var subtasks) || subtasks.ValueKind != JsonValueKind.Array) return Array.Empty<DroppedAcceptance>();

        var dropped = new List<DroppedAcceptance>();

        foreach (var subtask in subtasks.EnumerateArray())
        {
            if (!TryReadProperty(subtask, "acceptance", out var acceptance) || acceptance.ValueKind != JsonValueKind.Object) continue;

            if (DescribeUnboundPayload(acceptance) is not { } defect) continue;

            dropped.Add(new DroppedAcceptance { SubtaskId = ReadId(subtask), Kind = defect.Kind, Reason = defect.Reason });
        }

        return dropped;
    }

    /// <summary>Re-bind ONE raw acceptance draft and report only the droppable shape. A fatal violation cannot reach here — the plan-wide bind already threw on it and no plan came back to describe — so it is swallowed rather than reported twice.</summary>
    private static (BenchmarkGradingKind Kind, string Reason)? DescribeUnboundPayload(JsonElement acceptance)
    {
        try
        {
            var draft = acceptance.Deserialize<PlannerAcceptanceDraft>(WireOptions);

            if (draft is null) return null;

            return draft.ToRuntime(out var unbound) is null && unbound is not null ? (draft.Kind, unbound) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The raw-element read matches the case-insensitive binding the plan itself gets (<see cref="PlannerSchema.Options"/>), so a reply that shifts a key's case is described exactly as it was bound rather than dropping an acceptance nobody is told about.</summary>
    private static bool TryReadProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }

        value = default;
        return false;
    }

    private static string ReadId(JsonElement subtask) => TryReadProperty(subtask, "id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";

    /// <summary>
    /// <c>droppedAcceptances</c> is SERVER-stamped — the planner's own record of which acceptance it could not bind.
    /// The model schema does not declare it, but the schema check is deliberately lenient on additionalProperties and
    /// these options disallow no unmapped member, so a reply that invents the field would otherwise BIND a fabricated
    /// defect report, and one shaped <c>[{}]</c> would fail required-member binding — a NEW plan-killer inside the very
    /// change that exists to stop plans dying over model-quality misses. Read it as nothing;
    /// <c>LlmWorkflowPlanner.Deserialize</c> stamps the real value from its own bind, unconditionally.
    /// </summary>
    private sealed class UnauthorableDropRecordConverter : JsonConverter<IReadOnlyList<DroppedAcceptance>>
    {
        public override IReadOnlyList<DroppedAcceptance>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reader.Skip();   // consume whatever shape the model wrote (array / object / scalar) without binding any of it
            return null;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<DroppedAcceptance> value, JsonSerializerOptions options) => throw new NotSupportedException("Model response options are read-only; serialize normalized runtime plans with AgentJson.Options.");
    }

    private sealed class ModelAcceptanceConverter : JsonConverter<SupervisorAcceptanceSpec>
    {
        public override SupervisorAcceptanceSpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var draft = JsonSerializer.Deserialize<PlannerAcceptanceDraft>(ref reader, WireOptions) ?? throw new JsonException("Planner acceptance cannot be null when authored.");

            // Null ⇒ the one droppable shape: the subtask keeps its work with no oracle, and DescribeDroppedAcceptance
            // names it. Every other violation threw inside ToRuntime and takes the whole reply down, as before.
            return draft.ToRuntime(out _);
        }

        public override void Write(Utf8JsonWriter writer, SupervisorAcceptanceSpec value, JsonSerializerOptions options) => throw new NotSupportedException("Model response options are read-only; serialize normalized runtime plans with AgentJson.Options.");
    }
}
