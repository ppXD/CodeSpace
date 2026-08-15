using System.Buffers.Text;
using System.Text;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>Opaque keyset cursor shared by stable-name ordered storage profiles and credentials.</summary>
public readonly record struct StorageSettingsCursor(string StableName, Guid Id)
{
    public string Encode()
    {
        var raw = $"{StableName}\n{Id:N}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    public static StorageSettingsCursor? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;

        try
        {
            var raw = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var separator = raw.IndexOf('\n');
            if (separator is > 0 and <= 128 && raw.LastIndexOf('\n') == separator
                && Guid.TryParseExact(raw.AsSpan(separator + 1), "N", out var id) && id != Guid.Empty)
                return new StorageSettingsCursor(raw[..separator], id);
        }
        catch (FormatException) { }

        throw new InvalidOperationException("Invalid storage settings cursor.");
    }
}
