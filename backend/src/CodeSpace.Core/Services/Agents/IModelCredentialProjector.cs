using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// OPTIONAL sibling capability of <see cref="IAgentHarness"/> (Rule 7 — a capability a harness opts into, not a
/// widening of the core interface): a harness that authenticates to a model provider via environment variables
/// implements this to declare WHICH providers it can drive and HOW a resolved credential maps onto the exact
/// env var(s) its CLI reads. The executor resolves the credential, then — only if the chosen harness implements
/// this — projects it into the sandbox environment. A harness that needs no model key (a future local-only one,
/// or one authenticating via a config file) simply doesn't implement it.
///
/// <para>The mapping is (harness × provider) → a SET of env vars (key + optional base URL + maybe a gateway
/// auth-token), not one fixed pair — so a new harness or provider plugs in by implementing this, never by
/// editing the executor or the runner.</para>
/// </summary>
public interface IModelCredentialProjector
{
    /// <summary>The model-provider tags (<c>ILLMProviderModule.Provider</c>: "Anthropic", "OpenAI", …) this harness can authenticate to.</summary>
    IReadOnlyList<string> SupportedProviders { get; }

    /// <summary>
    /// Map a resolved credential onto the exact environment variables this harness's CLI reads (api key / gateway
    /// token / base URL). Returns only the vars that apply (a keyless provider yields just a base URL). Throws
    /// <see cref="ArgumentException"/> for a provider not in <see cref="SupportedProviders"/>.
    /// </summary>
    IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential);
}
