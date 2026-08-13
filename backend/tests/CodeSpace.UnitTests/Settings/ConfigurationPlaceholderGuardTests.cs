using CodeSpace.Core.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

/// <summary>
/// A deployment template that reached the running process.
///
/// <para>The case that produced this: a pod started with <c>Agents:RunSpoolDirectory</c> still holding
/// the literal <c>#{Agents:RunSpoolDirectory}</c>, because the release step that substitutes it did not
/// run. Every guard in the codebase asks whether a setting is SET; that string is set. So it passed,
/// and the first agent run failed with <c>Access to the path '/app/#{Agents:RunSpoolDirectory}' is
/// denied</c> — a permission error naming the variable that was actually missing, three layers from the
/// release definition where the fix lives.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ConfigurationPlaceholderGuardTests
{
    [Theory]
    [InlineData("#{Agents:RunSpoolDirectory}")]                       // the one that shipped
    [InlineData("/app/#{Agents:RunSpoolDirectory}")]                  // embedded in an otherwise real path
    [InlineData("#{Db:Password}")]
    [InlineData("#{DB_PASSWORD}")]                                     // SHOUTING_CASE, no colon
    [InlineData("__CONNECTION_STRING__")]                             // Azure DevOps token replacement
    [InlineData("https://#{Api:Host}/api/webhooks")]
    public void An_unrendered_placeholder_refuses_the_start(string value)
    {
        var act = () => ConfigurationPlaceholderGuard.ThrowIfUnrendered(ConfigurationOf(("Agents:RunSpoolDirectory", value)));

        var thrown = act.ShouldThrow<InvalidOperationException>();

        thrown.Message.ShouldContain("Agents:RunSpoolDirectory", customMessage: "the operator has to be told which key to go and define");
        thrown.Message.ShouldContain(value, customMessage: "and the literal text to search their release definition for");
    }

    /// <summary>
    /// The guard has to be quiet about everything a person meant, or it becomes the thing that stops
    /// deployments. This product's own variable paths are written <c>{{project.default.X}}</c> and appear
    /// in configured prompts; <c>${...}</c> is ordinary shell text in a command setting.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("/var/lib/codespace/spool")]
    [InlineData("{{project.default.repository}}")]
    [InlineData("${HOME}/spool")]
    [InlineData("Host=db;Password=p#1;Database=codespace")]           // a '#' that opens nothing
    [InlineData("issue #{number} on {repositoryId}")]                 // prose the product itself writes
    [InlineData("Fetch the diff of pull request #{number}")]          // and it really does write exactly this
    public void Anything_someone_meant_is_left_alone(string value)
    {
        var act = () => ConfigurationPlaceholderGuard.ThrowIfUnrendered(ConfigurationOf(("Agents:RunSpoolDirectory", value)));

        act.ShouldNotThrow();
    }

    [Fact]
    public void Every_offending_key_is_named_at_once()
    {
        // Told one at a time, an operator redeploys once per missing variable. The whole list is one trip.
        var configuration = ConfigurationOf(
            ("Agents:RunSpoolDirectory", "#{Agents:RunSpoolDirectory}"),
            ("Artifacts:StoreDirectory", "#{Artifacts:StoreDirectory}"),
            ("Webhooks:BaseUrl", "https://real.example.com"));

        var violations = ConfigurationPlaceholderGuard.Violations(configuration);

        violations.Count.ShouldBe(2);
        violations.ShouldContain(v => v.Contains("Agents:RunSpoolDirectory"));
        violations.ShouldContain(v => v.Contains("Artifacts:StoreDirectory"));
        violations.ShouldNotContain(v => v.Contains("Webhooks:BaseUrl"));
    }

    /// <summary>
    /// Not Production-only, unlike <see cref="DurableRootsGuard"/>. That one encodes a policy about where
    /// data may live, which genuinely differs by environment. This one catches a value nobody wrote, and a
    /// staging box running on a template is just as broken — it would only be discovered later and by
    /// someone with less context.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void The_environment_does_not_excuse_it(string environment)
    {
        var configuration = ConfigurationOf(
            ("ASPNETCORE_ENVIRONMENT", environment),
            ("Agents:RunSpoolDirectory", "#{Agents:RunSpoolDirectory}"));

        var act = () => ConfigurationPlaceholderGuard.ThrowIfUnrendered(configuration);

        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void The_repository_own_appsettings_are_clean()
    {
        // The shipped defaults are configuration too. If one of them ever carried a placeholder this guard
        // would refuse every start, including a developer's — so it is worth knowing here rather than there.
        var root = FindRepoRoot();

        foreach (var file in Directory.GetFiles(Path.Combine(root, "backend", "src", "CodeSpace.Api"), "appsettings*.json"))
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

            ConfigurationPlaceholderGuard.Violations(configuration).ShouldBeEmpty($"{Path.GetFileName(file)} ships a deployment placeholder");
        }
    }

    private static IConfiguration ConfigurationOf(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
