namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Everything an operator needs to tell one provider failure from another: what we asked, what the
/// provider answered, and with which status.
///
/// <para>Every field is optional because the interesting failures are the ones missing a field. A
/// call that timed out has no <see cref="StatusCode"/> and no <see cref="ResponseBody"/> — that
/// absence IS the diagnosis, and it is what separates "nine timeouts then a 403" from "403 ten
/// times".</para>
/// </summary>
public sealed record ProviderCallDiagnostic
{
    /// <summary>HTTP status the provider answered with; null when the call never got an HTTP answer.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The provider's response body, truncated at capture time. Null when there was no response.</summary>
    public string? ResponseBody { get; init; }

    /// <summary>The request we sent, already masked. Null only when the failure happened before a request was formed.</summary>
    public CapturedProviderRequest? Request { get; init; }
}
