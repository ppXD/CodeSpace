using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.Modules;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Modules;

public class ProviderModuleCatalogTests
{
    [Fact]
    public void Get_returns_module_for_registered_kind()
    {
        var catalog = new ProviderModuleCatalog(new IProviderModule[] { new GitHubProviderModule(), new GitLabProviderModule() });

        catalog.Get(ProviderKind.GitHub).ShouldBeOfType<GitHubProviderModule>();
        catalog.Get(ProviderKind.GitLab).ShouldBeOfType<GitLabProviderModule>();
    }

    [Fact]
    public void Get_returns_null_for_unregistered_kind()
    {
        var catalog = new ProviderModuleCatalog(new IProviderModule[] { new GitHubProviderModule() });

        catalog.Get(ProviderKind.GitLab).ShouldBeNull();
    }

    [Fact]
    public void Modules_lists_all_loaded_modules()
    {
        var catalog = new ProviderModuleCatalog(new IProviderModule[] { new GitHubProviderModule(), new GitLabProviderModule() });

        catalog.Modules.Count.ShouldBe(2);
    }

    [Fact]
    public void Constructor_throws_when_two_modules_claim_same_kind()
    {
        var act = () => new ProviderModuleCatalog(new IProviderModule[] { new GitHubProviderModule(), new GitHubProviderModule() });

        act.ShouldThrow<ArgumentException>();
    }

    // ── Drift detectors — catch "added a class but forgot to declare it in the module" ──

    [Fact]
    public void GitHubProviderModule_declares_every_GitHub_auth_strategy_in_assembly()
    {
        var module = new GitHubProviderModule();
        var declared = module.AuthStrategies.ToHashSet();
        var actual = AssemblyAuthStrategiesInNamespace("CodeSpace.Core.Services.Providers.GitHub");

        actual.Except(declared).ShouldBeEmpty($"Add the missing class(es) to {nameof(GitHubProviderModule)}.{nameof(IProviderModule.AuthStrategies)}");
    }

    [Fact]
    public void GitLabProviderModule_declares_every_GitLab_auth_strategy_in_assembly()
    {
        var module = new GitLabProviderModule();
        var declared = module.AuthStrategies.ToHashSet();
        var actual = AssemblyAuthStrategiesInNamespace("CodeSpace.Core.Services.Providers.GitLab");

        actual.Except(declared).ShouldBeEmpty($"Add the missing class(es) to {nameof(GitLabProviderModule)}.{nameof(IProviderModule.AuthStrategies)}");
    }

    [Fact]
    public void GitHubProviderModule_declares_every_GitHub_event_subscription_in_assembly()
    {
        var module = new GitHubProviderModule();
        var declared = module.EventSubscriptions.ToHashSet();
        var actual = AssemblyEventSubscriptionsInNamespace("CodeSpace.Core.Services.Providers.GitHub");

        actual.Except(declared).ShouldBeEmpty($"Add the missing class(es) to {nameof(GitHubProviderModule)}.{nameof(IProviderModule.EventSubscriptions)}");
    }

    [Fact]
    public void GitLabProviderModule_declares_every_GitLab_event_subscription_in_assembly()
    {
        var module = new GitLabProviderModule();
        var declared = module.EventSubscriptions.ToHashSet();
        var actual = AssemblyEventSubscriptionsInNamespace("CodeSpace.Core.Services.Providers.GitLab");

        actual.Except(declared).ShouldBeEmpty($"Add the missing class(es) to {nameof(GitLabProviderModule)}.{nameof(IProviderModule.EventSubscriptions)}");
    }

    private static HashSet<Type> AssemblyEventSubscriptionsInNamespace(string nsPrefix) => typeof(GitHubProviderModule).Assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && typeof(IProviderEventSubscription).IsAssignableFrom(t))
        .Where(t => t.Namespace?.StartsWith(nsPrefix, StringComparison.Ordinal) == true)
        .ToHashSet();

    [Fact]
    public void Module_descriptors_have_distinct_kinds()
    {
        var moduleTypes = typeof(GitHubProviderModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IProviderModule).IsAssignableFrom(t))
            .Select(t => (IProviderModule)Activator.CreateInstance(t)!)
            .ToList();

        moduleTypes.Select(m => m.Kind).Distinct().Count().ShouldBe(moduleTypes.Count);
    }

    private static HashSet<Type> AssemblyAuthStrategiesInNamespace(string nsPrefix) => typeof(GitHubProviderModule).Assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && typeof(IProviderAuthStrategy).IsAssignableFrom(t))
        .Where(t => t.Namespace?.StartsWith(nsPrefix, StringComparison.Ordinal) == true)
        .ToHashSet();
}
