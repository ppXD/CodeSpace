using System.Text.RegularExpressions;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// Text the supervisor's prompt carries from somewhere other than the server — an agent's closing summary, a
/// harness-reported error, a reviewer's critique quoting the artifact — rendered so that no line of it can stand at
/// the server's own indent. The prompt's verdict lines (<c>      acceptance PASSED …</c>, <c>(server) …</c>) are plain
/// lines at a fixed indent, so an agent that wrote one into its summary used to render a line byte-identical to the
/// server's verdict above the real one. Every line of such text now sits behind <see cref="LinePrefix"/>, whatever
/// break the author used to start it: a model reads <c>\r</c>, U+2028, U+2029, U+0085, a vertical tab and a form
/// feed as new lines too, so splitting on <c>\n</c> alone would leave the forgery one break away.
/// </summary>
internal static partial class AgentReportedText
{
    /// <summary>The data prefix every fenced line carries — the same one the oracle's evidence tail renders behind, so the prompt has one shape for "evidence, not instructions".</summary>
    internal const string LinePrefix = "        | ";

    /// <summary>Every line of <paramref name="text"/> behind <see cref="LinePrefix"/>, joined with <see cref="Environment.NewLine"/> like every other line of the prompt.</summary>
    internal static string Fenced(string text) => string.Join(Environment.NewLine, AnyLineBreak().Split(text).Select(line => LinePrefix + line));

    /// <summary><paramref name="text"/> on one line — every break replaced by a space — for a renderer that quotes it inside a sentence of its own.</summary>
    internal static string OneLine(string text) => AnyLineBreak().Replace(text, " ");

    [GeneratedRegex("\r\n|[\n\r\u2028\u2029\u0085\v\f]")]
    private static partial Regex AnyLineBreak();
}
