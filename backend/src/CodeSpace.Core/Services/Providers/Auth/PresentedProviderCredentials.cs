using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Redaction;

namespace CodeSpace.Core.Services.Providers.Auth;

/// <summary>
/// The tokens a provider instance has been handed to present, so the SDK-error translation in
/// <see cref="Resilience.ExternalCallResilience"/> can strike an upstream's echo of them. A provider call resolves its
/// token through <see cref="ProviderAuthResolver"/> and then runs under the resilience layer with the SAME
/// <see cref="ProviderInstance"/> object (<c>context.Instance</c>); the resilience layer is a singleton that never sees
/// the credential, so the token travels beside that object instead.
///
/// <para>Keyed by the entity OBJECT, held weakly: the needles live exactly as long as the scope that loaded the
/// instance, never in a process-wide list, and a second scope's instance object starts empty. A scope that resolves two
/// credentials of one instance strikes both — over-striking an error line is the safe side.</para>
/// </summary>
internal static class PresentedProviderCredentials
{
    private static readonly ConditionalWeakTable<ProviderInstance, ConcurrentDictionary<string, byte>> Presented = new();

    /// <summary>Remember that <paramref name="instance"/> was handed <paramref name="token"/> to present (ignored when too short to be a needle).</summary>
    public static void Remember(ProviderInstance instance, string? token)
    {
        foreach (var needle in CredentialNeedles.Usable([token])) Presented.GetOrCreateValue(instance).TryAdd(needle, 0);
    }

    /// <summary>A redactor over every token <paramref name="instance"/> was handed; <see cref="SecretRedactor.None"/> when it was handed none.</summary>
    public static SecretRedactor RedactorFor(ProviderInstance instance) => Presented.TryGetValue(instance, out var needles) ? CredentialNeedles.Redactor(needles.Keys) : SecretRedactor.None;
}
