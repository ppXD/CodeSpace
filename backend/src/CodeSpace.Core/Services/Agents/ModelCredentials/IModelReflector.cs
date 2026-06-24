using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// Discovers the models a credential can run by REFLECTING the provider's model endpoint — the auto-suggest half of
/// the pick-or-type surface, so an operator picks instead of looking up vendor docs. A SIBLING capability (Rule 7),
/// NOT a method on <see cref="IModelCredentialService"/>: only SOME credentials are reflectable (an OpenAI-compatible /
/// LiteLLM gateway exposing <c>/v1/models</c>), so reflection is its own narrow interface the refresh asks — never a
/// widened god-interface. Implementations live in a <c>Reflectors/</c> variant sub-folder (Rule 18.3).
/// </summary>
public interface IModelReflector
{
    /// <summary>Whether this reflector can discover models for the credential (e.g. its base URL is a reachable OpenAI-compatible gateway). False → the credential is manual-only and the refresh no-ops.</summary>
    bool CanReflect(ResolvedModelCredential credential);

    /// <summary>List the models the credential's endpoint advertises (just their ids — the pool is capability-generic). Only called when <see cref="CanReflect"/> is true.</summary>
    Task<IReadOnlyList<ReflectedModel>> ListModelsAsync(ResolvedModelCredential credential, CancellationToken cancellationToken);
}
