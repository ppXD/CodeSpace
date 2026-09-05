using System.Text.RegularExpressions;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// Pins the two wall-clock BOUNDS the live-brain supervisor lane rests on, so neither can be loosened by accident.
///
/// <para>Run 33972713055 is why they exist. <c>AgentTask.TimeoutSeconds</c> defaults to 3600 (1h) — the right production
/// floor, but SIX TIMES the gate's own per-attempt deadline, so a single wedged CLI session outlived the attempt that
/// contained it, ran the full hour, and pushed the supervisor-arcs job into its 120-minute cap for the first time. The
/// lane's fixture must therefore cap its spawned agents FAR below the production default, and the cap must stay below
/// the attempt deadline that is supposed to contain it — a relationship no single constant can express, which is
/// exactly why it is asserted here rather than left as a comment on either one.</para>
///
/// <para>A pure check (no DB, no secrets, no live model), tagged to run on the ordinary <c>Surface=Engine</c> E2E gate
/// — the bound must be verified on EVERY PR, not only on the real-model lane it protects.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class RealModelLaneBoundsTests
{
    [Fact]
    public void The_two_lane_caps_are_pinned()
    {
        // Rule 8: the relational asserts below say a cap must SIT below two other numbers — they stay green if both
        // caps are quietly halved, or if one is raised to 599s and left a single second inside the attempt deadline it
        // is supposed to have orders of magnitude of headroom against. Pin the literals so a re-tune is a deliberate,
        // reviewed edit of THIS line rather than an invisible drift of a value the lane's behaviour rests on.
        RealModelSupervisorWholeLoopE2ETests.FakeAgentTimeoutSeconds.ShouldBe(300);
        RealModelSupervisorWholeLoopE2ETests.RealAgentTimeoutSeconds.ShouldBe(480);
    }

    [Fact]
    public void The_lane_caps_its_spawned_agents_far_below_the_production_default()
    {
        // A FIXTURE value, never the production default: the lane's fakes exit in milliseconds and even its one real
        // coding agent finishes in minutes, so inheriting production's 1h would only ever bound a WEDGE — badly.
        var productionDefault = new AgentTask { Goal = "x", Harness = "codex-cli" }.TimeoutSeconds;

        productionDefault.ShouldBe(3600, "the production default moved — re-derive this lane's caps against the new value rather than leaving them anchored to a number that no longer exists");

        RealModelSupervisorWholeLoopE2ETests.FakeAgentTimeoutSeconds.ShouldBeLessThan(productionDefault!.Value);
        RealModelSupervisorWholeLoopE2ETests.RealAgentTimeoutSeconds.ShouldBeLessThan(productionDefault!.Value);
    }

    [Fact]
    public void A_wedged_agent_cannot_outlive_the_gate_attempt_that_contains_it()
    {
        // The actual invariant the run broke: an agent cap has to fit INSIDE the gate's per-attempt deadline, or a
        // single wedge escapes the bound designed to catch it and rides to the CI job's wall-clock cap instead.
        //
        // Both caps are measured against the TIGHTEST deadline any arm in this class runs under — the STRICT whole-loop
        // gate's 600s, which is the one bounding the headline, reaction and real-coding arms. The report-only arms sit
        // under the looser single-attempt deadline, so a cap that clears 600s clears that too.
        var tightestAttemptDeadline = Math.Min(RealModelGate.DefaultWholeLoopAttemptDeadlineSeconds, RealModelGate.DefaultAttemptDeadlineSeconds);

        RealModelSupervisorWholeLoopE2ETests.FakeAgentTimeoutSeconds
            .ShouldBeLessThan(tightestAttemptDeadline,
                "a fake agent that can outlive one gate attempt defeats the attempt deadline — the wedge would ride to the job cap, which is how run 33972713055 died");

        RealModelSupervisorWholeLoopE2ETests.RealAgentTimeoutSeconds
            .ShouldBeLessThan(tightestAttemptDeadline,
                "the real coding agent legitimately runs longer than a fake, but its arm gates through the STRICT whole-loop deadline — a cap above that bounds nothing");
    }

    [Fact]
    public void The_lanes_agent_profile_actually_carries_the_cap()
    {
        // Asserted against the REAL fragment the supervisor config is built from, not a copy of it: a cap that lives
        // only in a constant nobody wires bounds nothing (`?? 3600` in RealSupervisorActionExecutor.Spawn silently
        // restores the production default the moment the profile stops carrying `timeoutSeconds`).
        var fakeProfile = RealModelSupervisorWholeLoopE2ETests.AgentProfileJson(Guid.NewGuid(), RealModelSupervisorWholeLoopE2ETests.FakeAgentTimeoutSeconds, "", "");
        var realProfile = RealModelSupervisorWholeLoopE2ETests.AgentProfileJson(Guid.NewGuid(), RealModelSupervisorWholeLoopE2ETests.RealAgentTimeoutSeconds, "", "");

        fakeProfile.ShouldContain($"\"timeoutSeconds\": {RealModelSupervisorWholeLoopE2ETests.FakeAgentTimeoutSeconds}");
        realProfile.ShouldContain($"\"timeoutSeconds\": {RealModelSupervisorWholeLoopE2ETests.RealAgentTimeoutSeconds}");
    }

    [Fact]
    public void Every_real_model_arm_that_authors_an_agent_profile_builds_it_from_that_fragment()
    {
        // The half the assertion above cannot see: C# does not warn on an unused internal method, so re-inlining the
        // agentProfile object would leave AgentProfileJson green, unused, and the lane silently back on the 3600s
        // default. Scanned from source because the alternative — building the real config — needs the DB fixture.
        //
        // Scanned across EVERY real-model arm, not just the supervisor file: the delivery-gate arm authored its own
        // inline agentProfile and inherited the 3600s production default for exactly as long as this scan was pointed
        // at one path. A per-file list would rot the same way, so the set is DERIVED — any RealModel* source that
        // mentions an agentProfile is in scope, which is the property that actually matters.
        var authors = RealModelSourcesMentioning(ProfileKey);

        authors.Count.ShouldBeGreaterThanOrEqualTo(2,
            customMessage: "the scan must find the real-model arms that author an agentProfile, or it passes by finding nothing");

        var inlined = authors.Where(f => !AuthorsThroughTheHelper.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();

        inlined.ShouldBeEmpty(
            "every real-model arm's supervisor config must build its agentProfile through AgentProfileJson — an inlined object drops the timeoutSeconds cap and RealSupervisorActionExecutor.Spawn's `?? 3600` quietly restores the production default, which is longer than the whole gate attempt containing the agent");
    }

    /// <summary>The authored JSON key, assembled from parts so this file is never itself a match for its own scan.</summary>
    private const string ProfileKey = "\"agent" + "Profile\": ";

    /// <summary>An <c>agentProfile</c> built through the shared helper, however it is qualified (the supervisor file calls it unqualified; a sibling arm calls it through the class).</summary>
    private static readonly Regex AuthorsThroughTheHelper = new(
        Regex.Escape(ProfileKey) + @"\{\{[A-Za-z0-9_.]*" + nameof(RealModelSupervisorWholeLoopE2ETests.AgentProfileJson) + @"\(", RegexOptions.Compiled);

    /// <summary>Every <c>RealModel*.cs</c> test source (both test assemblies) containing <paramref name="needle"/>, bin/obj excluded.</summary>
    private static IReadOnlyList<string> RealModelSourcesMentioning(string needle) =>
        new[] { "backend/tests/CodeSpace.E2ETests", "backend/tests/CodeSpace.IntegrationTests" }
            .Select(rel => new DirectoryInfo(Path.Combine(FindRepoRoot(), rel)))
            .Where(d => d.Exists)
            .SelectMany(d => d.GetFiles("RealModel*.cs", SearchOption.AllDirectories))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f.FullName).Contains(needle, StringComparison.Ordinal))
            .Select(f => f.FullName)
            .ToList();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
