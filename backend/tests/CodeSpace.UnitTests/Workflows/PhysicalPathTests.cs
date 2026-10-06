using CodeSpace.Core.Services.Agents.Harnesses;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="PhysicalPath"/>: where the kernel really lands when it opens a path. <see cref="PhysicalPath.Directory"/>
/// keeps its Codex distrust coverage in <c>CodexHarnessTests</c>; these pin <see cref="PhysicalPath.File"/>, which the
/// Claude memory guard decides containment by, and <see cref="PhysicalPath.StaysInside"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PhysicalPathTests
{
    [Theory]
    [InlineData("/w/ws", "/w/ws", true)]          // the root itself
    [InlineData("/w/ws", "/w/ws/a/b.md", true)]   // below it
    [InlineData("/w/ws/", "/w/ws/a.md", true)]    // a root spelled with its trailing separator
    [InlineData("/w/ws", "/w/ws2/a.md", false)]   // a sibling that shares its prefix
    [InlineData("/w/ws", "/w/a.md", false)]       // above it
    [InlineData("/w/ws", "/w", false)]            // its parent
    [InlineData("/", "/etc/passwd", true)]        // the filesystem root holds everything
    public void StaysInside_is_the_root_or_below_it(string root, string path, bool inside) =>
        PhysicalPath.StaysInside(root, path).ShouldBe(inside);

    [Fact]
    public void A_chain_of_links_resolves_to_where_the_last_one_points()
    {
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        var target = tree.File("real/target.md", "x");
        tree.Link("a/second.md", "../real/target.md");
        var first = tree.Link("first.md", "a/second.md");

        PhysicalPath.File(first).ShouldBe(PhysicalPath.File(target));
        PhysicalPath.File(target).ShouldBe(Path.Combine(PhysicalPath.Directory(tree.Root), "real", "target.md"), "a plain file is where its directory really is");
    }

    [Fact]
    public void A_dotdot_in_a_link_target_climbs_from_where_the_link_really_is()
    {
        // deep → outside/p/q, so deep/../x.md is outside/p/x.md. Read as text it would be the x.md beside deep, which
        // exists too — the trap a lexical resolve falls into.
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        tree.Directory("outside/p/q");
        var outside = tree.File("outside/p/x.md", "OUTSIDE");
        tree.File("ws/x.md", "INSIDE");
        tree.Link("ws/deep", Path.Combine(tree.Root, "outside", "p", "q"));
        var link = tree.Link("ws/CLAUDE.md", "deep/../x.md");

        PhysicalPath.File(link).ShouldBe(PhysicalPath.File(outside));
    }

    [Fact]
    public void A_dotdot_after_a_linked_component_of_a_link_target_climbs_from_where_that_component_really_is()
    {
        // data → <root>/srv/link/../disk with srv/link → x/y: the kernel takes srv/link to x/y and climbs from there, so
        // data/ws is x/disk/ws. Read as text, the target would be srv/disk, which does not exist.
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        tree.Directory("x/y");
        var real = tree.Directory("x/disk/ws");
        tree.Link("srv/link", Path.Combine(tree.Root, "x", "y"));
        tree.Link("data", Path.Combine(tree.Root, "srv", "link", "..", "disk"));

        PhysicalPath.File(Path.Combine(tree.Root, "data", "ws")).ShouldBe(PhysicalPath.File(real));
        PhysicalPath.File(real).ShouldBe(Path.Combine(PhysicalPath.Directory(tree.Root), "x", "disk", "ws"));
    }

    [Fact]
    public void A_link_that_dangles_names_nothing()
    {
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        var link = tree.Link("CLAUDE.md", Path.Combine(tree.Root, "missing.md"));

        PhysicalPath.File(link).ShouldBeNull();
        PhysicalPath.File(Path.Combine(tree.Root, "never-written.md")).ShouldBeNull();
    }

    [Fact]
    public void A_link_that_loops_names_nothing()
    {
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        tree.Link("b.md", "a.md");
        var a = tree.Link("a.md", "b.md");

        PhysicalPath.File(a).ShouldBeNull("the kernel refuses a loop with ELOOP, so it reaches no file");
    }

    [Fact]
    public void A_path_through_a_file_names_nothing()
    {
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        var file = tree.File("plain.md", "x");

        PhysicalPath.File(Path.Combine(file, "CLAUDE.md")).ShouldBeNull("the kernel refuses a file used as a directory with ENOTDIR");
    }

    [Fact]
    public void A_directory_resolves_like_a_file()
    {
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        var real = tree.Directory("real");
        var link = tree.Link("link", real);

        PhysicalPath.File(link).ShouldBe(PhysicalPath.Directory(real));
    }
}
