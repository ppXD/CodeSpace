using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class ExternalTriggerRunAdmissionTests
{
    [Fact]
    public void Only_committed_reservations_consume_the_window()
    {
        using var admission = new ExternalTriggerRunAdmission();
        var id = Guid.NewGuid();
        for (var i = 0; i < 100; i++)
        {
            using var released = admission.Reserve(id);
            released.ShouldNotBeNull();
        }
        for (var i = 0; i < 60; i++)
        {
            using var committed = admission.Reserve(id);
            committed.ShouldNotBeNull();
            committed.Commit();
        }
        admission.Reserve(id).ShouldBeNull();
        admission.Reserve(Guid.NewGuid()).ShouldNotBeNull();
    }

    [Fact]
    public void A_late_release_cannot_refund_a_different_window()
    {
        var clock = new Clock();
        using var admission = new ExternalTriggerRunAdmission(clock);
        var id = Guid.NewGuid();
        using var old = admission.Reserve(id);
        clock.Now += TimeSpan.FromMinutes(1);
        for (var i = 0; i < 60; i++) admission.Reserve(id)!.Commit();
        old!.Release();
        admission.Reserve(id).ShouldBeNull();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
