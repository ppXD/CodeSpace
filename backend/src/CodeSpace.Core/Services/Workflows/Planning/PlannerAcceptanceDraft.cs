using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Workflows.Planning;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// The versioned model wire contract. Stored plans and AgentTask keep their existing runtime acceptance vocabulary;
/// only this explicit boundary maps typed model intent into it.
///
/// <para>Its verdict is binary and its consequence is not: an acceptance that binds becomes an oracle, and one that
/// does not is DROPPED with the reason it failed on — never partially interpreted, never assumed to be v2, never
/// fatal to the plan that carries it. <see cref="Bind"/> is the single definition both the runtime bind and the
/// re-ask's schema attribution read.</para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PlannerAcceptanceDraft
{
    public const int CurrentFormatVersion = 2;

    /// <summary>The wire version, deliberately NOT <c>required</c>: an absent one is a contract verdict this boundary must be able to REPORT next to whatever else the acceptance got wrong, not a bind failure that hides it. It is still never reinterpreted — anything but <see cref="CurrentFormatVersion"/> refuses to bind.</summary>
    public int? FormatVersion { get; init; }

    /// <summary>The oracle name as AUTHORED, parsed by this contract rather than by the serializer for the same reason: an oracle the instrument does not have a grader for has to be reportable in words that name the value the model chose.</summary>
    public string? Kind { get; init; }

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

    /// <summary>The oracle names this boundary binds — the kinds an <c>IBenchmarkGrader</c> exists for. Matched case-insensitively, like the serializer's own enum read, but by NAME only: a numeric string is not a name, so it cannot smuggle in a documented-but-unbuilt kind.</summary>
    private static readonly IReadOnlyDictionary<string, BenchmarkGradingKind> OracleKinds = new Dictionary<string, BenchmarkGradingKind>(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(BenchmarkGradingKind.TestsPass)] = BenchmarkGradingKind.TestsPass,
        [nameof(BenchmarkGradingKind.ArtifactPresent)] = BenchmarkGradingKind.ArtifactPresent,
        [nameof(BenchmarkGradingKind.LlmJudge)] = BenchmarkGradingKind.LlmJudge,
        [nameof(BenchmarkGradingKind.CitationsResolve)] = BenchmarkGradingKind.CitationsResolve,
        [nameof(BenchmarkGradingKind.ArtifactSchema)] = BenchmarkGradingKind.ArtifactSchema,
    };

    /// <summary>
    /// The wire contract. It either binds an oracle or says, in <paramref name="defect"/>, why it could not — it never
    /// throws and it never reinterprets: an acceptance this contract cannot bind is DROPPED with its reason, and the
    /// plan around it lives. Severity is no longer carried by an exception, because no acceptance-level defect is
    /// fatal to a plan any more (live run 34093741284: 4 of 4 subtasks, i.e. plan-map Launch broken on that model).
    ///
    /// <para>The VERSION is reported without short-circuiting the oracle walk, which is the one ordering decision left
    /// in here. A reply that omits <c>formatVersion</c> almost always omits the payload too — that run's model omitted
    /// both on every acceptance — and there is exactly ONE bounded re-ask to spend, so advice naming only the version
    /// would buy a second reply with the same payload still missing.</para>
    ///
    /// <para>Inside the oracle half the walk is still ORDERED — see <see cref="DescribePayloadDefect"/>.</para>
    /// </summary>
    public SupervisorAcceptanceSpec? ToRuntime(out string? defect)
    {
        var version = FormatVersion == CurrentFormatVersion ? null : $"Planner acceptance requires formatVersion {CurrentFormatVersion}; this one authored {(FormatVersion is null ? "none" : FormatVersion.Value.ToString())}.";
        var spec = BindOracle(out var oracle);

        defect = version is null ? oracle : oracle is null ? version : version + " " + oracle;

        return defect is null ? spec : null;
    }

    /// <summary>The oracle half of the contract: the kind first (nothing else can be judged without it), then the payload walk, then the runtime contract's own verdict on the assembled spec.</summary>
    private SupervisorAcceptanceSpec? BindOracle(out string? defect)
    {
        if (!OracleKinds.TryGetValue(Kind ?? "", out var kind))
        {
            defect = $"Planner acceptance requires one of these oracle kinds: {string.Join(", ", OracleKinds.Keys)}; this one authored {(Kind is null ? "none" : $"'{Cap(Kind)}'")}.";
            return null;
        }

        var commandOracle = kind == BenchmarkGradingKind.TestsPass;
        var payload = commandOracle ? Argv : ArtifactPaths;

        defect = DescribePayloadDefect(kind, commandOracle, payload);

        if (defect is not null) return null;

        var spec = new SupervisorAcceptanceSpec { Kind = kind, Command = payload!.ToArray(), OraclePaths = OraclePaths?.ToArray(), Description = Description, Rubric = Rubric, Schema = Schema };   // non-empty: the walk above passed

        // ArtifactPresent's OWN companion (P2.6 — validated as whichever content oracle it pairs with, even though
        // the spec's Kind itself stays ArtifactPresent here: ReconcileArtifactPresent is the one place that PROMOTES
        // it, and only when the operator's declared-deliverable set does not exclude the path).
        var validationKind = kind != BenchmarkGradingKind.ArtifactPresent ? kind
            : Rubric is not null ? BenchmarkGradingKind.LlmJudge
            : Schema is not null ? BenchmarkGradingKind.ArtifactSchema
            : kind;

        defect = AgentAcceptanceContract.ValidateAuthored(spec with { Kind = validationKind }) is { } invalid ? $"Planner acceptance {invalid}" : null;

        return defect is null ? spec : null;
    }

    /// <summary>
    /// The FIRST thing wrong with the authored payload, in contract order, or null when it is sound. The order is
    /// load-bearing for what an operator and the re-ask are TOLD: a payload the server would have to REINTERPRET
    /// (<c>argv</c> on a file oracle, even an empty <c>artifactPaths</c> on <c>TestsPass</c>) is named before the
    /// absence of the right one, so the reason reads "you sent the other payload" and never the misleading
    /// "you sent no payload".
    /// </summary>
    private string? DescribePayloadDefect(BenchmarkGradingKind kind, bool commandOracle, IReadOnlyList<string>? payload)
    {
        var name = commandOracle ? "argv" : "artifactPaths";

        if (commandOracle && ArtifactPaths is not null || !commandOracle && Argv is not null) return "Planner acceptance must supply argv for TestsPass or artifactPaths for a file oracle, never both or the other payload.";
        if (payload is not { Count: > 0 }) return $"Planner acceptance requires a non-empty {name} array.";
        if (payload.Any(value => value is null || value.Contains('\0'))) return $"Planner acceptance requires a {name} array without null values or NUL characters.";
        if (commandOracle ? string.IsNullOrWhiteSpace(payload[0]) : payload.Any(string.IsNullOrWhiteSpace))
            return commandOracle ? "Planner acceptance argv requires a non-blank executable; remaining arguments are preserved exactly." : "Planner acceptance artifactPaths requires non-blank file paths.";
        // P2.6: ArtifactPresent may additionally carry the OTHER kind's own payload as a content-oracle companion —
        // still never both at once, and still never a payload neither the kind nor its companion role owns.
        var rubricOwner = kind is BenchmarkGradingKind.LlmJudge or BenchmarkGradingKind.ArtifactPresent;
        var schemaOwner = kind is BenchmarkGradingKind.ArtifactSchema or BenchmarkGradingKind.ArtifactPresent;

        if (!rubricOwner && Rubric is not null || !schemaOwner && Schema is not null)
            return "Planner acceptance rubric/schema must belong to the selected oracle; unused requirements cannot be silently dropped.";
        if (kind == BenchmarkGradingKind.ArtifactPresent && Rubric is not null && Schema is not null)
            return "Planner acceptance ArtifactPresent may pair with an ArtifactSchema or an LlmJudge content check, never both.";
        if (Rubric?.Criteria?.Any(criterion => criterion is null) == true) return "Planner acceptance rubric criteria cannot contain null entries.";
        if (OraclePaths?.Any(value => string.IsNullOrWhiteSpace(value) || value.Contains('\0') || Path.IsPathRooted(value) || value.Split('/').Contains("..")) == true)
            return "Planner acceptance oraclePaths requires literal relative file paths without traversal.";

        return null;
    }

    /// <summary>
    /// Bind ONE raw acceptance object, or say why it cannot be bound. This is the SINGLE definition of the droppable
    /// classification both sides of the re-ask read: an acceptance the typed contract cannot turn into an oracle — for
    /// ANY reason — is DROPPED and reported, never reinterpreted and never fatal to the plan.
    ///
    /// <para>A shape the wire record itself cannot hold (a v1 <c>command</c> key, a wrongly-typed field) is caught here
    /// rather than at the reply boundary. That was the last route by which one subtask's acceptance could still take a
    /// whole plan down.</para>
    ///
    /// <para>A PRESENT acceptance that is not an object at all (<c>null</c>, <c>[]</c>, <c>""</c>, <c>5</c>) is a defect
    /// like any other, NOT "no oracle authored". Reading it as an absence reported nothing and claimed no path, so the
    /// schema's own <c>expected type 'object' but got null</c> at that position stayed unexplained — i.e. fatal — and
    /// <c>"acceptance": null</c> is exactly what a model writes when it has no oracle to offer.</para>
    /// </summary>
    internal static SupervisorAcceptanceSpec? Bind(JsonElement acceptance, out string? defect)
    {
        defect = null;

        if (acceptance.ValueKind != JsonValueKind.Object)
        {
            defect = $"Planner acceptance must be a JSON object; this one authored {DescribeKind(acceptance.ValueKind)}.";
            return null;
        }

        try
        {
            var draft = acceptance.Deserialize<PlannerAcceptanceDraft>(WireOptions);

            return draft is null ? null : draft.ToRuntime(out defect);
        }
        catch (JsonException failure)
        {
            defect = Describe(failure);
            return null;
        }
    }

    /// <summary>
    /// Fresh model replies accept only the known typed version. Historical persisted v1 plans continue to deserialize
    /// with AgentJson; they never pass through this model-response boundary.
    ///
    /// <para>No acceptance-level miss costs the plan: a subtask whose acceptance <see cref="Bind"/> refuses binds with
    /// NO acceptance and is named in <paramref name="dropped"/>. Those are model-quality misses the provider's bounded
    /// re-ask has already been spent on, and the plan's other subtasks are not evidence about them. Nothing is
    /// reinterpreted on the way out — a dropped acceptance is an absence, never a repaired or assumed oracle — and a
    /// violation of the plan's own SHAPE (a subtask with no title, <c>subtasks</c> that is not an array of objects)
    /// still throws, because there is no subtask left to degrade.</para>
    /// </summary>
    public static PlannedWorkflow? ReadResponse(JsonElement response, out IReadOnlyList<DroppedAcceptance> dropped)
    {
        var plan = response.Deserialize<PlannedWorkflow>(ResponseReadOptions);
        dropped = plan is null ? Array.Empty<DroppedAcceptance>() : DescribeUnboundAcceptances(response).Select(unbound => unbound.Drop).ToArray();
        return plan;
    }

    /// <summary>
    /// P2.6: a planner-authored <c>ArtifactPresent</c> grades mere existence, and the SAME subtask usually also
    /// instructs the agent to WRITE that path — so a bare check is self-certifying: nothing outside the agent's own
    /// output ever contradicts it. Pairing with a content-oracle companion (a <c>Rubric</c>/<c>Schema</c> authored
    /// alongside <c>ArtifactPresent</c> and already validated as complete in <see cref="BindOracle"/>) is always
    /// required — a bare check is dropped no matter what <paramref name="declaredDeliverablePaths"/> says.
    /// Membership is required ONLY once a declaration source exists: <paramref name="declaredDeliverablePaths"/>
    /// <c>null</c> means no caller has wired one yet, so a paired acceptance is admitted on trust; a non-null list
    /// (empty included) means the operator DID declare a set, so a path outside it is invented and refused no
    /// matter how well it is paired — pairing proves the CONTENT is right, never that the PATH is one anybody
    /// asked for.
    ///
    /// <para>Admitted, the acceptance is PROMOTED to the companion's kind — the ArtifactPresent framing has nothing
    /// left to check once its companion runs — which reuses the existing ArtifactSchema/LlmJudge graders untouched.
    /// Refused, it is DROPPED exactly like a bind failure: the subtask keeps its work and loses only its oracle,
    /// never fatal to the plan, matching the policy <see cref="ReadResponse"/> applies to every other unbindable
    /// acceptance.</para>
    /// </summary>
    internal static PlannedWorkflow ReconcileArtifactPresent(PlannedWorkflow plan, IReadOnlyCollection<string>? declaredDeliverablePaths, out IReadOnlyList<DroppedAcceptance> dropped)
    {
        // Short-circuit ONLY when there is nothing to reconcile at all — an admitted (promoted) subtask changes its
        // Acceptance.Kind without ever appearing in drops, so "no drops" is never the right test for "no changes".
        if (plan.Subtasks.All(subtask => subtask.Acceptance is not { Kind: BenchmarkGradingKind.ArtifactPresent }))
        {
            dropped = Array.Empty<DroppedAcceptance>();
            return plan;
        }

        var declared = declaredDeliverablePaths?.ToHashSet(StringComparer.Ordinal);
        var drops = new List<DroppedAcceptance>();
        var subtasks = plan.Subtasks.Select(subtask => ReconcileArtifactPresent(subtask, declared, drops)).ToList();

        dropped = drops;
        return plan with { Subtasks = subtasks };
    }

    /// <summary>One subtask's verdict — every kind but <c>ArtifactPresent</c> passes through untouched.</summary>
    private static PlannedSubtask ReconcileArtifactPresent(PlannedSubtask subtask, HashSet<string>? declared, List<DroppedAcceptance> drops)
    {
        if (subtask.Acceptance is not { Kind: BenchmarkGradingKind.ArtifactPresent } acceptance) return subtask;

        var reason = InadmissibleArtifactPresentReason(acceptance, declared);

        if (reason is null) return subtask with { Acceptance = acceptance with { Kind = acceptance.Rubric is not null ? BenchmarkGradingKind.LlmJudge : BenchmarkGradingKind.ArtifactSchema } };

        drops.Add(new DroppedAcceptance { SubtaskId = subtask.Id, Kind = nameof(BenchmarkGradingKind.ArtifactPresent), Reason = reason });

        return subtask with { Acceptance = null };
    }

    /// <summary>
    /// The FIRST reason this ArtifactPresent cannot stand — undeclared before uncovered, so an operator sees the
    /// more fundamental problem (the planner named a path nobody asked for) before the narrower one (a real
    /// deliverable graded by nothing but its own existence). The undeclared check applies ONLY when <paramref
    /// name="declared"/> is non-null: a null source has nothing to compare the path against, so admissibility
    /// then turns on pairing alone. Null when the acceptance is admissible as authored.
    /// </summary>
    private static string? InadmissibleArtifactPresentReason(SupervisorAcceptanceSpec acceptance, HashSet<string>? declared)
    {
        var undeclared = declared is not null ? acceptance.Command.FirstOrDefault(path => !declared.Contains(path)) : null;

        if (undeclared is not null)
            return $"Planner acceptance ArtifactPresent for '{undeclared}' is self-certifying: the same plan both writes and grades that path, so mere existence proves nothing an independent read did not already assume. Declare '{undeclared}' as an operator deliverable and pair the acceptance with an ArtifactSchema or LlmJudge content check, or grade the subtask with TestsPass instead.";

        if (acceptance.Rubric is null && acceptance.Schema is null)
            return $"Planner acceptance ArtifactPresent for '{acceptance.Command[0]}' has no paired ArtifactSchema or LlmJudge content check — file existence alone does not verify what the agent produced.";

        return null;
    }

    /// <summary>
    /// Name the subtasks whose acceptance the bind above dropped, each with the POSITION it was authored at. The
    /// subtask id lives on the RAW subtask element — the converter only ever sees the acceptance object — so the drops
    /// are described by re-running the SAME <see cref="Bind"/> over the raw acceptances. One definition of the
    /// contract, read twice, rather than a second copy of the rule that could drift from the one the bind enforces —
    /// and the same single definition the JSON-Schema side of the re-ask is interpreted against.
    ///
    /// <para>The index rides along because a JSON-Schema violation can only be attributed positionally:
    /// <c>$.subtasks[3].acceptance</c> is where the schema reports the same defect, and the model's own <c>id</c>
    /// appears nowhere in that path.</para>
    /// </summary>
    internal static IReadOnlyList<(int Index, DroppedAcceptance Drop)> DescribeUnboundAcceptances(JsonElement response)
    {
        if (!TryReadProperty(response, "subtasks", out var subtasks) || subtasks.ValueKind != JsonValueKind.Array) return Array.Empty<(int, DroppedAcceptance)>();

        var dropped = new List<(int, DroppedAcceptance)>();
        var index = 0;

        foreach (var subtask in subtasks.EnumerateArray())
        {
            var at = index++;

            // PRESENCE is the only pre-filter: a subtask that authored no acceptance at all lost no oracle and has
            // nothing to report. Everything a model DID author — of any JSON kind — goes to the contract itself.
            if (!TryReadProperty(subtask, "acceptance", out var acceptance)) continue;

            if (Bind(acceptance, out var defect) is not null || defect is null) continue;

            dropped.Add((at, new DroppedAcceptance { SubtaskId = ReadId(subtask), Kind = ReadKind(acceptance), Reason = defect }));
        }

        return dropped;
    }

    /// <summary>The serializer's own words for a shape the wire record cannot hold, minus the position it appends (measured against the acceptance fragment alone, so it reads as a lie about the reply) and minus the internal type names it quotes. This is a defect report an OPERATOR reads: it must name the offending JSON, not this file's classes.</summary>
    private static string Describe(JsonException failure)
    {
        var at = failure.Message.IndexOf(" Path: ", StringComparison.Ordinal);
        var reported = at < 0 ? failure.Message : failure.Message[..at];

        return $"Planner acceptance does not match the v{CurrentFormatVersion} contract: {reported.Replace(typeof(PlannerAcceptanceDraft).FullName!, "planner acceptance", StringComparison.Ordinal)}";
    }

    /// <summary>The oracle the model NAMED, echoed onto the drop record — <c>unbound</c> when the acceptance authored no readable kind at all. Diagnostics only: what a subtask is actually graded by is <see cref="BindOracle"/>'s verdict, never this.</summary>
    private static string ReadKind(JsonElement acceptance) =>
        TryReadProperty(acceptance, "kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() is { Length: > 0 } authored ? Cap(authored) : "unbound";

    /// <summary>The JSON kind an acceptance was authored as, in the words a MODEL reads back rather than the serializer's enum names ("Array", "True"). Only reached for a kind that cannot be an acceptance at all, so no oracle name flows through here.</summary>
    private static string DescribeKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "a non-object value",
    };

    /// <summary>Model-authored text reaches both the drop record and the re-ask prompt, so an absurd value must not be able to bloat either.</summary>
    private static string Cap(string authored) => authored.Length > 64 ? authored[..64] : authored;

    /// <summary>The raw-element read matches the case-insensitive binding the plan itself gets (<see cref="PlannerSchema.Options"/>), so a reply that shifts a key's case is described exactly as it was bound rather than dropping an acceptance nobody is told about.</summary>
    private static bool TryReadProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }

        value = default;
        return false;
    }

    /// <summary><see cref="Cap"/>ped for the same reason the authored kind is: this id rides into the ONE bounded re-ask, whose whole message is capped downstream at 512 characters, so a model-authored id long enough to fill that budget would truncate the advice itself away. No consumer joins on the recorded id (<c>plan.author</c> logs it and emits it), and no plan-local id a planner really writes comes near the cap.</summary>
    private static string ReadId(JsonElement subtask) => TryReadProperty(subtask, "id", out var id) && id.ValueKind == JsonValueKind.String ? Cap(id.GetString() ?? "") : "";

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
            // Consume the authored value WHOLE before judging it: a shape the wire record cannot hold must not leave
            // the reader stranded mid-object, and the bind below has to be able to fail without failing the reply.
            using var authored = JsonDocument.ParseValue(ref reader);

            // Null ⇒ the subtask keeps its work with NO oracle, and DescribeUnboundAcceptances names it. Nothing in
            // here is fatal: an acceptance the contract cannot bind costs that subtask its oracle, never the plan.
            return Bind(authored.RootElement, out _);
        }

        public override void Write(Utf8JsonWriter writer, SupervisorAcceptanceSpec value, JsonSerializerOptions options) => throw new NotSupportedException("Model response options are read-only; serialize normalized runtime plans with AgentJson.Options.");
    }
}
