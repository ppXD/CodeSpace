using System.Buffers;
using System.Security.Cryptography;
using System.Text;

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

    /// <summary>
    /// A stable, NON-REVERSIBLE fingerprint of this redactor's secret set (null when empty) — a SHA-256 over the
    /// ordered secrets, never the secrets themselves. Lets a re-attaching observer verify it rebuilt the SAME
    /// redactor that masked the original run before it re-tails the spool: a mismatch (the credential was deleted
    /// or rotated since launch) means it could freeze an unmaskable echoed key, so it must NOT re-tail. A SHA-256
    /// of a long, high-entropy API key is not a practical leak, so persisting the fingerprint (e.g. on the run
    /// handle) is safe where persisting the key would not be.
    ///
    /// <para>The digest is taken over a TOTAL order imposed here — length descending, then ordinal — so it is a
    /// function of the secret SET and of nothing else. Ordering at the hash rather than at the caller is what makes
    /// that a property of the fingerprint itself: the constructor's <c>OrderByDescending</c> alone would leave the
    /// digest resting on LINQ's sort being stable over whatever order the caller happened to enumerate in.</para>
    /// </summary>
    public string? Fingerprint
    {
        get
        {
            if (IsEmpty) return null;

            var ordered = _secrets.OrderByDescending(s => s.Length).ThenBy(s => s, StringComparer.Ordinal);

            // Domain-separated so the digest can't be confused with any other hash of the same value.
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("codespace-secret-fingerprint-v1\n" + string.Join('\n', ordered)));
            return Convert.ToHexString(bytes);
        }
    }

    /// <summary>
    /// This redactor plus <paramref name="additional"/> secrets — a new instance; the receiver is unchanged. For a
    /// secret that only EXISTS later in the launch (the per-run MCP capability token, minted after the credential
    /// resolve), so it becomes a needle without the credential having to be resolved a second time.
    /// </summary>
    public SecretRedactor With(IEnumerable<string> additional) => new(_secrets.Concat(additional));

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text) || IsEmpty) return text;

        foreach (var secret in _secrets) text = text.Replace(secret, Placeholder, StringComparison.Ordinal);

        return text;
    }

    /// <summary>
    /// Creates a stateful byte-stream redactor for durable stdout/stderr capture. It matches the UTF-8 bytes of the
    /// same known secrets while preserving every unrelated byte verbatim, including malformed UTF-8. A bounded suffix
    /// is held between calls so a secret split at any read boundary is still masked; callers persist only
    /// <see cref="SecretUtf8Redaction.SourceBytesConsumed"/> source bytes with each transformed result.
    /// </summary>
    public SecretUtf8RedactionStream CreateUtf8Stream() => new(_secrets);
}

public sealed class SecretUtf8RedactionStream
{
    private static readonly byte[] PlaceholderBytes = Encoding.UTF8.GetBytes(SecretRedactor.Placeholder);
    private readonly IReadOnlyList<byte[]> _patterns;
    private readonly int _maximumPatternLength;
    private byte[] _carry = [];

    internal SecretUtf8RedactionStream(IEnumerable<string> secrets)
    {
        _patterns = secrets.Select(Encoding.UTF8.GetBytes).Where(value => value.Length > 0).OrderByDescending(value => value.Length).ToArray();
        _maximumPatternLength = _patterns.Count == 0 ? 0 : _patterns.Max(value => value.Length);
    }

    public SecretUtf8Redaction Transform(ReadOnlySpan<byte> source, bool final)
    {
        var combined = new byte[_carry.Length + source.Length];
        _carry.CopyTo(combined, 0);
        source.CopyTo(combined.AsSpan(_carry.Length));
        var safeStartLimit = final || _maximumPatternLength == 0 ? combined.Length : Math.Max(0, combined.Length - _maximumPatternLength + 1);
        var output = new ArrayBufferWriter<byte>(Math.Max(combined.Length, 1));
        var offset = 0;

        while (offset < safeStartLimit)
        {
            var matched = Match(combined, offset);
            if (matched > 0)
            {
                output.Write(PlaceholderBytes);
                offset += matched;
                continue;
            }

            output.Write(combined.AsSpan(offset, 1));
            offset++;
        }

        _carry = combined.AsSpan(offset).ToArray();
        return new SecretUtf8Redaction(output.WrittenMemory.ToArray(), offset);
    }

    private int Match(byte[] source, int offset)
    {
        foreach (var pattern in _patterns)
        {
            if (offset + pattern.Length <= source.Length && source.AsSpan(offset, pattern.Length).SequenceEqual(pattern)) return pattern.Length;
        }

        return 0;
    }
}

public sealed record SecretUtf8Redaction(ReadOnlyMemory<byte> Bytes, int SourceBytesConsumed);
