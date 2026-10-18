using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.RunSources.Matchers;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>What leans on a credential, read the way a move would carry it: the same team, the same provider instance, in service.</summary>
public sealed partial class CredentialRelinkService
{
    public async Task<CredentialDependents> GetDependentsAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await LoadTeamCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false);
        var repositories = await LoadDependentRepositoriesAsync(credential, cancellationToken).ConfigureAwait(false);

        return new CredentialDependents
        {
            CredentialId = credential.Id,
            DisplayName = credential.DisplayName,
            Status = credential.Status,
            Retired = credential.DeletedDate != null,
            OwnerUserId = credential.OwnerUserId,
            ProviderInstanceId = credential.ProviderInstanceId,
            Repositories = repositories,
            ConnectionWebhooks = await LoadDependentConnectionWebhooksAsync(credential, cancellationToken).ConfigureAwait(false),
            Identities = await LoadDependentIdentitiesAsync(credential, cancellationToken).ConfigureAwait(false),
            Activations = await LoadDependentActivationsAsync(credential, repositories, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<List<DependentRepository>> LoadDependentRepositoriesAsync(Credential credential, CancellationToken cancellationToken)
    {
        var rows = await _db.Repository.AsNoTracking()
            .Where(r => r.CredentialId == credential.Id && r.TeamId == credential.TeamId && r.ProviderInstanceId == credential.ProviderInstanceId && r.DeletedDate == null)
            .OrderBy(r => r.FullPath)
            .Select(r => new { r.Id, r.FullPath, r.Status, r.LastError })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var projects = await LoadProjectsByRepositoryAsync(rows.Select(r => r.Id).ToList(), cancellationToken).ConfigureAwait(false);

        return rows.Select(r => new DependentRepository { Id = r.Id, FullPath = r.FullPath, Status = r.Status, LastError = r.LastError, Projects = projects[r.Id] }).ToList();
    }

    private async Task<Dictionary<Guid, List<DependentProject>>> LoadProjectsByRepositoryAsync(List<Guid> repositoryIds, CancellationToken cancellationToken)
    {
        var links = await _db.ProjectRepository.AsNoTracking()
            .Where(l => repositoryIds.Contains(l.RepositoryId) && l.DeletedDate == null && l.Project.DeletedDate == null)
            .OrderBy(l => l.Project.Name)
            .Select(l => new { l.RepositoryId, l.Project.Id, l.Project.Name, l.Project.Slug })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var byRepository = repositoryIds.ToDictionary(id => id, _ => new List<DependentProject>());

        foreach (var link in links) byRepository[link.RepositoryId].Add(new DependentProject { Id = link.Id, Name = link.Name, Slug = link.Slug });

        return byRepository;
    }

    private async Task<List<DependentConnectionWebhook>> LoadDependentConnectionWebhooksAsync(Credential credential, CancellationToken cancellationToken) =>
        await _db.ConnectionWebhook.AsNoTracking()
            .Where(w => w.CredentialId == credential.Id && w.ProviderInstanceId == credential.ProviderInstanceId && WebhookRegistrationLifecycle.InService.Contains(w.RegistrationStatus))
            .OrderBy(w => w.OwnerPath)
            .Select(w => new DependentConnectionWebhook { Id = w.Id, OwnerPath = w.OwnerPath, RegistrationStatus = w.RegistrationStatus })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<List<DependentIdentity>> LoadDependentIdentitiesAsync(Credential credential, CancellationToken cancellationToken)
    {
        var rows = await _db.UserProviderIdentity.AsNoTracking()
            .Where(i => i.CredentialId == credential.Id && i.DeletedDate == null)
            .OrderBy(i => i.ProviderUsername)
            .Select(i => new { i.Id, i.UserId, i.ProviderUsername })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var userIds = rows.Select(r => r.UserId).ToList();
        var names = await _db.User.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken).ConfigureAwait(false);

        return rows.Select(r => new DependentIdentity { Id = r.Id, UserId = r.UserId, UserName = names.GetValueOrDefault(r.UserId), ProviderUsername = r.ProviderUsername }).ToList();
    }

    /// <summary>
    /// The team's live triggers that name any of the repositories. Read from the activation's own config the way the
    /// matchers read it (<see cref="ActivationRepositoryReferences"/>), so a shape they learn is learned here: a trigger
    /// that names a stranded repository fires on nothing until the repository is bound again.
    /// </summary>
    private async Task<List<DependentActivation>> LoadDependentActivationsAsync(Credential credential, List<DependentRepository> repositories, CancellationToken cancellationToken)
    {
        if (repositories.Count == 0) return new List<DependentActivation>();

        var rows = await _db.WorkflowActivation.AsNoTracking()
            .Where(a => a.DeletedDate == null && a.Workflow.TeamId == credential.TeamId && a.Workflow.DeletedDate == null)
            .OrderBy(a => a.Workflow.Name).ThenBy(a => a.TypeKey)
            .Select(a => new { a.Id, a.WorkflowId, WorkflowName = a.Workflow.Name, a.TypeKey, a.Enabled, a.ConfigJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var bound = repositories.Select(r => r.Id).ToHashSet();

        return rows
            .Select(a => new { Row = a, Named = NamedAmong(a.ConfigJson, bound) })
            .Where(a => a.Named.Count > 0)
            .Select(a => new DependentActivation { ActivationId = a.Row.Id, WorkflowId = a.Row.WorkflowId, WorkflowName = a.Row.WorkflowName, TypeKey = a.Row.TypeKey, Enabled = a.Row.Enabled, RepositoryIds = a.Named })
            .ToList();
    }

    /// <summary>The bound repositories an activation config names. A config that is not JSON names none.</summary>
    private static List<Guid> NamedAmong(string configJson, HashSet<Guid> bound)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);

            return ActivationRepositoryReferences.Read(document.RootElement).Where(bound.Contains).ToList();
        }
        catch (JsonException)
        {
            return new List<Guid>();
        }
    }
}
