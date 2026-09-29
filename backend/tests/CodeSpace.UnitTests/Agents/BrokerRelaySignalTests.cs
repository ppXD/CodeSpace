using System.Collections.Concurrent;
using CodeSpace.Mcp.Relay;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins how the relay passes a terminating signal on to its CLI when the signal lands before the CLI has a pid. The
/// relay's handlers are in place before the CLI starts, so no signal meets the relay's default action (which would kill
/// it and leave the CLI running without its broker); one that arrives first is held and delivered the moment the pid is
/// known — exactly once, however the two race.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BrokerRelaySignalTests
{
    [Fact]
    public void A_signal_that_arrives_before_the_cli_starts_is_delivered_to_it_when_it_does()
    {
        var kills = new List<(int Pid, int Signal)>();
        var relay = new BrokerRelay.SignalRelay((pid, signal) => kills.Add((pid, signal)));

        relay.Received(15);
        kills.ShouldBeEmpty("there is no CLI to deliver it to yet");

        relay.Started(4242);
        kills.ToArray().ShouldBe(new (int Pid, int Signal)[] { (4242, 15) }, "the CLI gets the signal the relay was sent before it existed, as it would have without the relay");
    }

    [Fact]
    public void A_signal_after_the_cli_started_goes_straight_to_it()
    {
        var kills = new List<(int Pid, int Signal)>();
        var relay = new BrokerRelay.SignalRelay((pid, signal) => kills.Add((pid, signal)));

        relay.Started(4242);
        relay.Received(1);
        relay.Received(2);

        kills.ToArray().ShouldBe(new (int Pid, int Signal)[] { (4242, 1), (4242, 2) });
    }

    [Fact]
    public void Of_several_signals_before_the_cli_starts_it_gets_the_last()
    {
        var kills = new List<(int Pid, int Signal)>();
        var relay = new BrokerRelay.SignalRelay((pid, signal) => kills.Add((pid, signal)));

        relay.Received(1);
        relay.Received(15);
        relay.Started(4242);

        kills.ToArray().ShouldBe(new (int Pid, int Signal)[] { (4242, 15) }, "every one of them asks the CLI to stop; delivering the latest once is enough");
    }

    [Fact]
    public void A_cli_that_starts_between_the_relay_reading_no_pid_and_holding_the_signal_still_gets_it()
    {
        // The interleaving only the re-check after the hold can save: the start runs to its end while the signal is on
        // its way to being held, so the start finds nothing held, and the signal would sit held forever.
        var kills = new List<(int Pid, int Signal)>();
        BrokerRelay.SignalRelay relay = null!;
        relay = new BrokerRelay.SignalRelay((pid, signal) => kills.Add((pid, signal))) { BeforeHoldForTest = () => relay.Started(4242) };

        relay.Received(15);

        kills.ToArray().ShouldBe(new (int Pid, int Signal)[] { (4242, 15) });
    }

    [Fact]
    public void A_cli_that_starts_between_the_hold_and_the_re_check_gets_the_signal_once()
    {
        // The start takes the held signal; the re-check then finds it gone and must not send it a second time.
        var kills = new List<(int Pid, int Signal)>();
        BrokerRelay.SignalRelay relay = null!;
        relay = new BrokerRelay.SignalRelay((pid, signal) => kills.Add((pid, signal))) { AfterHoldForTest = () => relay.Started(4242) };

        relay.Received(15);

        kills.ToArray().ShouldBe(new (int Pid, int Signal)[] { (4242, 15) });
    }

    [Fact]
    public async Task A_signal_racing_the_cli_start_is_delivered_exactly_once()
    {
        for (var round = 0; round < 5_000; round++)
        {
            var kills = new ConcurrentQueue<(int Pid, int Signal)>();
            var relay = new BrokerRelay.SignalRelay((pid, signal) => kills.Enqueue((pid, signal)));
            using var go = new Barrier(2);

            var signal = Task.Run(() => { go.SignalAndWait(); relay.Received(1); });
            var start = Task.Run(() => { go.SignalAndWait(); relay.Started(7); });
            await Task.WhenAll(signal, start);

            kills.ToArray().ShouldBe(new (int Pid, int Signal)[] { (7, 1) }, $"round {round}: the signal is neither lost nor sent twice");
        }
    }
}
