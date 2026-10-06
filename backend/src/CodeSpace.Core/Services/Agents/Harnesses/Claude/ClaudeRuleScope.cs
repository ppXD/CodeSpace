using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CodeSpace.Core.Services.Agents.Harnesses.Claude;

/// <summary>
/// Whether the pinned CLI scopes a <c>.claude/rules</c> file by <c>paths:</c>, read the way 2.1.263 reads it. Only a rule
/// it reads with no globs loads from an <c>--add-dir</c> up front; a scoped one never does there (see
/// <c>ClaudeCodeHarness.AdditionalDirectoriesMemoryEnvVar</c>), so which kind a rule is decides whether its directory has
/// memory to load in place (<see cref="ClaudeWorkspaceMemory"/>) and how many bytes it adds.
///
/// <para>Mirrored from the CLI's bundle, step for step: a byte-order mark is dropped; the frontmatter is the text
/// between a leading <c>---</c> line and the next <c>---</c> anywhere after it; it is parsed as YAML and, when that fails,
/// parsed again with every top-level <c>key: value</c> whose value holds a YAML indicator double-quoted and every leading
/// tab turned into two spaces; <c>paths</c> counts only as strings, alone or in (nested) lists — the CLI's parser types a
/// plain <c>5</c>, <c>true</c> or <c>~</c> by the YAML 1.2 core schema, so none of those is a glob; each string is split at
/// commas outside braces, trimmed and brace-expanded within the CLI's own budget; a trailing <c>/**</c> is dropped and an
/// empty glob with it. No glob left, or nothing but <c>**</c>, and the rule is unconditional. A YAML corner where the
/// CLI's parser and YamlDotNet disagree can misplace a rule; that costs the in-place budget a rule's bytes, never what
/// the CLI itself loads, which the E2E pins against the real binary.</para>
/// </summary>
internal static partial class ClaudeRuleScope
{
    /// <summary>How much of a rule the runner reads first to classify it; when that opens a fence it does not close (<see cref="IsUnclosed"/>), the runner reads on to the close. Pinned by a test.</summary>
    internal const int MaxHeadBytes = 4096;

    /// <summary>The CLI's own brace-expansion budget for one <c>paths</c> value: at most this many globs (<c>M=1000</c> in 2.1.263)…</summary>
    private const int BraceResults = 1000;

    /// <summary>…and this many bytes spent (<c>I=4194304</c>); past either the pattern stays unexpanded.</summary>
    private const int BraceBytes = 4194304;

    /// <summary>JavaScript's <c>\s</c>, which every pattern of the CLI's here uses: .NET's own lacks U+FEFF and adds U+0085.</summary>
    private const string JsSpace = "[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]";

    /// <summary>JavaScript's <c>.</c>: anything but a line terminator.</summary>
    private const string JsDot = "[^\n\r\u2028\u2029]";

    private static readonly char[] JsSpaceChars = "\t\n\v\f\r \u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();

    [GeneratedRegex("^---" + JsSpace + "*\n([\\s\\S]*?)---")]
    private static partial Regex Fence();

    /// <summary>The fence's opening line, or as much of it as a cut text holds.</summary>
    [GeneratedRegex("^---" + JsSpace + "*(\n|\\z)")]
    private static partial Regex Opening();

    [GeneratedRegex("^([a-zA-Z_-]+):" + JsSpace + "+(" + JsDot + "+)\\z")]
    private static partial Regex TopLevelPair();

    [GeneratedRegex("[{}\\[\\]*&#!|>%@`]|: ")]
    private static partial Regex YamlIndicator();

    [GeneratedRegex("(?<=^|[\n\r\u2028\u2029])\t+")]
    private static partial Regex LeadingTabs();

    [GeneratedRegex("^([^{]*)\\{([^}]+)\\}(" + JsDot + "*)\\z")]
    private static partial Regex BraceGroup();

