using CodeSpace.Core.Services.OAuth;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// 🟢 High fidelity — the production <see cref="CredentialRefreshLock"/> and its real in-process semaphores; only the
/// cross-process tier is a recording fake, because ordering against it is the claim. A refresh and a re-authorization both
/// take this one lock — and the sweeps that move or retire what hangs on a dead credential take it without waiting
/// (<c>TryAcquireAsync</c>) — and what they share is process-wide state, so these pin the properties that make "the same lock"
/// true: the same credential excludes, another does not, and the two tiers are taken and given back in one fixed order.
/// </summary>
[Trait("Category", "Unit")]
public class CredentialRefreshLockTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void The_advisory_key_is_the_first_eight_bytes_of_the_credential_id()
    {
        // Another pod running the previous build derives the same key from the same id, so a change here would put a refresh
        // and a re-authorization on different advisory locks. Pinned against the bytes, not against the function.
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        CredentialRefreshLock.KeyOf(id).ShouldBe(BitConverter.ToInt64(id.ToByteArray(), 0));
        CredentialRefreshLock.KeyOf(id).ShouldBe(BitConverter.ToInt64(new byte[] { 0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66 }, 0));
    }

    [Fact]
    public async Task A_second_holder_of_the_same_credential_waits_until_the_first_lets_go()
    {
        var id = Guid.NewGuid();
        var crossProcess = new RecordingCrossProcessLock();
        var first = await CredentialRefreshLock.AcquireAsync(crossProcess, id, CancellationToken.None);

        var second = CredentialRefreshLock.AcquireAsync(crossProcess, id, CancellationToken.None);

        await Task.Delay(ShortWait);
        second.IsCompleted.ShouldBeFalse("a re-authorization and a refresh of one credential must never both be inside it");

        await first.DisposeAsync();

        await using var handle = await second.WaitAsync(Patience);
        crossProcess.Events.ShouldBe(new[] { "acquire", "release", "acquire" }, ignoreOrder: false, customMessage: "the second holder takes the advisory lock only after the first has given it back");
    }

    [Fact]
    public async Task Another_credential_is_not_held_up()
    {
        var crossProcess = new RecordingCrossProcessLock();
        await using var first = await CredentialRefreshLock.AcquireAsync(crossProcess, Guid.NewGuid(), CancellationToken.None);

        await using var other = await CredentialRefreshLock.AcquireAsync(crossProcess, Guid.NewGuid(), CancellationToken.None).WaitAsync(Patience);

        crossProcess.Events.ShouldBe(new[] { "acquire", "acquire" });
    }

    [Fact]
    public async Task The_advisory_lock_is_given_back_before_the_semaphore_so_a_waiter_never_overlaps_it()
    {
        var id = Guid.NewGuid();
        var crossProcess = new RecordingCrossProcessLock();
        var first = await CredentialRefreshLock.AcquireAsync(crossProcess, id, CancellationToken.None);
        var second = CredentialRefreshLock.AcquireAsync(crossProcess, id, CancellationToken.None);
        await Task.Delay(ShortWait);

        await first.DisposeAsync();
        await using var handle = await second.WaitAsync(Patience);

        crossProcess.Events.IndexOf("release").ShouldBeLessThan(crossProcess.Events.LastIndexOf("acquire"), "if the semaphore went first the waiter would acquire while the advisory lock is still held");
    }

    [Fact]
    public async Task A_wait_for_the_advisory_lock_that_fails_gives_the_semaphore_back()
    {
        var id = Guid.NewGuid();

        var failure = await Should.ThrowAsync<InvalidOperationException>(async () => await CredentialRefreshLock.AcquireAsync(new RecordingCrossProcessLock { FailAcquire = true }, id, CancellationToken.None));

        failure.Message.ShouldContain("advisory");
        await using var again = await CredentialRefreshLock.AcquireAsync(new RecordingCrossProcessLock(), id, CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_cancelled_wait_for_the_semaphore_never_reaches_the_advisory_lock()
    {
        var id = Guid.NewGuid();
        var crossProcess = new RecordingCrossProcessLock();
        await using var holder = await CredentialRefreshLock.AcquireAsync(crossProcess, id, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(ShortWait);

        await Should.ThrowAsync<OperationCanceledException>(async () => await CredentialRefreshLock.AcquireAsync(crossProcess, id, cancellation.Token));

        crossProcess.Events.ShouldBe(new[] { "acquire" }, ignoreOrder: false, customMessage: "the cancelled caller asked for the advisory lock without ever holding the semaphore");
    }

    [Fact]
    public async Task Disposing_a_handle_twice_releases_once()
    {
        var id = Guid.NewGuid();
        var crossProcess = new RecordingCrossProcessLock();
        var first = await CredentialRefreshLock.AcquireAsync(crossProcess, id, CancellationToken.None);

        await first.DisposeAsync();
        await first.DisposeAsync();

        // A second release of the semaphore would let two holders in; one acquire proves the count stayed at one.
        await using var second = await CredentialRefreshLock.AcquireAsync(crossProcess, id, CancellationToken.None).WaitAsync(Patience);
        var third = CredentialRefreshLock.AcquireAsync(crossProcess, id, CancellationToken.None);

        await Task.Delay(ShortWait);
        third.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_try_acquire_of_a_free_credential_takes_both_tiers_and_gives_them_back()
    {
        var id = Guid.NewGuid();
        var tryLock = new RecordingTryLock();

        var held = await CredentialRefreshLock.TryAcquireAsync(tryLock, id, CancellationToken.None);

        held.ShouldNotBeNull("nothing holds the credential");
        tryLock.Events.ShouldBe(new[] { "acquire" });

        await held.DisposeAsync();

        tryLock.Events.ShouldBe(new[] { "acquire", "release" });
        await using var again = await CredentialRefreshLock.AcquireAsync(new RecordingCrossProcessLock(), id, CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_try_acquire_of_a_credential_this_process_holds_returns_at_once_without_asking_the_advisory_lock()
    {
        var id = Guid.NewGuid();
        var tryLock = new RecordingTryLock();
        var holder = await CredentialRefreshLock.AcquireAsync(new RecordingCrossProcessLock(), id, CancellationToken.None);

        var attempt = await CredentialRefreshLock.TryAcquireAsync(tryLock, id, CancellationToken.None).WaitAsync(Patience);

        attempt.ShouldBeNull("a refresh or re-authorization in this process holds the credential, and a sweep does not queue behind it");
        tryLock.Events.ShouldBeEmpty("the in-process semaphore is the first tier: nothing is asked of Postgres once it says busy");

        await holder.DisposeAsync();

        await using var afterwards = await CredentialRefreshLock.TryAcquireAsync(tryLock, id, CancellationToken.None);
        afterwards.ShouldNotBeNull("once the holder lets go the credential is free for the sweep again");
    }

    [Fact]
    public async Task A_try_acquire_of_a_credential_another_pod_holds_returns_null_and_gives_the_semaphore_back()
    {
        var id = Guid.NewGuid();
        var tryLock = new RecordingTryLock { Busy = true };

        var attempt = await CredentialRefreshLock.TryAcquireAsync(tryLock, id, CancellationToken.None);

        attempt.ShouldBeNull("another pod holds the advisory lock");
        tryLock.Events.ShouldBe(new[] { "refused" });
        await using var again = await CredentialRefreshLock.AcquireAsync(new RecordingCrossProcessLock(), id, CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_try_acquire_whose_advisory_attempt_fails_gives_the_semaphore_back()
    {
        var id = Guid.NewGuid();

        var failure = await Should.ThrowAsync<InvalidOperationException>(async () => await CredentialRefreshLock.TryAcquireAsync(new RecordingTryLock { Fail = true }, id, CancellationToken.None));

        failure.Message.ShouldContain("advisory");
        await using var again = await CredentialRefreshLock.AcquireAsync(new RecordingCrossProcessLock(), id, CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_credential_held_by_a_try_acquire_holds_up_a_waiting_acquire_until_it_is_disposed()
    {
        // One semaphore for both ways in: a sweep that holds a credential excludes a re-authorization exactly as a refresh would.
        var id = Guid.NewGuid();
        var crossProcess = new RecordingCrossProcessLock();
        var sweep = await CredentialRefreshLock.TryAcquireAsync(new RecordingTryLock(), id, CancellationToken.None);

        var reauthorization = CredentialRefreshLock.AcquireAsync(crossProcess, id, CancellationToken.None);

        await Task.Delay(ShortWait);
        reauthorization.IsCompleted.ShouldBeFalse("the re-authorization waits for the sweep's hold, so it cannot write while the sweep is moving");

        await sweep!.DisposeAsync();

        await using var handle = await reauthorization.WaitAsync(Patience);
    }

    private sealed class RecordingCrossProcessLock : ICrossProcessLock
    {
        private readonly object _gate = new();
        private readonly List<string> _events = new();

        public bool FailAcquire { get; init; }

        public List<string> Events { get { lock (_gate) { return _events.ToList(); } } }

        public Task<IAsyncDisposable> AcquireAsync(long key, CancellationToken cancellationToken)
        {
            if (FailAcquire) throw new InvalidOperationException("simulated: the advisory lock could not be taken");

            lock (_gate) { _events.Add("acquire"); }

            return Task.FromResult<IAsyncDisposable>(new Release(this));
        }

        private sealed class Release : IAsyncDisposable
        {
            private readonly RecordingCrossProcessLock _owner;

            public Release(RecordingCrossProcessLock owner) { _owner = owner; }

            public ValueTask DisposeAsync()
            {
                lock (_owner._gate) { _owner._events.Add("release"); }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingTryLock : ICrossProcessTryLock
    {
        private readonly object _gate = new();
        private readonly List<string> _events = new();

        public bool Busy { get; init; }

        public bool Fail { get; init; }

        public List<string> Events { get { lock (_gate) { return _events.ToList(); } } }

        public Task<IAsyncDisposable?> TryAcquireAsync(long key, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("simulated: the advisory lock could not be tried");

            lock (_gate) { _events.Add(Busy ? "refused" : "acquire"); }

            return Task.FromResult<IAsyncDisposable?>(Busy ? null : new Release(this));
        }

        private sealed class Release : IAsyncDisposable
        {
            private readonly RecordingTryLock _owner;

            public Release(RecordingTryLock owner) { _owner = owner; }

            public ValueTask DisposeAsync()
            {
                lock (_owner._gate) { _owner._events.Add("release"); }

                return ValueTask.CompletedTask;
            }
        }
    }
}
