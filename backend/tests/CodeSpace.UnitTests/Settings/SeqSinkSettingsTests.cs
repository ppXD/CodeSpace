using System;
using System.Collections.Generic;
using System.IO;
using CodeSpace.Core.Settings.Logging;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

/// <summary>
/// 🟢 Unit: the two Seq settings read the configuration keys they claim to, and the shipped defaults are the ones
/// appsettings.json documents. Both halves fail silently in production if they drift, which is why they are pinned
/// here: a mistyped key name adds no sink at all and says nothing (the logger keeps printing to the console, so
/// nothing looks broken until someone goes looking in Seq for a log that never arrived), and a drifted default
/// aims a developer's logs at a port no Seq listens on.
///
/// <para>The defaults are asserted against the REAL <c>backend/src/CodeSpace.Api/appsettings.json</c> rather than a
/// copy of its values, so that file stays the single place the default lives — the setting classes deliberately
/// carry no fallback of their own.</para>
/// </summary>
[Trait("Category", "Unit")]
public class SeqSinkSettingsTests
{
    [Fact]
    public void Server_url_reads_the_key_it_claims()
    {
        // The literal is the operator's contract: a deployment overrides it as Serilog__Seq__ServerUrl, so renaming
        // the key silently strands whoever pinned the old name.
        SerilogServerUrlSetting.ConfigurationKey.ShouldBe("Serilog:Seq:ServerUrl");

        var setting = new SerilogServerUrlSetting(Build(new Dictionary<string, string?>
        {
            ["Serilog:Seq:ServerUrl"] = "https://seq.example.com",
        }));

        setting.Value.ShouldBe("https://seq.example.com");
    }

    [Fact]
    public void Api_key_reads_the_key_it_claims()
    {
        SerilogApiKeySetting.ConfigurationKey.ShouldBe("Serilog:Seq:ApiKey");

        var setting = new SerilogApiKeySetting(Build(new Dictionary<string, string?>
        {
            ["Serilog:Seq:ApiKey"] = "abc123",
        }));

        setting.Value.ShouldBe("abc123");
    }

    [Fact]
    public void Neither_setting_carries_a_fallback_of_its_own()
    {
        // Two sources of truth for the default would eventually disagree, and the one in the C# is the one nobody
        // reading appsettings.json would ever see. Absent config reads as absent.
        var empty = Build(new Dictionary<string, string?>());

        new SerilogServerUrlSetting(empty).Value.ShouldBeNull();
        new SerilogApiKeySetting(empty).Value.ShouldBeNull();
    }

    [Fact]
    public void The_shipped_default_is_a_seq_on_the_developers_own_machine()
    {
        new SerilogServerUrlSetting(ShippedApiConfiguration()).Value.ShouldBe("http://localhost:5341",
            customMessage: "the committed Serilog:Seq:ServerUrl no longer points at the default local Seq port — a developer following the README would log to nowhere");
    }

    [Fact]
    public void The_shipped_api_key_is_blank_because_a_local_seq_ingests_anonymously()
    {
        new SerilogApiKeySetting(ShippedApiConfiguration()).Value.ShouldBeNullOrEmpty();
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>The configuration a developer actually boots on — the committed appsettings.json, nothing layered over it.</summary>
    private static IConfiguration ShippedApiConfiguration() =>
        new ConfigurationBuilder().AddJsonFile(LocateApiAppSettings(), optional: false).Build();

    private static string LocateApiAppSettings()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "src", "CodeSpace.Api", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("backend/src/CodeSpace.Api/appsettings.json not found walking up from " + AppContext.BaseDirectory);
    }
}
