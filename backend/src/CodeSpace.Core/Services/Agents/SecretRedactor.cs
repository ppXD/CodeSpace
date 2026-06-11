namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Replaces a known set of secret values (a run's resolved model key / gateway token) with a placeholder in
/// any text bound for a PERSISTED or logged sink. Constructed per-run from the just-in-time-resolved
/// credential, so the agent run's append-only event log, result, and error can never freeze a key the harness
/// CLI happened to echo (an init banner, a 401 body). Exact, case-sensitive, longest-secret-first matching.
///
/// <para>Errs toward redaction: every non-empty secret is replaced wherever it appears. A model API key is
/// long + high-entropy, so over-matching legitimate text is a non-issue in practice — and under-redacting a
/// secret would be a leak, which is strictly worse than garbling a line.</para>
/// </summary>
public sealed class SecretRedactor
{
    public const string Placeholder = "***";

    /// <summary>A redactor with no secrets — <see cref="Redact"/> is the identity, so the no-credential run path stays zero-overhead.</summary>
    public static SecretRedactor None { get; } = new(Array.Empty<string>());

    private readonly IReadOnlyList<string> _secrets;

    public SecretRedactor(IEnumerable<string> secrets) =>
        // Drop blank/whitespace-only entries (never a real key, and masking runs of spaces would garble output);
        // longest first so a secret that contains a shorter one is masked before the shorter is matched inside it.
        _secrets = secrets.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderByDescending(s => s.Length).ToList();

    public bool IsEmpty => _secrets.Count == 0;

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text) || IsEmpty) return text;

        foreach (var secret in _secrets) text = text.Replace(secret, Placeholder, StringComparison.Ordinal);

        return text;
    }
}
