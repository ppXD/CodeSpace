namespace CodeSpace.Api.Http;

/// <summary>
/// Reads an anonymous endpoint's body only up to a limit. <c>[RequestSizeLimit]</c> makes Kestrel stop at the same
/// limit, but it does so by THROWING from the read, which the global exception filter would turn into a 500, and a host
/// without the size feature does not stop at all. This answers "too large" the same way under either.
/// </summary>
public static class BoundedRequestBody
{
    /// <summary>The body as text, or null when it is longer than <paramref name="maxBytes"/>.</summary>
    public static async Task<string?> ReadBoundedBodyAsync(this HttpRequest request, long maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes) return null;

        try
        {
            using var buffer = await ReadUpToAsync(request.Body, maxBytes + 1, cancellationToken).ConfigureAwait(false);
            if (buffer.Length > maxBytes) return null;

            using var reader = new StreamReader(buffer);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
    }

    /// <summary>At most <paramref name="limit"/> bytes of the body, rewound for reading.</summary>
    private static async Task<MemoryStream> ReadUpToAsync(Stream body, long limit, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (buffer.Length < limit)
        {
            var read = await body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }
}
