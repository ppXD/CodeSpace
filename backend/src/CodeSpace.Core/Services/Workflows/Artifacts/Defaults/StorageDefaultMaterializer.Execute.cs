using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults;

public sealed partial class StorageDefaultMaterializer
{
    /// <summary>
    /// Gives the team its own credential, decrypted from the instance-scope envelope and re-encrypted through the
    /// credential service.
    ///
    /// <para>The envelope could be copied as bytes — <see cref="Credentials.IPayloadEncryptor"/> takes no team, so the
    /// wrapping is instance-wide and the ciphertext is portable. It is decrypted and re-created instead so the team's
    /// credential is built by the service that owns credentials, with its stable-name uniqueness, its provider
    /// agreement check and its safe hint, rather than by a second writer reaching past it. The plaintext's lifetime is
    /// this method: every write decrypts the same secret anyway to hand it to the driver.</para>
    /// </summary>
    private async Task CreateCredentialAsync(CancellationToken cancellationToken)
    {
        if (_ctx.Template.CredentialId is not { } credentialId) return;

        var envelope = await _db.StorageDefaultCredential.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == credentialId, cancellationToken).ConfigureAwait(false)
            ?? throw Halt(new StorageMaterialization.DestinationUnusable("The template names a credential that no longer exists."));

        // Re-asserted here because the edit-time guard never runs on a read path: a template repointed between two
        // materializations can pair one revision's provider with another's envelope.
        if (!string.Equals(envelope.ProviderTypeKey, _ctx.Template.ProviderTypeKey, StringComparison.Ordinal))
            throw Halt(new StorageMaterialization.DestinationUnusable(
                $"The template's credential is for provider '{envelope.ProviderTypeKey}' but the template stores to '{_ctx.Template.ProviderTypeKey}'."));

        var secret = Decrypt(envelope);
        var created = await _credentials.CreateAsync(_ctx.TeamId, _ctx.ActorId, new CreateStorageCredentialCommand
        {
            StableName = $"codespace-default-{_ctx.DataClassTypeKey.Replace('/', '-')}",
            ProviderTypeKey = _ctx.Template.ProviderTypeKey,
            Secret = secret.RootElement,
            SafeHint = envelope.SafeHint,
        }, cancellationToken).ConfigureAwait(false);

