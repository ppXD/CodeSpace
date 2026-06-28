using System.Text;

namespace CodeSpace.Core.Services.Agents.Harnesses.Claude;

/// <summary>
/// Reproduces Claude Code's transcript-file location so a CONTINUE can RESTORE a prior session's JSONL where the CLI
/// looks for it on <c>--resume</c>. Claude stores a session at
/// <c>&lt;CLAUDE_CONFIG_DIR&gt;/projects/&lt;sanitized-cwd&gt;/&lt;session-id&gt;.jsonl</c>. The sanitizer is a BYTE-FOR-BYTE
/// port of the real claude 2.1.193 encoder (extracted from the binary): replace every char outside <c>[A-Za-z0-9]</c>
/// with <c>-</c>, and when the result exceeds 200 chars truncate to 200 and append <c>-&lt;base36 hash&gt;</c> of the
/// ORIGINAL cwd (so deep paths still map to a stable, collision-resistant dir). Pinned by <c>ClaudeTranscriptPathTests</c>
/// against ground-truth pairs produced by the real algorithm.
/// <code>
/// function ab(e){let t=e.replace(/[^a-zA-Z0-9]/g,"-");if(t.length&lt;=200)return t;return `${t.slice(0,200)}-${Byu(e)}`}
/// function Byu(e){return Math.abs(hRe(e)).toString(36)}
/// function hRe(e){let t=0;for(let n=0;n&lt;e.length;n++)t=(t&lt;&lt;5)-t+e.charCodeAt(n)|0;return t}
/// </code>
/// <para><b>The sharpest P3 hazard</b>: the cwd MUST be the RESOLVED real path the agent process runs in — on macOS
/// <c>/var/…</c> resolves to <c>/private/var/…</c>, and under bubblewrap it is the <c>--chdir</c> host path. Encoding the
/// wrong path lands the transcript under a DIFFERENT sanitized dir and <c>--resume</c> SILENTLY cold-starts (no error).
/// This helper is the PURE transform; passing the resolved cwd is the producer slice's responsibility.</para>
/// </summary>
internal static class ClaudeTranscriptPath
{
    /// <summary>Claude's <c>pXe</c>: a sanitized cwd longer than this is truncated + hash-suffixed.</summary>
    internal const int MaxSegmentLength = 200;

    /// <summary>Sanitize a cwd into Claude's projects-dir segment — a byte-exact port of the binary's <c>ab()</c>.</summary>
    public static string EncodeCwd(string cwd)
    {
        var sanitized = Sanitize(cwd ?? "");

        return sanitized.Length <= MaxSegmentLength ? sanitized : $"{sanitized[..MaxSegmentLength]}-{Hash(cwd ?? "")}";
    }

    /// <summary>The config-home-relative path of a session's transcript: <c>projects/&lt;sanitized-cwd&gt;/&lt;sessionId&gt;.jsonl</c>.</summary>
    public static string For(string cwd, string sessionId) => $"projects/{EncodeCwd(cwd)}/{sessionId}.jsonl";

    /// <summary>Every char outside <c>[A-Za-z0-9]</c> → <c>-</c> (claude's <c>replace(/[^a-zA-Z0-9]/g,"-")</c>).</summary>
    private static string Sanitize(string cwd)
    {
        var sb = new StringBuilder(cwd.Length);

        foreach (var c in cwd)
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');

        return sb.ToString();
    }

    /// <summary>Claude's <c>Byu(hRe())</c>: base36 of the absolute 32-bit Java-style string hash (<c>t = t*31 + c</c>, wrapping) of the ORIGINAL cwd.</summary>
    private static string Hash(string cwd)
    {
        var t = 0;

        foreach (var c in cwd)
            t = unchecked((t << 5) - t + c);   // (t<<5)-t == t*31; the int wrap matches JS's per-step `|0`

        return ToBase36(Math.Abs((long)t));    // (long) so abs(int.MinValue) matches JS Math.abs without a C# overflow throw
    }

    private static string ToBase36(long value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";

        if (value == 0) return "0";

        var sb = new StringBuilder();

        while (value > 0)
        {
            sb.Insert(0, digits[(int)(value % 36)]);
            value /= 36;
        }

        return sb.ToString();
    }
}
