using System.Text.RegularExpressions;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Harnesses.Claude;

/// <summary>
/// Pointer rules: repository memory one Read away. A rule scoped by <c>paths:</c> never loads from an added directory,
/// and nested memory past the in-place budget is not added at all, yet the unpinned CLI attached both once the run read
/// a file they cover. The pinned CLI still attaches a rule of the user's own (<c>CLAUDE_CONFIG_DIR/rules/**/*.md</c>)
/// whose <c>paths:</c> match a file its Read tool opens, matched relative to the run's cwd. So the run's config home gets
/// one such rule per scoped repository rule and per nested directory that holds memory,
/// <c>rules/codespace-repository-NNN.md</c>, numbered in walk order. Its <c>paths:</c> are the repository rule's own,
/// rebased onto the cwd (<see cref="ClaudeRuleScope.Rebase"/>) — or <c>/&lt;directory&gt;</c> for a directory's memory,
/// which the CLI reads exactly as the <c>/&lt;directory&gt;/**</c> it was observed to match — so it attaches on the reads
/// that attached the repository's own; its body is one sentence of the runner's naming the files to read. A nested
/// directory loaded in place gets one too: the CLI starts an Explore or Plan subagent without project memory, yet
/// attached a directory's memory there once the subagent read below it, and it still attaches a user rule there. Its
/// sentence says to read the files only if they are not already in context, as they are in the main conversation.
///
/// <para>No repository byte is ever written there. The user scope reads external imports, and nothing under the
/// config home is the repository's to vouch for, so a pointer carries the runner's sentence and the files' absolute
/// paths, each in backticks and of letters, digits and <c>. _ + - /</c> only — never an <c>@</c>, anywhere — and its
/// globs re-emitted, never copied. A memory file whose path holds anything else is left out of its directory's pointer by
/// name. A rule the CLI loaded with no globs would load before the first request with a user's authority, so every
/// pointer is read back through the CLI's own parse (<see cref="ClaudeRuleScope.Read"/>) and kept only when that gives
/// exactly the globs written: scoped, and scoped as meant — a glob holding <c>---</c>, which brace expansion can make,
/// would close the frontmatter early. What a pointer cannot carry exactly gets none, and the timeline says so.</para>
///
/// <para>A pointer names files the model may then read, so the directory it points into passes the same guard as any
/// added directory (<see cref="Guard"/>), against the same build budget, and one whose memory reaches outside the
/// workspace gets none. Each pointer is at most <see cref="MaxPointerBytes"/> — a directory's names its memory files while
/// it fits and counts the rest — and all of them together at most <see cref="MaxPointerTotalBytes"/>, so a repository
/// that plants thousands of rules neither fills the launch frame nor ends a run on the read that attaches its pointer.
/// At most <see cref="MaxPointerRules"/> pointers are written and as many directories checked for them; past any bound
/// the timeline says so.</para>
/// </summary>
internal static partial class ClaudeWorkspaceMemory
{
    /// <summary>The most pointer rules one run's config home carries, and directories checked for them. Pinned by a test.</summary>
    internal const int MaxPointerRules = 128;

    /// <summary>The most bytes one pointer rule holds, its frontmatter and its sentence together. Pinned by a test.</summary>
    internal const int MaxPointerBytes = 4096;

    /// <summary>The most bytes all of one run's pointer rules hold together. Pinned by a test.</summary>
    internal const int MaxPointerTotalBytes = 128 * 1024;

    /// <summary>Where a pointer rule sits in the config home, before its three-digit number and <c>.md</c>.</summary>
    internal const string PointerRulePrefix = "rules/codespace-repository-";

    /// <summary>The characters a path a pointer names may hold.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._+\-/]+\z")]
    private static partial Regex SafePointerPath();

    private const string UnsafePointerPath = "its path holds a character other than a letter, a digit or one of . _ + - /";

    /// <summary>The pointer rules for one build, in order, and one sentence for each one the runner could not write.</summary>
    private sealed record Pointers(IReadOnlyList<ConfigHomeFile> Files, IReadOnlyList<string> Notices);

