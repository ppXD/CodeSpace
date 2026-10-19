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
    public void Where_a_failure_happened_survives_the_trip_and_is_stored_beside_what_it_names()
    {
        var run = Guid.NewGuid();
        var failure = new PersistedFailure { Code = FailureCodes.ProviderUnauthorized, RepositoryId = Repository, Origin = new FailureOrigin { RunId = run, NodeId = "list" } };

        PersistedFailureJson.ToElement(failure).GetRawText().ShouldBe($$$"""{"code":"provider_unauthorized","repositoryId":"{{{Repository}}}","origin":{"runId":"{{{run}}}","nodeId":"list"}}""");

        var read = PersistedFailureJson.Read(PersistedFailureJson.AsPayload(failure));

        read.ShouldNotBeNull();
        read.Origin.ShouldBe(new FailureOrigin { RunId = run, NodeId = "list" });
        read.RepositoryId.ShouldBe(Repository);
    }

    [Fact]
    public void When_the_failure_happened_in_that_run_is_stored_with_the_origin_and_survives_the_trip()
    {
        var run = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 19, 4, 29, 49, TimeSpan.Zero);
        var failure = new PersistedFailure { Code = FailureCodes.ProviderUnauthorized, Origin = new FailureOrigin { RunId = run, NodeId = "list", At = at } };

        PersistedFailureJson.ToElement(failure).GetRawText().ShouldBe($$$"""{"code":"provider_unauthorized","origin":{"runId":"{{{run}}}","nodeId":"list","at":"2026-10-19T04:29:49+00:00"}}""");
        PersistedFailureJson.Read(PersistedFailureJson.AsPayload(failure))!.Origin.ShouldBe(new FailureOrigin { RunId = run, NodeId = "list", At = at });
    }

    [Fact]
    public void An_origin_written_before_it_kept_its_time_reads_with_none_and_is_stored_exactly_as_it_was()
    {
        var before = $$$"""{"code":"provider_unauthorized","origin":{"runId":"{{{Guid.NewGuid()}}}","nodeId":"list"}}""";
        var read = PersistedFailureJson.ReadElement(JsonDocument.Parse(before).RootElement)!;

        read.Origin!.At.ShouldBeNull("the record that carries it is then the only time there is");
        PersistedFailureJson.ToElement(read).GetRawText().ShouldBe(before, "an absent time is not written as null");
    }

    [Fact]
    public void A_failure_with_an_origin_that_names_no_node_keeps_the_run_alone()
    {
        var run = Guid.NewGuid();
        var json = PersistedFailureJson.ToElement(new PersistedFailure { Code = FailureCodes.CredentialDisconnected, Origin = new FailureOrigin { RunId = run } }).GetRawText();

        json.ShouldBe($$$"""{"code":"credential_disconnected","origin":{"runId":"{{{run}}}"}}""", "an absent node is not written as null");
        PersistedFailureJson.ReadElement(JsonDocument.Parse(json).RootElement)!.Origin!.NodeId.ShouldBeNull();
    }

    [Fact]
    public void A_failure_written_before_origins_reads_with_none_and_is_stored_exactly_as_it_was()
    {
        var before = $$$"""{"code":"credential_disconnected","details":{"credentialId":"{{{Credential}}}"},"repositoryId":"{{{Repository}}}"}""";
        var read = PersistedFailureJson.ReadElement(JsonDocument.Parse(before).RootElement)!;

        read.Origin.ShouldBeNull();
        PersistedFailureJson.ToElement(read).GetRawText().ShouldBe(before, "reading an old row and writing it back adds nothing");
    }

    [Fact]
    public void A_member_a_later_build_adds_to_a_failure_is_ignored_not_an_error()
    {
        // What an older reader meets after a rollback (and what this reader will meet from the next additive field): unknown members are skipped,
        // so the failure — and the remedy built from it — still reads, minus only what the older reader never knew to look for.
        var written = $$$$"""{"error":"x","failure":{"code":"credential_disconnected","origin":{"runId":"{{{{Guid.NewGuid()}}}}","nodeId":"n","someLaterMember":[1,2]},"aMemberFromTheFuture":{"a":1}}}""";

        PersistedFailureJson.Read(written)!.Code.ShouldBe(FailureCodes.CredentialDisconnected);
    }

    /// <summary>The failure exactly as it was before origins: a build rolled back to it (or a worker not yet upgraded) deserializes records with this shape.</summary>
    private sealed record PreviousShape(string Code, Dictionary<string, string>? Details, Guid? RepositoryId);

    /// <summary>The failure as it was when an origin named only the run and the node: a build rolled back to that one reads a record that now also carries the time.</summary>
    private sealed record PreviousOriginShape(string Code, PreviousOrigin? Origin);

    private sealed record PreviousOrigin(Guid RunId, string? NodeId);

    [Fact]
    public void A_build_that_predates_the_origins_time_reads_what_is_written_now_minus_the_time()
    {
        var run = Guid.NewGuid();
        var written = PersistedFailureJson.ToElement(new PersistedFailure { Code = FailureCodes.ProviderUnauthorized, Origin = new FailureOrigin { RunId = run, NodeId = "list", At = DateTimeOffset.UtcNow } });

        var old = written.Deserialize<PreviousOriginShape>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        old.Code.ShouldBe(FailureCodes.ProviderUnauthorized);
        old.Origin.ShouldBe(new PreviousOrigin(run, "list"), "the older reader still names the run and the node; the time it never knew is skipped");
    }

    [Fact]
    public void A_build_that_predates_origins_reads_what_is_written_now_minus_the_origin()
    {
        var run = Guid.NewGuid();
        var written = PersistedFailureJson.ToElement(new PersistedFailure { Code = FailureCodes.CredentialNeedsReconnect, Details = new Dictionary<string, string> { ["credentialId"] = Credential.ToString() }, RepositoryId = Repository, Origin = new FailureOrigin { RunId = run, NodeId = "list", At = DateTimeOffset.UtcNow } });

        // The same serializer defaults the ledger reads with: unknown members are skipped, so the older build still has the failure — and the remedy built from it.
        var old = written.Deserialize<PreviousShape>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        old.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);
        old.Details!["credentialId"].ShouldBe(Credential.ToString());
        old.RepositoryId.ShouldBe(Repository);
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
