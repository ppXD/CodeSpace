using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// The runtime gate's DECISION table, over the two pure functions the four paid stages share: what a protocol row
/// must carry before a comparison is even possible (<see cref="QualificationRuntimeGate.FrozenManifestOf"/>), and
/// what the comparison does when it fires (<see cref="QualificationRuntimeManifest.EnsureNoDrift"/> carrying the
/// stage). The database wiring — which row is read, which stage each of the four call sites passes, and that a
/// refused stage writes nothing — is proven against real Postgres in <c>PairedQualificationRunnerFlowTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class QualificationRuntimeGateTests
{
    [Theory]
    [InlineData(QualificationRuntimeStage.Admission)]
    [InlineData(QualificationRuntimeStage.Execution)]
    [InlineData(QualificationRuntimeStage.Resume)]
    [InlineData(QualificationRuntimeStage.Seal)]
    public void An_unchanged_runtime_passes_every_paid_stage(QualificationRuntimeStage stage)
    {
        var frozen = QualificationRuntimeManifestTests.Manifest();

        Should.NotThrow(() => QualificationRuntimeManifest.EnsureNoDrift(frozen, QualificationRuntimeManifestTests.Manifest(), stage));
        Should.NotThrow(() => QualificationRuntimeManifest.EnsureNoDrift(frozen, QualificationRuntimeGate.FrozenManifestOf(frozen.CanonicalJson())!, stage));
    }

    /// <summary>Every frozen group, at a real stage: the refusal names the group that moved AND where it was caught — the two facts that decide what an operator does next — and never a value.</summary>
    [Theory]
    [InlineData("harnesses", QualificationRuntimeStage.Admission)]
    [InlineData("runner", QualificationRuntimeStage.Execution)]
    [InlineData("credentialEndpoints", QualificationRuntimeStage.Resume)]
    [InlineData("reviewer", QualificationRuntimeStage.Seal)]
    [InlineData("execution", QualificationRuntimeStage.Admission)]
    public void A_drifted_group_is_refused_with_its_field_and_its_stage(string group, QualificationRuntimeStage stage)
    {
        var frozen = QualificationRuntimeManifestTests.Manifest();
        var observed = QualificationRuntimeManifestTests.Mutate(frozen, group);

        var failure = Should.Throw<RuntimeManifestDriftException>(() => QualificationRuntimeManifest.EnsureNoDrift(frozen, observed, stage));

        failure.Field.ShouldStartWith($"{QualificationRuntimeManifest.RootField}.{group}");
        failure.Stage.ShouldBe(stage);
        failure.Message.ShouldContain(stage.ToString(), Case.Sensitive, "an operator reading the refusal must see WHERE it fired: nothing is paid for yet at admission or execution, but a seal refusal means a complete campaign is waiting on its own host");
        failure.Details.ShouldNotBeNull()["stage"].ShouldBe(stage.ToString());
        failure.Kind.ShouldBe(FailureKind.Conflict, "a runtime substitution is a designed, operator-actionable refusal — state moved underneath the campaign — and FailureKind.Internal would mask this deliberately value-free message behind a generic one");
    }

    /// <summary>
    /// A LEGACY protocol — committed before the runtime bundle existed, so its manifest column is null — passes
    /// every stage untouched. The gate enforces a manifest that was actually frozen; inventing a baseline from
    /// today's host would refuse every pre-existing campaign on its own evidence.
    /// </summary>
    [Fact]
    public void A_protocol_that_froze_no_manifest_has_nothing_to_compare()
    {
        QualificationRuntimeGate.FrozenManifestOf(null).ShouldBeNull();
    }

    /// <summary>The baseline is the persisted BYTES, read back through <c>Parse</c> — never a manifest reassembled from the row's other fields, which could not reproduce this digest.</summary>
    [Fact]
    public void The_baseline_is_the_frozen_bytes_read_back_at_their_own_digest()
    {
        var frozen = QualificationRuntimeManifestTests.Manifest();

        var reloaded = QualificationRuntimeGate.FrozenManifestOf(frozen.CanonicalJson()).ShouldNotBeNull();

        reloaded.ManifestDigest().ShouldBe(frozen.ManifestDigest());
        QualificationRuntimeManifest.Compare(frozen, reloaded).ShouldBeNull();
    }

    /// <summary>A stage-less comparison (a diagnostic <c>Compare</c>, not one of the campaign's gated stages) omits the stage rather than spelling an invented one into a log.</summary>
    [Fact]
    public void A_comparison_outside_the_gated_stages_names_no_stage()
    {
        var frozen = QualificationRuntimeManifestTests.Manifest();

        var failure = Should.Throw<RuntimeManifestDriftException>(() => QualificationRuntimeManifest.EnsureNoDrift(frozen, QualificationRuntimeManifestTests.Mutate(frozen, "harnesses")));

        failure.Stage.ShouldBeNull();
        failure.Details.ShouldNotBeNull()["stage"].ShouldBeNull();
    }
}
