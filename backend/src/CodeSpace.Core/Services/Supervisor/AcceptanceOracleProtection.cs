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
/// <para><b>And only a program file the RUN OWNS.</b> A program file is not the oracle just because the command
/// runs it: when the check EXECUTES the deliverable (<c>sh solution.sh 7 5</c>, <c>python main.py</c>,
/// <c>node app.js</c>) that file is the SUBJECT under test, and restoring it from base voids exactly the work the
/// goal asked for. So the derived set is intersected with the files the run's OWN oracle inventory names — the
/// OPERATOR FLOOR's program files (<c>SupervisorGoalConfig.AcceptanceChecks</c>). A command's other program files
/// are reported (the grade says the check ran the candidate's own copy) and graded, never restored.</para>
///
/// <para><b>The AUTHORED door obeys the same fence.</b> An authored <c>ProtectedPaths</c> still outranks the
/// derivation for every file the check does not EXECUTE — fixtures, data, a judge script this command never runs.
/// But the brain that authors <c>sh solution.sh 7 5</c> against a goal that says "edit solution.sh" is exactly the
/// brain that would then list <c>solution.sh</c> in <c>protectedPaths</c>, and honoring that re-opens the live
/// regression verbatim: the stub goes back over a correct agent's work and no retry can pass. So an authored path
/// that is ALSO a program-position file of the SAME check and is NOT floor-owned degrades to the subject note
/// instead of a void. <c>SupervisorDecisionSchema</c>'s own field description carries the matching carve-out, so
/// the model is told the rule rather than merely corrected by it.</para>
///
/// <para><b>This carve-out is not complete — only direct.</b> It degrades an authored path only when that SAME
/// path is itself a program-position token of the command's own argv. An authored <c>protectedPaths: ["solution.sh"]</c>
/// beside a command like <c>sh check.sh</c>, where <c>check.sh</c> executes <c>solution.sh</c> INTERNALLY rather
/// than in its own argv, never surfaces to this derivation at all — <c>solution.sh</c> is not a program-position
/// token of THIS command, so it still restores and voids like any other authored oracle. The server has no way to
/// answer "what does this script run" for a command it never interprets; the residual is closed only by
/// <c>SupervisorDecisionSchema</c>'s instruction never to name a file the subtask is expected to modify, not by
/// anything enforced here.</para>
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

    /// <summary>The repo-relative paths <paramref name="argv"/> makes RUN-OWNED oracle bytes: the program file(s) <paramref name="oracleFloorPrograms"/> also names, kept only when <paramref name="repoFileExists"/> says the repository actually holds that file at the graded base. A program the candidate CREATED is not the operator's judge and is deliberately not protected; neither is one the run's own floor never runs.</summary>
    public static IReadOnlyList<string> DeriveProtectedPaths(IReadOnlyList<string>? argv, IReadOnlyList<string>? oracleFloorPrograms, Func<string, bool> repoFileExists) =>
        RunOwned(ProgramCandidates(argv), oracleFloorPrograms).Where(repoFileExists).ToList();

    /// <summary>
    /// Whether <paramref name="spec"/> can be protected at all — an AUTHORED <c>ProtectedPaths</c> that is not just
    /// this check's own subject, or a RUN-OWNED program candidate from its command — decided from the contract
    /// alone, before any clone or base-sha lookup.
    /// The ONE derivation both the grader (to widen its clone before the restore) and the per-unit base-sha
    /// resolver (<c>SupervisorTurnService.Rehydrate.cs</c>'s <c>OracleAnchorAsync</c>) must share: before that
    /// resolver consulted this same function it anchored a restore ONLY on an authored spec, so a per-unit oracle
    /// whose only protection was DERIVABLE (the shape every real operator floor actually has — nothing in Core or
    /// the UI ever authors <c>ProtectedPaths</c>) never got a base sha to restore from at all.
    /// </summary>
    public static bool MayProtect(SupervisorAcceptanceSpec spec, IReadOnlyList<string>? oracleFloorPrograms) =>
        AuthoredOracleCandidates(spec, oracleFloorPrograms).Count > 0 || CommandOracleCandidates(spec, oracleFloorPrograms).Count > 0;

    /// <summary>
    /// The AUTHORED <c>ProtectedPaths</c> this grade may actually restore: every path the contract named EXCEPT one
    /// this same check executes in a program position without the run's floor owning it — the SUBJECT under test,
    /// which the model is as able to mis-name as the derivation was to mis-derive. Empty (the caller falls through
    /// to the derived set) when the contract authored nothing, or authored only its own subject.
    /// </summary>
    public static IReadOnlyList<string> AuthoredOracleCandidates(SupervisorAcceptanceSpec spec, IReadOnlyList<string>? oracleFloorPrograms)
    {
        if (spec.ProtectedPaths is not { Count: > 0 } authored) return Array.Empty<string>();

        var subject = SubjectPrograms(spec, oracleFloorPrograms);

        return subject.Count == 0 ? authored : authored.Where(p => !subject.Contains(p, StringComparer.Ordinal)).ToList();
    }

    /// <summary>The program files the command EXECUTES that the run does NOT own — the SUBJECT under test, whatever the contract calls them.</summary>
    private static IReadOnlyList<string> SubjectPrograms(SupervisorAcceptanceSpec spec, IReadOnlyList<string>? oracleFloorPrograms)
    {
        var owned = CommandOracleCandidates(spec, oracleFloorPrograms);

        return CommandProgramCandidates(spec).Where(p => !owned.Contains(p, StringComparer.Ordinal)).ToList();
    }

    /// <summary>
    /// The RUN-OWNED program candidates of a TestsPass-shaped acceptance command: its program-position files that
    /// <paramref name="oracleFloorPrograms"/> — the run's own oracle inventory, derived from the OPERATOR FLOOR's
    /// argv through <see cref="ProgramCandidates"/> — also names. Empty when the run owns no oracle file, and empty
    /// for any oracle whose <c>Command</c> is NOT an argv (an <c>ArtifactPresent</c> contract's command is the list
    /// of deliverables the candidate must PRODUCE; restoring one of those from base would void the very work being
    /// verified, which is the opposite of protecting a judge).
    /// </summary>
    public static IReadOnlyList<string> CommandOracleCandidates(SupervisorAcceptanceSpec spec, IReadOnlyList<string>? oracleFloorPrograms) =>
        RunOwned(CommandProgramCandidates(spec), oracleFloorPrograms);

    /// <summary>
    /// EVERY program-position file the acceptance command executes — kind-gated exactly like
    /// <see cref="CommandOracleCandidates"/> but NOT narrowed to what the run owns. The superset the protected set
    /// is carved out of, so the grader can name the remainder for what it is: the SUBJECT the check runs, graded on
    /// the candidate's own bytes.
    /// </summary>
    public static IReadOnlyList<string> CommandProgramCandidates(SupervisorAcceptanceSpec spec) =>
        spec.Kind is null or BenchmarkGradingKind.TestsPass ? ProgramCandidates(spec.Command) : Array.Empty<string>();

    /// <summary>
    /// The clause a PASSING grade's <c>Detail</c> carries when the check ran a program file this grade did not
    /// protect. It rides the DETAIL because that is the one string a pass survives with: both folds drop the
    /// evidence tail on a pass (nothing to repair) and the decider's pass branch renders no evidence at all, so a
    /// self-graded pass reached the brain with no mention that no protected judge stood behind it.
    /// </summary>
    public const string SubjectDetailMarker = " \u2014 graded on the candidate's own ";

    /// <summary>The file list <paramref name="acceptanceDetail"/>'s <see cref="SubjectDetailMarker"/> carries, or null when this grade protected everything it ran. The ONE reader for the decider prompt and the recitation, so the two prompt sections cannot disagree about a row.</summary>
    public static string? SubjectFilesIn(string? acceptanceDetail)
    {
        var at = acceptanceDetail?.IndexOf(SubjectDetailMarker, StringComparison.Ordinal) ?? -1;

        return at < 0 ? null : acceptanceDetail![(at + SubjectDetailMarker.Length)..];
    }

    /// <summary>The clause an UNPROTECTED grade's Detail carries when a judge COULD have been protected but had no base to restore from (<c>SupervisorAcceptanceGrader</c>'s <c>Unprotected</c> outcome). Mutually exclusive with <see cref="SubjectDetailMarker"/> on the same grade — <c>OracleProtectionOutcome.WithSubject</c> overwrites rather than appends when a subject account also applies.</summary>
    public const string UnanchoredDetailMarker = "oracle: graded UNPROTECTED (";

    /// <summary>Whether <paramref name="acceptanceDetail"/> carries the <see cref="UnanchoredDetailMarker"/> — the ONE reader for a Room-level protection classification, mirroring <see cref="SubjectFilesIn"/>'s role for the subject case.</summary>
    public static bool IsUnanchored(string? acceptanceDetail) => acceptanceDetail?.Contains(UnanchoredDetailMarker, StringComparison.Ordinal) == true;

    /// <summary>
    /// The neutral clause a PASS carries for <paramref name="files"/> (from <see cref="SubjectFilesIn"/>) — worded
    /// so it is TRUE whichever half of <see cref="SupervisorAcceptanceGrader"/>'s collapsed list produced it: the
    /// SUBJECT under test (a file the run never owned) or the candidate's OWN new check script (a file the run owns
    /// but base never shipped). Naming either one "the SUBJECT under test" mis-names the other, so this says
    /// neither. The decider's verdict line and the recitation's compact both render this SAME phrase, so a
    /// self-graded pass cannot read one way in one prompt section and another in the other.
    /// </summary>
    public static string SubjectClausePhrase(string files) => $"graded on the candidate's OWN {files}, not a protected judge";

    /// <summary>
    /// The program-position tokens of <paramref name="argv"/>, normalized to repo-relative pathspecs and deduped —
    /// the candidate set the narrowing and existence filters carve down. Public because the grader needs to know
    /// BEFORE it clones whether protection is possible at all (a protected grade needs the base's history, not the
    /// agents' shallow clone), and because it is how a caller turns the OPERATOR FLOOR's own argv into the run's
    /// oracle inventory — one derivation, never a second copy that could drift from the one being narrowed.
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

    /// <summary>
    /// <paramref name="programs"/> narrowed to the ones the run's own oracle inventory names. A run that owns NO
    /// oracle file (no operator floor, or a floor like <c>dotnet test</c> that names no repo file) owns no derived
    /// judge either — its per-unit checks grade the candidate's own bytes and say so, which is strictly safer than
    /// restoring a file the goal may have required editing.
    /// </summary>
    private static IReadOnlyList<string> RunOwned(IReadOnlyList<string> programs, IReadOnlyList<string>? oracleFloorPrograms) =>
        oracleFloorPrograms is { Count: > 0 } floor ? programs.Where(p => floor.Contains(p, StringComparer.Ordinal)).ToList() : Array.Empty<string>();

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
