using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Budget;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Budget;

/// <summary>Set (or replace) the current team's standing cost cap. The team comes from <c>X-Team-Id</c>, never the body.</summary>
public sealed record SetTeamCostCapCommand : ICommand<TeamCostCap>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.BudgetManage;

    public required decimal CapUsd { get; init; }
}

/// <summary>Remove the current team's cap, dropping it back to the deployment fallback (or to a per-run ceiling only when there is none).</summary>
public sealed record ClearTeamCostCapCommand : ICommand<Unit>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.BudgetManage;
}
