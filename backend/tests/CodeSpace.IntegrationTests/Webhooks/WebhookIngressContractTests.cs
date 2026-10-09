using System.Net;
using System.Reflection;
using CodeSpace.Api.Controllers;
using CodeSpace.Api.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// The bounds on the anonymous ingress actions, pinned where a refactor would quietly drop them: every webhook and
/// callback action carries its body limit and the ingress rate-limit policy, every route value the policy keys on is
/// bounded in length by its route, and the limits partition first by who sent the request and then by what it is
/// addressed to. The behaviour behind each is proven over HTTP in <c>WebhookOutsiderHardeningE2ETests</c> and
/// <c>WebhookIngressSourceLimitE2ETests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WebhookIngressContractTests
{
    [Theory]
    [InlineData(typeof(WebhooksController), nameof(WebhooksController.Receive), WebhooksController.MaxDeliveryBodyBytes)]
    [InlineData(typeof(WebhooksController), nameof(WebhooksController.ReceiveConnection), WebhooksController.MaxDeliveryBodyBytes)]
    [InlineData(typeof(WorkflowCallbacksController), nameof(WorkflowCallbacksController.Resume), WorkflowCallbacksController.MaxCallbackBodyBytes)]
    public void Every_anonymous_ingress_action_bounds_its_body_and_is_rate_limited(Type controller, string action, long maxBytes)
    {
        var method = controller.GetMethod(action, BindingFlags.Instance | BindingFlags.Public).ShouldNotBeNull();

        ((IRequestSizeLimitMetadata)method.GetCustomAttribute<RequestSizeLimitAttribute>().ShouldNotBeNull()).MaxRequestBodySize.ShouldBe(maxBytes);
        controller.GetCustomAttribute<EnableRateLimitingAttribute>().ShouldNotBeNull().PolicyName.ShouldBe(WebhookIngressRateLimitExtension.PolicyName);
    }

    [Fact]
    public void The_limits_are_pinned()
    {
        // GitHub caps a payload at 25 MB; a callback is a resume signal. The ceiling sits above a busy group hook's traffic.
        WebhooksController.MaxDeliveryBodyBytes.ShouldBe(25L * 1024 * 1024);
        WorkflowCallbacksController.MaxCallbackBodyBytes.ShouldBe(1024 * 1024);
        WebhookIngressRateLimitExtension.PolicyName.ShouldBe("webhook-ingress");
        WebhookIngressRateLimitExtension.PermitsPerWindow.ShouldBe(2000);
        WebhookIngressRateLimitExtension.SourcePermitsPerWindow.ShouldBe(10_000);
        WebhookIngressRateLimitExtension.Window.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(typeof(WebhooksController), nameof(WebhooksController.Receive), "{webhookId:guid}")]
    [InlineData(typeof(WebhooksController), nameof(WebhooksController.ReceiveConnection), "connection/{connectionWebhookId:guid}")]
    [InlineData(typeof(WorkflowCallbacksController), nameof(WorkflowCallbacksController.Resume), "{token:length(32)}")]
    public void Every_route_value_the_limiter_keys_on_is_bounded_by_its_route(Type controller, string action, string template)
    {
        // The per-target limiter keys on these values before anything is looked up. Unconstrained, a token could be as
        // long as the request line, and every distinct one kept a limiter of that size alive past its window. 32 is the
        // engine's minted callback token: Guid.NewGuid().ToString("N").
        var method = controller.GetMethod(action, BindingFlags.Instance | BindingFlags.Public).ShouldNotBeNull();

        method.GetCustomAttribute<HttpPostAttribute>().ShouldNotBeNull().Template.ShouldBe(template);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData(null, "unknown")]
    public void The_source_partition_is_who_sent_the_request_and_nothing_they_chose(string? address, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues = new RouteValueDictionary { ["token"] = new string('a', 32) };
        context.Connection.RemoteIpAddress = address == null ? null : IPAddress.Parse(address);

        WebhookIngressRateLimitExtension.SourcePartitionKey(context).ShouldBe(expected);
    }

    [Theory]
    [InlineData("webhookId", "3f2c", "203.0.113.7", "3f2c|203.0.113.7")]
    [InlineData("connectionWebhookId", "9a1b", "203.0.113.7", "9a1b|203.0.113.7")]
    [InlineData("token", "tok", "198.51.100.2", "tok|198.51.100.2")]
    [InlineData("token", "tok", null, "tok|unknown")]
    [InlineData("other", "x", "198.51.100.2", "-|198.51.100.2")]
    public void A_partition_is_what_the_request_is_addressed_to_and_who_sent_it(string routeValue, string target, string? address, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues = new RouteValueDictionary { [routeValue] = target };
        context.Connection.RemoteIpAddress = address == null ? null : IPAddress.Parse(address);

        WebhookIngressRateLimitExtension.PartitionKey(context).ShouldBe(expected);
    }
}
