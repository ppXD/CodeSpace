using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// One harness executable exactly as the executing host would run it. The <see cref="BinarySha256"/> is the whole
/// point: <see cref="Version"/> is a PINNED CLAIM the adapter makes about itself (and an operator can repoint the
/// binary through the harness's own <c>CommandEnvVar</c> without touching it), so a campaign that froze only the
/// version string could be re-run against different bytes and never notice.
/// </summary>
public sealed record HarnessBinaryIdentity
{
    /// <summary>No executable resolved on this host — the bare name is absent from PATH, the configured path does not exist, or it names a directory (<c>File.Exists</c> reports false for a directory, the same as a missing file).</summary>
    public const string ReasonNotFound = "not-found";

    /// <summary>The executable resolved but its bytes could not be read (permissions, a dangling symlink).</summary>
    public const string ReasonUnreadable = "unreadable";

    /// <summary>The harness's stable tag — <c>codex-cli</c>, <c>claude-code</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The version the adapter targets, after its own env override.</summary>
    public required string Version { get; init; }

    /// <summary>Lower-hex SHA-256 of the executable's bytes (symlinks followed). Null exactly when <see cref="UnobservedReason"/> is set.</summary>
    public string? BinarySha256 { get; init; }

    /// <summary>Why no bytes could be observed — one of the <c>Reason*</c> constants. Null exactly when <see cref="BinarySha256"/> is set. An absence is FROZEN rather than skipped: a campaign whose host had no <c>codex</c> is a different campaign from one whose host had it.</summary>
    public string? UnobservedReason { get; init; }
}

/// <summary>
/// What this worker IS, for a campaign that must be able to say the paid cells all ran on the same runtime. There is
/// no image-digest source in this codebase yet, so <see cref="BuildIdentity"/> carries the executing assembly's
/// informational version (the same value the startup log and every Serilog event are enriched with) and the OS/arch
/// pair distinguishes two hosts that share it.
/// </summary>
public sealed record RunnerProfile
{
    /// <summary>The worker's build stamp — an image digest where a build supplies one, else the assembly informational version.</summary>
    public required string BuildIdentity { get; init; }

    public required string OsPlatform { get; init; }

    public required string OsArchitecture { get; init; }

    /// <summary>Whether a runnable, probe-passing <c>bwrap</c> exists here. False means every permission the tier expressed as "off" was NOT enforced by the OS.</summary>
    public required bool BubblewrapAvailable { get; init; }

    /// <summary>This deployment's <c>Sandbox:RequireConfinement</c> — on, an unconfinable host refuses to run rather than running bare.</summary>
    public required bool RequireConfinement { get; init; }

    /// <summary>This deployment's <c>Sandbox:MaxAutonomy</c> ceiling, as the tier name in force after parsing.</summary>
    public required string MaxAutonomy { get; init; }
}

/// <summary>Which arm of the campaign a credential endpoint belongs to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualificationCredentialRole
{
    Control,
    Candidate,
    Reviewer,
}

/// <summary>
/// The endpoint and secret IDENTITY behind one model row — never the secret. A rotated key, a repointed gateway, or a
/// swapped credential row all move <see cref="CredentialFingerprint"/> / <see cref="EndpointHost"/> while the model id
/// and row id stay put, which is exactly the substitution a paired campaign must refuse.
/// </summary>
public sealed record CredentialEndpointIdentity
{
    /// <summary>Stand-in host for a credential that pins no base URL — the provider module's own default endpoint, which is not knowable from the credential row.</summary>
    public const string ProviderDefaultHost = "provider-default";

    public required QualificationCredentialRole Role { get; init; }

    public required Guid ModelRowId { get; init; }

    public required Guid CredentialRowId { get; init; }

    /// <summary>The credential's provider tag — <c>Anthropic</c>, <c>OpenAI</c>, <c>OpenRouter</c>, <c>Ollama</c>.</summary>
    public required string ProviderKind { get; init; }

