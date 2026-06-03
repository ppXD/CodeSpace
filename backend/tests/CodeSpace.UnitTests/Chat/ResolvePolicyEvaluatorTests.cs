using System.Text.Json;
using CodeSpace.Core.Services.Chat.Interactions;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using Shouldly;

namespace CodeSpace.UnitTests.Chat;

/// <summary>
/// The pure resolve decision: given an interaction's component (which buttons are terminal / veto), its
/// policy, and the accumulated responses, should the wait resolve now? Veto is policy-independent;
/// First / Quorum are pluggable strategies. Votes are deduped per responder (last-wins / changeable).
/// </summary>
[Trait("Category", "Unit")]
public class ResolvePolicyEvaluatorTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static readonly IResolvePolicyEvaluator Evaluator =
        new ResolvePolicyEvaluator(new IResolvePolicyStrategy[] { new FirstResolvePolicyStrategy(), new QuorumResolvePolicyStrategy() });

    // approve / approve2 = terminal non-veto; reject = terminal veto; ack = non-terminal (discussion only).
    private static readonly ActionButtonsComponent Buttons = new()
    {
        Buttons = new List<InteractionButton>
        {
            new() { Key = "approve", Label = "Approve" },
            new() { Key = "approve2", Label = "Approve (alt)" },
            new() { Key = "reject", Label = "Reject", Vetoes = true },
            new() { Key = "ack", Label = "Ack", ResolvesWait = false },
        },
    };

    private static ResolvePolicy First => new() { Kind = ResolvePolicyKind.First };
    private static ResolvePolicy Quorum(int n) => new() { Kind = ResolvePolicyKind.Quorum, Count = n };

    private static MessageInteraction With(ResolvePolicy policy, params (Guid By, string Key)[] actions) => new()
    {
        Component = Buttons,
        Target = new WorkflowWaitTarget { Token = "t" },
        Resolve = policy,
        State = InteractionState.Open,
        Responses = actions.Select(a => new InteractionResponse { ByUserId = a.By, Kind = InteractionResponseKind.Action, Key = a.Key, AtUtc = DateTimeOffset.UnixEpoch }).ToList(),
    };

    [Fact]
    public void First_resolves_on_a_single_terminal_vote_and_not_before() =>
        Evaluator.ShouldResolve(With(First, (A, "approve"))).ShouldBeTrue();

    [Fact]
    public void First_with_no_terminal_vote_does_not_resolve() =>
        Evaluator.ShouldResolve(With(First)).ShouldBeFalse();

    [Fact]
    public void Quorum_needs_the_required_number_of_distinct_responders()
    {
        Evaluator.ShouldResolve(With(Quorum(2), (A, "approve"))).ShouldBeFalse("one of two");
        Evaluator.ShouldResolve(With(Quorum(2), (A, "approve"), (B, "approve"))).ShouldBeTrue("two distinct approvers reach it");
    }

    [Fact]
    public void Quorum_counts_a_responder_once_however_many_times_they_click() =>
        Evaluator.ShouldResolve(With(Quorum(2), (A, "approve"), (A, "approve"))).ShouldBeFalse("same person twice is one vote");

    [Fact]
    public void Quorum_dedups_to_each_responders_latest_key_so_a_changed_vote_moves()
    {
        // A switches approve → approve2: now `approve` has only B (1) and `approve2` has only A (1) — neither hits 2.
        Evaluator.ShouldResolve(With(Quorum(2), (A, "approve"), (B, "approve"), (A, "approve2"))).ShouldBeFalse();
    }

    [Fact]
    public void A_veto_short_circuits_any_policy()
    {
        Evaluator.ShouldResolve(With(Quorum(2), (A, "reject"))).ShouldBeTrue("one veto resolves even under a 2-quorum");
        Evaluator.ShouldResolve(With(Quorum(2), (A, "approve"), (B, "reject"))).ShouldBeTrue("a veto wins regardless of pending approvals");
    }

    [Fact]
    public void A_non_terminal_button_is_discussion_and_never_resolves() =>
        Evaluator.ShouldResolve(With(First, (A, "ack"), (B, "ack"))).ShouldBeFalse("ResolvesWait=false buttons don't decide");

    [Fact]
    public void A_quorum_of_one_behaves_like_first() =>
        Evaluator.ShouldResolve(With(Quorum(1), (A, "approve"))).ShouldBeTrue();

    [Fact]
    public void Comments_are_ignored_by_the_resolver()
    {
        var interaction = new MessageInteraction
        {
            Component = Buttons,
            Target = new WorkflowWaitTarget { Token = "t" },
            Resolve = First,
            State = InteractionState.Open,
            Responses = new List<InteractionResponse> { new() { ByUserId = A, Kind = InteractionResponseKind.Comment, Comment = "hi", AtUtc = DateTimeOffset.UnixEpoch } },
        };

        Evaluator.ShouldResolve(interaction).ShouldBeFalse("a comment is discussion, not a terminal vote");
    }

    [Fact]
    public void A_form_submit_is_a_terminal_vote()
    {
        var interaction = new MessageInteraction
        {
            Component = new FormComponent { Fields = JsonDocument.Parse("{}").RootElement },
            Target = new WorkflowWaitTarget { Token = "t" },
            Resolve = First,
            State = InteractionState.Open,
            Responses = new List<InteractionResponse> { new() { ByUserId = A, Kind = InteractionResponseKind.Action, Key = MessageInteractionPolicy.FormSubmitKey, AtUtc = DateTimeOffset.UnixEpoch } },
        };

        Evaluator.ShouldResolve(interaction).ShouldBeTrue();
    }
}
