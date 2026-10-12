using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local.Legacy;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.Local;

/// <summary>
/// The namespace identity of a filesystem destination is the directory its root REACHES, not the text an operator
/// typed. Every local driver opens its root through <c>Path.GetFullPath</c> and the operating system, so every spelling
/// below lands on one directory — and object keys carry no team segment, so two profiles naming that directory share
/// every object they both store. An identity that told two of these spellings apart let one team's retention sweep
/// delete another team's live bytes.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalStorageNamespaceTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "codespace-namespace-unit", Guid.NewGuid().ToString("N"));

    public LocalStorageNamespaceTests() => Directory.CreateDirectory(_scratch);

    public static TheoryData<string, string> ModulesAndSpellings()
    {
        var data = new TheoryData<string, string>();
        foreach (var module in new[] { "local-rwx", "local-legacy" })
            foreach (var spelling in new[] { "as-written", "trailing-slash", "dot-segment", "doubled-slash", "doubled-inner-slash", "parent-segment", "relative" })
                data.Add(module, spelling);
        return data;
    }

    [Theory]
    [MemberData(nameof(ModulesAndSpellings))]
    public void Every_spelling_of_an_existing_root_projects_to_one_namespace(string module, string spelling)
    {
        var root = Path.Combine(_scratch, "victim-root");
        Directory.CreateDirectory(root);

        Projection(module, Spell(root, spelling)).ShouldBe(Projection(module, root),
            $"'{Spell(root, spelling)}' opens the very directory '{root}' does, so it must be the same namespace");
    }

    [Theory]
    [MemberData(nameof(ModulesAndSpellings))]
    public void Every_spelling_of_a_root_that_does_not_exist_yet_projects_to_one_namespace(string module, string spelling)
    {
        // The directory the first write creates is the same whichever spelling created it, so the identity cannot wait
        // for the directory to exist before it agrees with itself.
        var root = Path.Combine(_scratch, "not-created-yet", "artifacts");

        Projection(module, Spell(root, spelling)).ShouldBe(Projection(module, root));
    }

    [Fact]
    public void A_symbolic_link_to_a_root_is_that_root()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateDirectory(Path.Combine(_scratch, "real-root")).FullName;
        var link = Path.Combine(_scratch, "alias-root");
        Directory.CreateSymbolicLink(link, root);

        Projection("local-rwx", link).ShouldBe(Projection("local-rwx", root), "every byte written through the link lands in the directory it points at");
    }

    [Fact]
    public void A_directory_beneath_a_linked_parent_that_does_not_exist_yet_is_beneath_the_link_target()
    {
        if (OperatingSystem.IsWindows()) return;

        var parent = Directory.CreateDirectory(Path.Combine(_scratch, "real-parent")).FullName;
        var link = Path.Combine(_scratch, "alias-parent");
        Directory.CreateSymbolicLink(link, parent);

        Projection("local-rwx", Path.Combine(link, "team-a")).ShouldBe(Projection("local-rwx", Path.Combine(parent, "team-a")));
    }

    [Fact]
    public void A_relative_link_resolves_against_the_directory_that_holds_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateDirectory(Path.Combine(_scratch, "relative-target")).FullName;
        var holder = Directory.CreateDirectory(Path.Combine(_scratch, "holder")).FullName;
        var link = Path.Combine(holder, "alias");
        Directory.CreateSymbolicLink(link, Path.Combine("..", "relative-target"));

        Projection("local-rwx", link).ShouldBe(Projection("local-rwx", root));
    }

    [Fact]
    public void A_link_target_that_climbs_out_of_another_link_climbs_from_where_that_link_landed()
    {
        // The operating system follows 'a' BEFORE it applies the '..' after it, so 'holder/L' -> 'a/..' with
        // 'holder/a' -> 'victim-root/objects' opens 'victim-root'. Collapsing 'a/..' as text first would name 'holder'
        // instead — a directory nothing is written to — and let a sweep through L delete the victim's bytes.
        if (OperatingSystem.IsWindows()) return;

        var victim = Directory.CreateDirectory(Path.Combine(_scratch, "victim-root", "objects")).Parent!.FullName;
        var holder = Directory.CreateDirectory(Path.Combine(_scratch, "holder")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(holder, "a"), Path.Combine(victim, "objects"));
        var link = Path.Combine(holder, "L");
        Directory.CreateSymbolicLink(link, Path.Combine("a", ".."));

        File.WriteAllText(Path.Combine(victim, "marker"), "reached");
        File.Exists(Path.Combine(link, "marker")).ShouldBeTrue("the precondition: the operating system opens the victim's root through L");

        Projection("local-rwx", link).ShouldBe(Projection("local-rwx", victim));
    }

    [Fact]
    public void A_link_cycle_has_no_identity_rather_than_an_invented_one()
    {
        // A root that never resolves cannot be compared with anything. Refusing it is what lets every caller fall back
        // to treating it as possibly the same as anything else.
        if (OperatingSystem.IsWindows()) return;

        var first = Path.Combine(_scratch, "loop-a");
        var second = Path.Combine(_scratch, "loop-b");
        Directory.CreateSymbolicLink(first, second);
        Directory.CreateSymbolicLink(second, first);

        Should.Throw<ArgumentException>(() => Projection("local-rwx", Path.Combine(first, "artifacts")));
    }

    [Fact]
    public void Two_different_directories_stay_two_namespaces()
    {
        // The guard on the guard: an identity that merged everything would keep every deduplicated byte forever.
        Projection("local-rwx", Path.Combine(_scratch, "team-a")).ShouldNotBe(Projection("local-rwx", Path.Combine(_scratch, "team-b")));
    }

    [Fact]
    public void Letter_case_is_folded_exactly_where_the_platform_filesystem_ignores_it()
    {
        var lower = Projection("local-rwx", Path.Combine(_scratch, "casing", "artifacts"));
        var upper = Projection("local-rwx", Path.Combine(_scratch, "casing", "ARTIFACTS"));

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) upper.ShouldBe(lower, "a case-insensitive filesystem opens one directory for both");
        else upper.ShouldNotBe(lower, "Linux compares names byte for byte, so these are two directories");
    }

    [Theory]
    [InlineData("local-rwx")]
    [InlineData("local-legacy")]
    public void The_projection_carries_the_root_and_nothing_else(string module)
    {
        var projection = JsonDocument.Parse(Projection(module, Path.Combine(_scratch, "only-root"))).RootElement;

        projection.EnumerateObject().Select(property => property.Name).ShouldBe(["rootPath"]);
    }

    [Theory]
    [InlineData("local-rwx")]
    [InlineData("local-legacy")]
    public void A_root_already_written_canonically_keeps_the_fingerprint_its_revision_stored(string module)
    {
        // Revisions are immutable and every one written before this projection existed stored the hash of its raw
        // configuration. Projecting a canonical root to itself is what keeps those stored values true.
        if (OperatingSystem.IsWindows()) return;

        var subject = Module(module);
        var canonical = Projection(module, Path.Combine(_scratch, "stable"));
        using var stored = JsonDocument.Parse(canonical);

        StorageProfileRules.NamespaceFingerprint(subject, stored.RootElement).ShouldBe(StorageProfileRules.NamespaceFingerprint(subject.TypeKey, stored.RootElement));
    }

    [Fact]
    public void The_fingerprint_of_a_canonical_production_root_is_byte_identical_to_the_one_already_stored()
    {
        // Hard-pinned: this is the value a local-rwx revision naming /srv/codespace/artifacts has carried since the
        // fingerprint was introduced. A change here is a change to every stored revision's meaning, not a refactor.
        if (OperatingSystem.IsWindows()) return;

        using var config = JsonDocument.Parse("""{"rootPath":"/srv/codespace/artifacts"}""");

        StorageProfileRules.NamespaceFingerprint(new LocalRwxStorageProviderModule(), config.RootElement)
            .ShouldBe("sha256:d8df91c032f0a9ea898c31eee26d176f30992458f399d57623dafe52e78c6a24");
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"rootPath":42}""")]
    [InlineData("""{"rootPath":"   "}""")]
    [InlineData("""[]""")]
    public void A_configuration_with_no_readable_root_has_no_identity(string config)
    {
        using var document = JsonDocument.Parse(config);

        Should.Throw<ArgumentException>(() => Module("local-rwx").GetNamespaceConfiguration(document.RootElement));
        Should.Throw<ArgumentException>(() => Module("local-legacy").GetNamespaceConfiguration(document.RootElement));
    }

    // ─── The physical object a key reaches ───────────────────────────────────

    public static TheoryData<string> ObjectReachingShapes() => ["as-written", "trailing-slash", "symlink-root", "dotdot-through-link", "objects-link", "nested-link"];

    [Theory]
    [MemberData(nameof(ObjectReachingShapes))]
    public void Every_path_the_operating_system_follows_to_one_file_is_one_object(string shape)
    {
        // The driver opens <root>/objects/<key> and the operating system follows a link ANYWHERE along it, so a root
        // the collector owns outright can still reach the victim's file through a link planted beneath it.
        if (OperatingSystem.IsWindows()) return;

        var victim = Directory.CreateDirectory(Path.Combine(_scratch, "victim-root")).FullName;
        var file = PlaceObject(victim);
        var collector = Reach(victim, shape);

        File.Exists(Path.Combine([collector, "objects", .. ObjectKey.Split('/')])).ShouldBeTrue($"the precondition: the operating system opens {file} through '{collector}'");

        ObjectIdentity(collector).ShouldBe(ObjectIdentity(victim), $"'{collector}' reaches the very file '{victim}' does, so deleting either deletes both");
    }

    [Fact]
    public void Every_spelling_of_a_root_whose_object_does_not_exist_yet_reaches_one_object()
    {
        var root = Path.Combine(_scratch, "not-created-yet", "artifacts");

        ObjectIdentity(root + "/.").ShouldBe(ObjectIdentity(root));
        ObjectIdentity(Path.GetRelativePath(Environment.CurrentDirectory, root)).ShouldBe(ObjectIdentity(root));
    }

    [Fact]
    public void Two_roots_hold_two_objects_at_one_key()
    {
        // The guard on the guard: content addressing gives every team storing these bytes this key, so merging two real
        // directories would keep every deduplicated byte forever.
        var first = Directory.CreateDirectory(Path.Combine(_scratch, "team-a")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_scratch, "team-b")).FullName;
        PlaceObject(first);
        PlaceObject(second);

        ObjectIdentity(first).ShouldNotBe(ObjectIdentity(second));
    }

    [Fact]
    public void A_key_that_is_itself_a_link_is_its_own_object()
    {
        // A delete unlinks the NAME it is given, so a key that is a link to the victim's file removes the link and
        // leaves the file. Following it would merge two objects the driver keeps apart — the safe direction, but a
        // reaper that could never collect a planted link would be a reaper anyone can stall.
        if (OperatingSystem.IsWindows()) return;

        var victim = Directory.CreateDirectory(Path.Combine(_scratch, "victim-root")).FullName;
        var collector = Directory.CreateDirectory(Path.Combine(_scratch, "collector-root")).FullName;
        var file = PlaceObject(victim);
        var name = Path.Combine([collector, "objects", .. ObjectKey.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(name)!);
        File.CreateSymbolicLink(name, file);

        ObjectIdentity(collector).ShouldNotBe(ObjectIdentity(victim));
    }

    [Fact]
    public async Task The_object_identity_names_the_file_the_real_driver_writes_and_deletes()
    {
        // Drift detector: the identity re-states the driver's layout (<root>/objects/<key>). If the driver ever moves
        // its objects, this is the test that notices before a sweep compares two paths neither of which is real.
        if (OperatingSystem.IsWindows()) return;

        var victim = Directory.CreateDirectory(Path.Combine(_scratch, "victim-root", "objects")).Parent!.FullName;
        var collector = Reach(victim, "objects-link");
        await using var driver = new LocalRwxArtifactStorageDriver(collector);
        await using var content = new MemoryStream("driver-written"u8.ToArray());

        var put = await driver.PutAsync(new ArtifactStoragePutRequest(ObjectKey, content) { Condition = ArtifactStorageWriteCondition.CreateOnly }, CancellationToken.None);

        put.IsSuccess.ShouldBeTrue(put.Error?.Message);

        File.Exists(ObjectIdentity(victim)).ShouldBeTrue("the identity must name the file the driver wrote, through the link, into the victim's root");
        (await driver.DeleteAsync(new ArtifactStorageDeleteRequest(ObjectKey), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        File.Exists(ObjectIdentity(victim)).ShouldBeFalse("and a delete through the collector's root removes exactly that file");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/workflow-artifacts/aa/bb/object")]
    [InlineData("workflow-artifacts/../../escape")]
    [InlineData("./workflow-artifacts/object")]
    [InlineData("workflow-artifacts/a\0b")]
    public void A_key_the_driver_would_refuse_has_no_identity(string objectKey)
    {
        using var config = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath = _scratch }));

        Should.Throw<ArgumentException>(() => new LocalRwxStorageProviderModule().PhysicalObject(config.RootElement, objectKey));
    }

    private const string ObjectKey = "workflow-artifacts/ab/cd/abcd0000000000000000000000000000000000000000000000000000000000ff";

    /// <summary>Writes the object where the local-rwx driver keeps it: <c>&lt;root&gt;/objects/&lt;key&gt;</c>.</summary>
    private static string PlaceObject(string root)
    {
        var file = Path.Combine([root, "objects", .. ObjectKey.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "victim bytes");
        return file;
    }

    /// <summary>A root the collector could name that the operating system resolves into <paramref name="victim"/>'s objects.</summary>
    private string Reach(string victim, string shape)
    {
        var collector = Path.Combine(_scratch, $"collector-{shape}");

        switch (shape)
        {
            case "as-written": return victim;
            case "trailing-slash": return victim + "/";
            case "symlink-root":
                Directory.CreateSymbolicLink(collector, victim);
                return collector;
            case "dotdot-through-link":
                Directory.CreateDirectory(collector);
                Directory.CreateSymbolicLink(Path.Combine(collector, "a"), Path.Combine(victim, "objects"));
                Directory.CreateSymbolicLink(Path.Combine(collector, "L"), Path.Combine("a", ".."));
                return Path.Combine(collector, "L");
            case "objects-link":
                Directory.CreateDirectory(collector);
                Directory.CreateSymbolicLink(Path.Combine(collector, "objects"), Path.Combine(victim, "objects"));
                return collector;
            case "nested-link":
                Directory.CreateDirectory(Path.Combine(collector, "objects"));
                Directory.CreateSymbolicLink(Path.Combine(collector, "objects", "workflow-artifacts"), Path.Combine(victim, "objects", "workflow-artifacts"));
                return collector;
            default: throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static string ObjectIdentity(string rootPath)
    {
        using var config = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath }));

        return new LocalRwxStorageProviderModule().PhysicalObject(config.RootElement, ObjectKey);
    }

    private static string Spell(string root, string spelling) => spelling switch
    {
        "as-written" => root,
        "trailing-slash" => root + "/",
        "dot-segment" => root + "/.",
        "doubled-slash" => root + "//",
        "doubled-inner-slash" => Path.GetDirectoryName(root) + "//" + Path.GetFileName(root),
        "parent-segment" => Path.Combine(root, "..", Path.GetFileName(root)),
        "relative" => Path.GetRelativePath(Environment.CurrentDirectory, root),
        _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, null),
    };

    private static IStorageProviderModule Module(string module) => module switch
    {
        "local-rwx" => new LocalRwxStorageProviderModule(),
        "local-legacy" => new LocalLegacyStorageProviderModule(),
        _ => throw new ArgumentOutOfRangeException(nameof(module), module, null),
    };

    /// <summary>The canonical JSON of the module's namespace projection — the exact string the fingerprint hashes.</summary>
    private static string Projection(string module, string rootPath)
    {
        using var config = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath }));

        return StorageProfileRules.CanonicalJson(Module(module).GetNamespaceConfiguration(config.RootElement));
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
