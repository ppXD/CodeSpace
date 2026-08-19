using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// What a harness adapter can ACTUALLY do, as a set of named flags rather than members bolted onto the harness
/// interface (Rule 7 / ISP — "can you resume?" is a fact about an adapter, not a reason to widen the contract every
/// adapter must implement). A reader checks the flag and degrades HONESTLY when it is absent; it never infers a
/// capability from the harness's name, which is how a harness that cannot report cost ends up recorded as costing
/// nothing.
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HarnessCapability
{
    /// <summary>The empty set. A descriptor that declares it fails validation — an adapter that can do nothing cannot be the one that ran.</summary>
    None = 0,

    /// <summary>Emits a MACHINE-READABLE native stream (JSONL frames, JSON-RPC messages) rather than prose only, so a capture is losslessly re-parsable instead of scraped.</summary>
    StructuredNativeStream = 1 << 0,

    /// <summary>Exposes the process stdout channel.</summary>
    StandardOutput = 1 << 1,

    /// <summary>Exposes stderr SEPARATELY from stdout, so a diagnostic line is never mistaken for a protocol frame.</summary>
    StandardError = 1 << 2,

    /// <summary>Reports model identity and token usage as FACT in its own stream — the difference between an exact cost and a guess derived from output length.</summary>
    ExactModelTelemetry = 1 << 3,

    /// <summary>Reports tool invocations and their results as FACT in its own stream, rather than leaving them to be inferred from rendered text.</summary>
    ExactToolTelemetry = 1 << 4,

    /// <summary>Can resume, or fork from, a prior session identified by a session id.</summary>
    ResumeOrFork = 1 << 5,

    /// <summary>Compacts its own context mid-run, which means a faithful record must capture the compaction boundary or the transcript silently loses its middle.</summary>
    Compaction = 1 << 6,

    /// <summary>Accepts mid-run steering or abort over its own channel, rather than only responding to process signals.</summary>
    SteerOrAbort = 1 << 7,

    /// <summary>Can export its session state and re-import it, so a run's state survives the process that produced it.</summary>
    StateExportImport = 1 << 8,

    /// <summary>Emits a protocol-level heartbeat, which lets a silent-but-alive run be distinguished from a hung one without probing the OS.</summary>
    ProtocolHeartbeat = 1 << 9,
}

/// <summary>How a harness speaks natively. This decides what a capture reads from, so it is identity, not preference: a <see cref="CliText"/> adapter can never be projected with the fidelity a <see cref="CliJsonl"/> one can.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HarnessNativeProtocolKind
{
    /// <summary>Line-delimited JSON frames on a process stream.</summary>
    CliJsonl,

    /// <summary>Unstructured process text — everything above the raw bytes is a projection, never an exact fact.</summary>
    CliText,

    /// <summary>Bidirectional JSON-RPC over a pipe or socket.</summary>
    JsonRpc,

    /// <summary>An in-process SDK whose events arrive as typed callbacks rather than bytes.</summary>
    Sdk,

    /// <summary>A remote service the adapter talks to over the network; the execution does not live on this host.</summary>
    Remote,
}

/// <summary>One declared setting a harness adapter reads. The list a key appears in decides how it is handled — a secret key is never rendered, logged, or persisted in the clear — so the same noun serves both schemas and a key may never appear in both.</summary>
public sealed record HarnessSettingDescriptor
{
    /// <summary>Stable key the adapter reads (config key or secret/env name).</summary>
    public required string Key { get; init; }

    /// <summary>Whether the adapter cannot run without it.</summary>
    public required bool Required { get; init; }

    /// <summary>Operator-facing explanation. Never a value — a default belongs to the adapter, and a secret has no place here at all.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// The immutable IDENTITY of one harness adapter: which harness it drives, at which adapter revision, over which
/// native protocol, against which native versions, and what it can be trusted to report. It is a data noun so that it
/// WILL BE snapshottable onto a run once <c>workflow_run_harness_descriptor</c> exists — that name is registered but
/// backs no table yet (see the forward-declaration list in <c>WorkflowRunDataNamesReachabilityTests</c>), and no
/// adapter populates a descriptor today. The shape is here first because the requirement is: a record read a year later
/// must be interpretable against the adapter that actually produced it, not against whatever the adapter has since
/// become.
/// </summary>
public sealed record HarnessDescriptor
{
    /// <summary>Stable type key, read <c>&lt;kind&gt;/v&lt;major&gt;</c> — e.g. <c>claude-code/v2</c>, <c>codex-cli/v2</c>. The major is the ADAPTER's contract generation, so a rewrite ships beside its predecessor instead of reinterpreting its records.</summary>
    public required string TypeKey { get; init; }

    /// <summary>Full version of this adapter, for pinning the exact translation a run was recorded with.</summary>
    public required string AdapterVersion { get; init; }