    /// <summary>One pointer the walk calls for: into <see cref="Found"/>'s directory, naming <see cref="Rule"/>, or naming the directory's own memory when that is null.</summary>
    private sealed record PointerSource(Found Found, ScopedRule? Rule);

    /// <summary>
    /// One build's pointer rules, the paths they name spelled from <paramref name="cwd"/>. <paramref name="announced"/>
    /// are the directories whose left-out notice the timeline already carries — every one offered to <c>--add-dir</c>.
    /// </summary>
    private sealed class PointerRules(string cwd, Guard guard, IEnumerable<string> announced)
    {
        private readonly List<ConfigHomeFile> _files = [];
        private readonly List<string> _notices = [];
        private readonly HashSet<string> _announced = announced.Select(Path.TrimEndingDirectorySeparator).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        private int _bytes;
        private bool _full;

        /// <summary>A pointer for every nested directory's memory and every scoped rule the walk found — in walk order, a directory's memory before its rules — until a bound is reached.</summary>
        public Pointers For(Nested nested)
        {
            foreach (var source in Sources(nested))
            {
                if (_full) break;

                if (!Admits(source.Found.Directory))
                {
                    _notices.Add($"Left the repository rules and nested memory past the first {MaxPointerRules} pointers out of this run: the runner writes no more.");
                    break;
                }

                if (LeftOut(source.Found.Directory)) continue;

                if (source.Rule is { } rule) PointAtRule(source.Found.Directory, rule);
                else PointAtMemory(source.Found, nested.OverBudget);
            }

            return new Pointers(_files, _notices);
        }

        private static IEnumerable<PointerSource> Sources(Nested nested) =>
            nested.Found.SelectMany(found => (found.Memory is not null ? [new PointerSource(found, null)] : Array.Empty<PointerSource>()).Concat(found.Rules.Select(rule => new PointerSource(found, rule))));

        /// <summary>Whether one more pointer into <paramref name="directory"/> stays within the bounds: <see cref="MaxPointerRules"/> files, and as many directories checked.</summary>
        private bool Admits(string directory)
        {
            if (_files.Count == MaxPointerRules) return false;

            return _directories.Contains(directory) || (_directories.Count < MaxPointerRules && _directories.Add(directory));
        }

        /// <summary>Whether the guard leaves the directory out; said once, unless the timeline already says it — or says the budget ran out before it was checked.</summary>
        private bool LeftOut(string directory)
        {
            if (guard.Of(directory).Escape is not { } why) return false;

            if (why != Unchecked && _announced.Add(Path.TrimEndingDirectorySeparator(directory))) _notices.Add(Notice(cwd, directory, why));

            return true;
        }

        /// <summary>A scoped rule's pointer: its globs rebased onto the cwd, and a sentence naming the rule.</summary>
        private void PointAtRule(string directory, ScopedRule rule)
        {
            var subject = $"the rule '{Printable(Path.GetRelativePath(cwd, rule.File))}'";
            var globs = rule.Globs.Select(glob => ClaudeRuleScope.Rebase(Below(directory), glob)).ToList();
            var unsafeGlob = globs.IndexOf(null);

            if (!SafePointerPath().IsMatch(rule.File)) Refuse(subject, UnsafePointerPath);
            else if (unsafeGlob >= 0) Refuse(subject, $"its paths: hold '{Printable(rule.Globs[unsafeGlob])}', which no pointer can carry exactly");
            else Point(subject, globs!, $"The repository rule `{rule.File}` applies to the file you just read. Read it now and follow it for files it matches.");
        }

