using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Supervisor;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;

/// <summary>
/// The RUNTIME every tests-pass check runs under, whatever lane graded it. A check runs in the graded tree, because
/// that tree is what it judges, so the tree can also reach the check's machinery. Three channels decided verdicts that
/// way: a relative PATH entry put a candidate's binary in front of the tool the judge calls, a module in the working
/// directory shadowed the standard library an inline <c>python -c</c> imports, and a user site or interpreter variable
/// would have done the same. This closes those channels and says, on the grade, which checks it could not isolate.
///
/// <para><b>Environment.</b> PATH keeps only absolute entries outside the graded tree. Python gets no user site and
/// writes no bytecode, and Node gets no extra module path or preloaded options. <c>PYTHONSAFEPATH</c> is deliberately
/// NOT set: it would drop a subject script's own directory from its <c>sys.path</c>, so every multi-file Python program a
/// judge runs (<c>python3 src/app.py</c> importing <c>src/lib.py</c>) would fail as honest work. What a judge STARTS is
/// labelled instead (below).</para>
///
/// <para><b>Python.</b> Code the PLATFORM owns runs as <c>python -I -B</c>: inline <c>-c</c> code (it lives in the spec)
/// and a pinned judge script. Isolated mode drops the working directory, the script's directory, every PYTHON*
/// variable and the user site (so <c>-B</c> stands in for the ignored <c>PYTHONDONTWRITEBYTECODE</c>). A small launcher
/// then puts back only what the code may legitimately use: a judge script's own directory goes first when the platform
/// restored it, and otherwise that directory or the graded tree is appended LAST. The subject's own modules still
/// import, and none of them can shadow the standard library or an installed package. When the code imports a name that
/// BOTH the appended directory and the interpreter supply (a subject named <c>calendar.py</c>), the interpreter's wins
/// and the launcher says so on stderr before any code runs, so the grade is labelled rather than silently judging the
/// standard library (<see cref="ShadowNote"/>).</para>
///
/// <para><b>What it cannot isolate.</b> It reports these instead of claiming a clean pass. That covers a runner such
/// as <c>make</c>, <c>npm</c> or <c>python -m pytest</c> that reads its configuration from the graded tree (however its
/// path is spelled), every shell judge (each command it starts runs with the graded tree as its working directory, so a
/// <c>python3 -c</c> or <c>pytest</c> it launches is not isolated), a node/ruby/perl/pwsh judge (its runtime resolves
/// libraries up through the graded tree, e.g. <c>node_modules</c>), a judge run directly through its own interpreter line,
/// a program the candidate's tree supplies, and a judge at the repository root, whose neighbours are the candidate's
/// work. The note rides the grade (<see cref="UnverifiedNoteMarker"/>); the verdict itself stands.</para>
///
/// <para><b>Residual.</b> A pinned python judge is clean as far as its own interpreter goes. A process it starts, or an
/// in-process runner it calls (<c>pytest.main</c>), reads the graded tree exactly as written, and a subject it imports
/// in-process can end the judge's process; the platform cannot see either from outside the judge.</para>
/// </summary>
public static class OracleRuntime
{
    /// <summary>The prefix of the note a grade carries when its check could not run isolated. Pinned by test (Rule 8): the grade's readers and the tests key on the literal.</summary>
    public const string UnverifiedNoteMarker = "oracle: UNVERIFIED (";

    /// <summary>The line the launcher writes to stderr, before any code runs, when the code imports a module that both the graded tree and the interpreter supply. Code that runs later can add a line like it (which only adds a label) but never remove it.</summary>
    internal const string ShadowMarker = "codespace-oracle: shadowed ";

    /// <summary>The PATH a check gets when the worker has none of its own that survives the filter.</summary>
    internal const string FallbackPath = "/usr/local/bin:/usr/bin:/bin";

