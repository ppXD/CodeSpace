using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.OAuth;

public sealed class PkceGenerator : IPkceGenerator, ISingletonDependency
{
    public PkcePair Generate()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return new PkcePair(verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
