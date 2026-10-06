using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the mirror of how the pinned CLI reads a <c>.claude/rules</c> file's <c>paths:</c> (<see cref="ClaudeRuleScope"/>):
/// a rule it reads with no globs loads from an <c>--add-dir</c> up front, so it is what gives a nested directory memory
/// to load in place and what that directory's bytes are counted from. Each expectation is what 2.1.263's own frontmatter
/// reader and glob normaliser make of the text, quirks included — the closing fence is the next <c>---</c> anywhere, a
/// brace group closes at its first <c>}</c>, a plain <c>2024</c> is a number.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ClaudeRuleScopeTests
{
    [Theory]
    [InlineData("a list", "---\npaths:\n  - \"src/**/*.ts\"\n  - lib/x\n---\nRule.\n", "src/**/*.ts|lib/x")]
    [InlineData("a string", "---\npaths: docs/*.md\n---\nRule.\n", "docs/*.md")]
    [InlineData("a string split at its commas", "---\npaths: \"docs/x, y\"\n---\nRule.\n", "docs/x|y")]
    [InlineData("a brace group", "---\npaths: \"{lib/a,b}\"\n---\nRule.\n", "lib/a|b")]
    [InlineData("an unquoted brace group the retry quotes", "---\npaths: {src,lib}/**\n---\nRule.\n", "src|lib")]
    [InlineData("an unquoted leading ** the retry quotes", "---\npaths: **/*.ts\n---\nRule.\n", "**/*.ts")]
    [InlineData("nested braces, closed at the first }", "---\npaths: \"{a,{b,c}}\"\n---\nRule.\n", "a}|b|c}")]
    [InlineData("a trailing /** dropped", "---\npaths: src/**\n---\nRule.\n", "src")]
    [InlineData("tabs the retry turns into spaces", "---\npaths:\n\t- src/a\n---\nRule.\n", "src/a")]
    [InlineData("a list of lists", "---\npaths:\n  - [a, [b]]\n---\nRule.\n", "a|b")]
    [InlineData("a quoted number", "---\npaths: '2024'\n---\nRule.\n", "2024")]
    [InlineData("an explicit string tag", "---\npaths: !!str 5\n---\nRule.\n", "5")]
    [InlineData("numbers beside a string in a list", "---\npaths: [1, true, src]\n---\nRule.\n", "src")]
    [InlineData("a closing fence inside a value", "---\npaths: a---b\n---\nRule.\n", "a")]
    [InlineData("a byte-order mark", "\ufeff---\npaths: src\n---\nRule.\n", "src")]
    [InlineData("CRLF line ends", "---\r\npaths: src\r\n---\r\nRule.\r\n", "src")]
    [InlineData("an unclosed flow list the retry quotes", "---\npaths: [a\n---\nRule.\n", "[a")]
    public void A_rule_whose_paths_name_a_glob_is_scoped_to_what_the_cli_reads(string shape, string text, string globs) =>
        ClaudeRuleScope.Read(text).ShouldBe(globs.Split('|'), shape);

    [Theory]
    [InlineData("no frontmatter", "Always run the tests.\n")]
    [InlineData("frontmatter without paths", "---\ndescription: style\n---\nRule.\n")]
    [InlineData("paths of ** only", "---\npaths: [\"**\", \"**/**\"]\n---\nRule.\n")]
    [InlineData("paths of /** only", "---\npaths: /**\n---\nRule.\n")]
    [InlineData("empty paths", "---\npaths:\n---\nRule.\n")]
    [InlineData("an empty list", "---\npaths: []\n---\nRule.\n")]
    [InlineData("a plain number", "---\npaths: 2024\n---\nRule.\n")]
    [InlineData("a boolean", "---\npaths: true\n---\nRule.\n")]
    [InlineData("a mapping", "---\npaths:\n  src: yes\n---\nRule.\n")]
    [InlineData("an empty string", "---\npaths: \"\"\n---\nRule.\n")]
    [InlineData("only commas", "---\npaths: \" , ,\"\n---\nRule.\n")]
    [InlineData("YAML that does not parse even retried", "---\npaths:\n  - a\n - b\n---\nRule.\n")]
    [InlineData("two documents", "---\na: 1\n...\n---\nb: 2\n")]
    [InlineData("an unclosed fence", "---\npaths: src\nRule.\n")]
    [InlineData("a fence not at the start", "\n---\npaths: src\n---\nRule.\n")]
    public void A_rule_the_cli_reads_without_globs_is_unconditional(string shape, string text) =>
        ClaudeRuleScope.Read(text).ShouldBeNull(shape);

    [Theory]
    [InlineData("a fence opened and not closed", "---\npaths: src\n", true)]
    [InlineData("an opening line cut short", "---   ", true)]
    [InlineData("a fence opened after a byte-order mark", "\ufeff---\npaths: src\n", true)]
    [InlineData("a closed fence", "---\npaths: src\n---\n", false)]
    [InlineData("no fence", "Always run the tests.\n", false)]
    [InlineData("four dashes", "----\npaths: src\n", false)]
    public void A_head_that_opens_a_fence_it_does_not_close_is_unclosed(string shape, string head, bool unclosed) =>
        ClaudeRuleScope.IsUnclosed(head).ShouldBe(unclosed, shape);

    [Fact]
    public void A_frontmatter_that_runs_past_the_head_reads_as_none_until_it_is_read_to_its_close()
    {
        // Why the walk reads on past an unclosed head: cut there, a scoped rule would read as unconditional.
        var text = $"---\npaths: src\ndescription: {new string('x', ClaudeRuleScope.MaxHeadBytes)}\n---\nRule.\n";

        ClaudeRuleScope.Read(text[..ClaudeRuleScope.MaxHeadBytes]).ShouldBeNull("fixture check: cut at the head, the rule has no closing fence");
        ClaudeRuleScope.IsUnclosed(text[..ClaudeRuleScope.MaxHeadBytes]).ShouldBeTrue();
        ClaudeRuleScope.Read(text).ShouldBe(new[] { "src" });
    }

    [Fact]
    public void A_long_list_of_paths_is_read_whole()
    {
        // The alias bound is one node per frontmatter byte, not per head byte, so a frontmatter past the head keeps every glob.
        var globs = Enumerable.Range(0, 3000).Select(i => $"g{i}").ToList();

        ClaudeRuleScope.Read($"---\npaths:\n{string.Concat(globs.Select(glob => $"  - {glob}\n"))}---\nRule.\n").ShouldBe(globs);
    }

    [Fact]
    public async Task Aliases_nested_inside_each_other_are_walked_a_bounded_number_of_times()
    {
        // Eight levels of eight aliases each name 8^8 paths through the same few nodes; walked naively, that never ends.
        var levels = Enumerable.Range(0, 8).Select(level => level == 0 ? "l0: &l0 [src]" : $"l{level}: &l{level} [{string.Join(", ", Enumerable.Repeat($"*l{level - 1}", 8))}]");
        var text = $"---\n{string.Join('\n', levels)}\npaths: *l7\n---\nRule.\n";

        var globs = await Task.Run(() => ClaudeRuleScope.Read(text)).WaitAsync(TimeSpan.FromSeconds(5));

        globs.ShouldNotBeNull().ShouldAllBe(glob => glob == "src");
    }

    [Fact]
    public void A_brace_pattern_past_the_clis_budget_stays_as_it_is()
    {
        // 1001 alternatives is past the CLI's 1000-glob budget, so the pattern is kept unexpanded, as the CLI keeps it.
        static string Alternatives(int count) => "{" + string.Join(",", Enumerable.Repeat("a", count)) + "}";

        ClaudeRuleScope.Read($"---\npaths: \"{Alternatives(1001)}\"\n---\nRule.\n").ShouldBe(new[] { Alternatives(1001) });
        ClaudeRuleScope.Read($"---\npaths: \"{Alternatives(1000)}\"\n---\nRule.\n").ShouldBe(Enumerable.Repeat("a", 1000), "fixture check: 1000 alternatives expand");
    }

    [Fact]
    public void The_head_bound_is_pinned() =>
        // A committed value, changed by PR: how much of each rule the walk reads to classify it.
        ClaudeRuleScope.MaxHeadBytes.ShouldBe(4096);
}
