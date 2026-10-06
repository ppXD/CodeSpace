using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.UnitTests.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: an acceptance grade runs the PRODUCING run's posture. Its setup step and its check execute bytes the agent
/// wrote, after the agent's own sandbox is gone, so they may never reach further than that agent could. Before this,
/// the setup step was hard-coded to the host network and neither step had a memory or CPU ceiling, whatever the tier.
///
/// <para>Three layers, each through production code. First, the derivation table (tier × egress allowlist ×
/// deployment ceiling). Second, the real <see cref="SupervisorAcceptanceGrader"/> with the real
/// <see cref="TestsPassGrader"/>, recording the exact specs it hands its runner. Third, those recorded specs fed
/// through <see cref="LocalProcessRunner.ChildCommand"/> on a stand-in bwrap path: the argv a confining Linux host
/// would launch, provable on any host. The real kernel's answer is the sandbox lane's
/// (<c>AcceptanceGradingPostureE2ETests</c>).</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class AcceptanceGradingPostureTests
{
    private const string Bwrap = "/usr/bin/bwrap";
    private static readonly string[] SetupCommand = { "npm", "ci" };
    private static readonly string[] CheckCommand = { "npm", "test" };

    // ── The derivation table ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    //          tier                           network                     egress                       extra hosts            deployment ceiling            → network  allowlist               memory  cpu
    [InlineData(AgentAutonomyLevel.Confined,  AgentNetworkAccess.Off, AgentEgressPolicy.Full,      null,                  AgentAutonomyLevel.Unleashed, false, null,                    1024, 100)]
    [InlineData(AgentAutonomyLevel.Standard,  AgentNetworkAccess.Off, AgentEgressPolicy.Full,      null,                  AgentAutonomyLevel.Unleashed, false, null,                    4096, 400)]
    [InlineData(AgentAutonomyLevel.Trusted,   AgentNetworkAccess.On,  AgentEgressPolicy.Full,      null,                  AgentAutonomyLevel.Unleashed, true,  null,                    6144, 400)]
    [InlineData(AgentAutonomyLevel.Unleashed, AgentNetworkAccess.On,  AgentEgressPolicy.Full,      null,                  AgentAutonomyLevel.Unleashed, true,  null,                    6144, 400)]
    [InlineData(AgentAutonomyLevel.Trusted,   AgentNetworkAccess.On,  AgentEgressPolicy.Allowlist, "Registry.NPMjs.org ", AgentAutonomyLevel.Unleashed, true,  "registry.npmjs.org",    6144, 400)]   // narrowed to the operator's hosts, normalized
    [InlineData(AgentAutonomyLevel.Trusted,   AgentNetworkAccess.On,  AgentEgressPolicy.Allowlist, null,                  AgentAutonomyLevel.Unleashed, false, null,                    6144, 400)]   // an allowlist with no host severs — never full egress
    [InlineData(AgentAutonomyLevel.Trusted,   AgentNetworkAccess.On,  AgentEgressPolicy.Full,      null,                  AgentAutonomyLevel.Standard,  false, null,                    4096, 400)]   // the deployment ceiling clamps tier AND network
    [InlineData(AgentAutonomyLevel.Unleashed, AgentNetworkAccess.On,  AgentEgressPolicy.Allowlist, "pypi.org",            AgentAutonomyLevel.Standard,  false, null,                    4096, 400)]
    [InlineData(AgentAutonomyLevel.Unleashed, AgentNetworkAccess.On,  AgentEgressPolicy.Full,      null,                  AgentAutonomyLevel.Trusted,   true,  null,                    6144, 400)]   // a ceiling that grants network clamps nothing
    [InlineData(AgentAutonomyLevel.Trusted,   AgentNetworkAccess.On,  AgentEgressPolicy.Full,      null,                  AgentAutonomyLevel.Confined,  false, null,                    1024, 100)]
    [InlineData(AgentAutonomyLevel.Trusted,   AgentNetworkAccess.Off, AgentEgressPolicy.Full,      null,                  AgentAutonomyLevel.Unleashed, false, null,                    6144, 400)]   // the run's own grant, not its tier's maximum
    [InlineData(AgentAutonomyLevel.Standard,  AgentNetworkAccess.On,  AgentEgressPolicy.Full,      null,                  AgentAutonomyLevel.Unleashed, false, null,                    4096, 400)]   // a grant its tier cannot hold is not honoured
    public void The_posture_is_the_producers_own_grant_clamped_by_the_deployment_ceiling(AgentAutonomyLevel tier, AgentNetworkAccess network, AgentEgressPolicy egress, string? extraHosts, AgentAutonomyLevel deploymentCeiling, bool expectedNetwork, string? expectedAllowlist, int expectedMemoryMb, int expectedCpuPercent)
    {
        var permissions = new AgentPermissions { Network = network, Egress = egress, EgressAllowHosts = extraHosts is null ? null : new[] { extraHosts } };

        var posture = AcceptanceGradingPosturePolicy.Derive(tier, permissions, deploymentCeiling, hostMemoryBudgetMb: null);

        posture.AllowNetwork.ShouldBe(expectedNetwork);
        posture.EgressAllowlist.ShouldBe(expectedAllowlist is null ? null : new[] { expectedAllowlist });
        posture.MaxMemoryMb.ShouldBe(expectedMemoryMb, "the clamped tier's committed memory row");
        posture.MaxCpuPercent.ShouldBe(expectedCpuPercent, "the clamped tier's committed cpu row");
        posture.Autonomy.ShouldBe(AgentAutonomyPolicy.Clamp(tier, deploymentCeiling));
    }

    [Fact]
    public void The_host_memory_budget_narrows_the_grade_as_it_narrows_the_agent() =>
        AcceptanceGradingPosturePolicy.Derive(AgentAutonomyLevel.Trusted, AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted), AgentAutonomyLevel.Unleashed, hostMemoryBudgetMb: 2048).MaxMemoryMb.ShouldBe(2048);

    [Theory]
    //          Sandbox:MaxAutonomy  Sandbox:AgentMemoryCeilingMb  → tier                          network  memory
    [InlineData("Standard",          "2048",                         AgentAutonomyLevel.Standard,  false,   2048)]   // both settings narrow the Trusted producer
    [InlineData(null,                null,                           AgentAutonomyLevel.Trusted,   true,    6144)]   // unset: the producer's own row, unnarrowed
    public void The_production_posture_reads_the_deployment_ceiling_and_the_host_memory_budget_from_configuration(string? maxAutonomy, string? memoryCeilingMb, AgentAutonomyLevel expectedTier, bool expectedNetwork, int expectedMemoryMb)
    {
        // For(task) is the only place either setting reaches a grade. Every lane test computes its expected posture
        // through For() itself, and the table above calls Derive with explicit arguments, so only this test sees
        // whether For() actually reads the operator's configuration.
        var producer = new AgentTask { Goal = "g", Harness = "test", Autonomy = AgentAutonomyLevel.Trusted, Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted) };
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RuntimeSettings.MaxAutonomyKey] = maxAutonomy,
            [RuntimeSettings.AgentMemoryCeilingMbKey] = memoryCeilingMb,
        }).Build();

        AcceptanceGradingPosture posture;
        using (RuntimeSettings.Override(RuntimeSettings.Read(configuration))) posture = AcceptanceGradingPosturePolicy.For(producer);

        posture.Autonomy.ShouldBe(expectedTier, "Sandbox:MaxAutonomy clamps the grade as it clamps the agent");
        posture.AllowNetwork.ShouldBe(expectedNetwork, "a clamped tier never derives network its ceiling denies");
        posture.MaxMemoryMb.ShouldBe(expectedMemoryMb, "Sandbox:AgentMemoryCeilingMb narrows the grade's memory ceiling");
    }

    [Fact]
    public void A_tier_the_policy_does_not_know_grades_as_confined()
    {
        var posture = AcceptanceGradingPosturePolicy.Derive((AgentAutonomyLevel)99, new AgentPermissions { Network = AgentNetworkAccess.On }, AgentAutonomyLevel.Unleashed, hostMemoryBudgetMb: null);

        posture.Autonomy.ShouldBe(AgentAutonomyLevel.Confined);
        posture.AllowNetwork.ShouldBeFalse();
        posture.MaxMemoryMb.ShouldBe(1024);
    }

    [Fact]
    public void A_grade_with_no_producer_is_network_off_under_the_confined_ceilings()
    {
        var posture = AcceptanceGradingPosturePolicy.FailClosed;
        var confined = AgentAutonomyPolicy.Ceilings(AgentAutonomyLevel.Confined, RuntimeSettings.Current.AgentMemoryCeilingMb);

        posture.AllowNetwork.ShouldBeFalse();
        posture.EgressAllowlist.ShouldBeNull();
        posture.MaxMemoryMb.ShouldBe(confined.MemoryMb);
        posture.MaxCpuPercent.ShouldBe(confined.CpuPercent);
    }

    [Fact]
    public void The_narrowed_setup_notice_prefix_is_pinned() =>
        // Rule 8: an operator whose setup stopped downloading on a network-off run searches the evidence for this.
        AcceptanceGradingPosturePolicy.SetupNetworkNoticePrefix.ShouldBe("setup network: ");

    [Fact]
    public void The_grade_details_the_retry_verdicts_read_are_pinned()
    {
        // Rule 8: the grader writes these and the infra classifier and the agent.code retry verdict read them, across
        // a durable resume payload. A rename on one side alone silently flips which failures re-bill an agent run.
        TestsPassGrader.ResourceExhaustedDetail.ShouldBe("tests-resource-exhausted");
        AgentAcceptanceContract.SetupSeveredDetailPrefix.ShouldBe("setup-failed-network-severed:");
    }

    [Theory]
    //          producer tier                 allowlist host        egress the runner actually enforced  → notice
    [InlineData(AgentAutonomyLevel.Standard, null,                 SandboxEgressMode.None,     "setup network: off, as the producing run had it (Standard) — the sandbox severed it; a setup that downloads needs an egress allowlist or a higher tier")]
    [InlineData(AgentAutonomyLevel.Trusted,  "registry.npmjs.org", SandboxEgressMode.Filtered, "setup network: narrowed to the producing run's egress allowlist (registry.npmjs.org) — the sandbox filtered it")]
    [InlineData(AgentAutonomyLevel.Trusted,  "registry.npmjs.org", SandboxEgressMode.None,     "setup network: off — this host cannot filter to the producing run's egress allowlist (registry.npmjs.org), so the sandbox severed it; a setup that downloads needs a host that filters egress, or a higher tier")]
    [InlineData(AgentAutonomyLevel.Standard, null,                 SandboxEgressMode.Full,     null)]   // an unconfined host: the setup kept the network, so the grade claims nothing
    [InlineData(AgentAutonomyLevel.Standard, null,                 null,                       null)]   // a runner that cannot say what it enforces: no claim either
    [InlineData(AgentAutonomyLevel.Trusted,  null,                 SandboxEgressMode.Full,     null)]
    public void The_notice_says_what_the_sandbox_did_to_the_setups_network_and_is_silent_when_it_did_nothing(AgentAutonomyLevel tier, string? allowHost, SandboxEgressMode? enforced, string? expected)
    {
        var permissions = AgentAutonomyPolicy.Derive(tier) with { Egress = allowHost is null ? AgentEgressPolicy.Full : AgentEgressPolicy.Allowlist, EgressAllowHosts = allowHost is null ? null : new[] { allowHost } };

        AcceptanceGradingPosturePolicy.SetupNetworkNotice(AcceptanceGradingPosturePolicy.Derive(tier, permissions, AgentAutonomyLevel.Unleashed, null), enforced).ShouldBe(expected);
    }

    [Theory]
    //          asks network  allowlist      confines  filters allowlist → enforced
    [InlineData(false,        null,          true,     false,            SandboxEgressMode.None)]       // bwrap's --unshare-net
    [InlineData(false,        null,          false,    false,            SandboxEgressMode.Full)]       // nothing on this host severs it
    [InlineData(true,         null,          true,     false,            SandboxEgressMode.Full)]
    [InlineData(true,         "pypi.org",    true,     true,             SandboxEgressMode.Filtered)]   // the per-run filtered namespace
    [InlineData(true,         "pypi.org",    false,    true,             SandboxEgressMode.Filtered)]   // the filtered namespace needs no bwrap
    [InlineData(true,         "pypi.org",    true,     false,            SandboxEgressMode.None)]       // an allowlist bwrap cannot filter fails closed to severed
    [InlineData(true,         "pypi.org",    false,    false,            SandboxEgressMode.Full)]       // and nothing enforces it where nothing confines
    public void The_runner_reports_the_egress_it_actually_enforces_not_the_one_the_spec_asks_for(bool allowNetwork, string? allowHost, bool confines, bool filtersAllowlist, SandboxEgressMode expected)
    {
        var spec = new SandboxSpec { Command = "npm", AllowNetwork = allowNetwork, EgressAllowlist = allowHost is null ? null : new[] { allowHost } };

        LocalProcessRunner.EnforcedEgress(spec, confines, filtersAllowlist).ShouldBe(expected);
    }

    // ── The grader's choke point, proved down to the argv ─────────────────────────────────────────────────

    [Theory]
    [InlineData(AgentAutonomyLevel.Confined)]
    [InlineData(AgentAutonomyLevel.Standard)]
    public async Task A_network_off_producers_setup_and_check_are_both_severed(AgentAutonomyLevel tier)
    {
        var posture = Posture(tier);
        var (setup, check) = await GradeAsync(posture);

        setup.AllowNetwork.ShouldBeFalse("the setup asked for the network, and the producer had none to give it");
        Argv(setup).ShouldContain("--unshare-net", customMessage: "under bubblewrap the setup gets a fresh, empty network namespace — it cannot reach a host the agent could not");
        Argv(check).ShouldContain("--unshare-net", customMessage: "the check's default network cut is unchanged");
    }

    [Theory]
    [InlineData(AgentAutonomyLevel.Trusted)]
    [InlineData(AgentAutonomyLevel.Unleashed)]
    public async Task A_network_granting_producers_setup_keeps_the_host_network_and_its_check_stays_severed(AgentAutonomyLevel tier)
    {
        var (setup, check) = await GradeAsync(Posture(tier));

        setup.AllowNetwork.ShouldBeTrue();
        setup.EgressAllowlist.ShouldBeNull();
        Argv(setup).ShouldNotContain("--unshare-net", customMessage: "a Trusted/Unleashed producer's setup still downloads — the clamp only narrows");
        Argv(check).ShouldContain("--unshare-net", customMessage: "the check never asked for the network, so the posture grants it none");
    }

    [Fact]
    public async Task An_allowlist_producers_setup_is_filtered_to_its_hosts_and_never_shares_the_host_network()
    {
        var permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted) with { Egress = AgentEgressPolicy.Allowlist, EgressAllowHosts = new[] { "registry.npmjs.org" } };
        var (setup, check) = await GradeAsync(AcceptanceGradingPosturePolicy.Derive(AgentAutonomyLevel.Trusted, permissions, AgentAutonomyLevel.Unleashed, null));

        var policy = LocalProcessRunner.EgressPolicyFor(setup, filtersAllowlist: true);
        policy.Mode.ShouldBe(SandboxEgressMode.Filtered, "a host that filters puts the setup in a per-run netns that reaches only the allowlist");
        policy.AllowedHosts.ShouldBe(new[] { "registry.npmjs.org" }, "the producer's operator-configured hosts, and nothing the grade does not need (no model API host, no git host)");

        string[] netns = { "ip", "netns", "exec", "cs-egr-deadbeef" };
        var filtered = Chain(setup, netns);
        filtered.Take(netns.Length).ShouldBe(netns, "the whole chain enters the filtered namespace");
        filtered.ShouldNotContain("--unshare-net", "bwrap inherits the filtered namespace rather than re-unsharing it away");

        Argv(setup).ShouldContain("--unshare-net", customMessage: "a confining host that cannot filter severs the setup — an allowlist never degrades to the host network");
        Argv(check).ShouldContain("--unshare-net");
    }

    [Fact]
    public async Task A_grade_with_no_posture_is_severed_under_the_confined_ceilings()
    {
        var (setup, check) = await GradeAsync(posture: null);
        var confined = AcceptanceGradingPosturePolicy.FailClosed;

        Argv(setup).ShouldContain("--unshare-net", customMessage: "missing posture fails closed — never the hard-coded host network this replaced");
        setup.MaxMemoryMb.ShouldBe(confined.MaxMemoryMb);
        check.MaxMemoryMb.ShouldBe(confined.MaxMemoryMb);
        check.MaxCpuPercent.ShouldBe(confined.MaxCpuPercent);
    }

    [Theory]
    [InlineData(AgentAutonomyLevel.Confined)]
    [InlineData(AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Trusted)]
    public async Task The_setup_and_the_check_both_run_under_the_producers_resource_ceilings(AgentAutonomyLevel tier)
    {
        var posture = Posture(tier);
        var (setup, check) = await GradeAsync(posture);

        setup.MaxMemoryMb.ShouldBe(posture.MaxMemoryMb, "an agent-planted install script is capped like the agent was");
        setup.MaxCpuPercent.ShouldBe(posture.MaxCpuPercent);
        check.MaxMemoryMb.ShouldBe(posture.MaxMemoryMb, "so is the check that imports the agent's code");
        check.MaxCpuPercent.ShouldBe(posture.MaxCpuPercent);
        posture.MaxMemoryMb.ShouldBeGreaterThan(0, "fixture check: a zero ceiling would mean unlimited and pass vacuously");
    }

    // ── The notice ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_network_narrowed_setup_records_one_notice_at_the_head_of_the_grades_evidence()
    {
        var artifacts = new SupervisorAcceptanceGraderTests.FakeArtifactStore();

        await GradeAsync(Posture(AgentAutonomyLevel.Standard), artifacts: artifacts);

        var evidence = artifacts.Puts.ShouldHaveSingleItem("the check's own evidence carries the notice").Text;
        evidence.ShouldStartWith(AcceptanceGradingPosturePolicy.SetupNetworkNoticePrefix + "off");
        evidence.Split(AcceptanceGradingPosturePolicy.SetupNetworkNoticePrefix).Length.ShouldBe(2, "one notice per grade");
    }

    [Fact]
    public async Task A_setup_that_kept_the_network_records_no_notice()
    {
        var artifacts = new SupervisorAcceptanceGraderTests.FakeArtifactStore();

        await GradeAsync(Posture(AgentAutonomyLevel.Trusted), artifacts: artifacts);

        artifacts.Puts.ShouldHaveSingleItem().Text.ShouldNotContain(AcceptanceGradingPosturePolicy.SetupNetworkNoticePrefix);
    }

    [Theory]
    [InlineData(SandboxStatus.Failed, "setup-failed-network-severed: npm ERR! network request failed")]
    [InlineData(SandboxStatus.TimedOut, "setup-failed-network-severed: timed out")]
    public async Task A_setup_the_sandbox_severed_that_fails_is_decided_by_the_posture_and_carries_the_notice_as_evidence(SandboxStatus setupStatus, string expectedDetail)
    {
        // The producer's posture is read off the same stored task on every attempt, so a severed setup fails the same
        // way on every respawn. The detail says so, which is the bit an authored agent.code retry reads: infra (never a
        // code verdict, never a revise round) AND decided by the grade's posture (never a re-billed respawn).
        var artifacts = new SupervisorAcceptanceGraderTests.FakeArtifactStore();
        var runners = new RecordingRunners(setupStatus, "npm ERR! network request failed");

        var grade = await GradeAsync(Posture(AgentAutonomyLevel.Standard), runners, artifacts);

        grade.Detail.ShouldBe(expectedDetail);
        grade.Class.ShouldBe(GradeFailureClass.Environment);
        AgentAcceptanceContract.IsInfraFailure(grade, workPresent: true).ShouldBeTrue();
        AgentAcceptanceContract.IsInfraFailure(grade.Detail, workPresent: true).ShouldBeTrue("the string path — the only one the agent.code node's resume payload carries — agrees");
        AgentAcceptanceContract.IsDecidedByGradePosture(grade.Detail).ShouldBeTrue();
        grade.EvidenceArtifactId.ShouldNotBeNull("the failure's evidence is where the operator reads why the setup could not download");
        artifacts.Puts.ShouldHaveSingleItem().Text.ShouldStartWith(AcceptanceGradingPosturePolicy.SetupNetworkNoticePrefix);
        grade.EvidenceTail.ShouldNotBeNull().ShouldContain("needs an egress allowlist or a higher tier");
    }

    [Theory]
    [InlineData(SandboxStatus.Failed, "setup-failed: npm ERR! network request failed")]
    [InlineData(SandboxStatus.TimedOut, "setup-timed-out")]
    public async Task A_network_off_setup_on_a_host_that_does_not_confine_keeps_its_transient_detail_and_claims_no_narrowing(SandboxStatus setupStatus, string expectedDetail)
    {
        // Nothing on this host severed the setup — it ran with the network it asked for — so its failure is the
        // ordinary transient one, and its evidence does not say "off" about a setup that kept the network.
        var artifacts = new SupervisorAcceptanceGraderTests.FakeArtifactStore();

        var grade = await GradeAsync(Posture(AgentAutonomyLevel.Standard), new RecordingRunners(setupStatus, "npm ERR! network request failed", confines: false), artifacts);

        grade.Detail.ShouldBe(expectedDetail);
        AgentAcceptanceContract.IsDecidedByGradePosture(grade.Detail).ShouldBeFalse();
        artifacts.Puts.ShouldBeEmpty("no notice, so the failure stays evidence-less exactly as before");
    }

    [Fact]
    public async Task An_allowlisted_setup_that_fails_stays_transient_and_says_it_was_filtered()
    {
        // A filtered setup still reaches its allowlisted hosts, so its failure may be a transient blip on one of them.
        var artifacts = new SupervisorAcceptanceGraderTests.FakeArtifactStore();
        var permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted) with { Egress = AgentEgressPolicy.Allowlist, EgressAllowHosts = new[] { "registry.npmjs.org" } };
        var posture = AcceptanceGradingPosturePolicy.Derive(AgentAutonomyLevel.Trusted, permissions, AgentAutonomyLevel.Unleashed, null);

        var grade = await GradeAsync(posture, new RecordingRunners(SandboxStatus.Failed, "npm ERR! 503", filtersAllowlist: true), artifacts);

        grade.Detail.ShouldBe("setup-failed: npm ERR! 503");
        AgentAcceptanceContract.IsDecidedByGradePosture(grade.Detail).ShouldBeFalse();
        artifacts.Puts.ShouldHaveSingleItem().Text.ShouldStartWith(AcceptanceGradingPosturePolicy.SetupNetworkNoticePrefix + "narrowed");
    }

    // ── A check killed at the grade's own ceiling ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Trusted)]
    public async Task A_check_killed_at_the_grades_memory_ceiling_is_an_environment_fact_not_a_code_verdict(AgentAutonomyLevel tier)
    {
        // Before the grade carried ceilings no grade step had a cgroup, so ResourceExhausted could not happen. Now it
        // can, and the agent's own run already reads the same kill as "not a fault in the agent's work".
        var posture = Posture(tier);
        var runners = new RecordingRunners(SandboxStatus.Success, "", checkStatus: SandboxStatus.ResourceExhausted);

        var grade = await GradeAsync(posture, runners, new SupervisorAcceptanceGraderTests.FakeArtifactStore());

        runners.Specs[1].MaxMemoryMb.ShouldBe(posture.MaxMemoryMb, "fixture check: the check ran under the posture's ceiling that killed it");
        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldBe(TestsPassGrader.ResourceExhaustedDetail);
        grade.Class.ShouldBe(GradeFailureClass.Environment);
        AgentAcceptanceContract.IsInfraFailure(grade, workPresent: true).ShouldBeTrue("the revise loop never buys a round for it");
        AgentAcceptanceContract.IsInfraFailure(grade.Detail, workPresent: true).ShouldBeTrue("the string path the agent.code node reads agrees");
    }

    [Fact]
    public async Task A_setup_that_fails_with_the_network_it_asked_for_stays_evidence_less_as_before()
    {
        var artifacts = new SupervisorAcceptanceGraderTests.FakeArtifactStore();

        var grade = await GradeAsync(Posture(AgentAutonomyLevel.Trusted), new RecordingRunners(SandboxStatus.Failed, "npm ERR!"), artifacts);

        grade.Detail.ShouldStartWith("setup-failed:");
        grade.EvidenceArtifactId.ShouldBeNull();
        artifacts.Puts.ShouldBeEmpty();
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────────────────────

    private static AcceptanceGradingPosture Posture(AgentAutonomyLevel tier) =>
        AcceptanceGradingPosturePolicy.Derive(tier, AgentAutonomyPolicy.Derive(tier), AgentAutonomyLevel.Unleashed, hostMemoryBudgetMb: null);

    /// <summary>Grade a scratch workspace with a setup step through the REAL grader and the real TestsPass oracle, and return the two specs it handed its runner.</summary>
    private static async Task<(SandboxSpec Setup, SandboxSpec Check)> GradeAsync(AcceptanceGradingPosture? posture, SupervisorAcceptanceGraderTests.FakeArtifactStore? artifacts = null)
    {
        var runners = new RecordingRunners(SandboxStatus.Success, "");
        await GradeAsync(posture, runners, artifacts ?? new SupervisorAcceptanceGraderTests.FakeArtifactStore());

        runners.Specs.Select(s => s.Command).ShouldBe(new[] { "npm", "npm" }, "fixture check: the setup step and then the check ran");
        runners.Specs[0].Args.ShouldBe(new[] { "ci" });

        return (runners.Specs[0], runners.Specs[1]);
    }

    private static async Task<BenchmarkGrade> GradeAsync(AcceptanceGradingPosture? posture, RecordingRunners runners, SupervisorAcceptanceGraderTests.FakeArtifactStore artifacts)
    {
        var directory = Directory.CreateTempSubdirectory("cs-grade-posture-").FullName;
        try
        {
            // Only the seams GradeDirectoryAsync reaches are real or recorded: the runner, the oracle registry and the
            // evidence store. The clone/patch/captured collaborators are never touched on this lane.
            var grader = new SupervisorAcceptanceGrader(null!, null!, runners, new BenchmarkGraderRegistry(new IBenchmarkGrader[] { new TestsPassGrader() }), null!, artifacts, null!, NullLogger<SupervisorAcceptanceGrader>.Instance);
            var spec = new SupervisorAcceptanceSpec { Command = CheckCommand, SetupCommand = SetupCommand };

            return await grader.GradeDirectoryAsync(new DirectoryAcceptanceGradeRequest { Directory = directory, Spec = spec, TeamId = Guid.NewGuid(), TimeoutSeconds = 30, Posture = posture }, CancellationToken.None);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>The argv a confining host launches for <paramref name="spec"/>, with no filtered namespace.</summary>
    private static IReadOnlyList<string> Argv(SandboxSpec spec) => Chain(spec, Array.Empty<string>());

    private static IReadOnlyList<string> Chain(SandboxSpec spec, IReadOnlyList<string> egressPrefix) =>
        LocalProcessRunner.ChildCommand(new LocalProcessRunner.CommandIsolationContext(spec, null, null, egressPrefix, Array.Empty<string>()), Bwrap, prlimit: null);

    /// <summary>
    /// Records every spec the grader runs; the setup step (the first) answers <paramref name="setupStatus"/> and the
    /// check (the second) <paramref name="checkStatus"/>. It reports the egress it enforces through the production
    /// table (<see cref="LocalProcessRunner.EnforcedEgress(SandboxSpec, bool, bool)"/>) for a host that
    /// <paramref name="confines"/> by default — the host whose narrowing the notice describes.
    /// </summary>
    private sealed class RecordingRunners(SandboxStatus setupStatus, string setupStderr, SandboxStatus checkStatus = SandboxStatus.Success, bool confines = true, bool filtersAllowlist = false) : ISandboxRunnerRegistry, ISandboxRunner, ISandboxEgressEnforcement
    {
        public List<SandboxSpec> Specs { get; } = new();
        public string Kind => SandboxKinds.Local;
        public IReadOnlyList<ISandboxRunner> All => new ISandboxRunner[] { this };
        public ISandboxRunner Resolve(string kind) => this;

        public SandboxEgressMode EnforcedEgress(SandboxSpec spec) => LocalProcessRunner.EnforcedEgress(spec, confines, filtersAllowlist);

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            var status = Specs.Count switch { 1 => setupStatus, 2 => checkStatus, _ => SandboxStatus.Success };
            var exitCode = status switch { SandboxStatus.Success => 0, SandboxStatus.ResourceExhausted => 137, _ => 1 };

            return Task.FromResult(new SandboxResult { Status = status, ExitCode = exitCode, Stdout = "", Stderr = status == SandboxStatus.Success ? "" : setupStderr });
        }
    }
}
