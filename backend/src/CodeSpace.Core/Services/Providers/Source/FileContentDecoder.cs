using System.Text;
using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Source;

/// <summary>
/// Pure decode of raw file bytes (already base64-decoded from the provider) into the
/// <see cref="RemoteFileContent"/> the Code browser renders. Encapsulates the three decisions every
/// provider would otherwise duplicate — too-big-to-inline (truncated), binary-vs-text, and UTF-8 text
/// extraction — so they're decided once and unit-tested without any SDK or IO.
/// </summary>
public static class FileContentDecoder
{
    /// <summary>
    /// Files larger than this are reported as truncated (<see cref="RemoteFileContent.Text"/> null) — the
    /// browser shows a "too large to preview" notice rather than streaming megabytes into the SPA. Matches
    /// GitHub's own content-API inline cap of 1 MB.
    /// </summary>
    public const long DefaultMaxInlineBytes = 1_000_000;

    /// <summary>
    /// Build the view model. <paramref name="content"/> null means the provider could not inline the bytes
    /// (e.g. GitHub omits content above 1 MB) ⇒ truncated. An empty array is a legitimately empty file ⇒
    /// empty text, not binary.
    /// </summary>
    public static RemoteFileContent Build(string path, string name, byte[]? content, string? sha, long? reportedSize = null, long maxInlineBytes = DefaultMaxInlineBytes)
    {
        var size = reportedSize ?? content?.LongLength ?? 0;

        if (content is null || content.LongLength > maxInlineBytes || size > maxInlineBytes)
            return new RemoteFileContent { Path = path, Name = name, Size = size, Sha = sha, IsTruncated = true };

        if (LooksBinary(content))
            return new RemoteFileContent { Path = path, Name = name, Size = size, Sha = sha, IsBinary = true };

        return new RemoteFileContent { Path = path, Name = name, Size = size, Sha = sha, Text = DecodeUtf8(content) };
    }

    /// <summary>A NUL byte within the first 8 KB ⇒ binary. Same heuristic git itself uses to classify blobs.</summary>
    private static bool LooksBinary(byte[] bytes)
    {
        var window = Math.Min(bytes.Length, 8000);

        for (var i = 0; i < window; i++)
            if (bytes[i] == 0) return true;

        return false;
    }

    /// <summary>Decode as UTF-8, stripping a leading BOM so the SPA never renders a stray ﻿.</summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return utf8.GetString(bytes, 3, bytes.Length - 3);

        return utf8.GetString(bytes);
    }
}
