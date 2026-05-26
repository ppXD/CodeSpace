using System.Text.Json;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;

namespace CodeSpace.Core.Services.Providers.Events;

/// <summary>
/// One subscription = one (provider kind, raw event name) pair plus the parsing that turns
/// that raw payload into a NormalizedEvent. The registry collects every subscription at
/// startup; the bind path asks "what raw events do you cover for GitHub?" and uses the
/// answer for webhook registration, while the receive path asks "who handles GitHub's push?"
/// and delegates to the matching subscription.
///
/// Adding support for a new event type (say GitHub check_run) is one new class — bind
/// auto-subscribes, receive auto-dispatches.
/// </summary>
public interface IProviderEventSubscription
{
    ProviderKind Kind { get; }

    /// <summary>Raw provider event name as sent in the webhook header (e.g. "push", "pull_request", "Push Hook").</summary>
    string RawEventName { get; }

    NormalizedEvent? Normalize(Guid repositoryId, JsonElement root, IReadOnlyDictionary<string, string> headers);
}