    /// <summary>Host of the credential's base URL, plus the port when it pins a non-default one. NEVER a path, query, fragment or userinfo: a self-hosted gateway URL can carry a token in its path, and this record is persisted.</summary>
    public required string EndpointHost { get; init; }

    /// <summary>HMAC-SHA256 of the secret material under the campaign salt, lower hex. The secret itself never enters this record, so no protocol or evidence row can carry it.</summary>
    public required string CredentialFingerprint { get; init; }

    /// <summary>Freeze one endpoint identity. Pure: <paramref name="secretMaterial"/> is consumed into the fingerprint and never retained.</summary>
    public static CredentialEndpointIdentity Observe(QualificationCredentialRole role, Guid modelRowId, Guid credentialRowId, string providerKind, string? baseUrl, string? secretMaterial, string campaignSalt) => new()
    {
        Role = role,
        ModelRowId = modelRowId,
        CredentialRowId = credentialRowId,
        ProviderKind = providerKind,
        EndpointHost = HostOf(baseUrl),
        CredentialFingerprint = Fingerprint(secretMaterial, campaignSalt),
    };

    /// <summary>Host (and non-default port) of an absolute base URL. A blank or unparseable value reads <see cref="ProviderDefaultHost"/> rather than echoing the raw string, which could itself be a path carrying a token.</summary>
    public static string HostOf(string? baseUrl)
    {
        // A scheme-less "host:port" (or bare host) is not itself invalid, but Uri parses it as a URI whose SCHEME
        // is the host and whose PATH is the port, leaving Authority empty — "localhost:8080" would otherwise read
        // as ProviderDefaultHost instead of its real host. Assuming https before parsing recovers the host:port;
        // the assumed scheme is discarded immediately after and never itself frozen. Userinfo needs no such
        // handling: Uri.Authority already excludes it (unlike the raw string), so a token in "user:token@host"
        // never reaches this record.
        var candidate = !string.IsNullOrEmpty(baseUrl) && !baseUrl.Contains("://", StringComparison.Ordinal) ? $"https://{baseUrl}" : baseUrl;

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Authority.Length > 0 ? uri.Authority : ProviderDefaultHost;
    }

    /// <summary>
    /// The salted fingerprint. The campaign salt is the HMAC KEY, so the same key under a different campaign
    /// fingerprints differently and no cross-campaign table of key hashes can be built. The absence of a secret (a
    /// keyless Ollama) is domain-separated from an empty one so the two can never collide.
    /// </summary>
    public static string Fingerprint(string? secretMaterial, string campaignSalt)
    {
        if (string.IsNullOrEmpty(campaignSalt)) throw new ArgumentException("A credential fingerprint requires a campaign salt.", nameof(campaignSalt));

        var message = $"{(secretMaterial is null ? 0 : 1)}:{secretMaterial}";

        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(campaignSalt), Encoding.UTF8.GetBytes(message)));
    }
}

/// <summary>
/// Who graded, and under which independence policy. A campaign whose judge silently became the producer's own model
/// measured something else entirely, so the resolution is frozen rather than re-derived at read time.
/// </summary>
public sealed record ReviewerResolution
{
    /// <summary>The rubric judge's credential-model row. Null when the campaign pins none and the judge auto-resolves.</summary>
    public Guid? JudgeModelRowId { get; init; }

    /// <summary>The output critic's credential-model row. Null when no critic runs.</summary>
    public Guid? CriticModelRowId { get; init; }

    /// <summary>The judge-independence policy generation in force — the observed-identity comparison version, not the corpus.</summary>
    public required string IndependencePolicyVersion { get; init; }
}

