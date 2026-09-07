using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// Pins <see cref="RealModelSpecPreviewE2ETests.ThrowIfInfraOutcome"/> — which of the compiler's swallowed model-call
/// outcomes the live arms must NOT score as a model verdict.
///
/// <para><b>The bug it protects against.</b> <c>TaskSpecCompiler.CallAsync</c> is built to DEGRADE: its own 45s cap,
/// a transport fault, and an unresolvable pool all return the same null reply a live model gives when it declines, and
/// the fault survives only as an <c>Outcome</c> string on <c>ModelCalls</c>. Run 34085042806 red the abstention arm 2/2
/// on "the compiler returned NO suggestion at all" while both of its proposal calls carried
/// <c>outcome=timed-out, elapsedMs=44999</c>. The gate would have absorbed a <see cref="TimeoutException"/> as
/// non-gating infra all along; nothing ever threw one.</para>
///
/// <para>The exception TYPE is the load-bearing half: <c>RealModelGate.IsGatewayInfraFailure</c> routes on
/// <c>TimeoutException =&gt; true</c> (pinned on its own side by <c>RealModelGateTests</c>), so a helper that threw
/// anything else would red the blessed wire exactly as before. Decided with no model, no database and no secrets, so it
/// runs on the ordinary <c>Category=E2E&amp;Surface=Engine</c> gate — every PR — rather than only on the lane it
/// protects, the same placement <c>SpecPreviewArgvVocabularyTests</c> and <c>RealModelLaneBoundsTests</c> use.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class SpecPreviewInfraOutcomeRoutingTests
{
    [Theory]
    // The gateway broke: the arm measured nothing, so it must not answer.
    [InlineData("timed-out")]
    [InlineData("failed")]
    [InlineData("unavailable")]
    public void A_broken_model_call_is_routed_as_gateway_infra_rather_than_scored(string outcome)
    {
        var thrown = Should.Throw<TimeoutException>(() => RealModelSpecPreviewE2ETests.ThrowIfInfraOutcome(ResultWith(new TaskSpecModelCall { Phase = "proposal", Outcome = outcome, ElapsedMilliseconds = 44999 })),
            customMessage: "RealModelGate.IsGatewayInfraFailure routes on TimeoutException; any other type reds the blessed wire for the owner's gateway being slow");

        thrown.Message.ShouldContain(outcome, Case.Sensitive, "the non-gating skip line is the only record of WHY the lane measured nothing");
        thrown.Message.ShouldContain("44999", customMessage: "the elapsed time is what separates the 45s cap from a fast transport drop");
        thrown.Message.ShouldContain("proposal", Case.Sensitive, "the compiler makes two calls per compile and only one of them may have broken");
    }

    [Theory]
    // The model ANSWERED. Whatever the arm makes of the reply is a real verdict and must reach the gate.
    [InlineData("succeeded")]
    // Deliberately NOT infra: a reply that would not bind is the model failing to produce conformant output.
    [InlineData("malformed")]
    [InlineData("cancelled")]
    public void An_answered_model_call_is_left_to_the_arms_own_verdict(string outcome)
    {
        RealModelSpecPreviewE2ETests.ThrowIfInfraOutcome(ResultWith(new TaskSpecModelCall { Phase = "proposal", Outcome = outcome, ElapsedMilliseconds = 4500 }));
    }

    [Fact]
    public void One_broken_call_among_several_is_enough_to_stop_the_arm()
    {
        // The compile makes an independent proposal and source-review call. The arms read fields the FIRST one
        // produces, so a healthy second call cannot repair a verdict the first never supplied.
        Should.Throw<TimeoutException>(() => RealModelSpecPreviewE2ETests.ThrowIfInfraOutcome(new CompileTaskSpecResult
        {
            ModelCalls = new[]
            {
                new TaskSpecModelCall { Phase = "proposal", Outcome = "succeeded", ElapsedMilliseconds = 4500 },
                new TaskSpecModelCall { Phase = "review", Outcome = "timed-out", ElapsedMilliseconds = 44999 },
            },
        })).Message.ShouldContain("review", Case.Sensitive);
    }

    [Fact]
    public void A_reply_that_recorded_no_calls_at_all_is_still_the_arms_own_verdict()
    {
        // ModelCalls is nullable on the wire (a legacy reply omits it). Throwing here would convert every arm's
        // "the compiler returned NOTHING" verdict — the one that catches a fake resolving instead of the live model —
        // into a silent skip, which is the failure this whole file was rewritten to stop.
        RealModelSpecPreviewE2ETests.ThrowIfInfraOutcome(new CompileTaskSpecResult());
        RealModelSpecPreviewE2ETests.ThrowIfInfraOutcome(new CompileTaskSpecResult { ModelCalls = Array.Empty<TaskSpecModelCall>() });
    }

    private static CompileTaskSpecResult ResultWith(TaskSpecModelCall call) => new() { ModelCalls = new[] { call } };
}
