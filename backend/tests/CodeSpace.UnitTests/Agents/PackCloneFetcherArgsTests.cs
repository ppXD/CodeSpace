using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the hardened clone argv (Rule 8): the redirect guard must be present so an allowlisted host can't 30x-bounce
/// the clone's egress to an internal host (a transport-layer bypass below the URL-host allowlist), and the
/// end-of-options <c>--</c> must precede the url so a url/ref beginning with <c>-</c> can't smuggle a git flag.
/// </summary>
[Trait("Category", "Unit")]
public class PackCloneFetcherArgsTests
{
    [Fact]
    public void Clone_argv_pins_the_redirect_guard_and_shallow_clone()
    {
        var args = PackCloneFetcher.BuildCloneArgs("https://github.com/owner/repo", reference: null, dir: "/tmp/x");

        // The redirect guard rides as a `-c key=value` BEFORE the clone subcommand.
        var cfgIndex = args.ToList().IndexOf("-c");
        cfgIndex.ShouldBeGreaterThanOrEqualTo(0);
        args[cfgIndex + 1].ShouldBe("http.followRedirects=false", "without this an allowlisted host can redirect egress to an internal host");

        args.ShouldContain("clone");
        args.ShouldContain("--depth");
    }

    [Fact]
    public void Clone_argv_ends_options_before_the_url_so_a_dash_url_cannot_smuggle_a_flag()
    {
        var args = PackCloneFetcher.BuildCloneArgs("https://github.com/owner/repo", reference: null, dir: "/tmp/dest").ToList();

        var dashDash = args.IndexOf("--");
        dashDash.ShouldBeGreaterThanOrEqualTo(0);
        args[dashDash + 1].ShouldBe("https://github.com/owner/repo", "the url is the first positional after --");
        args[dashDash + 2].ShouldBe("/tmp/dest");
    }

    [Fact]
    public void Clone_argv_passes_a_branch_reference_when_set()
    {
        var args = PackCloneFetcher.BuildCloneArgs("https://github.com/owner/repo", reference: "main", dir: "/tmp/dest").ToList();

        var branch = args.IndexOf("--branch");
        branch.ShouldBeGreaterThanOrEqualTo(0);
        args[branch + 1].ShouldBe("main");
        branch.ShouldBeLessThan(args.IndexOf("--"), "--branch is an option, so it precedes the end-of-options marker");
    }

    [Fact]
    public void Clone_argv_omits_branch_when_no_reference()
    {
        PackCloneFetcher.BuildCloneArgs("https://github.com/owner/repo", reference: null, dir: "/tmp/dest")
            .ShouldNotContain("--branch", "null reference = clone the default branch");
    }
}
