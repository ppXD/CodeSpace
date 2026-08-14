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

    /// <summary>
    /// The base file ships NO server. A deployment that has not named its Seq then gets honest
    /// console-only logging and says so at startup.
    ///
    /// <para>It used to ship <c>http://localhost:5341</c>, which every deployed pod inherited and
    /// which resolves inside the container to itself, where nothing listens. The batched sink retries
    /// against nothing forever and the operator sees an empty Seq — indistinguishable from an
    /// application with nothing to report. That cost a real incident: an error was in the pod's
    /// console the whole time while Seq was searched and found clean.</para>
    /// </summary>
    [Fact]
    public void The_base_file_ships_no_seq_so_an_unconfigured_deployment_is_honest_about_it()
    {
        new SerilogServerUrlSetting(ShippedApiConfiguration()).Value.ShouldBeNullOrEmpty(
            customMessage: "appsettings.json must not name a Seq. Any value here is inherited by every deployment that " +
                           "does not override it, and a wrong one is worse than none: it makes an empty Seq look like silence.");
    }

    /// <summary>The developer convenience the base file gives up, kept where it cannot reach a deployment.</summary>
    [Fact]
    public void Development_still_points_at_the_local_seq_so_a_developer_configures_nothing()
    {
        new SerilogServerUrlSetting(ShippedDevelopmentConfiguration()).Value.ShouldBe("http://localhost:5341",
            customMessage: "appsettings.Development.json is what makes a local Seq zero-config; without it a developer " +
                           "following the README logs to nowhere.");
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

    /// <summary>The configuration a DEVELOPER boots on — base plus the Development overlay, in the order the host layers them.</summary>
    private static IConfiguration ShippedDevelopmentConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonFile(LocateApiAppSettings(), optional: false)
            .AddJsonFile(LocateApiAppSettings().Replace("appsettings.json", "appsettings.Development.json"), optional: false)
            .Build();

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
