using System.Text.Json;
using System.Reflection;
using CodeSpace.Api.Controllers;
using CodeSpace.Api.Extensions;
using CodeSpace.Api.Http;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Autofac;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public sealed class ExternalTriggerIngressContractTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Verification_projects_only_identity_and_digest_without_a_transaction()
    {
        var reads = new AuthenticationReads();
        using var scope = fixture.BeginScope(builder =>
        {
            var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(fixture.ConnectionString)
                .UseSnakeCaseNamingConvention().AddInterceptors(reads).Options;
            builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>();
        });
        var result = await scope.Resolve<IMediator>().Send(new VerifyExternalTriggerQuery { TriggerId = Guid.NewGuid(), PresentedKey = "wrong" });
        result.Kind.ShouldBe(ExternalTriggerDeliveryKind.Unauthorized);
        var sql = reads.Commands.ShouldHaveSingleItem();
        sql.ShouldContain("key_digest");
        sql.ShouldNotContain("key_ciphertext");
        sql.ShouldNotContain("definition");
        sql.ShouldNotContain("activation");
        scope.Resolve<CodeSpaceDbContext>().Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public void The_ext_route_is_anonymous_bounded_and_rate_limited_like_webhook_ingress()
    {
        var controller = typeof(ExternalTriggersController);
        controller.GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
        controller.GetCustomAttribute<RouteAttribute>().ShouldNotBeNull().Template.ShouldBe("api/ext");
        var method = controller.GetMethod("Run").ShouldNotBeNull();
        method.GetCustomAttribute<HttpPostAttribute>().ShouldNotBeNull().Template.ShouldBe("{triggerId:guid}/run");
        ((IRequestSizeLimitMetadata)method.GetCustomAttribute<RequestSizeLimitAttribute>().ShouldNotBeNull()).MaxRequestBodySize.ShouldBe(1_048_577);
        controller.GetCustomAttribute<EnableRateLimitingAttribute>().ShouldNotBeNull().PolicyName.ShouldBe(WebhookIngressRateLimitExtension.PolicyName);
        method.GetParameters().ShouldNotContain(p => p.Name == "key" && p.GetCustomAttribute<FromQueryAttribute>() != null);
    }

    [Fact]
    public async Task Failed_authentication_does_not_read_the_request_stream()
    {
        using var scope = fixture.BeginScope();
        var context = new DefaultHttpContext();
        context.Request.Body = new UnreadableStream();
        var controller = new ExternalTriggersController(scope.Resolve<IMediator>()) { ControllerContext = new ControllerContext { HttpContext = context } };
        (await controller.Run(Guid.NewGuid(), CancellationToken.None)).ShouldBeOfType<UnauthorizedObjectResult>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task The_reader_counts_actual_bytes_without_ContentLength(int extra)
    {
        var context = new DefaultHttpContext();
        var bytes = new byte[1_048_576 + extra];
        Array.Fill(bytes, (byte)' ');
        bytes[0] = (byte)'{'; bytes[1] = (byte)'}';
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength.ShouldBeNull();
        if (extra == 0)
        {
            using var result = await BoundedJsonBodyReader.ReadAsync(context.Request, CancellationToken.None);
            result.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        }
        else await Should.ThrowAsync<BoundedJsonBodyReader.BodyTooLargeException>(() => BoundedJsonBodyReader.ReadAsync(context.Request, CancellationToken.None));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public async Task Parser_depth_has_a_controlled_boundary(int depth, bool accepted)
    {
        var json = string.Concat(Enumerable.Repeat("[", depth)) + "0" + string.Concat(Enumerable.Repeat("]", depth));
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        if (accepted)
        {
            using var body = await BoundedJsonBodyReader.ReadAsync(context.Request, CancellationToken.None);
            ExternalTriggerPayloadValidator.ValidatePayload(body.RootElement).ShouldBeEmpty();
        }
        else await Should.ThrowAsync<JsonException>(() => BoundedJsonBodyReader.ReadAsync(context.Request, CancellationToken.None));
    }

    [Fact]
    public void Equivalent_UUID_spellings_share_the_target_request_budget()
    {
        var a = new DefaultHttpContext();
        var b = new DefaultHttpContext();
        a.Request.RouteValues["triggerId"] = "56DA5193-D2A0-4309-9ED7-EEF395D72124";
        b.Request.RouteValues["triggerId"] = "56da5193d2a043099ed7eef395d72124";
        WebhookIngressRateLimitExtension.PartitionKey(a).ShouldBe(WebhookIngressRateLimitExtension.PartitionKey(b));
        a.Request.RouteValues["triggerId"] = Guid.NewGuid();
        WebhookIngressRateLimitExtension.PartitionKey(a).ShouldNotBe(WebhookIngressRateLimitExtension.PartitionKey(b));
    }

    private sealed class UnreadableStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new InvalidOperationException("Unauthenticated body was read.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new InvalidOperationException("Unauthenticated body was read.");
    }

    private sealed class AuthenticationReads : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            command.Transaction.ShouldBeNull("cheap authentication must never start a transaction");
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
