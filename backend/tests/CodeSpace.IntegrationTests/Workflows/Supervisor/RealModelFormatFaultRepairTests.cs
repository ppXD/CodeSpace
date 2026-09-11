using System.Net.Sockets;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Shouldly;
using Xunit.Sdk;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Pins the ONCE-ONLY bound on the real-model arms' format-fault repair. Everything runs through the helper's pure
/// seams with SYNTHETIC faults — no live model, no gateway, no process env mutation — so the bound is pinned at unit
/// cost and a drift ("buy two repairs", "repair a 429 too", "stop naming the repaired attempt") reds here instead of
/// on the owner's token budget.
///
/// <para>Why pinned at all: the arms' throw sites are three files apart, and the whole reason the helper exists is
/// that a bound living at each call site drifts. If this class is deleted, nothing else fails when a second repair
/// is bought.</para>
/// </summary>
public sealed class RealModelFormatFaultRepairTests
{
    /// <summary>The live gateway text, verbatim from the 9 consecutive main runs that measured nothing. Everything below classifies off THIS string, not a paraphrase — a marker that only matches a paraphrase pins nothing.</summary>
    private const string FormatFault = "API Error: Content block is not a thinking block";

    [Fact]
    public async Task A_format_fault_on_both_attempts_skips_once_and_records_two_attempts()
    {
        var attempts = new List<bool>();

        var fault = await Should.ThrowAsync<AgentExecutionInfraException>(() => RealModelFormatFaultRepair.WithMitigatedRetryAsync(repair =>
        {
            attempts.Add(AgentRetryCauses.IsFormatFaultMitigated(repair(WarmTask())));
            throw new AgentExecutionInfraException($"live revise run {Guid.NewGuid()}: {FormatFault}");
        }));

        attempts.Count.ShouldBe(RealModelFormatFaultRepair.MaxAttempts,
            customMessage: "the repair is bought EXACTLY ONCE — a third dispatch would only re-bill a broken gateway, which is the bound AgentCodeNode already encodes for the production respawn");
        attempts[0].ShouldBeFalse("attempt 1 must dispatch the arm's OWN configuration — a mitigated first attempt would silently measure something the arm never declared");
        attempts[1].ShouldBeTrue("attempt 2 must dispatch production's mitigation — two unmitigated attempts repair nothing and only re-bill the gateway");

        fault.Message.ShouldContain("BOTH", customMessage: "the skip reason must say a repair was attempted and still measured nothing");
        fault.Message.ShouldContain(RealModelFormatFaultRepair.MitigatedRepair, customMessage: "the skip reason must name the repair that did not hold");
        fault.Message.ShouldContain(FormatFault, customMessage: "the skip reason must carry the gateway's own text — it is the only evidence of what broke");
    }

    [Fact]
    public async Task A_format_fault_then_a_pass_reports_the_pass_and_names_the_repair()
    {
        var dispatched = new List<AgentTask>();

        var (outcome, note) = await RealModelFormatFaultRepair.WithMitigatedRetryAsync(repair =>
        {
            var task = repair(WarmTask());
            dispatched.Add(task);

            if (dispatched.Count == 1) throw new AgentExecutionInfraException($"live revise run {Guid.NewGuid()}: {FormatFault}");

            return Task.FromResult((RealModelOutcome.Drove, "the actual CLI drove the arc to the accept head"));
        });

        outcome.ShouldBe(RealModelOutcome.Drove, customMessage: "the repaired attempt's measurement IS the arm's verdict — that is the whole point of buying the repair");
        dispatched.Count.ShouldBe(RealModelFormatFaultRepair.MaxAttempts);

        note.ShouldContain("attempt 2/2", customMessage: "a lane reader must be able to tell a first-attempt pass from a repaired one without opening the log");
        note.ShouldContain(RealModelFormatFaultRepair.MitigatedRepair, customMessage: "a pass produced with extended thinking disabled is a materially different configuration (BenchmarkScorecard.TallyFormatFaults) and must say so");
        note.ShouldContain(FormatFault, customMessage: "the verdict must name the fault the repair bought its way past");

        // The repaired dispatch is production's own composition, read back through production's own predicate — not a
        // test-tree lookalike. Both halves: the conversation dropped AND the thinking budget zeroed.
        AgentRetryCauses.IsFormatFaultMitigated(dispatched[1]).ShouldBeTrue();
        dispatched[1].ResumeFromSessionId.ShouldBeNull();
        dispatched[1].RestoredTranscript.ShouldBeNull();
        dispatched[0].ResumeFromSessionId.ShouldNotBeNull(customMessage: "attempt 1 must dispatch the task the arm authored, untouched");
    }

    [Fact]
    public async Task A_first_attempt_pass_is_reported_as_unrepaired()
    {
        var dispatches = 0;

        var (outcome, note) = await RealModelFormatFaultRepair.WithMitigatedRetryAsync(_ =>
        {
            dispatches++;
            return Task.FromResult((RealModelOutcome.Drove, "the actual CLI drove the arc to the accept head"));
        });

        dispatches.ShouldBe(1, customMessage: "a clean first attempt must never pay for a second dispatch");
        outcome.ShouldBe(RealModelOutcome.Drove);
        note.ShouldContain("attempt 1/2");
        note.ShouldNotContain(RealModelFormatFaultRepair.MitigatedRepair, customMessage: "an unrepaired pass must not advertise a repair it never needed");
    }

