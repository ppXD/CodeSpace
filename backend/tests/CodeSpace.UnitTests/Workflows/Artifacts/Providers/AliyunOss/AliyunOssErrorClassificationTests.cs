using System.Net;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// The one bit that separates "the bucket was deleted" from "the provider is having a bad minute".
///
/// <para>Both classify to <see cref="ArtifactStorageErrorCode.Unavailable"/>, and abandonment believes only
/// non-retryable answers — so if <c>NoSuchBucket</c> ever came back retryable, the deleted-bucket exit would stop
/// working, and if a 5xx ever came back non-retryable, one bad second would close records over readable bytes.</para>
/// </summary>
public sealed class AliyunOssErrorClassificationTests
{
    [Fact]
    public async Task A_deleted_bucket_is_a_durable_answer()
    {
        using var response = Response(HttpStatusCode.NotFound, "NoSuchBucket");

        var error = await AliyunOssErrors.FromResponseAsync(response, "objects/x", CancellationToken.None);

        error.Code.ShouldBe(ArtifactStorageErrorCode.Unavailable);
        error.IsRetryable.ShouldBeFalse("retrying does not bring a deleted bucket back, and this bit is what lets abandonment close its records");
    }

    [Fact]
    public async Task A_server_fault_is_an_answer_about_the_moment()
    {
        using var response = Response(HttpStatusCode.InternalServerError, providerCode: null);

        var error = await AliyunOssErrors.FromResponseAsync(response, "objects/x", CancellationToken.None);

        error.Code.ShouldBe(ArtifactStorageErrorCode.Unavailable);
        error.IsRetryable.ShouldBeTrue("a 5xx wears the same code as a deleted bucket, and retryability is the only thing telling them apart");
    }

    [Fact]
    public void A_network_fault_is_an_answer_about_the_moment()
    {
        AliyunOssErrors.Transport(new HttpRequestException("unreachable"), "objects/x").IsRetryable
            .ShouldBeTrue("an unreachable host says nothing about whether the namespace still exists");
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string? providerCode) => new(status)
    {
        Content = new StringContent(providerCode == null
            ? "<Error><Message>boom</Message></Error>"
            : $"<Error><Code>{providerCode}</Code><Message>gone</Message></Error>"),
    };
}
