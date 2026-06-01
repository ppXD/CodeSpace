using System.Reflection;
using Autofac;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Mediation;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Providers.Modules;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Plugins;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Core.Settings;
using CodeSpace.Core.Settings.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace CodeSpace.Core;

public class CodeSpaceModule : Autofac.Module
{
    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly Assembly[] _assemblies;

    public CodeSpaceModule(ILogger logger, IConfiguration configuration)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _assemblies = new[] { typeof(CodeSpaceModule).Assembly };
    }

    protected override void Load(ContainerBuilder builder)
    {
        RegisterSettings(builder);
        RegisterMediator(builder);
        RegisterPersistence(builder);
        RegisterProviderModules(builder);
        RegisterPluginModules(builder);
        RegisterLLMProviderModules(builder);
        RegisterDependency(builder);
        RegisterCurrentUser(builder);
        RegisterAmbient(builder);
        RegisterVariableEncryption(builder);
    }

    /// <summary>
    /// Singleton AES-GCM encryption for the unified <c>variable</c> subsystem.
    /// Master key sourced from <see cref="Services.Variables.VariableEncryptionConfig.MasterKeyEnvVar"/>
    /// (preferred), falling back to <see cref="Services.Variables.VariableEncryptionConfig.LegacyMasterKeyEnvVar"/>
    /// so existing dev / test environments don't break during the transition.
    /// Dev fallback + WARN in Development; fail-fast everywhere else.
    /// </summary>
    private void RegisterVariableEncryption(ContainerBuilder builder)
    {
        var preferred = Environment.GetEnvironmentVariable(Services.Variables.VariableEncryptionConfig.MasterKeyEnvVar);
        var legacy = Environment.GetEnvironmentVariable(Services.Variables.VariableEncryptionConfig.LegacyMasterKeyEnvVar);
        var envValue = !string.IsNullOrWhiteSpace(preferred) ? preferred : legacy;

        var aspNetCoreEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var isDevelopment = string.Equals(aspNetCoreEnv, "Development", StringComparison.OrdinalIgnoreCase);

        byte[] masterKey;
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            masterKey = Convert.FromBase64String(envValue);
        }
        else if (isDevelopment)
        {
            Serilog.Log.Warning(
                "VariableEncryption: neither {Preferred} nor {Legacy} set; using DEVELOPMENT-ONLY fallback key. " +
                "Set {Preferred} in any non-Development deployment.",
                Services.Variables.VariableEncryptionConfig.MasterKeyEnvVar,
                Services.Variables.VariableEncryptionConfig.LegacyMasterKeyEnvVar,
                Services.Variables.VariableEncryptionConfig.MasterKeyEnvVar);
            masterKey = new byte[32];
            for (int i = 0; i < 32; i++) masterKey[i] = (byte)i;
        }
        else
        {
            throw new InvalidOperationException(
                $"{Services.Variables.VariableEncryptionConfig.MasterKeyEnvVar} must be set to a base64-encoded " +
                "32-byte AES-256 key in non-Development environments. Generate one with `openssl rand -base64 32`.");
        }

        var encryption = new Services.Variables.AesGcmVariableEncryption(masterKey);
        builder.RegisterInstance<Services.Variables.IVariableValueEncryption>(encryption).SingleInstance();
    }

    private static void RegisterAmbient(ContainerBuilder builder)
    {
        // TimeProvider — the OAuth state store + token refresh use this to compute
        // expiry; test fixtures swap it for a deterministic clock to make TTL tests reliable.
        builder.RegisterInstance(TimeProvider.System).As<TimeProvider>().SingleInstance();
    }

    private void RegisterSettings(ContainerBuilder builder)
    {
        var settingTypes = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IConfigurationSetting).IsAssignableFrom(t))
            .ToArray();

        builder.RegisterTypes(settingTypes).AsSelf().SingleInstance();
    }

    private void RegisterMediator(ContainerBuilder builder)
    {
        builder.RegisterModule(new MediatorModule(_assemblies));
    }

    private void RegisterPersistence(ContainerBuilder builder)
    {
        var connectionString = new CodeSpaceConnectionString(_configuration).Value;

        builder.Register(_ =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<CodeSpaceDbContext>();

                optionsBuilder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

                // The global User query filter (exclude bots) sits across a required TeamMembership→User
                // relationship; that's intentional (a bot membership's principal is filtered out of
                // human rosters), so silence EF's defensive warning about the interaction.
                optionsBuilder.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

                return optionsBuilder.Options;
            })
            .As<DbContextOptions<CodeSpaceDbContext>>()
            .SingleInstance();

        builder.Register(c =>
            {
                var options = c.Resolve<DbContextOptions<CodeSpaceDbContext>>();
                var currentUser = c.ResolveOptional<ICurrentUser>();
                var botVisibility = c.ResolveOptional<IBotVisibility>();
                return new CodeSpaceDbContext(options, currentUser, botVisibility);
            })
            .AsSelf()
            .As<DbContext>()
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();
    }

    private void RegisterProviderModules(ContainerBuilder builder)
    {
        var modules = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IProviderModule).IsAssignableFrom(t))
            .Select(t => (IProviderModule)Activator.CreateInstance(t)!)
            .ToList();

        foreach (var module in modules) RegisterProviderModule(builder, module);

        builder.Register(c => new ProviderModuleCatalog(modules)).As<IProviderModuleCatalog>().SingleInstance();
    }

    private static void RegisterProviderModule(ContainerBuilder builder, IProviderModule module)
    {
        foreach (var type in module.Capabilities) builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerLifetimeScope();
        foreach (var type in module.AuthStrategies) builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerLifetimeScope();
        foreach (var type in module.EventSubscriptions) builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerLifetimeScope();
        foreach (var type in module.AuxiliaryServices) builder.RegisterType(type).AsSelf().SingleInstance();
    }

    private void RegisterPluginModules(ContainerBuilder builder)
    {
        var modules = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IPluginModule).IsAssignableFrom(t))
            .Select(t => (IPluginModule)Activator.CreateInstance(t)!)
            .ToList();

        foreach (var module in modules) RegisterPluginModule(builder, module);

        builder.Register(c => new PluginModuleCatalog(modules)).As<IPluginModuleCatalog>().SingleInstance();

        // Registries that consume the union of every plugin's contributions.
        builder.RegisterType<NodeRegistry>().As<INodeRegistry>().SingleInstance();
        builder.RegisterType<RunSourceMatcherRegistry>().As<IRunSourceMatcherRegistry>().SingleInstance();
    }

    private static void RegisterPluginModule(ContainerBuilder builder, IPluginModule module)
    {
        // Nodes + matchers are singletons — stateless, manifest cached. Auxiliary services
        // pick their lifetime via IDependency markers (RegisterDependency below).
        foreach (var type in module.Nodes) builder.RegisterType(type).AsSelf().AsImplementedInterfaces().SingleInstance();
        foreach (var type in module.RunSourceMatchers) builder.RegisterType(type).AsSelf().AsImplementedInterfaces().SingleInstance();
        foreach (var type in module.AuxiliaryServices) builder.RegisterType(type).AsSelf().SingleInstance();
    }

    private void RegisterLLMProviderModules(ContainerBuilder builder)
    {
        var modules = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ILLMProviderModule).IsAssignableFrom(t))
            .Select(t => (ILLMProviderModule)Activator.CreateInstance(t)!)
            .ToList();

        foreach (var module in modules)
        {
            builder.RegisterType(module.Client).AsSelf().AsImplementedInterfaces().SingleInstance();
            foreach (var aux in module.AuxiliaryServices) builder.RegisterType(aux).AsSelf().SingleInstance();
        }

        builder.RegisterType<LLMClientRegistry>().As<ILLMClientRegistry>().SingleInstance();
    }

    private void RegisterDependency(ContainerBuilder builder)
    {
        foreach (var type in typeof(IDependency).Assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && typeof(IDependency).IsAssignableFrom(t)))
        {
            if (typeof(IScopedDependency).IsAssignableFrom(type))
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerLifetimeScope();
            else if (typeof(ISingletonDependency).IsAssignableFrom(type))
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces().SingleInstance();
            else if (typeof(ITransientDependency).IsAssignableFrom(type))
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerDependency();
            else
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces();
        }
    }

    private void RegisterCurrentUser(ContainerBuilder builder)
    {
        builder.Register<ICurrentUser>(c =>
        {
            var accessor = c.ResolveOptional<IHttpContextAccessor>();

            if (accessor?.HttpContext != null) return c.Resolve<ApiUser>();

            // No HTTP context → background work (Hangfire workers, scheduled jobs, DbUp).
            // BackgroundSeederUser holds the Admin role so tenancy bypass works the same way
            // as the seeded system user does for human admins.
            return new BackgroundSeederUser();
        }).InstancePerLifetimeScope();
    }
}
