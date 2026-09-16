using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using CodeSpace.Core;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Mediation;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using MediatR.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Pins the order of the MediatR pipeline, because one pair in it is load-bearing and nothing else states it.
///
/// <para><c>RequestFailureObserver</c> is registered as an <c>IRequestExceptionAction</c> — an observer that CANNOT
/// swallow — and the reason it must never be MediatR's sibling <c>IRequestExceptionHandler</c> is that the exception
/// processors run INSIDE <c>TransactionalBehavior</c>: an exception marked handled there returns normally back up
/// through the transaction, which then COMMITS the very work that failed. That claim was prose in two files and
/// pinned by nothing, while the order itself is decided by how Autofac happens to enumerate two registration passes
/// in <see cref="MediatorModule"/> (<c>RegisterMediatR</c>'s behaviours and the explicit <c>RegisterGeneric</c>
/// ones). Reorder those, or swap the container, and the prose silently becomes false.</para>
///
/// <para>Measured by resolving the REAL module's <c>IEnumerable&lt;IPipelineBehavior&lt;,&gt;&gt;</c> — the same
/// enumerable MediatR folds into the chain, first element outermost — rather than by reading the registration list,
/// which is not the same thing and was the assumption worth checking.</para>
/// </summary>
[Trait("Category", "Unit")]
public class MediatorPipelineOrderTests
{
    /// <summary>
    /// Outermost first, for a command carrying the deepest marker chain the module gates on
    /// (<c>ICommand&lt;T&gt;</c> + <c>IRequireTeamPermission</c>). The line that matters is the last three:
    /// the exception processors are INSIDE <see cref="TransactionalBehavior{TRequest,TResponse}"/>.
    /// </summary>
    private static readonly string[] ExpectedOrder =
    [
        Name(typeof(CodeSpace.Core.Middlewares.Logging.LoggingBehavior<,>)),
        Name(typeof(PasswordRotationRequiredBehavior<,>)),
        Name(typeof(TeamMembershipAuthorizationBehavior<,>)),
        Name(typeof(TeamPermissionAuthorizationBehavior<,>)),
        Name(typeof(CodeSpace.Core.Middlewares.Visibility.BotVisibilityBehavior<,>)),
        Name(typeof(TransactionalBehavior<,>)),
        Name(typeof(RequestPostProcessorBehavior<,>)),
        Name(typeof(RequestPreProcessorBehavior<,>)),
        Name(typeof(RequestExceptionActionProcessorBehavior<,>)),
        Name(typeof(RequestExceptionProcessorBehavior<,>)),
    ];

    [Fact]
    public void The_exception_processors_run_inside_the_transaction()
    {
        var order = ResolvedPipeline();

        order.ShouldBe(ExpectedOrder, ignoreOrder: false,
            customMessage: "the MediatR pipeline order changed. It is not decorative: RequestFailureObserver is an " +
                           "IRequestExceptionAction precisely BECAUSE the exception processors run inside " +
                           "TransactionalBehavior, and both files say so in prose. If this list is now right and the " +
                           "prose is wrong, fix the prose in MediatorModule and RequestFailureObserver in the same " +
                           $"change.\n  expected: {string.Join(" > ", ExpectedOrder)}\n  actual:   {string.Join(" > ", order)}");

        order.IndexOf(Name(typeof(TransactionalBehavior<,>)))
            .ShouldBeLessThan(order.IndexOf(Name(typeof(RequestExceptionProcessorBehavior<,>))),
                customMessage: "the transaction must wrap the exception processors, not the other way round");
    }

    /// <summary>
    /// The landmine the order above arms. MediatR invokes <c>IRequestExceptionHandler</c> from the same processor
    /// that sits inside the transaction, and a handler that marks an exception handled makes <c>Send</c> return a
    /// response — so <c>TransactionalBehavior</c> never sees a failure and commits. The first one anybody adds must
    /// be a deliberate decision, not an inherited one, so the count is pinned at zero.
    /// </summary>
    [Fact]
    public void No_request_exception_handler_exists()
    {
        var handlers = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestExceptionHandler<,,>)))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        handlers.ShouldBeEmpty(
            "IRequestExceptionHandler can mark an exception HANDLED, and its processor runs inside " +
            "TransactionalBehavior — a handled exception therefore returns normally and the behavior commits the " +
            "transaction that had just failed. Observe failures with IRequestExceptionAction (RequestFailureObserver) " +
            "instead. If a handler is genuinely wanted, move TransactionalBehavior inside the processors first:\n  " +
            string.Join("\n  ", handlers));
    }

    /// <summary>A generic behaviour's name without its arity suffix, so the pinned list survives a rename but not a reorder.</summary>
    private static string Name(Type behavior) => behavior.Name.Split('`')[0];

    /// <summary>The behaviour names MediatR would fold, outermost first — resolved from the real module so a registration change is re-measured rather than re-assumed.</summary>
    private static List<string> ResolvedPipeline()
    {
        var builder = new ContainerBuilder();

        builder.RegisterModule(new MediatorModule(typeof(CodeSpaceModule).Assembly));
        RegisterBehaviorDependencies(builder);

        var behaviors = builder.Build().Resolve<IEnumerable<IPipelineBehavior<CancelRunCommand, CancelRunOutcome?>>>().ToList();

        behaviors.ShouldNotBeEmpty("the container resolved no pipeline behaviour at all — every check here would pass vacuously");

        return behaviors.Select(b => b.GetType().Name.Split('`')[0]).ToList();
    }

    /// <summary>Only what the behaviours' constructors need. Nothing here is exercised: the order under test is a property of the registrations, not of what the behaviours do.</summary>
    private static void RegisterBehaviorDependencies(ContainerBuilder builder)
    {
        builder.RegisterInstance(NullLoggerFactory.Instance).As<ILoggerFactory>();
        builder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>));
        builder.RegisterType<UnauthenticatedUser>().As<ICurrentUser>();
        builder.RegisterType<UnsetTeam>().As<ICurrentTeam>();
        builder.RegisterType<BotVisibility>().As<IBotVisibility>();
        builder.RegisterType<PostCommitActions>().As<IPostCommitActions>();
        builder.RegisterType<TeamMembershipResolver>().AsSelf();
        builder.Register(_ => new CodeSpaceDbContext(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseInMemoryDatabase(nameof(MediatorPipelineOrderTests)).Options)).AsSelf();
    }

    private sealed class UnauthenticatedUser : ICurrentUser
    {
        public Guid? Id => null;
        public string Name => nameof(UnauthenticatedUser);
        public IReadOnlyList<string> Roles => [];
        public IReadOnlyList<string> Permissions => [];
        public bool PasswordMustChange => false;
        public bool HasRole(string role) => false;
        public bool HasPermission(string permission) => false;
    }

    private sealed class UnsetTeam : ICurrentTeam
    {
        public Guid? Id => null;
        public bool IsSet => false;
    }
}
