using System.Text.Json;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;

namespace CodeSpace.IntegrationTests.Binding;

/// <summary>
/// Test-only stub so the integration provider (Kind=Git) registers SOME raw event name —
/// the bind path's outbox payload would otherwise carry an empty SubscribedEvents list and
/// trip RepositoryWebhook.SubscribedEvents.ShouldNotBeEmpty() in the binding flow tests.
/// The Normalize body is never exercised in integration tests (no real webhook payloads are
/// posted for this fake kind).
/// </summary>
public sealed class TestEventSubscription : IProviderEventSubscription
{
    public ProviderKind Kind => ProviderKind.Git;
    public string RawEventName => "test-event";

    public NormalizedEvent? Normalize(Guid repositoryId, JsonElement root, IReadOnlyDictionary<string, string> headers) => null;
}
