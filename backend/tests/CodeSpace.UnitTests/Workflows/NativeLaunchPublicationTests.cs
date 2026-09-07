using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

public sealed partial class NativeLaunchRegistryTests
{
    [Fact]
    public async Task Create_only_native_metadata_is_not_visible_before_serialization_and_flush_complete()
    {
        await using var fixture = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var publication = Task.Run(() => NativeLaunchFiles.TryCreate(fixture.Directory, NativeLaunchProtocol.StopFile, new PausedStopPublication(entered, release)));
        try
        {
            entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("the real serializer must be paused after opening its output stream");
            File.Exists(Path.Combine(fixture.Directory, NativeLaunchProtocol.StopFile)).ShouldBeFalse("readers must not see an empty or partially written final stop/commitment file");
        }
        finally { release.Set(); (await publication).ShouldBeTrue(); }
        NativeLaunchFiles.Read<NativeLaunchStop>(fixture.Directory, NativeLaunchProtocol.StopFile).Reason.ShouldBe("deadline");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"reason\":\"deadline\"}")]
    public async Task An_incomplete_stop_record_does_not_fail_observation_or_invent_a_deadline(string incomplete)
    {
        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        var stopPath = Path.Combine(fixture.Directory, NativeLaunchProtocol.StopFile);
        await File.WriteAllTextAsync(stopPath, incomplete);
        try
        {
            using var observing = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Should.ThrowAsync<OperationCanceledException>(() => fixture.Runner.AttachAsync(handle, (_, _) => Task.CompletedTask, observing.Token));
            NativeProcess.IsAlive(fixture.ReadReceipt().Execution!).ShouldBeTrue("an unreadable stop record must not be interpreted as a deadline cancellation");
        }
        finally { File.Delete(stopPath); }
    }

    private sealed class PausedStopPublication(ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        public string Reason
        {
            get
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The publication test did not release serialization.");
                return "deadline";
            }
        }
        public DateTimeOffset At => DateTimeOffset.UtcNow;
    }
}
