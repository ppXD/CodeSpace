using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class PersistenceSecretRedactorTests
{
    [Fact]
    public void Redact_BagMasksNestedKeysValuesAndExactNonStringScalars()
    {
        var subject = new PersistenceSecretRedactor(["token-123", "42", "true"]);
        var values = new Dictionary<string, JsonElement>
        {
            ["token-123-key"] = JsonSerializer.SerializeToElement(new
            {
                text = "prefix token-123 suffix",
                nested = new object[] { 42, true, 420, false },
            }),
        };

        var result = subject.Redact(values);

        Assert.True(result.Changed);
        Assert.True(result.Value.ContainsKey("[REDACTED]-key"));
        var json = result.Value["[REDACTED]-key"].GetRawText();
        Assert.Contains("prefix [REDACTED] suffix", json);
        Assert.Contains("\"[REDACTED]\"", json);
        Assert.Contains("420", json);
        Assert.Contains("false", json);
        Assert.DoesNotContain("token-123", json);
    }

    [Fact]
    public void Redact_BagReportsUnchangedWithoutMutatingRuntimeValues()
    {
        var subject = new PersistenceSecretRedactor(["secret"]);
        var original = JsonSerializer.SerializeToElement(new { value = "public" });
        var values = new Dictionary<string, JsonElement> { ["result"] = original };

        var result = subject.Redact(values);

        Assert.False(result.Changed);
        Assert.Equal(original.GetRawText(), result.Value["result"].GetRawText());
        Assert.Equal("{\"value\":\"public\"}", values["result"].GetRawText());
    }

    [Fact]
    public void FoundIn_NamesExactlyTheSecretsRedactWouldMask()
    {
        var subject = new PersistenceSecretRedactor(["token-123", "42", "absent-secret", "key-x"]);
        var value = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["note"] = "prefix token-123 suffix",
            ["key-x-name"] = 420,
            ["count"] = 42,
        });

        var found = subject.FoundIn(value);

        Assert.Equal(["42", "key-x", "token-123"], found.OrderBy(secret => secret, StringComparer.Ordinal).ToArray());
        Assert.True(subject.Redact(value).Changed);
        Assert.Empty(new PersistenceSecretRedactor(["42"]).FoundIn(JsonSerializer.SerializeToElement(new { count = 420 })));
    }

    [Fact]
    public void FromScope_MasksValuesTheRunInheritedAsWellAsItsOwn()
    {
        var scope = new NodeRunScope
        {
            Trigger = new Dictionary<string, JsonElement>(),
            Team = new Dictionary<string, JsonElement> { ["OWN"] = JsonSerializer.SerializeToElement("own-team-secret") },
            SecretPaths = new HashSet<string> { "team.OWN", "trigger.token" },
            InheritedSecretValues = ["inherited-parent-wf-secret"],
        };

        var redacted = PersistenceSecretRedactor.FromScope(scope).Redact("own-team-secret and inherited-parent-wf-secret");

        Assert.Equal("[REDACTED] and [REDACTED]", redacted.Value);
    }
}
