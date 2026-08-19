using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Contracts;

/// <summary>Version and digest vocabulary shared by every lossless Workflow Run data reference.</summary>
public static class WorkflowRunDataContract
{
    public const int CurrentVersion = 1;
    public const string Sha256Algorithm = "sha256/v1";

    public static bool IsSupported(int version) => version == CurrentVersion;

    /// <summary>Whether a digest is a canonical lowercase SHA-256 value. Shared by every reference that binds bytes, so one spelling of "a digest is well-formed" cannot drift from another.</summary>
    public static bool IsCanonicalSha256(string? digest) => digest is { Length: 64 } && digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// The REGISTERED table names whose aggregate root is a Workflow Run. Registration is the reservation of a name, not
/// evidence of a table: several of these are still FORWARD DECLARATIONS with no EF entity behind them.
/// <c>WorkflowRunDataNamesReachabilityTests</c> resolves every name in <see cref="All"/> against
/// <c>CodeSpaceDbContext</c> and lists the unbacked ones explicitly, so which are real and which are promises is a
/// checked fact rather than a claim in this comment — and shipping one moves its name off that list.
///
/// <para>The prefix is an ownership boundary, not merely a naming preference: storage-plane aggregates such as
/// <c>artifact_object</c> remain global even when one of their references points at a run. Existing legacy tables are
/// migrated separately; they are never renamed as a rider.</para>
/// </summary>
public static class WorkflowRunDataNames
{
    public const string Prefix = "workflow_run_";
    public const string ModelCall = Prefix + "model_call";
    public const string ModelCallAttempt = Prefix + "model_call_attempt";
    public const string HarnessExecution = Prefix + "harness_execution";
    public const string HarnessProcessAttempt = Prefix + "harness_process_attempt";
    public const string HarnessDescriptor = Prefix + "harness_descriptor";
    public const string HarnessReductionCheckpoint = Prefix + "harness_reduction_checkpoint";
    public const string RunnerHandle = Prefix + "runner_handle";
    public const string NativeRecord = Prefix + "native_record";
    public const string SemanticEvent = Prefix + "semantic_event";
    public const string ToolCall = Prefix + "tool_call";
    public const string ToolCallAttempt = Prefix + "tool_call_attempt";
    public const string LogStream = Prefix + "log_stream";
    public const string LogSegment = Prefix + "log_segment";
    public const string Session = Prefix + "session";
    public const string SessionStateRevision = Prefix + "session_state_revision";
    public const string CaptureGap = Prefix + "capture_gap";
    public const string DataManifest = Prefix + "data_manifest";

    private static readonly IReadOnlyList<string> Registered = Array.AsReadOnly(new[]
    {
        ModelCall, ModelCallAttempt, HarnessExecution, HarnessProcessAttempt, HarnessDescriptor,
        HarnessReductionCheckpoint, RunnerHandle, NativeRecord, SemanticEvent, ToolCall, ToolCallAttempt, LogStream,
        LogSegment, Session, SessionStateRevision, CaptureGap, DataManifest,
    });

    public static IReadOnlyList<string> All => Registered;

    public static bool IsRunOwned(string? tableName) => tableName?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}

/// <summary>Stable owner nouns for an artifact referenced by the Workflow Run data plane.</summary>
public static class WorkflowRunDataOwnerKinds
{
    public const string ModelCall = "model-call";
    public const string ModelCallAttempt = "model-call-attempt";
    public const string HarnessExecution = "harness-execution";
    public const string HarnessProcessAttempt = "harness-process-attempt";
    public const string HarnessDescriptor = "harness-descriptor";
    public const string HarnessReductionCheckpoint = "harness-reduction-checkpoint";
    public const string RunnerHandle = "runner-handle";
    public const string NativeRecord = "native-record";
    public const string SemanticEvent = "semantic-event";
    public const string ToolCall = "tool-call";
    public const string ToolCallAttempt = "tool-call-attempt";
    public const string LogStream = "log-stream";
    public const string LogSegment = "log-segment";
    public const string Session = "session";
    public const string SessionStateRevision = "session-state-revision";
    public const string CaptureGap = "capture-gap";
    public const string DataManifest = "data-manifest";