    [Theory]
    // Every other infra class the arms already skip on: no repair exists for these, so re-dispatching them would
    // spend the owner's tokens on the same weather. One attempt, then the fault flies exactly as it does today.
    [InlineData("API Error: Request rejected (429) AccountQuotaExceeded")]
    [InlineData("API Error: 500 litellm.InternalServerError: Hosted_vllmException - . Received Model Group=hosted_vllm-group\nAvailable Model Group Fallbacks=None")]
    [InlineData("API Error: 401 Authentication Error")]
    [InlineData("write EPIPE")]
    public async Task A_non_format_infra_fault_buys_no_repair(string error)
    {
        var dispatches = 0;

        var fault = await Should.ThrowAsync<AgentExecutionInfraException>(() => RealModelFormatFaultRepair.WithMitigatedRetryAsync(_ =>
        {
            dispatches++;
            throw new AgentExecutionInfraException($"live revise run {Guid.NewGuid()}: {error}");
        }));

        dispatches.ShouldBe(1, customMessage: "one repair is bought for the FORMAT fault only — the shape ApplyFormatFaultMitigation exists for; every other infra class must skip immediately as before");
        fault.Message.ShouldContain(error);
        fault.Message.ShouldNotContain("BOTH", customMessage: "an unrepaired fault must propagate untouched, not be rewrapped as a spent repair");
    }

    [Fact]
    public async Task A_fault_that_is_not_on_todays_skip_path_is_never_re_driven()
    {
        var dispatches = 0;

        // An assertion failure whose text QUOTES the gateway message is a red, not weather. Re-driving it would turn
        // a genuine failure into a skip — the exact false-green RealModelRunClassifier exists to prevent.
        await Should.ThrowAsync<XunitException>(() => RealModelFormatFaultRepair.WithMitigatedRetryAsync(_ =>
        {
            dispatches++;
            throw new XunitException($"the reviewer must not report {FormatFault}");
        }));

        dispatches.ShouldBe(1);
    }

    [Fact]
    public async Task A_code_fault_is_reported_from_the_first_attempt_and_never_repaired()
    {
        var dispatches = 0;

        var (outcome, note) = await RealModelFormatFaultRepair.WithMitigatedRetryAsync(_ =>
        {
            dispatches++;
            return Task.FromResult((RealModelOutcome.CodeFault, "the native revise path did not execute"));
        });

        dispatches.ShouldBe(1, customMessage: "a CodeFault is a regression the gate must red on at once — never capability variance to be re-driven");
        outcome.ShouldBe(RealModelOutcome.CodeFault);
        note.ShouldContain("attempt 1/2");
    }

    [Fact]
    public async Task A_cold_restage_arm_re_drives_the_identical_staging_exactly_once()
    {
        var dispatches = 0;

        var (outcome, note) = await RealModelFormatFaultRepair.WithColdRestageAsync(() =>
        {
            dispatches++;

            if (dispatches == 1) throw new AgentExecutionInfraException($"the resumed claude run did not complete (status=Failed, error={FormatFault}) — gateway/exec infra, not a recall verdict");

            return Task.FromResult((RealModelOutcome.Drove, "the resumed agent RECALLED the codeword"));
        });

        dispatches.ShouldBe(RealModelFormatFaultRepair.MaxAttempts, customMessage: "the resume arm buys ONE cold re-stage, bounded by the same helper — not a loop that re-bills the gateway");
        outcome.ShouldBe(RealModelOutcome.Drove);
        note.ShouldContain("attempt 2/2");
        note.ShouldContain(RealModelFormatFaultRepair.ColdRestageRepair, customMessage: "the resume arm's repair is a COLD re-stage, never the mitigation that would drop the transcript under test");
        note.ShouldNotContain(AgentRetryCauses.MaxThinkingTokensEnvVar, customMessage: "the resume arm must never claim a thinking-disabled repair — its subject is the warm resume, measured in the arm's own configuration");
    }

    [Fact]
    public void The_repairable_predicate_reads_productions_own_marker_vocabulary()
    {
        // Anchored on production's classifier rather than a second copy of the marker: a widened/renamed marker there
        // must move the arms with it, exactly as it already moves RealModelRunClassifier.
        RealModelFormatFaultRepair.IsRepairable(new AgentExecutionInfraException(FormatFault)).ShouldBeTrue();
        AgentRetryCauses.Classify(FormatFault).ShouldBe(AgentRetryCauses.GatewayFormatFault);

        // Wrapped one level down — the arms' faults reach the helper through whatever the drive lambda was doing.
        RealModelFormatFaultRepair.IsRepairable(new AggregateException(new AgentExecutionInfraException(FormatFault))).ShouldBeTrue();

        // On today's skip path but NOT a format fault → no repair.
        RealModelFormatFaultRepair.IsRepairable(new LlmApiException("429", 429, LlmErrorCategory.RateLimited, "")).ShouldBeFalse();
        RealModelFormatFaultRepair.IsRepairable(new SocketException((int)SocketError.ConnectionReset)).ShouldBeFalse();
    }

    /// <summary>A WARM task — carries a restored conversation, so the mitigation's fresh-conversation half is observable, not vacuous.</summary>
    private static AgentTask WarmTask() =>
        new() { Goal = "correct payload.txt", Harness = "claude-code", ResumeFromSessionId = Guid.NewGuid().ToString(), RestoredTranscript = "{\"type\":\"user\"}" };
}