    /// <summary>
    /// The launcher <c>python -I</c> runs in front of platform-owned code. Its first argument says what follows: <c>code</c> (inline <c>-c</c>
    /// source), <c>front</c> (a judge script whose directory the platform restored, so it leads the path) or <c>back</c>
    /// (a judge whose directory is the candidate's tree, so it trails the standard library). Inline code gets the working
    /// directory appended last, which is where a plain <c>-c</c> would have found the subject's modules. In the two
    /// trailing modes it first names, on stderr (<see cref="ShadowMarker"/>), every module the code imports that the
    /// appended directory AND the interpreter both supply.
    /// </summary>
    internal const string PythonLauncher = """
        import os as _o, sys as _s
        _m, _t = _s.argv.pop(1), _s.argv.pop(1)
        _d = _o.getcwd() if _m == "code" else _o.path.dirname(_o.path.abspath(_t))
        def _h(_c):
            import ast as _a, importlib.util as _u
            _n = set()
            for _x in _a.walk(_a.parse(_c)):
                if isinstance(_x, _a.Import): _n.update(_y.name.partition(".")[0] for _y in _x.names)
                elif isinstance(_x, _a.ImportFrom) and not _x.level and _x.module: _n.add(_x.module.partition(".")[0])
            _p = lambda _y: _o.path.isfile(_o.path.join(_d, _y + ".py")) or _o.path.isfile(_o.path.join(_d, _y, "__init__.py"))
            return sorted(_y for _y in _n if _p(_y) and _u.find_spec(_y))
        if _m == "front":
            _s.path.insert(0, _d)
        else:
            try:
                if _m == "code":
                    _q = _h(_t)
                else:
                    with open(_t, "rb") as _f: _q = _h(_f.read())
            except Exception:
                _q = []
            if _q: print("codespace-oracle: shadowed " + ",".join(_q), file=_s.stderr, flush=True)
            _s.path.append(_d)
        if _m == "code":
            _c = compile(_t, "<string>", "exec")
            del _o, _m, _t, _d, _h, _q
            exec(_c, __import__("__main__").__dict__)
        else:
            _s.argv[0] = _t
            __import__("runpy").run_path(_t, run_name="__main__")
        """;

    private static readonly Regex Python = new(@"^python(\d+(\.\d+)*)?$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "sh", "bash", "zsh", "dash", "ksh" };

    private static readonly HashSet<string> ScriptInterpreters = new(StringComparer.Ordinal) { "node", "ruby", "perl", "pwsh", "powershell" };

    /// <summary>Program names that re-resolve or fetch the program they finally run, so nothing about that program is pinned.</summary>
    private static readonly HashSet<string> Launchers = new(StringComparer.Ordinal) { "env", "npx" };

    /// <summary>Python options that take a separate value.</summary>
    private static readonly HashSet<string> PythonValueOptions = new(StringComparer.Ordinal) { "-W", "-X" };

    /// <summary>
    /// The command the grader runs for <paramref name="argv"/> in <paramref name="gradingDirectory"/>, where
    /// <paramref name="pinnedPaths"/> are the repo-relative paths the caller restored from platform-owned bytes (a
    /// directory ends with <c>/</c>) and <paramref name="hostPath"/> is the worker's own PATH.
    /// </summary>
    public static OracleCommand Build(IReadOnlyList<string> argv, string gradingDirectory, IReadOnlyCollection<string> pinnedPaths, string? hostPath)
    {
        var environment = IsolatedEnvironment(hostPath, gradingDirectory);

        var (args, reason) = Resolve(argv, gradingDirectory, pinnedPaths);

        return new OracleCommand(argv[0], args, environment, reason, ReportsShadowing(args));
    }

    /// <summary>
    /// <paramref name="hostPath"/> without every entry the graded tree could put a binary in: an empty entry (the
    /// working directory), a relative one, and any inside <paramref name="gradingDirectory"/>. Order is kept, so the
    /// worker's own tools resolve exactly as before.
    /// </summary>
    public static string FixedPath(string? hostPath, string gradingDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gradingDirectory));

        var kept = (hostPath ?? "").Split(Path.PathSeparator).Where(entry => entry.Length > 0 && Path.IsPathRooted(entry) && !IsWithin(Path.GetFullPath(entry), root)).Distinct(StringComparer.Ordinal).ToList();