    private static readonly IReadOnlySet<string> Registered = new HashSet<string>(StringComparer.Ordinal)
    {
        ModelCall, ModelCallAttempt, HarnessExecution, HarnessProcessAttempt, HarnessDescriptor,
        HarnessReductionCheckpoint, RunnerHandle, NativeRecord, SemanticEvent, ToolCall, ToolCallAttempt, LogStream,
        LogSegment, Session, SessionStateRevision, CaptureGap, DataManifest,
    };

    public static bool IsSupported(string? ownerKind) => ownerKind is not null && Registered.Contains(ownerKind);
}

/// <summary>
/// Whether the referenced bytes completely represent the claimed role. Only exact states may enter strict agent,
/// resume, oracle, or completion reads; every other state must remain visible and fail closed to recovery/Park.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunCaptureCompleteness
{
    Exact,
    RedactedExact,
    Partial,
    Unavailable,
    Corrupt,
    LegacyUnknown,
}

public static class WorkflowRunCaptureCompletenessExtensions
{
    public static bool IsStrictlyReadable(this WorkflowRunCaptureCompleteness value) =>
        value is WorkflowRunCaptureCompleteness.Exact or WorkflowRunCaptureCompleteness.RedactedExact;
}

/// <summary>
/// V1 content reference for data captured while a Workflow Run executes. It binds durable bytes to the exact run,
/// logical owner, attempt lineage, digest, size, and capture completeness; consumers validate before reading and
/// never reinterpret missing content as an empty string.
/// </summary>
public sealed record WorkflowRunArtifactRefV1
{
    public required int ContractVersion { get; init; }
    public required Guid WorkflowRunId { get; init; }
    public required string OwnerKind { get; init; }
    public required string OwnerId { get; init; }
    public required string Role { get; init; }
    public required Guid ArtifactId { get; init; }
    public required string DigestAlgorithm { get; init; }
    public required string Digest { get; init; }
    public required long SizeBytes { get; init; }
    public required WorkflowRunCaptureCompleteness Completeness { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkUnitRef? WorkUnit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AttemptId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AttemptOrdinal { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExecutionGeneration { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!WorkflowRunDataContract.IsSupported(ContractVersion))
            errors.Add($"contractVersion '{ContractVersion}' is unsupported");
        if (WorkflowRunId == Guid.Empty)
            errors.Add("workflowRunId must be non-empty");
        if (!WorkflowRunDataOwnerKinds.IsSupported(OwnerKind))
            errors.Add($"ownerKind '{OwnerKind}' is unsupported by contract v{ContractVersion}");
        if (string.IsNullOrWhiteSpace(OwnerId))
            errors.Add("ownerId must be non-empty");
        if (string.IsNullOrWhiteSpace(Role))
            errors.Add("role must be non-empty");
        if (ArtifactId == Guid.Empty)
            errors.Add("artifactId must be non-empty");
        if (!string.Equals(DigestAlgorithm, WorkflowRunDataContract.Sha256Algorithm, StringComparison.Ordinal))
            errors.Add($"digestAlgorithm '{DigestAlgorithm}' is unsupported");
        if (!WorkflowRunDataContract.IsCanonicalSha256(Digest))
            errors.Add("digest must be a canonical lowercase SHA-256 value");
        if (SizeBytes < 0)
            errors.Add("sizeBytes must be non-negative");
        if (!Enum.IsDefined(Completeness))
            errors.Add($"completeness '{Completeness}' is unsupported");
        if (AttemptId == Guid.Empty)
            errors.Add("attemptId, when present, must be non-empty");
        if ((AttemptOrdinal is not null || ExecutionGeneration is not null) && AttemptId is null)
            errors.Add("attemptId is required when attemptOrdinal or executionGeneration is present");
        if (AttemptOrdinal is <= 0)
            errors.Add("attemptOrdinal, when present, must be one-based");
        if (ExecutionGeneration is <= 0)
            errors.Add("executionGeneration, when present, must be positive");

        return errors;
    }
}