        _ctx.CredentialRef = created.CredentialRef;
    }

    private JsonDocument Decrypt(StorageDefaultCredential envelope)
    {
        try
        {
            return JsonDocument.Parse(_encryptor.Decrypt(envelope.EncryptedPayload));
        }
        catch (Exception exception) when (exception is not MaterializationHaltException)
        {
            // The envelope is unreadable by THIS deployment — a rotated master key, or a payload written by another.
            // Reported as an unusable destination rather than thrown, because the operator's fix is to re-enter the
            // secret on the template, and a crash would tell them nothing about which of the two it was.
            throw Halt(new StorageMaterialization.DestinationUnusable("The template's stored secret could not be decrypted by this deployment."));
        }
    }

    /// <summary>
    /// Assembles the team's namespace and creates the profile, then activates it.
    ///
    /// <para>Two calls rather than one because no service can produce an Active profile in a single step, and that is
    /// deliberate: a profile is Draft until someone says otherwise. The assembled config is validated in full by the
    /// profile service — with <c>required</c> intact, unlike template admission — so a namespace join that produced
    /// something the provider's own schema rejects fails HERE, before any route exists, rather than at the first write.</para>
    /// </summary>
    private async Task CreateActiveProfileAsync(CancellationToken cancellationToken)
    {
        _ctx.AssembledConfig = AssembleConfig();

        var profile = await _profiles.CreateAsync(_ctx.TeamId, _ctx.ActorId, new CreateStorageProfileCommand
        {
            StableName = $"codespace-default-{_ctx.DataClassTypeKey.Replace('/', '-')}",
            ProviderTypeKey = _ctx.Template.ProviderTypeKey,
            NonSecretConfig = _ctx.AssembledConfig,
            CredentialRef = _ctx.CredentialRef,
        }, cancellationToken).ConfigureAwait(false);

        var activated = await _profiles.SetStateAsync(_ctx.TeamId, _ctx.ActorId, new SetStorageProfileStateCommand
        {
            ProfileId = profile.Id,
            ExpectedXmin = profile.Xmin,
            ExpectedCurrentRevision = profile.CurrentRevision,
            State = StorageProfileStateValue.Active,
        }, cancellationToken).ConfigureAwait(false) ?? throw Halt(new StorageMaterialization.RaceLost());

        _ctx.ProfileId = activated.Id;
        _ctx.ProfileRevision = activated.CurrentRevision;
    }

    /// <summary>
    /// The template's partial config plus this team's namespace, written into the property the PROVIDER names. The
    /// property is never chosen here: only the provider knows which of its fields is the namespace, and template
    /// admission already refuses a config that sets it, so this can never overwrite an operator's value.
    /// </summary>
    private JsonElement AssembleConfig()
    {
        using var partial = JsonDocument.Parse(_ctx.Template.NonSecretConfigJson);
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in partial.RootElement.EnumerateObject()) property.WriteTo(writer);
            writer.WriteString(_ctx.Subdivision.TeamNamespaceProperty, _ctx.Subdivision.ComposeTeamNamespace(_ctx.Template.NamespaceRoot, _ctx.TeamSegment));
            writer.WriteEndObject();
        }

        using var assembled = JsonDocument.Parse(buffer.ToArray());
        return assembled.RootElement.Clone();
    }

    /// <summary>
    /// Writes and discards one real object at the team's own namespace, BEFORE any route names the profile.
    ///
    /// <para>This is the step the whole transaction exists for. Activating a route makes the artifact plane fail
    /// CLOSED for this data class: there is no fallback to local, so a destination that rejects the write does not
    /// degrade the team, it fails runs that would otherwise have succeeded and destroys their diffs and transcripts
    /// with them. And the route cannot be walked back — Active never returns to Draft. A probe is the only way to
    /// learn the answer while the answer can still change anything, and it qualifies the credential AND the assembled
    /// namespace together, because it exercises both.</para>
    ///
    /// <para>It runs inside the transaction, holding the team's bootstrap lock across a network round trip. That is
    /// the deliberate trade: the lock is per-team and taken only when a team has no route at all, so the contention it
    /// can cause is bounded by one bootstrap per team, while the alternative — probing outside the transaction — can
    /// only probe a profile that is already committed and therefore already undeletable.</para>
    /// </summary>
    private async Task ProveDestinationWritableAsync(CancellationToken cancellationToken)
    {
        var result = await _probe.ProbeAsync(new StorageProfileProbeRequest(_ctx.TeamId, _ctx.ProfileId, _ctx.ProfileRevision, VerifyWriteAccess: true), cancellationToken)
            .ConfigureAwait(false);

        // Exhaustive by CASE, and Available ONLY. ReadOnly means reads work and writes do not, which is precisely the
        // destination this must refuse; Degraded is an answer the probe could not make confidently, and a route
        // activated on it cannot be walked back if the doubt turns out to be real. A status added later fails to
        // compile here rather than silently qualifying.
        var refusal = result.Status switch
        {
            StorageProfileProbeStatusValue.Available => null,
            StorageProfileProbeStatusValue.ReadOnly => "The destination accepted a read but refused a write.",
            StorageProfileProbeStatusValue.Degraded => "The destination answered, but not well enough to prove it will hold this team's artifacts.",
            StorageProfileProbeStatusValue.Unavailable => "The destination could not be reached.",
            StorageProfileProbeStatusValue.Cancelled => "The destination probe did not finish.",
            _ => "The destination probe returned a status this build does not recognise.",
        };

        if (refusal == null) return;

        throw Halt(new StorageMaterialization.DestinationUnusable(Describe(refusal, result.Failure)));
    }

    /// <summary>The refusal plus whatever the provider itself said, so the operator is told which of the two to fix.</summary>
    private static string Describe(string refusal, StorageProfileProbeFailure? failure) =>
        failure == null ? refusal : $"{refusal} ({failure.Stage}/{failure.Code})";

    /// <summary>
    /// Creates the route pinned to the exact revision just activated, then activates it. Pinned rather than
    /// current-at-write so the bytes this team is about to store are addressed by the namespace this pipeline PROVED,
    /// not by whatever a later revision of the profile might name.
    /// </summary>
    private async Task CreateActiveRouteAsync(CancellationToken cancellationToken)
    {
        var route = await _routes.CreateAsync(_ctx.TeamId, _ctx.ActorId, new CreateStorageRouteCommand
        {
            DataClassTypeKey = _ctx.DataClassTypeKey,
            StorageProfileId = _ctx.ProfileId,
            ProfileRevisionMode = StorageProfileRevisionModeValue.Pinned,
            PinnedProfileRevision = _ctx.ProfileRevision,
        }, cancellationToken).ConfigureAwait(false);

        var activated = await _routes.SetStateAsync(_ctx.TeamId, _ctx.ActorId, new SetStorageRouteStateCommand
        {
            RouteId = route.Id,
            ExpectedXmin = route.Xmin,
            ExpectedCurrentRevision = route.CurrentRevision,
            State = StorageRouteStateValue.Active,
        }, cancellationToken).ConfigureAwait(false) ?? throw Halt(new StorageMaterialization.RaceLost());

        _ctx.RouteId = activated.Id;
    }

    /// <summary>
    /// The claim that this team was put on the deployment default. It is permanent and its identity columns are
    /// immutable, so a row written for a team whose route was never established would be a false claim nothing
    /// downstream could correct — and the staleness question <c>SourceRevision</c> exists to answer would report a
    /// half-adopted team as up to date.
    ///
    /// <para>What prevents that is the TRANSACTION, not this call's position in the pipeline: every step rolls back
    /// together, so the row cannot outlive a route that was not created. Writing it last is ordering for a reader,
    /// and a mutation that moves it earlier is correctly NOT caught by any test here. Were these writes ever split
    /// into separate commits, the position would become load-bearing and would need a test of its own.</para>
    /// </summary>
    private async Task RecordProvenanceAsync(CancellationToken cancellationToken)
    {
        _db.StorageDefaultMaterialization.Add(new StorageDefaultMaterialization
        {
            Id = Guid.NewGuid(),
            TeamId = _ctx.TeamId,
            DataClassTypeKey = _ctx.DataClassTypeKey,
            StorageProfileId = _ctx.ProfileId,
            SourceRevision = _ctx.Template.Revision,
            CreatedDate = _clock.GetUtcNow(),
            CreatedBy = _ctx.ActorId,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
