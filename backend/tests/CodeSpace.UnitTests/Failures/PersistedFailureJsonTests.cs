using System.Collections.Generic;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Failures;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Failures;

/// <summary>
/// How a persisted failure sits in a ledger payload and in an agent result: an additive <c>failure</c> object, readable back
/// exactly, and absent — never an error — on every row written before failures were kept.
/// </summary>
[Trait("Category", "Unit")]
public class PersistedFailureJsonTests
{
    private static readonly Guid Credential = Guid.NewGuid();
    private static readonly Guid Repository = Guid.NewGuid();

    [Fact]
    public void A_failure_survives_the_trip_through_a_record_payload()
    {
        var failure = new PersistedFailure { Code = FailureCodes.CredentialDisconnected, Details = new Dictionary<string, string> { ["credentialId"] = Credential.ToString() }, RepositoryId = Repository };
        var payload = JsonSerializer.Serialize(new { error = "The credential was disconnected", outputs = new { }, duration_ms = 5L, failure = PersistedFailureJson.ToElement(failure) });

        var read = PersistedFailureJson.Read(payload);

        read.ShouldNotBeNull();
        read.Code.ShouldBe(FailureCodes.CredentialDisconnected);
        read.DetailGuid("credentialId").ShouldBe(Credential);
        read.RepositoryId.ShouldBe(Repository);
    }

    [Fact]
    public void The_stored_shape_is_the_one_a_client_and_an_agent_result_both_read()
    {
        var json = PersistedFailureJson.ToElement(new PersistedFailure { Code = FailureCodes.ProviderUnauthorized, RepositoryId = Repository }).GetRawText();

        json.ShouldBe($$"""{"code":"provider_unauthorized","repositoryId":"{{Repository}}"}""", "camelCase, and a detail that is absent is not written as null");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"error":"Node 'x' failed.","outputs":{},"duration_ms":3}""")]     // a record written before failures were kept
    [InlineData("""{"error":"x","failure":null}""")]
    [InlineData("""{"error":"x","failure":"a string"}""")]
    [InlineData("""{"error":"x","failure":{"details":{}}}""")]                        // no code
    [InlineData("""{"error":"x","failure":{"code":""}}""")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void A_record_without_a_readable_failure_reads_as_none_and_never_throws(string? payload)
    {
        PersistedFailureJson.Read(payload).ShouldBeNull();
    }

    [Fact]
    public void Details_are_kept_as_the_text_they_were_thrown_as()
    {
        var details = PersistedFailure.DetailsAsText(new Dictionary<string, object?> { ["providerStatus"] = 401, ["credentialId"] = Credential, ["ignored"] = null });

        details.ShouldNotBeNull();
        details["providerStatus"].ShouldBe("401");
        details["credentialId"].ShouldBe(Credential.ToString());
        details.ContainsKey("ignored").ShouldBeFalse();
        PersistedFailure.DetailsAsText(new Dictionary<string, object?> { ["only"] = null }).ShouldBeNull("nothing said is nothing kept");
        PersistedFailure.DetailsAsText(null).ShouldBeNull();
    }

    [Fact]
    public void A_blank_or_malformed_detail_reads_as_absent()
    {
        var failure = new PersistedFailure { Code = "x", Details = new Dictionary<string, string> { ["blank"] = " ", ["guid"] = "nope", ["empty"] = Guid.Empty.ToString() } };

        failure.Detail("blank").ShouldBeNull();
        failure.Detail("missing").ShouldBeNull();
        failure.DetailGuid("guid").ShouldBeNull();
        failure.DetailGuid("empty").ShouldBeNull();
    }

    [Fact]
    public void An_agent_result_names_its_failure_or_a_refused_push()
    {
        var own = $$$"""{"status":"Failed","exitReason":"credential_disconnected","failure":{"code":"credential_disconnected","details":{"credentialId":"{{{Credential}}}"},"repositoryId":"{{{Repository}}}"}}""";
        var refusedPush = """{"status":"Succeeded","exitReason":"completed","publishError":"refused","publishFailureCode":"git_credential_rejected"}""";
        var refusedRepo = $$$"""{"status":"Succeeded","exitReason":"completed","repositoryResults":[{"alias":"a","publishFailureCode":null},{"alias":"b","repositoryId":"{{{Repository}}}","publishFailureCode":"git_credential_rejected"}]}""";
        var clean = """{"status":"Succeeded","exitReason":"completed","repositoryResults":[{"alias":"a"}]}""";

        RunFailureReader.ReadAgentResult(own)!.DetailGuid("credentialId").ShouldBe(Credential);
        RunFailureReader.ReadAgentResult(refusedPush)!.Code.ShouldBe(FailureCodes.GitCredentialRejected);
        RunFailureReader.ReadAgentResult(refusedRepo)!.RepositoryId.ShouldBe(Repository, "the first repository whose push was refused");
        RunFailureReader.ReadAgentResult(clean).ShouldBeNull();
        RunFailureReader.ReadAgentResult("not json").ShouldBeNull();
    }
}
