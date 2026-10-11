using System.Reflection;
using CodeSpace.Api.Controllers;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeSpace.IntegrationTests.Authorization;

/// <summary>
/// Every API controller reaches the application through <see cref="IMediator"/> and nothing else.
///
/// <para>Why pin it: team membership, repository access, credential access and team permissions are all MediatR
/// pipeline behaviors, selected by the marker on the request. A controller that resolves a service directly skips
/// every one of them, and nothing in the diff says so. The run-ledger SSE stream did exactly that: it injected the
/// record streamer, so <c>X-Team-Id</c> was trusted as written and a removed member, an outsider, or a member of a
/// deleted team could live-tail any run whose id they knew. This makes the next such dependency a build-time
/// failure instead of an audit finding.</para>
///
/// <para>Category=Unit: pure reflection over the API assembly, no database. It lives in this project only because the
/// unit project does not reference <c>CodeSpace.Api</c>.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ControllersDispatchThroughMediatorConventionTests
{
    [Fact]
    public void Every_controller_constructor_takes_only_the_mediator()
    {
        var offenders = Controllers()
            .SelectMany(controller => controller.GetConstructors().SelectMany(ctor => ctor.GetParameters()).Where(p => p.ParameterType != typeof(IMediator)).Select(p => $"{controller.Name}({p.ParameterType.Name} {p.Name})"))
            .ToList();

        offenders.ShouldBeEmpty(
            "these controllers depend on something other than IMediator, so their actions can reach application code without " +
            "any authorization behavior running. Put the work behind a request carrying the right IRequire* marker:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_controller_action_injects_a_service_per_request()
    {
        var offenders = Controllers()
            .SelectMany(controller => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(action => action.GetParameters().Where(p => p.GetCustomAttribute<FromServicesAttribute>() != null).Select(p => $"{controller.Name}.{action.Name}([FromServices] {p.ParameterType.Name})")))
            .ToList();

        offenders.ShouldBeEmpty("[FromServices] on an action is the same mediator bypass as a constructor dependency:\n  " + string.Join("\n  ", offenders));
    }

    private static IReadOnlyList<Type> Controllers()
    {
        var controllers = typeof(WorkflowRunsController).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

        controllers.ShouldContain(typeof(WorkflowRunsController), "the scan must see the API controllers or every check here passes vacuously");

        return controllers;
    }
}
