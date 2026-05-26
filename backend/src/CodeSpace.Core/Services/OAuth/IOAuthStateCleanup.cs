namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Sweeps expired <c>oauth_pending_state</c> rows. ConsumeAsync already removes a row on
/// successful exchange, but rows whose user abandoned the flow (closed the tab between
/// init and callback) accumulate. This is the janitor.
/// </summary>
public interface IOAuthStateCleanup
{
    Task<int> DeleteExpiredAsync(CancellationToken cancellationToken);
}
