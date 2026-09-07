using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// C3 — which repo paths an acceptance COMMAND makes ORACLE bytes, derived from the command itself.
///
/// <para><see cref="Messages.Agents.SupervisorAcceptanceSpec.ProtectedPaths"/> only ever arrives on a
/// MODEL-authored per-subtask spec; nothing in Core or the UI writes one. The operator's stop-time acceptance
/// floor — the most trusted oracle in the system, the gate a run cannot ship past — was built as a bare argv spec
/// with no protection at all, so a candidate could edit the very <c>check.sh</c> the floor grades it with and the
/// floor would faithfully run the edited script. This derives the protection the operator never had to write.</para>
///
/// <para><b>The PROGRAM position only, never an argument.</b> <c>dotnet test Foo.sln</c> names a solution a
/// candidate may legitimately have to edit (adding a test project); restoring it from base would VOID honest work
/// and fail a correct candidate. The script that decides the verdict is a different thing from the code it decides
/// about — only the former is the judge, and only the judge is protected.</para>
///
/// <para>Pure by construction: repository existence is answered by a caller-supplied predicate, so the extraction
/// rule is unit-testable without git and the production caller answers it off the clone it already has.</para>
/// </summary>
public static class AcceptanceOracleProtection
{
    /// <summary>Programs that RUN another program — the oracle is the script they are handed, never the interpreter itself. Matched on the file name so <c>/bin/sh</c> counts too.</summary>
    private static readonly HashSet<string> Interpreters = new(StringComparer.Ordinal)
    {
        "sh", "bash", "zsh", "dash", "ksh", "env", "python", "python2", "python3", "node", "npx", "pwsh", "powershell", "ruby", "perl",
    };

    /// <summary>Shell operators that end one command and start another, so the NEXT token is a program again (<c>sh -c "npm ci &amp;&amp; ./check.sh"</c> runs two judges, not one).</summary>
    private static readonly HashSet<string> CommandSeparators = new(StringComparer.Ordinal) { "&&", "||", "|", ";", "&" };

    private static readonly char[] Whitespace = { ' ', '\t', '\n', '\r' };

    /// <summary>Characters that make a token something other than a plain repo pathspec — a glob, a substitution, a quoted fragment, an env assignment. Never guessed at: excluded.</summary>
    private static readonly char[] NotAPathspec = "$*?[]{}()'\"`\\<>=!&|;:".ToCharArray();

    /// <summary>The repo-relative paths <paramref name="argv"/> makes oracle bytes: its program file(s), kept only when <paramref name="repoFileExists"/> says the repository actually holds that file at the graded base. A program the candidate CREATED is not the operator's judge and is deliberately not protected.</summary>
    public static IReadOnlyList<string> DeriveProtectedPaths(IReadOnlyList<string>? argv, Func<string, bool> repoFileExists) =>
        ProgramCandidates(argv).Where(repoFileExists).ToList();

    /// <summary>
    /// Whether <paramref name="spec"/> can be protected at all — AUTHORED <c>ProtectedPaths</c>, or a derivable
    /// program candidate from its command — decided from the contract alone, before any clone or base-sha lookup.
    /// The ONE derivation both the grader (to widen its clone before the restore) and the per-unit base-sha
    /// resolver (<c>SupervisorTurnService.Rehydrate.cs</c>'s <c>OracleBaseShaAsync</c>) must share: before that
    /// resolver consulted this same function it anchored a restore ONLY on an authored spec, so a per-unit oracle
    /// whose only protection was DERIVABLE (the shape every real operator floor actually has — nothing in Core or
    /// the UI ever authors <c>ProtectedPaths</c>) never got a base sha to restore from at all. The grader could then
    /// only say the judge went unprotected; it could never put the pristine script back.
    /// </summary>
    public static bool MayProtect(SupervisorAcceptanceSpec spec) => spec.ProtectedPaths is { Count: > 0 } || CommandOracleCandidates(spec).Count > 0;

