using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Generic HTTP request node — the universal escape hatch. Lets a workflow call any HTTP API
/// without needing a bespoke provider node. Proves the architecture supports non-business-
/// specific nodes cleanly.
///
/// Inputs:
///   url      — full request URL (required)
///   method   — GET | POST | PUT | DELETE | PATCH (default: GET)
///   headers  — flat object of { "Header-Name": "value" }
///   body     — string body (skipped for GET / DELETE). Auto-stringified if object.
/// Config:
///   timeoutSeconds — request timeout (default: 30, max: 120)
///   parseJson      — when true (default), attempt to parse response body as JSON for downstream {{ref}} access
/// Outputs:
///   status   — HTTP status code (integer)
///   ok       — true iff 2xx
///   body     — response body (parsed JSON when parseJson and content-type matches, else string)
///   headers  — response headers as object
/// </summary>
public sealed class HttpRequestNode : INodeRuntime
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxTimeoutSeconds = 120;

    private readonly IHttpClientFactory _httpClientFactory;

    public HttpRequestNode(IHttpClientFactory httpClientFactory) { _httpClientFactory = httpClientFactory; }

    public string TypeKey => "http.request";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "HTTP request",
        Category = "Tools",
        Kind = NodeKind.Regular,
        IconKey = "globe",
        Description = "Make an HTTP request to any URL. Universal escape hatch when no bespoke node exists.",
        // HTTP requests with mutating verbs (POST/PUT/PATCH/DELETE) are side-effecting. GET
        // is technically safe to retry, but the manifest is a node-level marker and the
        // operator can't promise which verb their template will pick. Mark the whole node
        // side-effecting so the abandoned-run guard treats it conservatively.
        IsSideEffecting = true,
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "timeoutSeconds": { "type": "integer", "minimum": 1, "maximum": 120, "default": 30 },
                "parseJson":      { "type": "boolean", "default": true, "description": "Auto-parse JSON response bodies so downstream nodes can {{ref}} into them." }
              }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "url":     { "type": "string", "minLength": 1 },
                "method":  { "type": "string", "enum": ["GET","POST","PUT","DELETE","PATCH"], "default": "GET" },
                "headers": { "type": "object" },
                "body":    { "type": ["string","object","null"] }
              },
              "required": ["url"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "status":  { "type": "integer" },
                "ok":      { "type": "boolean" },
                "body":    {},
                "headers": { "type": "object" }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
            return NodeResult.Fail("Input 'url' is required.");

        var url = urlElement.GetString();
        if (string.IsNullOrWhiteSpace(url)) return NodeResult.Fail("Input 'url' must be a non-empty string.");

        var method = ReadMethod(context.Inputs);
        var timeoutSeconds = Math.Clamp(ReadInt(context.Config, "timeoutSeconds", DefaultTimeoutSeconds), 1, MaxTimeoutSeconds);
        var parseJson = ReadBool(context.Config, "parseJson", true);

        var http = _httpClientFactory.CreateClient(nameof(HttpRequestNode));
        http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        using var request = BuildRequest(method, url, context.Inputs);

        context.Logger.LogInformation("HTTP {Method} {Url} (timeout={Timeout}s)", method, url, timeoutSeconds);

        // Wrap the external HTTP call so the ledger records a
        // (external_call.started, external_call.completed | external_call.failed) pair.
        // Operators reading the run-detail UI then see "this node called X, got Y" without
        // having to grep server logs. Timeouts + transport failures still surface as
        // NodeResult.Fail so the engine's failure semantics are unchanged.
        try
        {
            return await context.Observability.TraceExternalCallAsync(
                target: url,
                method: method.Method,
                requestPayload: BuildRequestPayloadAudit(method, url, context.Inputs),
                action: async ct =>
                {
                    using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                    var bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    var bodyValue = parseJson && IsJsonContent(response.Content.Headers.ContentType)
                        ? TryParseJson(bodyText)
                        : JsonSerializer.SerializeToElement(bodyText);

                    var headersValue = SerializeHeaders(response.Headers, response.Content.Headers);

                    var outputs = new Dictionary<string, JsonElement>
                    {
                        ["status"] = JsonSerializer.SerializeToElement((int)response.StatusCode),
                        ["ok"] = JsonSerializer.SerializeToElement(response.IsSuccessStatusCode),
                        ["body"] = bodyValue,
                        ["headers"] = headersValue
                    };

                    context.Logger.LogInformation("HTTP response {Status} ({Length} bytes)", (int)response.StatusCode, bodyText.Length);

                    return NodeResult.Ok(outputs);
                },
                completionExtractor: result =>
                {
                    // Surface the status code on the completed record so the timeline shows
                    // "GET https://api.example.com → 200" instead of just "completed".
                    if (result.Outputs.TryGetValue("status", out var status) && status.ValueKind == JsonValueKind.Number)
                        return new ExternalCallCompletion { StatusCode = status.GetInt32() };
                    return new ExternalCallCompletion();
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // TaskCanceledException with our own token NOT triggered = client-side timeout.
            // (Observability already emitted external_call.failed; re-translate to NodeResult.Fail.)
            return NodeResult.Fail($"HTTP request timed out after {timeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            return NodeResult.Fail($"HTTP request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Build a redacted summary of the request for the ledger's request_payload field. Headers
    /// might contain secrets (Authorization, X-API-Key); the engine's redactor took care of
    /// the resolved-value form already, but here we explicitly keep only structural fields
    /// + URL — the body is omitted to bound the ledger size.
    /// </summary>
    private static JsonElement? BuildRequestPayloadAudit(HttpMethod method, string url, IReadOnlyDictionary<string, JsonElement> inputs)
    {
        var headerNames = inputs.TryGetValue("headers", out var h) && h.ValueKind == JsonValueKind.Object
            ? h.EnumerateObject().Select(p => p.Name).ToArray()
            : Array.Empty<string>();

        return JsonSerializer.SerializeToElement(new
        {
            method = method.Method,
            url,
            header_names = headerNames,
        });
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, IReadOnlyDictionary<string, JsonElement> inputs)
    {
        var request = new HttpRequestMessage(method, url);

        if (inputs.TryGetValue("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in headersElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                // Content-* headers go on the content, request headers go on the request.
                // TryAddWithoutValidation lets us pass through whatever the operator wrote.
                request.Headers.TryAddWithoutValidation(prop.Name, prop.Value.GetString());
            }
        }

        var allowsBody = method != HttpMethod.Get && method != HttpMethod.Delete;

        if (allowsBody && inputs.TryGetValue("body", out var bodyElement) && bodyElement.ValueKind != JsonValueKind.Null)
        {
            var bodyString = bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString() ?? ""
                : bodyElement.GetRawText();
            var contentType = bodyElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? "application/json"
                : "text/plain";
            request.Content = new StringContent(bodyString, Encoding.UTF8, contentType);
        }

        return request;
    }

    private static HttpMethod ReadMethod(IReadOnlyDictionary<string, JsonElement> inputs)
    {
        if (!inputs.TryGetValue("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            return HttpMethod.Get;

        return (methodElement.GetString() ?? "GET").ToUpperInvariant() switch
        {
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "PATCH" => HttpMethod.Patch,
            _ => HttpMethod.Get
        };
    }

    private static JsonElement TryParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return JsonSerializer.SerializeToElement<string?>(null);
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch { return JsonSerializer.SerializeToElement(text); }
    }

    private static bool IsJsonContent(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType is "application/json" or "application/problem+json" or "text/json";

    private static JsonElement SerializeHeaders(HttpResponseHeaders responseHeaders, HttpContentHeaders contentHeaders)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in responseHeaders) dict[h.Key] = string.Join(", ", h.Value);
        foreach (var h in contentHeaders) dict[h.Key] = string.Join(", ", h.Value);
        return JsonSerializer.SerializeToElement(dict);
    }

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> bag, string key, int fallback)
    {
        if (!bag.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number) return fallback;
        return value.TryGetInt32(out var i) ? i : fallback;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, JsonElement> bag, string key, bool fallback)
    {
        if (!bag.TryGetValue(key, out var value)) return fallback;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };
    }
}