        return kept.Count == 0 ? FallbackPath : string.Join(Path.PathSeparator, kept);
    }

    /// <summary>Two notes as one line, either side optional — the grade carries one <c>OracleNote</c> and several layers may each owe it a clause.</summary>
    public static string? CombineNotes(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? NullIfEmpty(second) : string.IsNullOrEmpty(second) ? first : $"{first}; {second}";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static IReadOnlyDictionary<string, string> IsolatedEnvironment(string? hostPath, string gradingDirectory) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PATH"] = FixedPath(hostPath, gradingDirectory),
        ["PYTHONNOUSERSITE"] = "1",
        ["PYTHONDONTWRITEBYTECODE"] = "1",
        ["NODE_PATH"] = "",
        ["NODE_OPTIONS"] = "",
    };

    private static bool IsWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// The argv to hand the runner (after <c>argv[0]</c>) and, when the check could not be isolated, why. The program is
    /// classified by its FILE NAME first, so <c>/usr/bin/make</c> is the same runner as <c>make</c> and <c>/bin/sh</c> the
    /// same shell as <c>sh</c>. A program spelled as a path inside the graded tree runs through its own interpreter line;
    /// any other program (a bare word, or an absolute path outside the tree) runs against the graded tree, which can
    /// supply its configuration.
    /// </summary>
    private static (IReadOnlyList<string> Args, string? Reason) Resolve(IReadOnlyList<string> argv, string gradingDirectory, IReadOnlyCollection<string> pinnedPaths)
    {
        var name = Path.GetFileName(argv[0]);
        var rest = argv.Skip(1).ToList();

        if (Python.IsMatch(name)) return ResolvePython(name, rest, gradingDirectory, pinnedPaths);

        if (Launchers.Contains(name)) return (rest, $"`{name}` resolves the program it runs at grade time");

        if (Shells.Contains(name)) return (rest, ShellReason(name, rest, gradingDirectory, pinnedPaths));

        if (ScriptInterpreters.Contains(name)) return (rest, InterpreterReason(name, rest, gradingDirectory, pinnedPaths));

        if (IsInTree(argv[0], gradingDirectory)) return (rest, PinReason(argv[0], gradingDirectory, pinnedPaths) ?? $"{RepoRelative(argv[0], gradingDirectory)} runs through its own interpreter line, which this grade cannot isolate");

        return (rest, $"`{name}` runs against the graded tree, which can supply its configuration");
    }

    /// <summary>Whether <paramref name="token"/> names a file in the graded tree: a relative path-shaped token, or an absolute path inside <paramref name="gradingDirectory"/>.</summary>
    private static bool IsInTree(string token, string gradingDirectory) =>
        Path.IsPathRooted(token) ? IsWithin(Path.GetFullPath(token), Root(gradingDirectory)) : token.Contains('/') || AcceptanceOracleProtection.RepoPath(token) is not null;

    private static string Root(string gradingDirectory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(gradingDirectory));

    /// <summary>Whether the resolved argv runs the launcher in a mode that appends a directory after the interpreter's own, and so reports a name both supply.</summary>
    private static bool ReportsShadowing(IReadOnlyList<string> args)
    {
        var at = args.ToList().IndexOf(PythonLauncher);

        return at >= 0 && at + 1 < args.Count && args[at + 1] is "code" or "back";
    }

    /// <summary>
    /// A python argv as the isolated launcher's argv. Interpreter options before the program are kept as written; the
    /// program is inline code, a pinned script, an unpinned script (the candidate's own, left as it is) or something
    /// the launcher cannot stand in front of (<c>-m</c>, stdin, an option it does not parse).
    /// </summary>
    private static (IReadOnlyList<string> Args, string? Reason) ResolvePython(string name, List<string> rest, string gradingDirectory, IReadOnlyCollection<string> pinnedPaths)
    {
        var options = new List<string>();

        for (var i = 0; i < rest.Count; i++)
        {
            var token = rest[i];

            if (token == "-c" && i + 1 < rest.Count) return (Launch(options, "code", rest[i + 1], rest.Skip(i + 2)), null);

            if (token == "-m") return (rest, $"`{name} -m` loads its modules and configuration from the graded tree");

            if (PythonValueOptions.Contains(token) && i + 1 < rest.Count)
            {
                options.Add(token);
                options.Add(rest[++i]);
                continue;
            }

            if (IsPlainPythonFlag(token))
            {
                options.Add(token);
                continue;
            }

            if (token.StartsWith('-')) return (rest, $"`{name} {token}` could not be isolated");

            return ResolvePythonScript(options, token, rest.Skip(i + 1), gradingDirectory, pinnedPaths, rest);
        }

        return (rest, $"`{name}` names no program to run");
    }

    /// <summary>A single-dash python option the launcher can keep in front of <c>-I</c>: plain letter flags, or a joined <c>-W</c>/<c>-X</c> value. A letter that consumes the NEXT token (<c>c</c>, <c>m</c>) inside a combined flag is not one.</summary>
    private static bool IsPlainPythonFlag(string token)
    {
        if (token.Length < 2 || token[0] != '-' || token[1] == '-') return false;

        if (token[1] is 'W' or 'X') return token.Length > 2;

        return token[1..].All(c => char.IsAsciiLetter(c) && c is not 'c' and not 'm' and not 'W' and not 'X');
    }

    private static (IReadOnlyList<string> Args, string? Reason) ResolvePythonScript(List<string> options, string script, IEnumerable<string> scriptArgs, string gradingDirectory, IReadOnlyCollection<string> pinnedPaths, List<string> original)
    {
        var reason = PinReason(script, gradingDirectory, pinnedPaths);

        if (reason is not null && !IsFilePinned(script, gradingDirectory, pinnedPaths)) return (original, reason);

        return (Launch(options, reason is null ? "front" : "back", script, scriptArgs), reason);
    }

    private static IReadOnlyList<string> Launch(List<string> options, string mode, string target, IEnumerable<string> args) =>
        options.Concat(new[] { "-I", "-B", "-c", PythonLauncher, mode, target }).Concat(args).ToList();

    /// <summary>
    /// Why a shell command is not isolated — always: a judge whose own bytes are pinned still runs every command it
    /// starts with the graded tree as their working directory, so those are reported too. Flags may precede the script
    /// (<c>sh -e tests/check.sh</c>); <c>-o</c> takes a value; any flag that carries <c>c</c> means the script is inline.
    /// </summary>
    private static string ShellReason(string name, List<string> rest, string gradingDirectory, IReadOnlyCollection<string> pinnedPaths)
    {
        for (var i = 0; i < rest.Count; i++)
        {
            var token = rest[i];

            if (token == "--") return i + 1 < rest.Count ? ScriptReason(name, rest[i + 1], gradingDirectory, pinnedPaths) : $"`{name}` names no script to run";

            if (token is "-o" or "+o")
            {
                i++;
                continue;
            }

            if (token.Length > 1 && token[0] is '-' or '+')
            {
                if (token[1..].Contains('c')) return $"`{name} -c` runs inline code that resolves files and commands against the graded tree";
                continue;
            }

            return ScriptReason(name, token, gradingDirectory, pinnedPaths);
        }

        return $"`{name}` names no script to run";
    }

    /// <summary>Why a node/ruby/perl/pwsh command is not isolated — always. Their options load extra code or inline it, so any option before the script is its own reason; otherwise the script's pin, and then the runtime's own reach into the graded tree.</summary>
    private static string InterpreterReason(string name, List<string> rest, string gradingDirectory, IReadOnlyCollection<string> pinnedPaths)
    {
        if (rest.Count == 0) return $"`{name}` names no script to run";

        if (rest[0].StartsWith('-')) return $"`{name} {rest[0]}` could not be isolated";

        return ScriptReason(name, rest[0], gradingDirectory, pinnedPaths);
    }

    /// <summary>The reason a script judge run by <paramref name="runtime"/> is not isolated: its own pin first, else what that runtime resolves against the graded tree.</summary>
    private static string ScriptReason(string runtime, string script, string gradingDirectory, IReadOnlyCollection<string> pinnedPaths) =>
        PinReason(script, gradingDirectory, pinnedPaths) ?? RuntimeReach(runtime);

    private static string RuntimeReach(string runtime) => runtime switch
    {
        "node" => "`node` resolves what its judge requires by walking up into the graded tree (node_modules, package.json)",
        _ when Shells.Contains(runtime) => $"`{runtime}` runs every command its judge starts with the graded tree as their working directory, so those are not isolated",
        _ => $"`{runtime}` resolves the libraries its judge loads against the graded tree",
    };

    /// <summary>
    /// Why running <paramref name="program"/> is not an isolated judge, or null when it is: a file outside the graded
    /// tree (the worker's own), or one whose whole directory the caller pinned as holding only platform-owned bytes.
    /// </summary>
    private static string? PinReason(string program, string gradingDirectory, IReadOnlyCollection<string> pinnedPaths)
    {
        if (Path.IsPathRooted(program) && !IsWithin(Path.GetFullPath(program), Root(gradingDirectory))) return null;

        var path = RepoRelative(program, gradingDirectory);

        if (path is null) return $"{program} is not a file the platform can pin";

        if (IsScopePinned(path, pinnedPaths)) return null;

        if (!AcceptanceOracleProtection.Covers(pinnedPaths, path)) return $"{path} is not a platform-owned judge — the candidate's tree supplies it";

        return path.Contains('/')
            ? $"only {path} itself is platform-owned, not the files beside it"
            : $"{path} lives at the repository root, so only that file is platform-owned and what it reads from the tree is the candidate's";
    }

    private static bool IsFilePinned(string program, string gradingDirectory, IReadOnlyCollection<string> pinnedPaths) => RepoRelative(program, gradingDirectory) is { } path && AcceptanceOracleProtection.Covers(pinnedPaths, path);

    /// <summary>Whether the directory <paramref name="path"/> lives in is (within) a pinned directory — never true at the root, whose directory is the candidate's whole tree.</summary>
    private static bool IsScopePinned(string path, IReadOnlyCollection<string> pinnedPaths)
    {
        var scope = AcceptanceOracleProtection.JudgeScope(path);

        return scope.EndsWith('/') && pinnedPaths.Any(p => p.EndsWith('/') && scope.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>The program as a repo-relative path: an absolute path inside the graded tree made relative, otherwise the shared normalization.</summary>
    private static string? RepoRelative(string program, string? gradingDirectory)
    {
        if (gradingDirectory is not null && Path.IsPathRooted(program))
            return Path.GetRelativePath(Path.GetFullPath(gradingDirectory), Path.GetFullPath(program)).Replace(Path.DirectorySeparatorChar, '/');

        return AcceptanceOracleProtection.RepoPath(program);
    }

    /// <summary>
    /// The note a grade owes for the launcher's shadow report in <paramref name="stderr"/> (<see cref="ShadowMarker"/>),
    /// or null when there is none. Every reported line counts: the launcher's own comes first, and anything later can only
    /// add names — a label, never a way to remove one.
    /// </summary>
    public static string? ShadowNote(string? stderr)
    {
        var names = (stderr ?? "").Split('\n').Where(line => line.StartsWith(ShadowMarker, StringComparison.Ordinal)).SelectMany(line => line[ShadowMarker.Length..].Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.Ordinal).ToList();

        return names.Count == 0 ? null : UnverifiedNote($"{string.Join(", ", names)} in the graded tree shares a name the standard library or an installed package supplies, and the check imported the interpreter's, not the candidate's");
    }

    /// <summary>The one line a grade carries for a check it could not stand behind, with <paramref name="reason"/> saying why.</summary>
    public static string UnverifiedNote(string reason) => $"{UnverifiedNoteMarker}{reason})";

    /// <summary>One check as the runner will run it: the program, its arguments, the isolated environment, and — when the check could not be isolated — the reason, which <see cref="Note"/> renders for the grade. <see cref="ReportsShadowing"/> says the launcher will name, on stderr, any module it resolved away from the graded tree.</summary>
    public sealed record OracleCommand(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Environment, string? UnverifiedReason, bool ReportsShadowing = false)
    {
        /// <summary>The single line a grade carries when this check was not isolated; null when it was.</summary>
        public string? Note => UnverifiedReason is null ? null : UnverifiedNote(UnverifiedReason);
    }
}
