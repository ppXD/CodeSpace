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
    private static readonly JsonSerializerOptions ResponseReadOptions = new(PlannerSchema.Options) { Converters = { new ModelAcceptanceConverter() } };

    public SupervisorAcceptanceSpec ToRuntime()
    {
        if (FormatVersion != CurrentFormatVersion) throw new JsonException($"Unsupported planner acceptance formatVersion {FormatVersion}; expected {CurrentFormatVersion}.");
        var commandOracle = Kind == BenchmarkGradingKind.TestsPass;
        if (!commandOracle && Kind is not (BenchmarkGradingKind.ArtifactPresent or BenchmarkGradingKind.LlmJudge or BenchmarkGradingKind.CitationsResolve or BenchmarkGradingKind.ArtifactSchema))
            throw new JsonException($"Unsupported planner acceptance kind '{Kind}'.");
        if (commandOracle && ArtifactPaths is not null || !commandOracle && Argv is not null)
            throw new JsonException("Planner acceptance must supply argv for TestsPass or artifactPaths for a file oracle, never both or the other payload.");
        var payload = commandOracle ? Argv : ArtifactPaths;
        if (payload is not { Count: > 0 } || payload.Any(value => value is null || value.Contains('\0')))
            throw new JsonException($"Planner acceptance requires a non-empty {(commandOracle ? "argv" : "artifactPaths")} array without null values or NUL characters.");
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

    /// <summary>Fresh model replies accept only the known typed version. Historical persisted v1 plans continue to deserialize with AgentJson; they never pass through this model-response boundary.</summary>
    public static PlannedWorkflow? ReadResponse(JsonElement response) => response.Deserialize<PlannedWorkflow>(ResponseReadOptions);

    private sealed class ModelAcceptanceConverter : JsonConverter<SupervisorAcceptanceSpec>
    {
        public override SupervisorAcceptanceSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var draft = JsonSerializer.Deserialize<PlannerAcceptanceDraft>(ref reader, WireOptions) ?? throw new JsonException("Planner acceptance cannot be null when authored.");
            return draft.ToRuntime();
        }

        public override void Write(Utf8JsonWriter writer, SupervisorAcceptanceSpec value, JsonSerializerOptions options) => throw new NotSupportedException("Model response options are read-only; serialize normalized runtime plans with AgentJson.Options.");
    }
}
