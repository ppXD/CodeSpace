using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins <see cref="AllowedModelPool.Choose"/> — the one choice the launch and the dispatch both make of which pooled
/// ROW an authored pin runs on. A model name is unique only per credential, so the pin is honoured from its most
/// specific form down, and a note — worded by cause, naming model and credential — appears exactly when something moved.
/// </summary>
[Trait("Category", "Unit")]
public class AllowedModelPoolTests
{
    private static readonly Guid CredA = Guid.NewGuid();
    private static readonly Guid CredB = Guid.NewGuid();

    private static PooledModelRow Row(Guid id, string model, Guid credential, string credentialName, bool usable = true, ModelCapabilityTier? tier = null) =>
        new() { Id = id, ModelId = model, ModelCredentialId = credential, CredentialName = credentialName, Provider = "Anthropic", Usable = usable, CapabilityTier = tier };

    /// <summary>Two pooled rows carrying the SAME model name on two credentials — the smaller id first, which is the row a name lookup's tie-break used to pick.</summary>
    private static (PooledModelRow Smaller, PooledModelRow Larger) SameNamePair()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.OrderBy(id => id).ToArray();
        return (Row(ids[0], "sonnet", CredB, "Gateway key"), Row(ids[1], "sonnet", CredA, "Direct key"));
    }

    [Fact]
    public void A_pinned_row_is_honoured_over_a_same_named_row_on_another_credential()
    {
        var (smaller, larger) = SameNamePair();

        var choice = AllowedModelPool.Choose([smaller.Id, larger.Id], [smaller, larger], new ModelPin(larger.Id, "sonnet", CredA));

        choice.Row.ShouldBe(larger, "the operator pinned THIS row — a name lookup's row-id tie-break must never swap it for its twin on another key");
        choice.Note.ShouldBeNull("nothing moved");
    }

    [Fact]
    public void A_pinned_model_on_a_pinned_credential_runs_that_pair()
    {
        var (smaller, larger) = SameNamePair();

        AllowedModelPool.Choose([smaller.Id, larger.Id], [smaller, larger], new ModelPin(null, "sonnet", CredA)).Row.ShouldBe(larger);
    }

    [Fact]
    public void The_same_model_on_another_credential_says_the_credential_moved()
    {
        var onlyOnB = Row(Guid.NewGuid(), "sonnet", CredB, "Gateway key");
        var direct = Row(Guid.NewGuid(), "haiku", CredA, "Direct key");

        var choice = AllowedModelPool.Choose([onlyOnB.Id, direct.Id], [onlyOnB, direct], new ModelPin(null, "sonnet", CredA));

        choice.Row.ShouldBe(onlyOnB);
        choice.Note.ShouldBe("Model 'sonnet' has no usable row on credential 'Direct key' in this run's allowed model pool; running 'sonnet' on 'Gateway key' instead.");
    }

    [Fact]
    public void A_model_outside_the_pool_runs_the_pools_default_and_names_both_ends()
    {
        var pooled = Row(Guid.NewGuid(), "haiku", CredA, "Direct key");
        var outside = Row(Guid.NewGuid(), "opus", CredA, "Direct key");

        var choice = AllowedModelPool.Choose([pooled.Id], [pooled, outside], new ModelPin(outside.Id, "opus", CredA));

        choice.Row.ShouldBe(pooled);
        choice.Note.ShouldBe("Model 'opus' on 'Direct key' is not in this run's allowed model pool; running 'haiku' on 'Direct key' instead.", "the reason names the model and credential, never a row id");
    }

    [Fact]
    public void A_pooled_row_that_can_no_longer_run_is_named_by_that_cause_not_as_outside_the_pool()
    {
        var disabled = Row(Guid.NewGuid(), "opus", CredA, "Direct key", usable: false);
        var fallback = Row(Guid.NewGuid(), "haiku", CredB, "Gateway key");

        var choice = AllowedModelPool.Choose([disabled.Id, fallback.Id], [disabled, fallback], new ModelPin(disabled.Id, "opus", CredA));

        choice.Row.ShouldBe(fallback);
        choice.Note.ShouldBe("Model 'opus' on 'Direct key' is in this run's allowed model pool but disabled, or its credential is no longer active; running 'haiku' on 'Gateway key' instead.");
    }

    [Fact]
    public void A_credential_only_pin_runs_that_credentials_best_pooled_row()
    {
        // A persona can supply just a credential: the pool is honoured AND the credential is, when the pool holds it.
        var basic = Row(Guid.NewGuid(), "haiku", CredA, "Direct key", tier: ModelCapabilityTier.Basic);
        var strong = Row(Guid.NewGuid(), "sonnet", CredA, "Direct key", tier: ModelCapabilityTier.Strong);
        var elsewhere = Row(Guid.NewGuid(), "gpt", CredB, "Gateway key", tier: ModelCapabilityTier.Strong);

        var choice = AllowedModelPool.Choose([basic.Id, strong.Id, elsewhere.Id], [basic, strong, elsewhere], new ModelPin(null, null, CredA));

        choice.Row.ShouldBe(strong, "the agent-plane ranking inside the pinned credential's pooled rows");
        choice.Note.ShouldBeNull();
    }

    [Fact]
    public void A_credential_only_pin_the_pool_does_not_hold_says_so()
    {
        var elsewhere = Row(Guid.NewGuid(), "gpt", CredB, "Gateway key");

        var choice = AllowedModelPool.Choose([elsewhere.Id], [elsewhere], new ModelPin(null, null, CredA));

        choice.Row.ShouldBe(elsewhere);
        choice.Note.ShouldBe("The pinned credential carries no usable model in this run's allowed model pool; running 'gpt' on 'Gateway key' instead.");
    }

    [Fact]
    public void Nothing_authored_runs_the_pools_default_with_no_note()
    {
        var pooled = Row(Guid.NewGuid(), "haiku", CredA, "Direct key");

        var choice = AllowedModelPool.Choose([pooled.Id], [pooled], new ModelPin(null, null, null));

        choice.Row.ShouldBe(pooled);
        choice.Note.ShouldBeNull("the pool's default is simply the model that runs — nothing authored moved");
    }

    [Fact]
    public void A_pool_with_nothing_usable_chooses_no_row_and_says_why()
    {
        var disabled = Row(Guid.NewGuid(), "haiku", CredA, "Direct key", usable: false);

        var choice = AllowedModelPool.Choose([disabled.Id], [disabled], new ModelPin(null, "haiku", null));

        choice.Row.ShouldBeNull();
        choice.Note.ShouldBe(AllowedModelPool.NothingRunsNote);
    }

    [Fact]
    public void A_pinned_row_outside_the_pool_is_read_so_its_note_can_name_it()
    {
        var pinned = Guid.NewGuid();
        var pool = new[] { Guid.NewGuid() };

        AllowedModelPool.RowsToRead(pool, new ModelPin(pinned, null, null)).ShouldBe([pool[0], pinned]);
        AllowedModelPool.RowsToRead(pool, new ModelPin(pool[0], null, null)).ShouldBe(pool);
    }
}
