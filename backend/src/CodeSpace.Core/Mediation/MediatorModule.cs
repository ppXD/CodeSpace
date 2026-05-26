using System.Reflection;
using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Middlewares.Logging;
using CodeSpace.Core.Middlewares.Transactional;
using MediatR;
using MediatR.Extensions.Autofac.DependencyInjection;
using MediatR.Extensions.Autofac.DependencyInjection.Builder;

namespace CodeSpace.Core.Mediation;

public class MediatorModule : Autofac.Module
{
    private readonly Assembly[] _assemblies;

    public MediatorModule(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        _assemblies = assemblies;
    }

    protected override void Load(ContainerBuilder builder)
    {
        var configuration = MediatRConfigurationBuilder.Create(_assemblies).WithAllOpenGenericHandlerTypesRegistered().Build();

        builder.RegisterMediatR(configuration);

        // Pipeline order (outermost first):
        //   Logging                — scopes log properties around the whole call
        //   AuthenticatedUser      — cheapest auth check (just principal.Id != null)
        //   PasswordRotationGuard  — blocks everything except ChangePasswordCommand while
        //                             the user has password_must_change=true
        //   GlobalAdmin            — for IRequireGlobalAdmin commands, no DB hit
        //   TeamMembership / Repo / Credential — DB-backed tenancy checks
        //   Transactional          — innermost, wraps DB writes only after auth has passed
        builder.RegisterGeneric(typeof(LoggingBehavior<,>)).As(typeof(IPipelineBehavior<,>)).InstancePerLifetimeScope();
        builder.RegisterGeneric(typeof(AuthenticatedUserAuthorizationBehavior<,>)).As(typeof(IPipelineBehavior<,>)).InstancePerLifetimeScope();
        builder.RegisterGeneric(typeof(PasswordRotationRequiredBehavior<,>)).As(typeof(IPipelineBehavior<,>)).InstancePerLifetimeScope();
        builder.RegisterGeneric(typeof(GlobalAdminAuthorizationBehavior<,>)).As(typeof(IPipelineBehavior<,>)).InstancePerLifetimeScope();
        builder.RegisterGeneric(typeof(TeamMembershipAuthorizationBehavior<,>)).As(typeof(IPipelineBehavior<,>)).InstancePerLifetimeScope();
        builder.RegisterGeneric(typeof(RepositoryAccessAuthorizationBehavior<,>)).As(typeof(IPipelineBehavior<,>)).InstancePerLifetimeScope();
        builder.RegisterGeneric(typeof(CredentialAccessAuthorizationBehavior<,>)).As(typeof(IPipelineBehavior<,>)).InstancePerLifetimeScope();
        builder.RegisterGeneric(typeof(TransactionalBehavior<,>)).As(typeof(IPipelineBehavior<,>)).InstancePerLifetimeScope();
    }
}
