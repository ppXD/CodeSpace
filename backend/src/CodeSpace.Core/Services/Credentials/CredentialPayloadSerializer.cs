using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials;

public sealed class CredentialPayloadSerializer : ICredentialPayloadSerializer, ISingletonDependency
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Serialize(CredentialPayload payload) => JsonSerializer.Serialize(payload, payload.GetType(), Options);

    public CredentialPayload Deserialize(AuthType type, string json)
    {
        CredentialPayload? payload = type switch
        {
            AuthType.Pat => JsonSerializer.Deserialize<PatPayload>(json, Options),
            AuthType.ProjectAccessToken => JsonSerializer.Deserialize<ProjectAccessTokenPayload>(json, Options),
            AuthType.GroupAccessToken => JsonSerializer.Deserialize<GroupAccessTokenPayload>(json, Options),
            AuthType.OAuth => JsonSerializer.Deserialize<OAuthPayload>(json, Options),
            AuthType.GitHubApp => JsonSerializer.Deserialize<GitHubAppPayload>(json, Options),
            AuthType.SshKey => JsonSerializer.Deserialize<SshKeyPayload>(json, Options),
            AuthType.BasicAuth => JsonSerializer.Deserialize<BasicAuthPayload>(json, Options),
            _ => throw new NotSupportedException($"AuthType {type} has no payload schema")
        };

        return payload ?? throw new InvalidOperationException($"Failed to deserialize {type} payload from json");
    }

    public TPayload Deserialize<TPayload>(AuthType type, string json) where TPayload : CredentialPayload
    {
        return (TPayload)Deserialize(type, json);
    }
}