/// <summary>
/// Every execution-affecting default the suite digest does NOT already cover: how much effort each arm asks for, what
/// the completion protocol does to a terminal status, how long a cell may take, and the exact evaluator and
/// prompt/schema bytes the graders and deciders ran.
/// </summary>
public sealed record ExecutionSettings
{
    /// <summary>The effort tier each TaskLaunch arm requests, keyed by benchmark mode. Null for the arm that asks the router to classify. A remap changes what the campaign measured without touching one task.</summary>
    public required IReadOnlyDictionary<string, string?> ArmEffortTiers { get; init; }

    /// <summary>The enforcement mode a run with no explicit opt-in is stamped with on this build.</summary>
    public required CompletionEnforcementMode DefaultCompletionMode { get; init; }

    public required int CompletionPolicyVersion { get; init; }

    /// <summary>Grace added to a task's own budget before the cell driver declares the run non-terminal.</summary>
    public required int CellDriveGraceSeconds { get; init; }

    /// <summary>The wall budget a deep-effort route gives an agent run.</summary>
    public required int DeepAgentTimeoutSeconds { get; init; }

    /// <summary>This host's LLM HTTP request timeout — env-overridable, so it genuinely varies per worker.</summary>
    public required int LlmRequestTimeoutSeconds { get; init; }

    /// <summary>The acceptance grader generation that produced the campaign's oracle verdicts.</summary>
    public required string AcceptanceEvaluatorVersion { get; init; }

    /// <summary>The delivery evaluator generation behind the campaign's publish-manifest grades.</summary>
    public required string DeliveryEvaluatorVersion { get; init; }

    /// <summary>Lower-hex SHA-256 of the live planner system prompt.</summary>
    public required string PlannerPromptDigest { get; init; }

    /// <summary>Lower-hex SHA-256 of the canonicalized live planner response schema.</summary>
    public required string PlannerSchemaDigest { get; init; }

    /// <summary>Lower-hex SHA-256 of the live supervisor decision system prompt.</summary>
    public required string SupervisorPromptDigest { get; init; }

    /// <summary>Lower-hex SHA-256 of the canonicalized live supervisor decision schema.</summary>
    public required string SupervisorSchemaDigest { get; init; }
}

/// <summary>
/// THE frozen runtime bundle behind one paid paired qualification campaign — the half of "what was measured" that a
/// suite digest, a model row id and a code revision do NOT pin: the harness bytes actually executed, the worker
/// runtime, the gateway endpoint and secret identity behind each arm, who graded, and every execution-affecting
/// default. Frozen once beside the protocol, folded into <c>PairedQualificationProtocol.ProtocolDigest</c>, and
/// compared against a fresh observation at admission, execution, recovery and seal — so a campaign that resumes on a
/// host with a different <c>claude</c>, a rotated key, or a repointed gateway is REFUSED instead of quietly pooling
/// two different experiments into one claim.
///
/// <para><b>Secret-safe by construction.</b> No field here can hold secret material: a credential contributes its
/// provider, its host, its row id and a salted HMAC of its key. The record is persisted verbatim on the protocol row
/// and copied into immutable evidence, so this is a hard property, pinned by a serialization test — not a convention.</para>
///
/// <para>Digested through <see cref="ToolCallKey.Canonicalize"/>, the pinned canonical-JSON transform (ordinal key
/// sort, whitespace-free, lossless culture-invariant number tokens). Property DECLARATION order therefore never
/// reaches the digest, and the same manifest hashes identically in any process, on any culture.</para>
/// </summary>
public sealed record QualificationRuntimeManifest
{
    /// <summary>The path <see cref="Compare"/> reports when the two manifests differ in shape at the root.</summary>
    public const string RootField = "manifest";

    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Every harness whose executable this campaign could drive, ordered by kind.</summary>
    public required IReadOnlyList<HarnessBinaryIdentity> Harnesses { get; init; }

    public required RunnerProfile Runner { get; init; }

    /// <summary>One entry per participating model row — control, candidate, and the reviewer/judge when one is resolved.</summary>
    public required IReadOnlyList<CredentialEndpointIdentity> CredentialEndpoints { get; init; }

    public required ReviewerResolution Reviewer { get; init; }

