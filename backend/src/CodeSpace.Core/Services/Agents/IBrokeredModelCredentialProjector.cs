using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// OPTIONAL sibling of <see cref="IModelCredentialProjector"/> (Rule 7 — a capability a harness opts into, not a
/// widening of the one beside it): a harness whose CLI can be pointed at a DIFFERENT endpoint implements this to map
/// a <see cref="BrokeredModelCredential"/> onto the env vars that send its model traffic through the run's broker.
///
/// <para><b>Why it is not a method on <see cref="IModelCredentialProjector"/>.</b> That interface's answer is "which
/// env var carries a key". This one's is "can this CLI be re-pointed at all, and how" — and the two harnesses that
/// exist already answer it by different mechanisms: Claude Code reads <c>ANTHROPIC_BASE_URL</c> from the environment,
/// while Codex 0.142.x IGNORES <c>OPENAI_BASE_URL</c> as an env var and has to be re-pointed through a <c>-c</c>
/// model-provider override its own <c>BuildInvocation</c> re-injects. A harness with no such override cannot be
/// brokered at all; it simply does not implement this, and the executor's fail-closed / disclose decision applies
/// exactly as it does to a deployment with no broker.</para>
///
/// <para>The projection carries NO provider tag on purpose. Whatever the upstream is, from the CLI's point of view
/// the broker IS a gateway — a base URL plus a bearer — so the shape is one, not one per provider. Which upstream
/// header that bearer is exchanged for is the broker's business, decided from the credential it holds.</para>
/// </summary>
public interface IBrokeredModelCredentialProjector
{
    /// <summary>
    /// Map a brokered credential onto the exact environment variables this harness's CLI reads: the broker's base URL
    /// plus the run token in the var that CLI authenticates with. It MUST NOT emit the upstream provider key — it is
    /// never handed one — and a caller may assert exactly that.
    /// </summary>
    IReadOnlyDictionary<string, string> ProjectBrokered(BrokeredModelCredential brokered);
}
