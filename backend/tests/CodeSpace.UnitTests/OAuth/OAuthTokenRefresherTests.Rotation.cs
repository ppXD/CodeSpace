using System.Security.Cryptography;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Credentials;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// The rotated pair must reach the row. The provider consumes the refresh token the moment it answers, so from then on the
/// new pair is the credential — and it is stored nowhere but in the refresher's hands until the write lands. A key ring
/// that cannot seal it is found out before the provider is asked; a store that fails is retried and, if it keeps
/// failing, kept in this process for the next call to store; a compare-and-swap lost to another pair never drops the
/// pair just issued.
/// </summary>
public partial class OAuthTokenRefresherTests
{
    private static OAuthPayload ExpiredPair(string accessToken = "old", string refreshToken = "r-0") => new() { AccessToken = accessToken, RefreshToken = refreshToken, ExpiresAt = Now.AddSeconds(-1) };

    [Fact]
    public async Task A_key_ring_that_cannot_seal_the_rotated_pair_fails_before_the_provider_consumes_the_refresh_token()
    {
        // Sealing happens in the writer, after the exchange. A Data Protection fault there loses a pair the provider has
        // already issued, so the same fault must stop the refresh while the old refresh token is still good.
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var provider = new RecordingOAuthClient("new", "r-1", Now.AddHours(2));

        await Should.ThrowAsync<CryptographicException>(() => RunAsync(stale, ctx, new StoredRow(stale), provider, encryptor: new UnsealableEncryptor()));

        provider.RefreshCalls.ShouldBe(0, "the provider must not be asked to consume a refresh token while the pair it returns could not be sealed");
    }

    [Fact]
    public async Task A_store_that_fails_once_is_retried_and_the_rotated_pair_lands()
    {
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var provider = new RecordingOAuthClient("new", "r-1", Now.AddHours(2));
        FlakyPayloadWriter? flaky = null;

        var (result, exchanges, persists) = await RunAsync(stale, ctx, row, provider, decorateWriter: w => flaky = new FlakyPayloadWriter(w, failures: 1));

        result.AccessToken.ShouldBe("new");
        exchanges.ShouldBe(1);
        persists.ShouldBe(1);
        flaky!.Attempts.ShouldBe(2);
        row.Read().RefreshToken.ShouldBe("r-1");
    }

    [Fact]
    public async Task A_store_that_keeps_failing_is_tried_three_times_then_the_next_call_stores_the_pair_without_asking_the_provider_again()
    {
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var provider = new RecordingOAuthClient("new", "r-1", Now.AddHours(2));
        var log = new CapturingLogger();
        FlakyPayloadWriter? flaky = null;

        await Should.ThrowAsync<IOException>(() => RunAsync(stale, ctx, row, provider, decorateWriter: w => flaky = new FlakyPayloadWriter(w, failures: int.MaxValue), logger: log));

        flaky!.Attempts.ShouldBe(3, "one store and two retries, each on a connection of its own");
        row.Read().RefreshToken.ShouldBe("r-0", "fixture check: nothing was stored");
        var logged = log.Entries.Single(e => e.Level == LogLevel.Error);
        logged.Message.ShouldBe($"rotated pair NOT stored for credential {ctx.Credential.Id}; this process keeps it and stores it on the credential's next use", "the line an operator searches for, naming the credential");
        logged.Exception.ShouldBeOfType<IOException>();
        log.Entries.Select(e => e.Message).ShouldAllBe(m => !m.Contains("r-1") && !m.Contains("r-0") && !m.Contains("new"), "no token in the log");

        // The next call is what a later run does: it loads the row — still holding the consumed token — and refreshes.
        var (result, exchanges, persists) = await RunAsync(stale, ctx, row, provider);

        result.AccessToken.ShouldBe("new");
        exchanges.ShouldBe(1, "the provider consumed r-0 on the first call; presenting it again is refused");
        provider.RefreshCalls.ShouldBe(1);
        persists.ShouldBe(1);
        row.Read().RefreshToken.ShouldBe("r-1");
    }

