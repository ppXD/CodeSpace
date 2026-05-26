using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Shared System.Text.Json settings for workflow definition I/O. We deliberately mirror
/// ASP.NET Core's "Web" defaults (camelCase, case-insensitive on read, string enums) so
/// the JSON shape on disk (templates) matches what the API serializes/deserializes — a
/// definition loaded from disk, posted through the SPA, and read back via the API is
/// byte-for-byte identical aside from whitespace.
/// </summary>
public static class WorkflowJson
{
    public static JsonSerializerOptions Options { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
