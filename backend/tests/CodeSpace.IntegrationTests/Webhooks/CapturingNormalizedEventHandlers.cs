using CodeSpace.Messages.Events.Issue;
using CodeSpace.Messages.Events.PullRequest;
using CodeSpace.Messages.Events.Push;
using MediatR;

namespace CodeSpace.IntegrationTests.Webhooks;

// One handler per concrete NormalizedEvent type — MediatR dispatches by runtime type.

public sealed class CapturingPullRequestOpenedHandler : INotificationHandler<PullRequestOpenedEvent>
{
    private readonly CapturedNormalizedEvents _collector;
    public CapturingPullRequestOpenedHandler(CapturedNormalizedEvents collector) { _collector = collector; }
    public Task Handle(PullRequestOpenedEvent notification, CancellationToken cancellationToken) { _collector.Add(notification); return Task.CompletedTask; }
}

public sealed class CapturingPullRequestSynchronizedHandler : INotificationHandler<PullRequestSynchronizedEvent>
{
    private readonly CapturedNormalizedEvents _collector;
    public CapturingPullRequestSynchronizedHandler(CapturedNormalizedEvents collector) { _collector = collector; }
    public Task Handle(PullRequestSynchronizedEvent notification, CancellationToken cancellationToken) { _collector.Add(notification); return Task.CompletedTask; }
}

public sealed class CapturingPullRequestMergedHandler : INotificationHandler<PullRequestMergedEvent>
{
    private readonly CapturedNormalizedEvents _collector;
    public CapturingPullRequestMergedHandler(CapturedNormalizedEvents collector) { _collector = collector; }
    public Task Handle(PullRequestMergedEvent notification, CancellationToken cancellationToken) { _collector.Add(notification); return Task.CompletedTask; }
}

public sealed class CapturingPullRequestClosedHandler : INotificationHandler<PullRequestClosedEvent>
{
    private readonly CapturedNormalizedEvents _collector;
    public CapturingPullRequestClosedHandler(CapturedNormalizedEvents collector) { _collector = collector; }
    public Task Handle(PullRequestClosedEvent notification, CancellationToken cancellationToken) { _collector.Add(notification); return Task.CompletedTask; }
}

public sealed class CapturingPushReceivedHandler : INotificationHandler<PushReceivedEvent>
{
    private readonly CapturedNormalizedEvents _collector;
    public CapturingPushReceivedHandler(CapturedNormalizedEvents collector) { _collector = collector; }
    public Task Handle(PushReceivedEvent notification, CancellationToken cancellationToken) { _collector.Add(notification); return Task.CompletedTask; }
}

public sealed class CapturingIssueOpenedHandler : INotificationHandler<IssueOpenedEvent>
{
    private readonly CapturedNormalizedEvents _collector;
    public CapturingIssueOpenedHandler(CapturedNormalizedEvents collector) { _collector = collector; }
    public Task Handle(IssueOpenedEvent notification, CancellationToken cancellationToken) { _collector.Add(notification); return Task.CompletedTask; }
}

public sealed class CapturingIssueClosedHandler : INotificationHandler<IssueClosedEvent>
{
    private readonly CapturedNormalizedEvents _collector;
    public CapturingIssueClosedHandler(CapturedNormalizedEvents collector) { _collector = collector; }
    public Task Handle(IssueClosedEvent notification, CancellationToken cancellationToken) { _collector.Add(notification); return Task.CompletedTask; }
}
