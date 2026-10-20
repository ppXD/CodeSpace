using System.Net;
using System.Reflection;
using CodeSpace.Api.Controllers;
using CodeSpace.Api.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Trait("Category", "Integration")]
public sealed class ExternalTriggerIngressContractTests
{
    [Fact]
    public void The_ext_route_is_anonymous_bounded_and_rate_limited_like_webhook_ingress()
    {
        var controller = typeof(WebhooksController).Assembly.GetType("CodeSpace.Api.Controllers.ExternalTriggersController").ShouldNotBeNull();
        controller.GetCustomAttribute<ApiControllerAttribute>().ShouldNotBeNull();
        controller.GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
        controller.GetCustomAttribute<RouteAttribute>().ShouldNotBeNull().Template.ShouldBe("api/ext");
        var method = controller.GetMethod("Run").ShouldNotBeNull();
        method.GetCustomAttribute<HttpPostAttribute>().ShouldNotBeNull().Template.ShouldBe("{slug}/run");
        ((IRequestSizeLimitMetadata)method.GetCustomAttribute<RequestSizeLimitAttribute>().ShouldNotBeNull()).MaxRequestBodySize.ShouldBe(WebhooksController.MaxDeliveryBodyBytes);
        var policy = method.GetCustomAttribute<EnableRateLimitingAttribute>() ?? controller.GetCustomAttribute<EnableRateLimitingAttribute>();
        policy.ShouldNotBeNull().PolicyName.ShouldBe(WebhookIngressRateLimitExtension.PolicyName);
    }

    [Fact]
    public void The_target_partition_uses_the_slug_and_source_address()
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues = new RouteValueDictionary { ["slug"] = "alerts" };
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        WebhookIngressRateLimitExtension.PartitionKey(context).ShouldBe("alerts|203.0.113.7");
    }
}
