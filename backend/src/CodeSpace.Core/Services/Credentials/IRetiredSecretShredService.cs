namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Blanks the ciphertext of secrets that are retired but still decryptable. Every retirement in this build blanks its own
/// secret; this catches what a build older than migration 0249 retires without blanking — during a rolling deploy, or
/// after a rollback — since 0249's own backfill ran once, before those rows existed.
/// </summary>
public interface IRetiredSecretShredService
{
    /// <summary>
    /// Blank every retired secret still holding ciphertext: exactly the rows 0249's backfill blanks. Idempotent and safe on
    /// several workers at once — a blanked row is no longer a candidate, and each statement re-checks its row as it
    /// writes. Returns how many rows this pass blanked.
    /// </summary>
    Task<int> ShredAsync(CancellationToken cancellationToken);
}
