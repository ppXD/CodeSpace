using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>Authenticated, team-scoped storage discovery and admin control-plane surfaces.</summary>
[ApiController]
[Route("api/storage")]
public class StorageController : ControllerBase
{
    internal const long MaxMutationBodyBytes = 128 * 1024;
    private readonly IMediator _mediator;

    public StorageController(IMediator mediator) { _mediator = mediator; }

    /// <summary>
    /// Provider types available in this build, including public configuration and write-only-input schemas. Returns
    /// descriptor metadata only — never profile/secret values or the module's runtime factory type.
    /// </summary>
    [HttpGet("provider-modules")]
    public async Task<IActionResult> ListProviderModules(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListStorageProviderModulesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// The versioned data classes a storage route may name in this build. A route for any other key would list as
    /// configured storage that no runtime consumer ever asks for, so this is the exact set the picker may offer.
    /// </summary>
    [HttpGet("data-classes")]
    public async Task<IActionResult> ListDataClasses(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListRoutedDataClassesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("profiles")]
    public async Task<IActionResult> ListProfiles(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListStorageProfilesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("placements/integrity")]
    public async Task<IActionResult> GetPlacementIntegrity(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetPlacementIntegrityQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("profiles/page")]
    public async Task<IActionResult> ListProfilePage([FromQuery] ListStorageProfilePageQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("profiles/{profileId:guid}")]
    public async Task<IActionResult> GetProfile([FromRoute] Guid profileId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetStorageProfileQuery { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet("profiles/{profileId:guid}/placements")]
    public async Task<IActionResult> ListProfilePlacements([FromRoute] Guid profileId, [FromQuery] ListProfilePlacementsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("profiles/{profileId:guid}/placements/totals")]
    public async Task<IActionResult> GetProfilePlacementTotals([FromRoute] Guid profileId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetProfilePlacementTotalsQuery { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// What this profile can still say about the artifact rows written before the CAS plane. Report-only: the pass
    /// resolves and asks, and writes nothing.
    /// </summary>
    [HttpGet("profiles/{profileId:guid}/legacy-placements")]
    public async Task<IActionResult> GetLegacyPlacementSurvey([FromRoute] Guid profileId, [FromQuery] GetLegacyPlacementSurveyQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Runs one bounded phase-two pass. Evidence validates every member of one sealed manifest and retains its
    /// smallest confirmed destination witness; only the final Evidence page admits idempotent sidecar minting.
    /// Neither phase relinks an immutable legacy row.
    /// </summary>
    [HttpPost("profiles/{profileId:guid}/legacy-placements/adopt")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> AdoptLegacyPlacements([FromRoute] Guid profileId, [FromBody] AdoptLegacyPlacementsCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("profiles/{profileId:guid}/placements/abandon")]
    public async Task<IActionResult> AbandonProfilePlacements([FromRoute] Guid profileId, [FromBody] AbandonProfilePlacementsCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("profiles")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> CreateProfile([FromBody] CreateStorageProfileCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("profiles/{profileId:guid}/revisions")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> AppendProfileRevision([FromRoute] Guid profileId, [FromBody] AppendStorageProfileRevisionCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPut("profiles/{profileId:guid}/state")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> SetProfileState([FromRoute] Guid profileId, [FromBody] SetStorageProfileStateCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Qualifies provider configuration and its secret against the real destination, persisting nothing.
    ///
    /// <para>Not addressed under a profile because there is no profile: this is the answer an operator needs BEFORE
    /// one exists. A storage profile cannot be deleted, so testing a key by saving one first is how a mistyped secret
    /// becomes a row nobody can remove.</para>
    /// </summary>
    [HttpPost("probes")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> ProbeConfiguration([FromBody] ProbeStorageConfigurationCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("profiles/{profileId:guid}/probe")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> ProbeProfile([FromRoute] Guid profileId, [FromBody] ProbeStorageProfileCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("routes/page")]
    public async Task<IActionResult> ListRoutePage([FromQuery] ListStorageRoutePageQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("routes/{routeId:guid}")]
    public async Task<IActionResult> GetRoute([FromRoute] Guid routeId, [FromQuery] GetStorageRouteQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RouteId = routeId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost("routes")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> CreateRoute([FromBody] CreateStorageRouteCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("routes/{routeId:guid}/revisions")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> AppendRouteRevision([FromRoute] Guid routeId, [FromBody] AppendStorageRouteRevisionCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { RouteId = routeId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPut("routes/{routeId:guid}/state")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> SetRouteState([FromRoute] Guid routeId, [FromBody] SetStorageRouteStateCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { RouteId = routeId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet("credentials")]
    public async Task<IActionResult> ListCredentials(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListStorageCredentialsQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("credentials/page")]
    public async Task<IActionResult> ListCredentialPage([FromQuery] ListStorageCredentialPageQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("credentials/{credentialId:guid}")]
    public async Task<IActionResult> GetCredential([FromRoute] Guid credentialId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetStorageCredentialQuery { CredentialId = credentialId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost("credentials")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> CreateCredential([FromBody] CreateStorageCredentialCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("credentials/{credentialId:guid}/revisions")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> AppendCredentialRevision([FromRoute] Guid credentialId, [FromBody] AppendStorageCredentialRevisionCommand command, CancellationToken cancellationToken)
    {
        command.CredentialId = credentialId;
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost("credentials/{credentialId:guid}/revoke")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> RevokeCredential([FromRoute] Guid credentialId, [FromBody] RevokeStorageCredentialCommand command, CancellationToken cancellationToken)
    {
        command.CredentialId = credentialId;
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>Where this team stands on the deployment's default for every routed data class.</summary>
    [HttpGet("adoptions")]
    public async Task<IActionResult> ListAdoptions(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListStorageAdoptionsQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Takes this team onto the deployment's default for one data class.
    ///
    /// <para>Always 200 with a named outcome, never 404 or 409. "The deployment authored no default", "this team
    /// already adopted it" and "the destination refused a write" are all answers a Settings screen has to render
    /// differently, and a status code collapses them into an error the screen can only apologise for.</para>
    /// </summary>
    [HttpPost("adoptions")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> Adopt([FromBody] AdoptStorageDefaultCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