        /// <summary>A nested directory's pointer: every file below it, and a sentence naming its memory files — each whose path no pointer may carry left out by name. One loaded in place that gets none still loads; only its pointer is left out.</summary>
        private void PointAtMemory(Found found, bool overBudget)
        {
            var below = Below(found.Directory);
            var subject = overBudget ? $"the memory in '{Printable(below)}'" : $"the pointer to the memory in '{Printable(below)}'";
            var files = found.Memory!.Files.Where(file => SafePointerPath().IsMatch(file)).ToList();
            var unnamed = found.Memory.Files.Except(files).ToList();

            if (!SafePointerPath().IsMatch(found.Directory))
            {
                Refuse(subject, UnsafePointerPath);
                return;
            }

            if (unnamed.Count > 0) _notices.Add($"Left the memory file '{Printable(Path.GetRelativePath(cwd, unnamed[0]))}'{(unnamed.Count > 1 ? $" and {unnamed.Count - 1} more like it" : "")} out of the pointer to '{Printable(below)}': {UnsafePointerPath}.");

            if (files.Count == 0) return;

            if (DirectorySentence(found.Directory, below, files, overBudget) is { } sentence) Point(subject, [$"/{below}"], sentence);
            else Refuse(subject, $"its pointer would run past {MaxPointerBytes} bytes");
        }

        /// <summary>
        /// A directory pointer's sentence: past the in-place budget, that its memory was not preloaded; in place, where it
        /// is should it not be in context. It names <paramref name="files"/> in order while the pointer stays within
        /// <see cref="MaxPointerBytes"/>, and counts the rest — all of which sit under the directory's <c>.claude</c>, as
        /// its <c>CLAUDE.md</c> comes first. Null when not even the first name fits.
        /// </summary>
        private static string? DirectorySentence(string directory, string below, IReadOnlyList<string> files, bool overBudget)
        {
            var lead = overBudget ? $"Repository instructions for `{below}/` were not preloaded: " : $"Repository instructions for `{below}/` are in ";
            var tail = overBudget ? $". Read them now and follow them while you work under `{below}/`." : $". Read them now if they are not already in your context, and follow them while you work under `{below}/`.";
            var room = MaxPointerBytes - ClaudeRuleScope.Frontmatter([$"/{below}"]).Length - lead.Length - tail.Length - 1;
            var named = new List<string>();

            foreach (var file in files)
            {
                var more = files.Count - named.Count - 1;

                if (Listed(named.Append(file)).Length + (more > 0 ? Remainder(directory, more).Length : 0) > room) break;

                named.Add(file);
            }

            return named.Count == 0 ? null : lead + Listed(named) + (named.Count < files.Count ? Remainder(directory, files.Count - named.Count) : "") + tail;
        }

        private static string Listed(IEnumerable<string> files) => string.Join(", ", files.Select(file => $"`{file}`"));

        private static string Remainder(string directory, int count) => $" and {count} more memory files under `{directory}/.claude/`";

        /// <summary>
        /// The pointer file, kept only when the CLI's own parse of it gives back exactly <paramref name="globs"/> — never
        /// unconditional, never scoped other than meant — and while it and every pointer before it stay within
        /// <see cref="MaxPointerBytes"/> and <see cref="MaxPointerTotalBytes"/>. Past the total no pointer follows. Every
        /// byte a pointer holds is ASCII, so its length is its size.
        /// </summary>
        private void Point(string subject, IReadOnlyList<string> globs, string body)
        {
            var content = ClaudeRuleScope.Frontmatter(globs) + body + "\n";

            if (ClaudeRuleScope.Read(content)?.SequenceEqual(globs) != true) Refuse(subject, "the CLI would read its pointer with other globs than the runner wrote");
            else if (content.Length > MaxPointerBytes) Refuse(subject, $"its pointer would run past {MaxPointerBytes} bytes");
            else if (_bytes + content.Length > MaxPointerTotalBytes) Fill();
            else Write(content);
        }

        private void Write(string content)
        {
            _bytes += content.Length;
            _files.Add(new ConfigHomeFile { RelativePath = $"{PointerRulePrefix}{_files.Count:000}.md", Content = content });
        }

        private void Fill()
        {
            _full = true;
            _notices.Add($"Left the repository rules and nested memory past the first {_files.Count} pointers out of this run: together they would run past {MaxPointerTotalBytes} bytes.");
        }

        private void Refuse(string subject, string why) => _notices.Add($"Left {subject} out of this run: {why}.");

        /// <summary>The directory relative to the cwd, empty for the cwd itself.</summary>
        private string Below(string directory) => Path.GetRelativePath(cwd, directory) switch
        {
            "." => "",
            var relative => relative,
        };
    }
}
