using System.Text.RegularExpressions;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Slice B3a — classifies a shell command line as DANGEROUS so the <c>agent.run_command</c> gate can escalate it to a
/// human even on a high-autonomy run (closes H7: the tool was gate-shallow — any command auto-ran at Unleashed). It is a
/// curated DENYLIST of high-signal, low-false-positive patterns: a catastrophic recursive delete (root / home / glob),
/// a pipe-to-shell (remote code execution), privilege escalation, a force-push (history rewrite), world-writable perms,
/// a filesystem format / raw device write, a power-state change, a fork bomb. NOT airtight — the sandbox (bubblewrap) is
/// the real boundary; this adds a HUMAN CHECKPOINT for the obvious dangerous command, conservatively: a miss runs
/// sandbox-bounded, a false positive merely asks a human. The command line scanned is the executable joined with its
/// args, so a shell wrapper (<c>sh -c "rm -rf /"</c>) is caught by the inner text too. Pure + static → exhaustively
/// unit-tested; the pattern set is pinned so any change is a reviewed decision (Rule 8 spirit).
/// </summary>
public static class CommandRiskClassifier
{
    // NonBacktracking: these patterns are regular (no backreferences / lookaround), and the scanned command line is
    // MODEL-CONTROLLED + unbounded — a backtracking engine would be a ReDoS surface (the rm flag run `\S*[rR]\S*` is
    // quadratic on a long failing input). NonBacktracking runs in guaranteed linear time with identical match results.
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private static readonly Regex[] Dangerous =
    {
        // Recursive delete whose TARGET is catastrophic — the root / a root glob / home / a top-level SYSTEM directory —
        // not a relative or scratch path (so `rm -rf node_modules`, `rm -rf ./build`, `rm -rf /tmp/work-xyz` stay safe).
        // System roots are clearly-system, never-a-workspace dirs (home/opt deliberately omitted — an agent workspace
        // can live there, and the sandbox already blocks host-FS writes; this is a best-effort human checkpoint).
        new(@"\brm\s+-\S*[rR]\S*(\s+-\S+)*\s+(/\*|/(?:usr|etc|var|bin|sbin|lib|lib64|boot|root|sys|proc|dev)(?:/\S*)?|/|~|\$\{?HOME\}?)(\s|$)", Opts),
        // Pipe into a shell — the curl|sh remote-code-execution shape (only reachable via `sh -c ""…""`, caught in the joined text).
        new(@"\|\s*(sudo\s+)?(ba|z|k|da)?sh\b", Opts),
        // Privilege escalation — an unattended sandboxed agent must never sudo unreviewed.
        new(@"\bsudo\b", Opts),
        // Force push — rewrites published history.
        new(@"\bgit\s+push\b.*(--force\b|--force-with-lease\b|\s-f\b)", Opts),
        // World-writable permissions.
        new(@"\bchmod\s+(-\S+\s+)*[0-7]?7{3}\b", Opts),
        // Format a filesystem.
        new(@"\bmkfs\S*\b", Opts),
        // Raw write to a block device (dd of=/dev/… or a redirect into one).
        new(@"\bdd\b[^|;&]*\bof=\s*/dev/", Opts),
        new(@">\s*/dev/(sd|nvme|hd|disk|mmcblk)", Opts),
        // Power-state change.
        new(@"\b(shutdown|reboot|halt|poweroff)\b", Opts),
        // Fork bomb :(){ :|:& };:
        new(@":\s*\(\s*\)\s*\{", Opts),
    };

    /// <summary>True when the command line matches a curated dangerous pattern (then the gate requires a human even at high autonomy). Null / blank ⇒ false. Pure.</summary>
    public static bool IsDangerous(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;

        return Dangerous.Any(p => p.IsMatch(commandLine));
    }
}
