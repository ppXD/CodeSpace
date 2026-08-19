using System.Text;
using System.Text.Json;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// A BACKEND-NEUTRAL durable reference to a launched execution — enough for a process that did not launch it to
/// re-find, observe, and reclaim it. DECLARED, not yet in service: <c>workflow_run_runner_handle</c> is a registered
/// name with no table behind it (see the forward-declaration allow-list in
/// <c>WorkflowRunDataNamesReachabilityTests</c>), and nothing in production reads or writes this record — the handle
/// runs persist today is <c>SandboxHandle</c>.
///
/// <para>Everything backend-specific lives inside <see cref="Locator"/> and nowhere else: the local runner puts its
/// pid, process start time and spool directory there; a container runner puts a container id and log cursor; a
/// remote runner puts a service-side execution reference. Hoisting any of those onto this record — as
/// <c>SandboxHandle</c> does today with its pid and spool path — is exactly what makes a non-local backend
/// unrepresentable, because every reader then assumes a local line-oriented process.</para>
///
/// <para>The <see cref="ClaimOwnerId"/>/<see cref="FenceToken"/> pair is the reclaim primitive: a worker that takes
/// over an execution raises the fence, so a resurrected older observer writing with a lower fence is rejected rather
/// than allowed to interleave with the new one.</para>
/// </summary>
public sealed record RunnerHandleEnvelope
{
    /// <summary>Runner kind that owns this handle (e.g. <c>local</c>) — how a reader resolves who can interpret <see cref="Locator"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Version of THIS runner kind's locator shape, one-based. Owned by the runner, so a backend evolves its locator without reversioning the envelope.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>The harness execution this handle refers to.</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>The host the execution is pinned to, when it is pinned. Null ⇒ any worker may claim it — the difference between a local process only its own host can reap and a remote execution anyone can observe.</summary>
    public string? HostAffinity { get; init; }

    /// <summary>The BACKEND's own identifier for the execution, when the execution lives outside this system. Null for a runner that owns the process itself.</summary>
    public string? RemoteExecutionId { get; init; }

    /// <summary>The runner's opaque locator payload — read only by the runner named in <see cref="Kind"/>, never interpreted by shared code. A JSON object so a backend adds fields without a migration.</summary>
    public required JsonElement Locator { get; init; }

    /// <summary>Opaque, runner-interpreted resume cursor a re-attaching observer continues from (a byte offset, a log token, a sequence number). Null ⇒ from the beginning.</summary>
    public string? CheckpointRef { get; init; }

    /// <summary>Absolute wall-clock cap SNAPSHOTTED at launch, so the timeout survives the observer that set it. Null ⇒ no wall-clock cap.</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>Who currently holds the execution. Compared with the reader's own identity before it acts on the handle.</summary>
    public required string ClaimOwnerId { get; init; }

    /// <summary>Monotonically increasing claim fence, one-based. A write carrying a lower fence than the stored one lost its claim and must be rejected.</summary>
    public required long FenceToken { get; init; }

    /// <summary>Every reason this handle cannot be acted on. Empty ⇒ usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Kind))
            errors.Add("kind must be non-empty");
        if (SchemaVersion <= 0)
            errors.Add("schemaVersion must be one-based");
        if (ExecutionId == Guid.Empty)
            errors.Add("executionId must be non-empty");
        if (Locator.ValueKind != JsonValueKind.Object)
            errors.Add($"locator must be a JSON object, not '{Locator.ValueKind}'");
        if (Deadline == DateTimeOffset.MinValue)
            errors.Add("deadline, when present, must be an absolute instant");
        if (string.IsNullOrWhiteSpace(ClaimOwnerId))
            errors.Add("claimOwnerId must be non-empty");
        if (FenceToken <= 0)
            errors.Add("fenceToken must be one-based");

        return errors;
    }

    /// <summary>
    /// Value equality that actually compares the locator. The generated record equality compares
    /// <see cref="Locator"/> through <c>EqualityComparer&lt;JsonElement&gt;.Default</c>, which has no value
    /// semantics and degrades to comparing the backing document REFERENCE — so a handle never equals a copy of
    /// itself, not even its own round trip. That silently breaks the one question this record exists to answer:
    /// is the claim I hold still the claim that is stored?
    /// </summary>
    public bool Equals(RunnerHandleEnvelope? other) =>
        other is not null
        && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
        && SchemaVersion == other.SchemaVersion
        && ExecutionId == other.ExecutionId
        && string.Equals(HostAffinity, other.HostAffinity, StringComparison.Ordinal)
        && string.Equals(RemoteExecutionId, other.RemoteExecutionId, StringComparison.Ordinal)
        && string.Equals(LocatorText(Locator), LocatorText(other.Locator), StringComparison.Ordinal)
        && string.Equals(CheckpointRef, other.CheckpointRef, StringComparison.Ordinal)
        && Deadline == other.Deadline
        && string.Equals(ClaimOwnerId, other.ClaimOwnerId, StringComparison.Ordinal)
        && FenceToken == other.FenceToken;

    /// <summary>Hashes a subset of what <see cref="Equals(RunnerHandleEnvelope)"/> compares — every field here is also compared, which is the whole contract a hash owes — and hashes the locator by the same raw text equality uses, so the two can never disagree.</summary>
    public override int GetHashCode() => HashCode.Combine(Kind, SchemaVersion, ExecutionId, LocatorText(Locator), CheckpointRef, Deadline, ClaimOwnerId, FenceToken);

    /// <summary>Prints the handle's identity and claim, never the locator's contents. <see cref="Locator"/> is the field runners are told to fill with backend material — including a service-side execution reference — so the generated record printout would spill it into the first log line or exception message that formats a handle.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}, SchemaVersion = {SchemaVersion}, ExecutionId = {ExecutionId}, ");
        builder.Append($"HostAffinity = {HostAffinity}, RemoteExecutionId = {RemoteExecutionId}, ");
        builder.Append($"LocatorLength = {LocatorText(Locator).Length}, CheckpointRef = {CheckpointRef}, ");
        builder.Append($"Deadline = {Deadline:O}, ClaimOwnerId = {ClaimOwnerId}, FenceToken = {FenceToken}");

        return true;
    }

    /// <summary>The locator's raw JSON text, empty when it was never set. Raw text is the only canonical form shared code may take of an opaque payload — it has no licence to reorder or reformat what a runner wrote — and reusing it for equality, hashing and size keeps all three consistent by construction.</summary>
    private static string LocatorText(JsonElement locator) => locator.ValueKind == JsonValueKind.Undefined ? string.Empty : locator.GetRawText();
}
