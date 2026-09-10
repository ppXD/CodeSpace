using System.Globalization;
using System.Text;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Exceptions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// The frozen runtime bundle's identity contract: every group of it reaches the digest, the digest reaches the
/// protocol identity, the same bundle hashes identically anywhere, drift is named by field, and no secret material can
/// leave through the persisted bytes.
/// </summary>
[Trait("Category", "Unit")]
public sealed class QualificationRuntimeManifestTests
{
    private const string Secret = "sk-ant-live-9f3c2b7e-DO-NOT-PERSIST";

    [Theory]
    [InlineData("harnesses")]
    [InlineData("runner")]
    [InlineData("credentialEndpoints")]
    [InlineData("reviewer")]
    [InlineData("execution")]
    public void Changing_any_frozen_group_moves_the_manifest_and_the_protocol_digest(string group)
    {
        var frozen = Manifest();
        var drifted = Mutate(frozen, group);

        drifted.ManifestDigest().ShouldNotBe(frozen.ManifestDigest(), $"a changed {group} is a different runtime bundle and must not reuse its digest");
        ProtocolDigestFor(drifted).ShouldNotBe(ProtocolDigestFor(frozen), $"a changed {group} must move the protocol identity, or a substituted runtime could rejoin the campaign");
        QualificationRuntimeManifest.Compare(frozen, drifted).ShouldNotBeNull().ShouldStartWith($"{QualificationRuntimeManifest.RootField}.{group}");
    }

    [Fact]
    public void The_same_bundle_digests_identically_across_instances_and_cultures()
    {
        var digest = Manifest().ManifestDigest();
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Manifest().ManifestDigest().ShouldBe(digest, "a worker running under another culture must agree with the process that froze the campaign");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        digest.Length.ShouldBe(64);
        digest.ShouldBe(digest.ToUpperInvariant(), "the digest shares the protocol digest's upper-hex column check");
    }

    [Fact]
    public void A_persisted_manifest_round_trips_to_the_same_digest()
    {
        var frozen = Manifest();

        var reloaded = QualificationRuntimeManifest.Parse(frozen.CanonicalJson());

        reloaded.ManifestDigest().ShouldBe(frozen.ManifestDigest());
        reloaded.CanonicalJson().ShouldBe(frozen.CanonicalJson(), "the persisted bytes must be a fixed point, or a recovery comparison would drift on serialization alone");
        QualificationRuntimeManifest.Compare(frozen, reloaded).ShouldBeNull();
    }

