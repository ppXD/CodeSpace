using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Core.Services.Workflows.RunSources.Admission;
using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Webhooks;

/// <summary>
/// The keys the unique index and the claim table decide on. Each one is what makes a flood collapse: a refusal row per
/// (hook, reason) per day, one accepted body per hook, one run per (activation, pull request, head) per debounce window.
/// </summary>
[Trait("Category", "Unit")]
public class WebhookIngestionKeysTests
{
    private static readonly Guid Hook = Guid.NewGuid();

    [Fact]
    public void A_refusal_collapses_per_hook_and_reason_within_a_day()
    {
        var morning = new DateTimeOffset(2026, 10, 8, 1, 0, 0, TimeSpan.Zero);
        var evening = morning.AddHours(20);

        WebhookIngestionService.BuildRefusalWindowKey(Hook, WorkflowRunRequestRejectionReasons.SignatureInvalid, evening).ShouldBe(WebhookIngestionService.BuildRefusalWindowKey(Hook, WorkflowRunRequestRejectionReasons.SignatureInvalid, morning));
        WebhookIngestionService.BuildRefusalWindowKey(Hook, WorkflowRunRequestRejectionReasons.SignatureInvalid, morning.AddDays(1)).ShouldNotBe(WebhookIngestionService.BuildRefusalWindowKey(Hook, WorkflowRunRequestRejectionReasons.SignatureInvalid, morning));
        WebhookIngestionService.BuildRefusalWindowKey(Hook, WorkflowRunRequestRejectionReasons.WebhookInactive, morning).ShouldNotBe(WebhookIngestionService.BuildRefusalWindowKey(Hook, WorkflowRunRequestRejectionReasons.SignatureInvalid, morning));
        WebhookIngestionService.BuildRefusalWindowKey(Guid.NewGuid(), WorkflowRunRequestRejectionReasons.SignatureInvalid, morning).ShouldNotBe(WebhookIngestionService.BuildRefusalWindowKey(Hook, WorkflowRunRequestRejectionReasons.SignatureInvalid, morning));
    }

    [Fact]
    public void A_body_is_claimed_per_hook_by_its_hash()
    {
        WebhookIngestionService.BuildBodyClaimKey(Hook, """{"a":1}""").ShouldBe(WebhookIngestionService.BuildBodyClaimKey(Hook, """{"a":1}"""));
        WebhookIngestionService.BuildBodyClaimKey(Hook, """{"a":1}""").ShouldNotBe(WebhookIngestionService.BuildBodyClaimKey(Hook, """{"a":2}"""));
        WebhookIngestionService.BuildBodyClaimKey(Guid.NewGuid(), """{"a":1}""").ShouldNotBe(WebhookIngestionService.BuildBodyClaimKey(Hook, """{"a":1}"""));
        WebhookIngestionService.BuildBodyClaimKey(Hook, new string('x', 1_000_000)).Length.ShouldBeLessThan(120, "the key is a digest, never the body");
    }

    [Fact]
    public void A_pull_request_is_debounced_per_activation_number_and_head()
    {
        // The head is in the key: a reopen loop re-sends a head that already ran, while a push moves the head and its
        // commits must get their own run however soon after the last.
        var activation = Guid.NewGuid();
        var repository = Guid.NewGuid();

        PullRequestTriggerAdmission.BuildDebounceKey(activation, repository, 7, "sha-1").ShouldBe(PullRequestTriggerAdmission.BuildDebounceKey(activation, repository, 7, "sha-1"));
        PullRequestTriggerAdmission.BuildDebounceKey(activation, repository, 7, "sha-2").ShouldNotBe(PullRequestTriggerAdmission.BuildDebounceKey(activation, repository, 7, "sha-1"));
        PullRequestTriggerAdmission.BuildDebounceKey(activation, repository, 8, "sha-1").ShouldNotBe(PullRequestTriggerAdmission.BuildDebounceKey(activation, repository, 7, "sha-1"));
        PullRequestTriggerAdmission.BuildDebounceKey(Guid.NewGuid(), repository, 7, "sha-1").ShouldNotBe(PullRequestTriggerAdmission.BuildDebounceKey(activation, repository, 7, "sha-1"));
        PullRequestTriggerAdmission.BuildDebounceKey(activation, Guid.NewGuid(), 7, "sha-1").ShouldNotBe(PullRequestTriggerAdmission.BuildDebounceKey(activation, repository, 7, "sha-1"));
    }

    [Fact]
    public void The_windows_are_pinned()
    {
        // Committed values, changed by a PR that says why. The debounce is short so a reopen loop collapses while a reopen
        // a minute later still runs; the replay window outlives GitHub's own redelivery window.
        PullRequestTriggerAdmission.DebounceWindow.ShouldBe(TimeSpan.FromSeconds(60));
        WebhookIngestionService.ReplayWindow.ShouldBe(TimeSpan.FromDays(7));
        WebhookIngestionService.RefusalAuditWindow.ShouldBe(TimeSpan.FromDays(1));
    }
}
