using System;
using System.Collections.Generic;
using System.Linq;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Makes the UNFINISHED half of the team-permission tier visible.
///
/// <para>The tier is adopted by a handful of commands; every other team-scoped write still passes on
/// membership alone, which means a Viewer can perform it. That is a deliberate staging decision, but
/// staged work that nothing counts is indistinguishable from finished work — the adopted commands
/// read like enforcement is live, and a reviewer of a later diff has no way to see the remainder.</para>
///
/// <para>So the remainder is named here. Adopting a command means deleting its line; adding a NEW
/// team write without a permission fails this test until the author either declares one or admits
/// the omission by adding the name. The count in <see cref="Adoption_is_visible"/> is the honest
/// headline: it is the number of team writes a Viewer can still perform.</para>
/// </summary>
[Trait("Category", "Unit")]
public class TeamWritePermissionAdoptionTests
{
    /// <summary>
    /// Team-scoped writes still gated by membership alone. NOT an exemption list — a work list.
    /// Every entry is a capability the matrix intends to gate and does not yet.
    /// </summary>
    private static readonly IReadOnlySet<string> AwaitingPermission = new HashSet<string>
    {
        "AddConversationMemberCommand",
        "AddCredentialCommand",
        "AddCredentialedModelCommand",
        "AddProviderInstanceCommand",
        "AnswerRunAskCommand",
        "AuthorStoreAgentCommand",
        "AuthorStoreSkillCommand",
        "BindRepositoriesBulkCommand",
        "BindRepositoryCommand",
        "CompileTaskSpecCommand",
        "ConfirmRunPlanCommand",
        "ContinueRunCommand",
        "CreateAgentDefinitionCommand",
        "CreateChannelCommand",
        "CreateGroupConversationCommand",
        "CreateProjectCommand",
        "DeleteAgentDefinitionCommand",
        "DeleteMessageCommand",
        "DeleteProjectCommand",
        "DeleteProjectVariableCommand",
        "DeleteProviderInstanceCommand",
        "DeleteSkillCommand",
        "DeleteTeamVariableCommand",
        "DeleteWorkflowCommand",
        "DeleteWorkflowVariableCommand",
        "EditMessageCommand",
        "ImportAgentPackCommand",
        "ImportPackFromUrlCommand",
        "InitCredentialOAuthCommand",
        "InstantiateAgentFromStoreCommand",
        "InstantiateSkillFromStoreCommand",
        "LaunchTaskCommand",
        "LinkProviderIdentityByPatCommand",
        "MarkConversationReadCommand",
        "MoveRepositoryToProjectCommand",
        "OpenRunPullRequestCommand",
        "PlanWorkflowFromTaskCommand",
        "PostMessageCommand",
        "RefreshCredentialedModelsCommand",
        "ReissueWaitCommand",
        "RemoveCredentialedModelCommand",
        "RenameSessionCommand",
        "ReplayRunCommand",
        "RerunMapBranchCommand",
        "RerunMapBranchesCommand",
        "RerunRunFromNodeCommand",
        "RespondToMessageCommand",
        "ResumeRunCommand",
        "RevokeModelCredentialCommand",
        "SetAgentSkillsCommand",
        "SetDefaultCredentialedModelCommand",
        "SetProjectVariableCommand",
        "SetTeamVariableCommand",
        "SetWorkflowEnabledCommand",
        "SetWorkflowVariableCommand",
        "SyncPackCommand",
        "UnlinkProviderIdentityCommand",
        "UpdateAgentDefinitionCommand",
        "UpdateModelCredentialCommand",
        "UpdateProjectCommand",
        "UpdateProviderInstanceCommand",
        "UpdateWorkflowCommand",
    };

    [Fact]
    public void Every_team_write_declares_a_permission_or_is_named_as_pending()
    {
        var unaccounted = TeamWriteCommands()
            .Where(t => !typeof(IRequireTeamPermission).IsAssignableFrom(t))
            .Where(t => !AwaitingPermission.Contains(t.Name))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        unaccounted.ShouldBeEmpty(
            "these team-scoped writes are gated by membership alone, so any Viewer can perform them. Declare the " +
            "permission from TeamPermissions that fits, or add the name to AwaitingPermission to record that the gap " +
            "is known:\n  " + string.Join("\n  ", unaccounted));
    }

    [Fact]
    public void The_pending_list_does_not_rot()
    {
        // A name that has since adopted a permission, or that no longer exists, must leave the list —
        // otherwise the headline count below overstates the remaining work and stops meaning anything.
        foreach (var name in AwaitingPermission)
        {
            var type = TeamWriteCommands().SingleOrDefault(t => t.Name == name);

            type.ShouldNotBeNull($"pending write '{name}' no longer exists — remove it from AwaitingPermission");
            typeof(IRequireTeamPermission).IsAssignableFrom(type!).ShouldBeFalse($"pending write '{name}' now declares a permission — remove it from AwaitingPermission");
        }
    }

    [Fact]
    public void Adoption_is_visible()
    {
        var writes = TeamWriteCommands().ToList();
        var adopted = writes.Count(t => typeof(IRequireTeamPermission).IsAssignableFrom(t));

        // Pinned so the number moves only when someone means it to. Raise the adopted count as the
        // sweep lands; when AwaitingPermission empties, delete this test along with it.
        adopted.ShouldBe(5);
        AwaitingPermission.Count.ShouldBe(62);
        writes.Count.ShouldBe(67, "every team write is either adopted or pending — no third state");
    }

    private static IEnumerable<Type> TeamWriteCommands()
    {
        var types = typeof(TeamPermissions).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IRequireTeamMembership).IsAssignableFrom(t))
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
            .ToList();

        types.ShouldNotBeEmpty("the reflection scan found no team writes — every check in this class would pass vacuously");

        return types;
    }
}