    public required ExecutionSettings Execution { get; init; }

    /// <summary>
    /// The EXACT bytes both persisted on the protocol row and digested. One serialization deliberately serves both:
    /// a column written from one form and a digest taken over another could describe different manifests, and the
    /// row would still verify against itself.
    /// </summary>
    public string CanonicalJson()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(this, Canonical));

        return ToolCallKey.Canonicalize(document.RootElement);
    }

    /// <summary>The manifest's own identity: upper-hex SHA-256 over <see cref="CanonicalJson"/>, matching the <c>ProtocolDigest</c> family and its <c>^[0-9A-F]{64}$</c> column check.</summary>
    public string ManifestDigest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson())));

    /// <summary>Read a frozen manifest back from its persisted JSON — the inverse of <see cref="CanonicalJson"/>, so a round-trip through the protocol row re-digests identically.</summary>
    public static QualificationRuntimeManifest Parse(string persistedJson) =>
        JsonSerializer.Deserialize<QualificationRuntimeManifest>(persistedJson, Canonical) ?? throw new ArgumentException("The persisted qualification runtime manifest is not a JSON object.", nameof(persistedJson));

    /// <summary>
    /// The FIRST field on which a fresh observation departs from what the campaign froze, in canonical (ordinal) order
    /// — or null when the two are identical. Structural rather than field-by-field on purpose: a field added to this
    /// manifest is compared the day it is added, with no second list to keep in step.
    /// </summary>
    public static string? Compare(QualificationRuntimeManifest frozen, QualificationRuntimeManifest observed)
    {
        using var frozenDocument = JsonDocument.Parse(JsonSerializer.Serialize(frozen, Canonical));
        using var observedDocument = JsonDocument.Parse(JsonSerializer.Serialize(observed, Canonical));

        return FirstDifference(frozenDocument.RootElement, observedDocument.RootElement, RootField);
    }

    /// <summary>Refuse an observation that drifted from the frozen bundle, naming the field and — when the comparison belongs to one of the campaign's gated stages — where it was caught. Pure: no I/O, no logging.</summary>
    public static void EnsureNoDrift(QualificationRuntimeManifest frozen, QualificationRuntimeManifest observed, QualificationRuntimeStage? stage = null)
    {
        if (Compare(frozen, observed) is not { } field) return;

        throw new RuntimeManifestDriftException(field, frozen.ManifestDigest(), observed.ManifestDigest(), stage);
    }

    private static string? FirstDifference(JsonElement frozen, JsonElement observed, string path)
    {
        if (frozen.ValueKind != observed.ValueKind) return path;

        return frozen.ValueKind switch
        {
            JsonValueKind.Object => FirstObjectDifference(frozen, observed, path),
            JsonValueKind.Array => FirstArrayDifference(frozen, observed, path),
            _ => ToolCallKey.Canonicalize(frozen) == ToolCallKey.Canonicalize(observed) ? null : path,
        };
    }

    private static string? FirstObjectDifference(JsonElement frozen, JsonElement observed, string path)
    {
        var names = frozen.EnumerateObject().Select(property => property.Name)
            .Union(observed.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var name in names)
        {
            var child = $"{path}.{name}";

            if (!frozen.TryGetProperty(name, out var frozenChild) || !observed.TryGetProperty(name, out var observedChild)) return child;

            if (FirstDifference(frozenChild, observedChild, child) is { } difference) return difference;
        }

        return null;
    }

    private static string? FirstArrayDifference(JsonElement frozen, JsonElement observed, string path)
    {
        var frozenItems = frozen.EnumerateArray().ToList();
        var observedItems = observed.EnumerateArray().ToList();

        if (frozenItems.Count != observedItems.Count) return path;

        for (var index = 0; index < frozenItems.Count; index++)
            if (FirstDifference(frozenItems[index], observedItems[index], $"{path}[{index}]") is { } difference) return difference;

        return null;
    }
}
