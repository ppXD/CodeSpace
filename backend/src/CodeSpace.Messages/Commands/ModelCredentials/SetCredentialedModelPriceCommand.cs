using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ModelCredentials;

/// <summary>
/// D1 — price ONE model on a credential: USD per 1,000,000 input / output tokens. This is what makes a cost cap
/// enforceable for a model the built-in price table never heard of (every Codex / OpenAI / Custom-gateway id); a run
/// with a cap refuses to spend on a model nobody can price.
///
/// <para>Both ids come from the route (Rule 17); the two prices come from the body. Passing BOTH as null CLEARS the
/// price (back to the env override / built-in table) — there is no separate "clear" verb. Setting only one is
/// rejected: half a price prices nothing, and silently keeping the other half would look priced without being it.</para>
/// </summary>
public sealed record SetCredentialedModelPriceCommand : ICommand<Guid>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.ModelsManage;

    public Guid ModelCredentialId { get; init; }
    public Guid ModelRowId { get; init; }

    /// <summary>USD per 1,000,000 input (prompt) tokens. Null (with <see cref="OutputUsdPerMillion"/> also null) clears the price.</summary>
    public decimal? InputUsdPerMillion { get; init; }

    /// <summary>USD per 1,000,000 output (completion) tokens. Null (with <see cref="InputUsdPerMillion"/> also null) clears the price.</summary>
    public decimal? OutputUsdPerMillion { get; init; }
}
