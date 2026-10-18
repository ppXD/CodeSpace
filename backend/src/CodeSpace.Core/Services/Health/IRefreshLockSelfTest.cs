using CodeSpace.Messages.Health;

namespace CodeSpace.Core.Services.Health;

/// <summary>Whether <c>pg_advisory_lock</c> is exclusive across the connections this process opens — the property OAuth token refresh serializes on.</summary>
public interface IRefreshLockSelfTest
{
    /// <summary>The latest finding, running the test first when there is none or it is older than its lifetime. Never throws for a test that cannot run (that is <see cref="RefreshLockOutcome.Inconclusive"/>); a cancelled <paramref name="cancellationToken"/> still cancels.</summary>
    Task<RefreshLockVerdict> VerdictAsync(CancellationToken cancellationToken);
}