    /// <summary>Native harness versions this adapter is known to speak. A run against a version outside this list is recorded, never silently trusted.</summary>
    public required IReadOnlyList<string> SupportedNativeVersions { get; init; }

    /// <summary>What the adapter reads natively.</summary>
    public required HarnessNativeProtocolKind NativeProtocol { get; init; }

    /// <summary>The declared capability SET. Additive: a new flag is a new capability, never a new interface member.</summary>
    public required HarnessCapability Capabilities { get; init; }

    /// <summary>Non-secret settings the adapter reads.</summary>
    public IReadOnlyList<HarnessSettingDescriptor> ConfigSchema { get; init; } = Array.Empty<HarnessSettingDescriptor>();

    /// <summary>Secret settings the adapter reads. Kept a SEPARATE list from <see cref="ConfigSchema"/> so "this key holds a credential" is a structural fact rather than a naming convention.</summary>
    public IReadOnlyList<HarnessSettingDescriptor> SecretSchema { get; init; } = Array.Empty<HarnessSettingDescriptor>();

    /// <summary>Whether this adapter declares <paramref name="capability"/>. <see cref="HarnessCapability.None"/> answers false: <c>HasFlag(None)</c> is vacuously true, and asking for nothing must never read as a declared capability.</summary>
    public bool Supports(HarnessCapability capability) => capability != HarnessCapability.None && Capabilities.HasFlag(capability);

    /// <summary>Every reason this descriptor cannot be trusted as an adapter identity. Empty ⇒ usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!IsCanonicalTypeKey(TypeKey))
            errors.Add($"typeKey '{TypeKey}' must read '<kind>/v<major>'");
        if (string.IsNullOrWhiteSpace(AdapterVersion))
            errors.Add("adapterVersion must be non-empty");
        if (SupportedNativeVersions.Count == 0 || SupportedNativeVersions.Any(string.IsNullOrWhiteSpace))
            errors.Add("supportedNativeVersions must list at least one non-empty native version");
        if (!Enum.IsDefined(NativeProtocol))
            errors.Add($"nativeProtocol '{NativeProtocol}' is unsupported");
        if (Capabilities == HarnessCapability.None)
            errors.Add("capabilities must declare at least one flag");

        errors.AddRange(SchemaErrors());

        return errors;
    }

    private IEnumerable<string> SchemaErrors()
    {
        var configKeys = ConfigSchema.Select(setting => setting.Key).ToList();
        var secretKeys = SecretSchema.Select(setting => setting.Key).ToList();

        if (configKeys.Concat(secretKeys).Any(string.IsNullOrWhiteSpace))
            yield return "every config and secret schema key must be non-empty";
        if (HasDuplicates(configKeys) || HasDuplicates(secretKeys))
            yield return "config and secret schema keys must each be distinct";
        if (configKeys.Intersect(secretKeys, StringComparer.Ordinal).Any())
            yield return "a schema key is either config or secret, never both";
    }

    /// <summary>
    /// Value equality that compares the declared lists ELEMENT-WISE. The generated record equality compares them
    /// through <c>EqualityComparer&lt;IReadOnlyList&lt;T&gt;&gt;.Default</c>, i.e. by reference — so a descriptor
    /// never equals an equal-valued copy of itself, and the question this snapshot exists to answer ("is the
    /// adapter that produced this run still the adapter I have?") would always answer "changed".
    /// </summary>
    public bool Equals(HarnessDescriptor? other) =>
        other is not null
        && string.Equals(TypeKey, other.TypeKey, StringComparison.Ordinal)
        && string.Equals(AdapterVersion, other.AdapterVersion, StringComparison.Ordinal)
        && NativeProtocol == other.NativeProtocol
        && Capabilities == other.Capabilities
        && SupportedNativeVersions.SequenceEqual(other.SupportedNativeVersions, StringComparer.Ordinal)
        && ConfigSchema.SequenceEqual(other.ConfigSchema)
        && SecretSchema.SequenceEqual(other.SecretSchema);

    /// <summary>Hashes a subset of what <see cref="Equals(HarnessDescriptor)"/> compares — every field here is also compared, and equal lists have equal counts, which is the whole contract a hash owes.</summary>
    public override int GetHashCode() => HashCode.Combine(TypeKey, AdapterVersion, NativeProtocol, Capabilities, SupportedNativeVersions.Count, ConfigSchema.Count, SecretSchema.Count);

    private static bool HasDuplicates(IReadOnlyList<string> keys) => keys.Distinct(StringComparer.Ordinal).Count() != keys.Count;

    private static bool IsCanonicalTypeKey(string? typeKey)
    {
        var parts = typeKey?.Split('/');

        if (parts is not { Length: 2 } || string.IsNullOrWhiteSpace(parts[0]) || parts[1].Length < 2 || parts[1][0] != 'v') return false;

        return parts[1].Skip(1).All(char.IsAsciiDigit);
    }
}
