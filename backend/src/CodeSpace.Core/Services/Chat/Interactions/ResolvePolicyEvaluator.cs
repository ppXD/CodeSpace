using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Chat.Interactions;

namespace CodeSpace.Core.Services.Chat.Interactions;

/// <summary>
/// Decides whether an interaction's accumulated responses now satisfy its <see cref="ResolvePolicy"/> — so
/// the respond path knows whether a terminal click should RESOLVE the wait or just record a vote and leave
/// the card Open. Veto is handled here (policy-independent short-circuit); the per-kind decision is
/// delegated to the matching <see cref="IResolvePolicyStrategy"/>.
/// </summary>
public interface IResolvePolicyEvaluator
{
    bool ShouldResolve(MessageInteraction interaction);
}

public sealed class ResolvePolicyEvaluator : IResolvePolicyEvaluator, ISingletonDependency
{
    private readonly IReadOnlyDictionary<ResolvePolicyKind, IResolvePolicyStrategy> _byKind;

    public ResolvePolicyEvaluator(IEnumerable<IResolvePolicyStrategy> strategies) => _byKind = strategies.ToDictionary(s => s.Kind);

    public bool ShouldResolve(MessageInteraction interaction)
    {
        var votes = CurrentTerminalVotes(interaction);

        // A veto resolves immediately regardless of the policy (e.g. one "request changes" blocks a quorum).
        if (votes.Any(v => v.Vetoes)) return true;

        var nonVeto = votes.Where(v => !v.Vetoes).ToList();

        return _byKind.TryGetValue(interaction.Resolve.Kind, out var strategy) && strategy.IsSatisfied(nonVeto, interaction.Resolve);
    }

    /// <summary>
    /// Each responder's CURRENT terminal vote: their last terminal action in the log (last-wins, so a
    /// changed vote counts once), tagged with whether that action vetoes. A form's submit is a terminal,
    /// non-veto vote; a non-terminal button (ResolvesWait=false) and comments are ignored.
    /// </summary>
    private static IReadOnlyList<TerminalVote> CurrentTerminalVotes(MessageInteraction interaction)
    {
        var byResponder = new Dictionary<Guid, TerminalVote>();

        foreach (var r in interaction.Responses)
        {
            if (r.Kind != InteractionResponseKind.Action || r.Key is null) continue;
            if (!IsTerminal(interaction.Component, r.Key, out var vetoes)) continue;

            byResponder[r.ByUserId] = new TerminalVote(r.ByUserId, r.Key, vetoes);
        }

        return byResponder.Values.ToList();
    }

    private static bool IsTerminal(InteractionComponent component, string key, out bool vetoes)
    {
        vetoes = false;

        switch (component)
        {
            case ActionButtonsComponent buttons:
                var button = buttons.Buttons.FirstOrDefault(b => b.Key == key);
                if (button is null || !button.ResolvesWait) return false;
                vetoes = button.Vetoes;
                return true;

            case FormComponent:
                return key == MessageInteractionPolicy.FormSubmitKey;

            default:
                return false;
        }
    }
}