    /// <summary>
    /// The argv's program candidates for a TestsPass-shaped acceptance — empty for any oracle whose <c>Command</c>
    /// is NOT an argv. An <c>ArtifactPresent</c> contract's command is the list of deliverables the candidate must
    /// PRODUCE; restoring one of those from base would void the very work being verified, which is the opposite of
    /// protecting a judge.
    /// </summary>
    public static IReadOnlyList<string> CommandOracleCandidates(SupervisorAcceptanceSpec spec) =>
        spec.Kind is null or BenchmarkGradingKind.TestsPass ? ProgramCandidates(spec.Command) : Array.Empty<string>();

    /// <summary>
    /// The program-position tokens of <paramref name="argv"/>, normalized to repo-relative pathspecs and deduped —
    /// the candidate set <see cref="DeriveProtectedPaths"/> filters by existence. Public because the grader needs
    /// to know BEFORE it clones whether protection is possible at all (a protected grade needs the base's history,
    /// not the agents' shallow clone).
    /// </summary>
    public static IReadOnlyList<string> ProgramCandidates(IReadOnlyList<string>? argv)
    {
        if (argv is not { Count: > 0 }) return Array.Empty<string>();

        var candidates = new List<string>();
        var atProgram = true;

        foreach (var token in Flatten(argv))
        {
            if (CommandSeparators.Contains(token)) { atProgram = true; continue; }

            if (!atProgram) continue;

            if (token.StartsWith('-')) continue;                // a flag never ends the search for the program (`sh -c ./check.sh`)
            if (IsEnvAssignment(token)) continue;               // `CI=1 ./check.sh` — the assignment PRECEDES the program
            if (IsInterpreter(token)) continue;                 // the script it is handed is the oracle, not the shell

            if (Normalize(token) is { } path && !candidates.Contains(path, StringComparer.Ordinal)) candidates.Add(path);

            atProgram = false;                                  // everything after the program is an ARGUMENT — never protected
        }

        return candidates;
    }

    /// <summary>argv, with any token that carries a whole command line (<c>sh -c "./check.sh --fast"</c>) split into its own words — otherwise the judge inside a <c>-c</c> string would be invisible.</summary>
    private static IEnumerable<string> Flatten(IReadOnlyList<string> argv) =>
        argv.Where(a => !string.IsNullOrWhiteSpace(a)).SelectMany(a => a.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsInterpreter(string token) => Interpreters.Contains(token[(token.LastIndexOf('/') + 1)..]);

    /// <summary>
    /// A leading shell env assignment (<c>CI=1 ./check.sh</c>). It must be SKIPPED rather than treated as the
    /// program: reading it as one leaves the real judge behind it unprotected, and silently — the assignment
    /// carries an <c>=</c>, so it is never a pathspec and nothing downstream complains.
    /// </summary>
    private static bool IsEnvAssignment(string token)
    {
        var equals = token.IndexOf('=');

        if (equals <= 0 || char.IsAsciiDigit(token[0])) return false;

        for (var i = 0; i < equals; i++)
            if (!char.IsAsciiLetterOrDigit(token[i]) && token[i] != '_') return false;

        return true;
    }

    /// <summary>
    /// A repo-relative pathspec, or null when the token cannot be one — absolute, escaping the repo root, carrying
    /// shell syntax, or simply not path-SHAPED. The last rule is what keeps <c>dotnet</c> / <c>npm</c> / <c>make</c>
    /// from costing every ordinary floor grade a full-history clone and a probe: a bare word with neither a
    /// directory separator nor an extension is a binary on PATH, never a file the repository ships.
    /// </summary>
    private static string? Normalize(string token)
    {
        var relative = token.StartsWith("./", StringComparison.Ordinal);
        var path = relative ? token[2..] : token;

        if (path.Length == 0 || path[0] == '/' || path[0] == '~') return null;
        if (path.AsSpan().IndexOfAny(NotAPathspec) >= 0) return null;
        if (path.Split('/').Any(segment => segment is "..")) return null;
        if (!relative && !path.Contains('/') && !Path.HasExtension(path)) return null;

        return path;
    }
}