    [Fact]
    public void No_serialized_form_of_the_manifest_carries_the_secret()
    {
        var manifest = Manifest();
        var persisted = manifest.CanonicalJson();

        persisted.ShouldNotContain(Secret);
        persisted.ShouldNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)));
        persisted.ShouldNotContain("sk-ant");
        persisted.Contains("token-in-the-path").ShouldBeFalse("a base URL can carry a token in its path, so only the host may be frozen");
        persisted.Contains(CredentialEndpointIdentity.Fingerprint(Secret, "campaign-salt")).ShouldBeTrue("the salted fingerprint is what stands in for the key");
    }

    [Theory]
    [InlineData("campaign-salt", "campaign-salt", true)]
    [InlineData("campaign-salt", "another-campaign", false)]
    public void A_credential_fingerprint_is_stable_per_salt_and_moves_with_it(string first, string second, bool equal)
    {
        var initial = CredentialEndpointIdentity.Fingerprint(Secret, first);
        var repeated = CredentialEndpointIdentity.Fingerprint(Secret, second);

        (initial == repeated).ShouldBe(equal);
        initial.Length.ShouldBe(64);
    }

    [Fact]
    public void A_fingerprint_separates_a_rotated_a_missing_and_an_empty_secret()
    {
        var key = CredentialEndpointIdentity.Fingerprint(Secret, "campaign-salt");

        CredentialEndpointIdentity.Fingerprint("sk-ant-live-rotated", "campaign-salt").ShouldNotBe(key);
        CredentialEndpointIdentity.Fingerprint(null, "campaign-salt")
            .ShouldNotBe(CredentialEndpointIdentity.Fingerprint(string.Empty, "campaign-salt"), "a keyless credential and an empty key are different credentials");
        Should.Throw<ArgumentException>(() => CredentialEndpointIdentity.Fingerprint(Secret, string.Empty));
    }

    [Theory]
    [InlineData("https://gateway.example.com/v1/messages?key=token-in-the-path", "gateway.example.com")]
    [InlineData("https://gateway.example.com:8443/v1", "gateway.example.com:8443")]
    [InlineData("https://gateway.example.com", "gateway.example.com")]
    [InlineData(null, CredentialEndpointIdentity.ProviderDefaultHost)]
    [InlineData("", CredentialEndpointIdentity.ProviderDefaultHost)]
    [InlineData("not a url", CredentialEndpointIdentity.ProviderDefaultHost)]
    public void An_endpoint_identity_keeps_the_host_and_never_a_path_or_query(string? baseUrl, string expected)
    {
        CredentialEndpointIdentity.HostOf(baseUrl).ShouldBe(expected);
    }

    [Fact]
    public void An_unchanged_bundle_reports_no_drift()
    {
        QualificationRuntimeManifest.Compare(Manifest(), Manifest()).ShouldBeNull();
        Should.NotThrow(() => QualificationRuntimeManifest.EnsureNoDrift(Manifest(), Manifest()));
    }

    [Fact]
    public void Compare_names_the_first_drifted_field_in_canonical_order()
    {
        var frozen = Manifest();
        var drifted = Mutate(Mutate(frozen, "runner"), "execution");

        QualificationRuntimeManifest.Compare(frozen, drifted)
            .ShouldBe("manifest.execution.defaultCompletionMode", "execution sorts before runner, so the earlier divergence is the one reported");
    }

    [Fact]
    public void A_drifted_runtime_is_refused_with_the_field_and_both_digests()
    {
        var frozen = Manifest();
        var drifted = Mutate(frozen, "harnesses");

        var failure = Should.Throw<RuntimeManifestDriftException>(() => QualificationRuntimeManifest.EnsureNoDrift(frozen, drifted));

        failure.Field.ShouldBe("manifest.harnesses[0].binarySha256");
        failure.FrozenDigest.ShouldBe(frozen.ManifestDigest());
        failure.ObservedDigest.ShouldBe(drifted.ManifestDigest());
        failure.Message.ShouldNotContain(Secret);
    }

    [Fact]
    public void A_missing_or_extra_group_is_named_rather_than_silently_equal()
    {
        var frozen = Manifest();
        var shortened = frozen with { CredentialEndpoints = frozen.CredentialEndpoints.Take(2).ToList() };

        QualificationRuntimeManifest.Compare(frozen, shortened).ShouldBe("manifest.credentialEndpoints");
    }

    private static string ProtocolDigestFor(QualificationRuntimeManifest manifest) =>
        PairedTaskLaunchQualificationRunner.ProtocolDigest(Protocol(manifest));

    private static PairedQualificationProtocol Protocol(QualificationRuntimeManifest manifest) => new()
    {
        ObservationGroupId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        TeamId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        SuiteDigest = "sha256:hidden",
        SuiteVersion = "suite/v1",
        CodeRevision = new string('a', 40),
        ControlModelRowId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        CandidateModelRowId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        RuntimeManifestJson = manifest.CanonicalJson(),
        RuntimeManifestDigest = manifest.ManifestDigest(),
        RequiresCellAdmission = true,
        RequiresResultDigest = true,
        StatisticsVersion = PairedQualificationOutcome.StatisticsVersion,
        Criterion = nameof(PairedQualificationCriterion.Quality),
        SessionsPerCell = 2,
        MinimumIndependentClusters = 1,
        MinimumStrata = 1,
        MinimumRequiredExecutionClusters = 1,
        MinimumEvaluatorHealth = 1,
        MaxCostUsdPerLaunch = 3m,
        MinimumQualityLift = 0.05,
        NonInferiorityMargin = -0.02,
        MinimumCostReduction = 0.2,
        RequireDistinctObservedModels = true,
        OrderingSeed = "frozen-order",
    };

    private static QualificationRuntimeManifest Mutate(QualificationRuntimeManifest manifest, string group) => group switch
    {
        "harnesses" => manifest with { Harnesses = manifest.Harnesses.Select((harness, index) => index == 0 ? harness with { BinarySha256 = new string('b', 64) } : harness).ToList() },
        "runner" => manifest with { Runner = manifest.Runner with { BubblewrapAvailable = !manifest.Runner.BubblewrapAvailable } },
        "credentialEndpoints" => manifest with { CredentialEndpoints = manifest.CredentialEndpoints.Select((endpoint, index) => index == 0 ? endpoint with { EndpointHost = "swapped.example.invalid" } : endpoint).ToList() },
        "reviewer" => manifest with { Reviewer = manifest.Reviewer with { IndependencePolicyVersion = "llm-rubric-judge/v1" } },
        "execution" => manifest with { Execution = manifest.Execution with { DefaultCompletionMode = CompletionEnforcementMode.Enforced } },
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Every frozen group needs a mutation here — a group with none would be untested."),
    };

    private static QualificationRuntimeManifest Manifest() => new()
    {
        Harnesses =
        [
            new HarnessBinaryIdentity { Kind = "claude-code", Version = "2.1.263", BinarySha256 = new string('a', 64) },
            new HarnessBinaryIdentity { Kind = "codex-cli", Version = "0.142.2", UnobservedReason = HarnessBinaryIdentity.ReasonNotFound },
        ],
        Runner = new RunnerProfile
        {
            BuildIdentity = "1.0.0+abcdef",
            OsPlatform = "Linux",
            OsArchitecture = "X64",
            BubblewrapAvailable = true,
            RequireConfinement = false,
            MaxAutonomy = "Unleashed",
        },
        CredentialEndpoints =
        [
            Endpoint(QualificationCredentialRole.Control, 1, "https://gateway.example.com/v1/messages?key=token-in-the-path"),
            Endpoint(QualificationCredentialRole.Candidate, 2, "https://gateway.example.com/v1/messages?key=token-in-the-path"),
            Endpoint(QualificationCredentialRole.Reviewer, 3, null),
        ],
        Reviewer = new ReviewerResolution { JudgeModelRowId = Row(3), IndependencePolicyVersion = "llm-rubric-judge/v2-observed-identity" },
        Execution = new ExecutionSettings
        {
            ArmEffortTiers = new Dictionary<string, string?> { ["TaskLaunchAuto"] = null, ["TaskLaunchQuick"] = "quick" },
            DefaultCompletionMode = CompletionEnforcementMode.Shadow,
            CompletionPolicyVersion = 2,
            CellDriveGraceSeconds = 60,
            DeepAgentTimeoutSeconds = 7200,
            LlmRequestTimeoutSeconds = 600,
            AcceptanceEvaluatorVersion = "supervisor-acceptance/v7",
            DeliveryEvaluatorVersion = "publish-manifest/v1",
            PlannerPromptDigest = new string('1', 64),
            PlannerSchemaDigest = new string('2', 64),
            SupervisorPromptDigest = new string('3', 64),
            SupervisorSchemaDigest = new string('4', 64),
        },
    };

    private static CredentialEndpointIdentity Endpoint(QualificationCredentialRole role, int seed, string? baseUrl) =>
        CredentialEndpointIdentity.Observe(role, Row(seed), Row(seed + 10), "Anthropic", baseUrl, Secret, "campaign-salt");

    private static Guid Row(int seed) => Guid.Parse($"{seed:D8}-0000-0000-0000-000000000000");
}
