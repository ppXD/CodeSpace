using System.Text.Json;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Events;

[Trait("Category", "Unit")]
public class ProviderEventSubscriptionRegistryTests
{
    [Fact]
    public void Find_returns_subscription_on_kind_and_raw_event_match()
    {
        var registry = new ProviderEventSubscriptionRegistry(new IProviderEventSubscription[]
        {
            new StubSubscription(ProviderKind.GitHub, "push"),
            new StubSubscription(ProviderKind.GitLab, "Push Hook")
        });

        registry.Find(ProviderKind.GitHub, "push").ShouldNotBeNull();
        registry.Find(ProviderKind.GitLab, "Push Hook").ShouldNotBeNull();
    }

    [Fact]
    public void Find_returns_null_when_no_subscription_matches()
    {
        var registry = new ProviderEventSubscriptionRegistry(new IProviderEventSubscription[]
        {
            new StubSubscription(ProviderKind.GitHub, "push")
        });

        registry.Find(ProviderKind.GitHub, "check_run").ShouldBeNull();
        registry.Find(ProviderKind.GitLab, "push").ShouldBeNull();
    }

    [Fact]
    public void GetSubscribedRawEvents_returns_all_raw_events_for_kind()
    {
        var registry = new ProviderEventSubscriptionRegistry(new IProviderEventSubscription[]
        {
            new StubSubscription(ProviderKind.GitHub, "push"),
            new StubSubscription(ProviderKind.GitHub, "pull_request"),
            new StubSubscription(ProviderKind.GitHub, "issues"),
            new StubSubscription(ProviderKind.GitLab, "Push Hook")
        });

        var events = registry.GetSubscribedRawEvents(ProviderKind.GitHub);

        events.Count.ShouldBe(3);
        events.ShouldContain("push");
        events.ShouldContain("pull_request");
        events.ShouldContain("issues");
    }

    [Fact]
    public void GetSubscribedRawEvents_returns_empty_for_kind_with_no_subscriptions()
    {
        var registry = new ProviderEventSubscriptionRegistry(new IProviderEventSubscription[]
        {
            new StubSubscription(ProviderKind.GitHub, "push")
        });

        registry.GetSubscribedRawEvents(ProviderKind.GitLab).ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_throws_when_two_subscriptions_claim_same_kind_and_raw_event()
    {
        var act = () => new ProviderEventSubscriptionRegistry(new IProviderEventSubscription[]
        {
            new StubSubscription(ProviderKind.GitHub, "push"),
            new StubSubscription(ProviderKind.GitHub, "push")
        });

        act.ShouldThrow<ArgumentException>();
    }

    private sealed class StubSubscription : IProviderEventSubscription
    {
        public StubSubscription(ProviderKind kind, string rawEventName)
        {
            Kind = kind;
            RawEventName = rawEventName;
        }

        public ProviderKind Kind { get; }
        public string RawEventName { get; }

        public NormalizedEvent? Normalize(Guid repositoryId, JsonElement root, IReadOnlyDictionary<string, string> headers) => null;
    }
}