    [GeneratedRegex("^(~|null|Null|NULL|true|True|TRUE|false|False|FALSE|[-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+|[-+]?(\\.[0-9]+|[0-9]+(\\.[0-9]*)?)([eE][-+]?[0-9]+)?|[-+]?\\.(inf|Inf|INF)|\\.(nan|NaN|NAN))?\\z")]
    private static partial Regex CoreSchemaNonString();

    /// <summary>The globs <paramref name="text"/>, a rule's text or as much of it as holds its frontmatter, is scoped to as the CLI normalises them; null when the CLI loads it unconditionally.</summary>
    public static IReadOnlyList<string>? Read(string text)
    {
        var fence = Fence().Match(Unmarked(text));

        if (!fence.Success) return null;

        var frontmatter = fence.Groups[1].Value;

        return Normalise(Expand(Strings(PathsOf(frontmatter), frontmatter.Length)));
    }

    /// <summary>The CLI's last step on a rule's globs (<c>dgs</c> in 2.1.263): a trailing <c>/**</c> dropped, an empty glob with it; null — unconditional — when none is left or nothing but <c>**</c>.</summary>
    public static IReadOnlyList<string>? Normalise(IEnumerable<string> globs)
    {
        var normalised = globs.Select(glob => glob.EndsWith("/**", StringComparison.Ordinal) ? glob[..^3] : glob).Where(glob => glob.Length > 0).ToList();

        return normalised.Count == 0 || normalised.All(glob => glob == "**") ? null : normalised;
    }

    /// <summary>Whether <paramref name="text"/>, the start of a rule, opens a fence it does not close — its opening line cut included — so that more of the rule could still change what <see cref="Read"/> makes of it.</summary>
    public static bool IsUnclosed(string text)
    {
        var unmarked = Unmarked(text);

        return Opening().IsMatch(unmarked) && !Fence().IsMatch(unmarked);
    }

    /// <summary>The text without the byte-order mark the CLI drops.</summary>
    private static string Unmarked(string text) => text.StartsWith('\ufeff') ? text[1..] : text;

    /// <summary>The frontmatter's <c>paths</c> node: from the YAML as written or, when that does not parse, as the CLI retries it; none when neither parses or the document is not one mapping.</summary>
    private static YamlNode? PathsOf(string yaml)
    {
        var parsed = TryParse(yaml, out var root) || TryParse(Untabbed(Requoted(yaml)), out root);

        return parsed && root is YamlMappingNode map ? map.Children.FirstOrDefault(child => child.Key is YamlScalarNode { Value: "paths" }).Value : null;
    }

