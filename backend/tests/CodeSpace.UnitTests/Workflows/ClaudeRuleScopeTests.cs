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

    // ── Rebasing a rule's globs onto the cwd, for a pointer rule ──

    [Theory]
    [InlineData("", "src/**/*.ts", "src/**/*.ts")]          // the rule's directory is the cwd: as written
    [InlineData("", "*.ts", "*.ts")]
    [InlineData("", "/top", "/top")]
    [InlineData("", "!gen", "!gen")]
    [InlineData("a", "*.ts", "/a/**/*.ts")]                 // no slash: any depth below the rule's directory
    [InlineData("a", "src", "/a/**/src")]
    [InlineData("a", "src/", "/a/**/src/")]                 // a trailing slash alone anchors nothing
    [InlineData("a", "x?.ts", "/a/**/x?.ts")]
    [InlineData("a", "**", "/a/**/**")]
    [InlineData("a", "lib/a", "/a/lib/a")]                  // a slash in the middle: below the rule's directory only
    [InlineData("a", "/x", "/a/x")]                         // a leading slash: the same
    [InlineData("a", "/x/", "/a/x/")]
    [InlineData("a", "**/x", "/a/**/x")]
    [InlineData("a", "!gen", "!/a/**/gen")]                 // a negation keeps negating
    [InlineData("a", "!lib/gen", "!/a/lib/gen")]
    [InlineData("pkg/sub", "x/*.md", "/pkg/sub/x/*.md")]
    [InlineData("repo-1/pkg_v1.2+x", "*.md", "/repo-1/pkg_v1.2+x/**/*.md")]
    public void A_glob_is_rebased_onto_the_cwd_as_gitignore_anchors_it(string below, string glob, string rebased) =>
        ClaudeRuleScope.Rebase(below, glob).ShouldBe(rebased);

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a'b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a`b")]
    [InlineData("a:b")]
    [InlineData("#a")]
    [InlineData("a#b")]
    [InlineData("[ab]")]
    [InlineData("{a,b}")]
    [InlineData("a,b")]
    [InlineData("@scope/x")]
    [InlineData("a/@b")]
    [InlineData("~/x")]
    [InlineData("$HOME")]
    [InlineData("a|b")]
    [InlineData("a>b")]
    [InlineData("a%b")]
    [InlineData("a&b")]
    [InlineData("na\u00efve")]
    [InlineData("a!b")]
    [InlineData("!!a")]
    [InlineData("!")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//abs")]
    [InlineData("a//b")]
    [InlineData("x//")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("!../x")]
    [InlineData(".")]
    [InlineData("./x")]
    [InlineData("a/./b")]
    public void A_glob_a_pointer_cannot_carry_exactly_is_refused(string glob)
    {
        ClaudeRuleScope.Rebase("a", glob).ShouldBeNull(glob);
        ClaudeRuleScope.Rebase("", glob).ShouldBeNull(glob);
    }

    [Theory]
    [InlineData("my pkg")]
    [InlineData("@scope/x")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    public void A_directory_a_pointer_cannot_name_exactly_is_refused(string below) =>
        ClaudeRuleScope.Rebase(below, "*.ts").ShouldBeNull(below);

    [Fact]
    public void Every_rebased_rule_reads_back_through_the_clis_own_parse_scoped_and_as_written()
    {
        // The output invariant: a pointer's frontmatter, read the way the CLI reads it, gives back exactly the rebased
        // globs — never none, never ** alone (which would load the pointer before the first request at the user's
        // authority), never with a trailing /** the CLI drops, which would change what a glob anchors.
        var rules = new[]
        {
            "---\npaths:\n  - \"src/**/*.ts\"\n  - lib/x\n---\n",
            "---\npaths: \"*.ts\"\n---\n",
            "---\npaths: src/**\n---\n",
            "---\npaths: src/**/**\n---\n",
            "---\npaths: [\"**\", \"!gen\"]\n---\n",
            "---\npaths: \"{lib/a,b}\"\n---\n",
            "---\npaths: \"docs/x, y\"\n---\n",
            "---\npaths:\n  - \"gen/**/*.txt\"\n  - \"!gen/keep/*.txt\"\n---\n",
            "---\npaths: [\"/top/**\", \"**/\", \"x?.md\", \"a/\"]\n---\n",
            "---\npaths: '2024'\n---\n",
        };

        foreach (var rule in rules)
        {
            var globs = ClaudeRuleScope.Read(rule).ShouldNotBeNull($"fixture check: {rule} is scoped");

            foreach (var below in new[] { "", "a", "repo-1/pkg", "x.y/z_w+v-u" })
            {
                var rebased = globs.Select(glob => ClaudeRuleScope.Rebase(below, glob).ShouldNotBeNull($"{glob} below '{below}'")).ToList();

                ClaudeRuleScope.Read(ClaudeRuleScope.Frontmatter(rebased) + "Body.\n").ShouldBe(rebased, $"{rule} below '{below}'");
            }
        }
    }

    [Fact]
    public void A_glob_ending_in_slash_star_star_is_written_with_one_more_so_the_cli_reads_it_as_meant()
    {
        // src/**/** reads as src/** — anchored to the rule's directory. Written as src/**, the CLI would drop that /**
        // too and read src, which matches a src directory at any depth.
        var globs = ClaudeRuleScope.Read("---\npaths: src/**/**\n---\n").ShouldNotBeNull();

        globs.ShouldBe(new[] { "src/**" }, "fixture check");
        ClaudeRuleScope.Frontmatter(globs).ShouldBe("---\npaths:\n  - \"src/**/**\"\n---\n");
        ClaudeRuleScope.Normalise(new[] { "src/**/**" }).ShouldBe(new[] { "src/**" });
    }

    [Theory]
    [InlineData("**")]
    [InlineData("**/**|/**")]
    [InlineData("|")]
    [InlineData("")]
    public void Globs_the_cli_normalises_to_nothing_or_star_star_alone_are_unconditional(string globs) =>
        ClaudeRuleScope.Normalise(globs.Length == 0 ? [] : globs.Split('|')).ShouldBeNull();
}