    [Fact]
    public async Task A_kept_pair_is_revoked_at_the_provider_when_the_credential_was_disconnected_meanwhile()
    {
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var provider = new RecordingOAuthClient("new", "r-1", Now.AddHours(2));

        await Should.ThrowAsync<IOException>(() => RunAsync(stale, ctx, row, provider, decorateWriter: w => new FlakyPayloadWriter(w, failures: int.MaxValue)));

        row.Revoke();

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(stale, ctx, row, provider));

        failure.Message.ShouldContain("was revoked");
        provider.RevokedTokens.ShouldBe(new[] { "r-1", "new" }, ignoreOrder: false, customMessage: "the pair the provider issued for a credential the user disconnected is stored nowhere, so it ends there");

        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(stale, ctx, row, provider));

        provider.RevokedTokens.Count.ShouldBe(2, "a kept pair is spent once");
    }

    [Fact]
    public async Task A_kept_pair_older_than_the_pair_the_row_now_holds_is_dropped_in_its_favour()
    {
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var provider = new RecordingOAuthClient("new", "r-1", Now.AddHours(2));

        await Should.ThrowAsync<IOException>(() => RunAsync(stale, ctx, row, provider, decorateWriter: w => new FlakyPayloadWriter(w, failures: int.MaxValue)));

        row.Store(new OAuthPayload { AccessToken = "theirs", RefreshToken = "r-9", ExpiresAt = Now.AddHours(3) });

        var (result, _, persists) = await RunAsync(stale, ctx, row, provider);

        result.AccessToken.ShouldBe("theirs");
        persists.ShouldBe(0);
        row.Read().RefreshToken.ShouldBe("r-9");
    }

    [Theory]
    [InlineData(-10, "new", "r-1")]    // the row holds an older pair — a stale copy put back — so the pair just issued is stored over it
    [InlineData(10, "theirs", "r-9")]  // the row holds a newer pair, which is adopted
    public async Task A_refresh_whose_compare_and_swap_lost_to_another_pair_keeps_the_newer_of_the_two(int rowExpiresMinutesAfterIssued, string expectedAccessToken, string expectedStoredRefreshToken)
    {
        // The row changed while the provider answered, and not by a revocation: it is not empty. Adopting whatever it holds
        // would drop the issued pair when the row holds a copy of the consumed one.
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var issuedExpiry = Now.AddHours(2);
        var provider = new RecordingOAuthClient("new", "r-1", issuedExpiry, onRefreshing: () => row.Store(new OAuthPayload { AccessToken = "theirs", RefreshToken = "r-9", ExpiresAt = issuedExpiry.AddMinutes(rowExpiresMinutesAfterIssued) }));

        var (result, exchanges, _) = await RunAsync(stale, ctx, row, provider);

        result.AccessToken.ShouldBe(expectedAccessToken);
        row.Read().RefreshToken.ShouldBe(expectedStoredRefreshToken);
        exchanges.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]   // the re-read of the row throws, as a connection that drops does
    [InlineData(false)]  // the re-read answers with something that is not a payload, as a corrupted row does
    public async Task A_failure_after_the_compare_and_swap_was_lost_keeps_the_pair_for_the_next_call(bool readThrows)
    {
        // The writer answered false — the row changed, and is not empty — and the very next steps (reading the row, pointing
        // the entity at it) fail. The pair the provider just issued is stored nowhere at that moment, so it must be kept.
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var log = new CapturingLogger();
        FaultOnceAfterArmingReader? reader = null;
        var provider = new RecordingOAuthClient("new", "r-1", Now.AddHours(2), onRefreshing: () =>
        {
            row.Store(StaleCopy(1));
            reader!.Arm();
        });

        var failure = await Record.ExceptionAsync(() => RunAsync(stale, ctx, row, provider, decorateReader: r => reader = new FaultOnceAfterArmingReader(r, readThrows), logger: log));

        failure.ShouldNotBeNull("the failure surfaces to the caller");
        row.Read().RefreshToken.ShouldBe("r-stale-1", "fixture check: the row holds the stale copy, so the pair is stored nowhere");
        log.Entries.Single(e => e.Level == LogLevel.Error).Message.ShouldBe($"rotated pair NOT stored for credential {ctx.Credential.Id}; this process keeps it and stores it on the credential's next use");

        var (result, _, persists) = await RunAsync(stale, ctx, row, provider);

        result.AccessToken.ShouldBe("new");
        provider.RefreshCalls.ShouldBe(1, "the provider consumed r-0 on the first call; presenting it again is refused");
        persists.ShouldBe(1);
        row.Read().RefreshToken.ShouldBe("r-1");
    }

    [Fact]
    public async Task A_row_that_is_changed_to_an_older_pair_in_every_round_leaves_the_pair_kept_for_the_next_call()
    {
        // A stale copy of a consumed pair put back each time the refresher stores: after three rounds it stops taking the
        // row over and keeps the pair instead, still handing the caller a live token.
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var provider = new RecordingOAuthClient("new", "r-1", Now.AddHours(2));
        var log = new CapturingLogger();
        StaleCopyPutBackWriter? putBack = null;

        var (result, exchanges, persists) = await RunAsync(stale, ctx, row, provider, decorateWriter: w => putBack = new StaleCopyPutBackWriter(w, row, rounds: 3), logger: log);

        result.AccessToken.ShouldBe("new", "the caller still gets the pair the provider issued");
        exchanges.ShouldBe(1);
        persists.ShouldBe(0);
        putBack!.Attempts.ShouldBe(3, "three rounds, then the pair is kept");
        row.Read().RefreshToken.ShouldBe("r-stale-3", "fixture check: nothing stored the pair");
        log.Entries.Single(e => e.Level == LogLevel.Error).Message.ShouldBe($"rotated pair NOT stored for credential {ctx.Credential.Id}; this process keeps it and stores it on the credential's next use");

        var (next, nextExchanges, nextPersists) = await RunAsync(stale, ctx, row, provider);

        next.AccessToken.ShouldBe("new");
        nextExchanges.ShouldBe(1, "storing the kept pair needs no second exchange");
        nextPersists.ShouldBe(1);
        row.Read().RefreshToken.ShouldBe("r-1");
    }

    [Fact]
    public async Task A_store_whose_acknowledgement_was_lost_is_recognised_as_the_pair_just_issued()
    {
        // The row took the pair, but the connection died before the answer reached the refresher; the retry's
        // compare-and-swap then finds the row changed — to exactly the pair it was about to store.
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var provider = new RecordingOAuthClient("new", "r-1", Now.AddHours(2));

        var (result, exchanges, _) = await RunAsync(stale, ctx, row, provider, decorateWriter: w => new AckLosingPayloadWriter(w));

        result.AccessToken.ShouldBe("new");
        exchanges.ShouldBe(1);
        row.Read().RefreshToken.ShouldBe("r-1");
    }

    [Fact]
    public async Task The_entity_is_marked_stored_each_time_it_is_pointed_at_what_the_row_holds()
    {
        // The refresher points the caller's entity at the stored payload (see Adopt), and the writer does after a store. A
        // context that tracks the entity must be told, or its next SaveChanges writes the payload back.
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var tracked = new RecordingTrackedEntry();

        await RunAsync(stale, ctx, row, new RecordingOAuthClient("new", "r-1", Now.AddHours(2)), trackedEntry: tracked);

        tracked.PayloadsWhenMarked.ShouldNotBeEmpty();
        tracked.PayloadsWhenMarked.ShouldAllBe(payload => payload == Serializer.Serialize(stale), "each time the refresher adopts what the row holds, the entity already carries it");
    }

    // ─── Test plumbing ─────────────────────────────────────────────────────────

    private sealed class RecordingTrackedEntry : ITrackedCredentialEntry
    {
        public List<string> PayloadsWhenMarked { get; } = new();

        public void MarkPayloadStored(Credential credential) => PayloadsWhenMarked.Add(credential.EncryptedPayload);
    }

    /// <summary>A Data Protection key ring that cannot protect — a key-ring store that is down, a key that cannot be created — while it can still read.</summary>
    private sealed class UnsealableEncryptor : IPayloadEncryptor
    {
        public string Encrypt(string plaintext) => throw new CryptographicException("simulated: the key ring cannot protect");

        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>A store whose first <c>failures</c> attempts throw, then goes through.</summary>
    private sealed class FlakyPayloadWriter : ICredentialPayloadWriter
    {
        private readonly ICredentialPayloadWriter _inner;
        private int _failuresLeft;

        public int Attempts;

        public FlakyPayloadWriter(ICredentialPayloadWriter inner, int failures)
        {
            _inner = inner;
            _failuresLeft = failures;
        }

        public Task<bool> UpdatePayloadAsync(Credential credential, string expectedEncryptedPayload, CredentialPayload newPayload, CancellationToken cancellationToken)
        {
            Attempts++;

            if (_failuresLeft-- > 0) throw new IOException("simulated: the connection was terminated while storing the pair");

            return _inner.UpdatePayloadAsync(credential, expectedEncryptedPayload, newPayload, cancellationToken);
        }
    }

    /// <summary>A consumed pair a unit of work that loaded the credential before the rotation puts back; older than anything the provider issues in these tests.</summary>
    private static OAuthPayload StaleCopy(int n) => new() { AccessToken = $"at-stale-{n}", RefreshToken = $"r-stale-{n}", ExpiresAt = Now.AddMinutes(n) };

    /// <summary>Once armed, the next read fails — by throwing, or by answering with something that is not a payload — and the ones after it go through.</summary>
    private sealed class FaultOnceAfterArmingReader : ICredentialPayloadReader
    {
        private readonly ICredentialPayloadReader _inner;
        private readonly bool _throws;
        private bool _armed;

        public FaultOnceAfterArmingReader(ICredentialPayloadReader inner, bool throws)
        {
            _inner = inner;
            _throws = throws;
        }

        public void Arm() => _armed = true;

        public Task<string> ReadEncryptedPayloadAsync(Guid credentialId, CancellationToken cancellationToken)
        {
            if (!_armed) return _inner.ReadEncryptedPayloadAsync(credentialId, cancellationToken);

            _armed = false;

            return _throws ? throw new IOException("simulated: the connection dropped while re-reading the row") : Task.FromResult("{ this is not a payload");
        }

        public Task<bool> IsRetiredAsync(Guid credentialId, CancellationToken cancellationToken) => _inner.IsRetiredAsync(credentialId, cancellationToken);
    }

    /// <summary>For its first <c>rounds</c> stores, a different stale copy is put back into the row just before the store, so the compare-and-swap finds it changed.</summary>
    private sealed class StaleCopyPutBackWriter : ICredentialPayloadWriter
    {
        private readonly ICredentialPayloadWriter _inner;
        private readonly StoredRow _row;
        private readonly int _rounds;

        public int Attempts;

        public StaleCopyPutBackWriter(ICredentialPayloadWriter inner, StoredRow row, int rounds)
        {
            _inner = inner;
            _row = row;
            _rounds = rounds;
        }

        public Task<bool> UpdatePayloadAsync(Credential credential, string expectedEncryptedPayload, CredentialPayload newPayload, CancellationToken cancellationToken)
        {
            if (++Attempts <= _rounds) _row.Store(StaleCopy(Attempts));

            return _inner.UpdatePayloadAsync(credential, expectedEncryptedPayload, newPayload, cancellationToken);
        }
    }

    /// <summary>The first store reaches the row and then throws, as a connection that dies after the commit does.</summary>
    private sealed class AckLosingPayloadWriter : ICredentialPayloadWriter
    {
        private readonly ICredentialPayloadWriter _inner;
        private bool _lost;

        public AckLosingPayloadWriter(ICredentialPayloadWriter inner) { _inner = inner; }

        public async Task<bool> UpdatePayloadAsync(Credential credential, string expectedEncryptedPayload, CredentialPayload newPayload, CancellationToken cancellationToken)
        {
            var stored = await _inner.UpdatePayloadAsync(credential, expectedEncryptedPayload, newPayload, cancellationToken);

            if (_lost) return stored;

            _lost = true;
            throw new IOException("simulated: the connection died after the commit");
        }
    }

    private sealed class CapturingLogger : ILogger<OAuthTokenRefresher>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
