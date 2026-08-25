using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;

namespace CodeSpace.UnitTests.Workflows;

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
}
