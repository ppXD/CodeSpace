namespace CodeSpace.Core.Services.Webhooks;

/// <summary>
/// Ingress for a delivery from a group / organization hook.
///
/// <para>A sibling of <see cref="IWebhookIngestionService"/> rather than a method on it, because the
/// id the caller holds means a different thing. There it identifies the REPOSITORY — the callback
/// URL is the answer to "what is this about", and ingestion never reads the body to find out. Here
/// it identifies only the HOOK, one URL carries every project under the owner, and the repository is
/// still to be found in the payload. A caller that has one id cannot use the other's entry point,
/// and an interface that offered both would let it try.</para>
///
/// <para>One class implements both, which is the point: everything after "which repository" — the
/// signature check, the normalizer, the rejection rows an operator reads — is shared, so a hook that
/// fails reads the same whichever scope it was in.</para>
/// </summary>
public interface IConnectionWebhookIngestionService
{
    /// <summary>
    /// Verify against THIS hook's own secret, read the repository out of the body, and publish for
    /// the bound repository it names. A delivery for a repository nobody bound is dropped and
    /// recorded rather than raised — it is what a group hook is expected to carry.
    /// </summary>
    Task IngestConnectionAsync(Guid connectionWebhookId, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}