    private static bool TryParse(string yaml, out YamlNode? root)
    {
        root = null;

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            root = stream.Documents.Count == 1 ? stream.Documents[0].RootNode : null;
            return true;
        }
        catch (YamlException)
        {
            return false;
        }
    }

    /// <summary>The CLI's retry: a top-level <c>key: value</c> whose value is unquoted, no flow list, and holds a YAML indicator becomes <c>key: "value"</c>.</summary>
    private static string Requoted(string yaml) => string.Join('\n', yaml.Split('\n').Select(Requote));

    private static string Requote(string line)
    {
        var pair = TopLevelPair().Match(line);

        if (!pair.Success) return line;

        var (key, value) = (pair.Groups[1].Value, pair.Groups[2].Value);

        if (IsQuoted(value) || IsFlowList(value) || !YamlIndicator().IsMatch(value)) return line;

        return $"{key}: \"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsQuoted(string value) => (value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''));

    private static bool IsFlowList(string value) => value.StartsWith('[') && value.EndsWith(']') && TryParse(value, out var node) && node is YamlSequenceNode;

    private static string Untabbed(string yaml) => LeadingTabs().Replace(yaml, tabs => new string(' ', 2 * tabs.Length));

    /// <summary>
    /// Every string <c>paths</c> holds, alone or in lists at any depth, in order; anything else holds none. At most one
    /// node is visited per byte of the frontmatter, <paramref name="bytes"/>, more than it holds without aliases: a list of
    /// lists of the same alias would otherwise be walked once per path through it, which grows exponentially with the
    /// nesting.
    /// </summary>
    private static List<string> Strings(YamlNode? node, int bytes)
    {
        var strings = new List<string>();
        var pending = new Stack<YamlNode>();
        var visits = 0;

        if (node is not null) pending.Push(node);

        while (pending.TryPop(out var current) && ++visits <= bytes)
        {
            if (current is YamlScalarNode scalar && IsString(scalar)) strings.Add(scalar.Value ?? "");

            if (current is YamlSequenceNode sequence)
            {
                for (var i = sequence.Children.Count - 1; i >= 0; i--) pending.Push(sequence.Children[i]);
            }
        }

        return strings;
    }

    /// <summary>Whether the CLI's parser reads the scalar as a string: an explicit <c>!!str</c> or a quoted or block scalar does; a plain one does unless the YAML 1.2 core schema types it as null, a boolean or a number.</summary>
    private static bool IsString(YamlScalarNode scalar)
    {
        if (!scalar.Tag.IsEmpty && !scalar.Tag.IsNonSpecific) return scalar.Tag.Value == "tag:yaml.org,2002:str";

        return scalar.Style != ScalarStyle.Plain || !CoreSchemaNonString().IsMatch(scalar.Value ?? "");
    }

    /// <summary>Every glob the strings name, in order: each split at its top-level commas, then brace-expanded against one shared budget.</summary>
    private static List<string> Expand(IEnumerable<string> values)
    {
        var budget = new BraceBudget { Results = BraceResults, Bytes = BraceBytes };

        return values.SelectMany(Split).ToList().SelectMany(pattern => Braces(pattern, budget)).ToList();
    }

    /// <summary>A string split at each comma outside braces, each piece trimmed, empty pieces dropped. A <c>}</c> before any <c>{</c> takes the depth below zero, and no comma splits there.</summary>
    private static List<string> Split(string value)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var c in value)
        {
            if (c == ',' && depth == 0)
            {
                AddTrimmed(pieces, current);
                continue;
            }

            depth += c switch { '{' => 1, '}' => -1, _ => 0 };
            current.Append(c);
        }

        AddTrimmed(pieces, current);
        return pieces;
    }

    private static void AddTrimmed(List<string> pieces, StringBuilder current)
    {
        var piece = current.ToString().Trim(JsSpaceChars);

        if (piece.Length > 0) pieces.Add(piece);

        current.Clear();
    }

    /// <summary>
    /// One pattern's brace expansion as the CLI does it: the first group of each pending pattern replaced by each of its
    /// comma-separated alternatives until none is left, innermost text kept as written. Past the budget the pattern
    /// stays as it is, and what was already spent of the budget stays spent.
    /// </summary>
    private static List<string> Braces(string pattern, BraceBudget budget)
    {
        if (!pattern.Contains('{')) return [pattern];

        var done = new List<string>();
        var pending = new Stack<string>();

        pending.Push(pattern);

        while (pending.TryPop(out var current))
        {
            var group = BraceGroup().Match(current);

            if (!group.Success)
            {
                done.Add(current);
                continue;
            }

            var alternatives = group.Groups[2].Value.Split(',').Select(alternative => alternative.Trim(JsSpaceChars)).ToList();

            budget.Bytes -= current.Length;

            var count = done.Count + pending.Count + alternatives.Count;

            if (budget.Bytes < 0 || count > budget.Results || (long)count * pattern.Length > budget.Bytes) return [pattern];

            for (var i = alternatives.Count - 1; i >= 0; i--) pending.Push(group.Groups[1].Value + alternatives[i] + group.Groups[3].Value);
        }

        budget.Results -= done.Count;
        budget.Bytes -= (long)done.Count * pattern.Length;
        return done;
    }

    private sealed class BraceBudget
    {
        public long Results { get; set; }

        public long Bytes { get; set; }
    }
}
