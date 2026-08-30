using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

/// <summary>
/// What a closed placement's record says about WHY it closed.
///
/// <para>Deleting the bytes and abandoning the record land on the same state through the same finalize, so the verb
/// is the only thing that tells an operator whether the destination still holds anything — and the observation is
/// the only thing that tells them what was seen before the record was closed on it.</para>
///
/// <para>The ledger is readable by anyone who can read the database, so what may be written there is fixed by SHAPE:
/// an observation names a coordinate the row already carries and a provider code, because those are the only
/// parameters its authors take. A bearer that reached this column would be durable, replicated and unnoticed, and
/// the way it would arrive is a factory that accepted prose — which is what this pins.</para>
/// </summary>
public sealed class ClosureDetailsTests
{
    private const string ObjectKey = "artifacts/9f2c";
    private const BindingFlags AnyAccess = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const BindingFlags AnyInstanceValue = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void The_verb_separates_bytes_that_were_removed_from_a_record_that_was_merely_closed()
    {
        // Pinned literals: this is the word an operator's query matches on, and renaming it silently empties theirs.
        Detail(ArtifactLocationClosureDetails.Deleted(), "closure").ShouldBe("deleted");
        Detail(Abandoned(Unservable()), "closure").ShouldBe("abandoned");
    }

    [Fact]
    public void Both_conclusive_closures_read_back_as_the_different_answers_they_are()
    {
        // Two ways a record closes with nothing deleted, and an operator triaging strays has to tell them apart: at
        // one key the destination could not serve anything, at the other it is serving somebody else's bytes.
        Detail(Abandoned(Unservable()), "observed")
            .ShouldBe($"the destination answered 'Missing' for {ObjectKey} while still answering for itself");
        Detail(Abandoned(ArtifactLocationAbandonment.HoldsSomethingElse(ObjectKey)), "observed")
            .ShouldBe($"the destination holds something other than this object at {ObjectKey}");
    }

    [Fact]
    public void A_delete_records_no_observation_it_never_made() =>
        Detail(ArtifactLocationClosureDetails.Deleted(), "observed").ShouldBeNull();

    [Fact]
    public void Nothing_but_a_placements_own_key_and_a_provider_code_can_be_written_into_the_ledger()
    {
        // The guarantee, as the shape that enforces it rather than as a filter over text: every way to author an
        // observation takes the object key the row already carries beside it, or a code from a closed enum. A
        // provider's own message, the URL it was reached at, a credential broker's complaint — none of them has a
        // parameter to arrive through. Widening one of these factories to take prose fails here, which is the point:
        // it makes putting caller-held text into a ledger every operator can read an edit, not an accident.
        //
        // Accessibility is not the boundary. The type is internal, so an internal author is reachable from exactly as
        // far as a public one — every caller in this assembly — and a pass that read only the public subset would wave
        // the internal widening through while looking like it had checked. Everything reachable outside this one file
        // is everything that is NOT private.
        typeof(ArtifactLocationAbandonment).GetConstructors(AnyAccess)
            .ShouldAllBe(entry => entry.IsPrivate, "a constructor reachable outside the type accepts any string at all");

        var authors = typeof(ArtifactLocationAbandonment).GetMethods(AnyAccess)
            .Where(author => author.IsStatic && author.ReturnType == typeof(ArtifactLocationAbandonment)).ToList();

        authors.ShouldNotBeEmpty("a pass that found no authors would enforce nothing while looking like it did");

        foreach (var parameter in authors.SelectMany(author => author.GetParameters()))
            Authored(parameter).ShouldBeFalse($"{parameter.Member.Name}({parameter.Name}) lets a caller write its own ledger text");
    }

    [Fact]
    public void An_abandonment_holds_the_one_sentence_its_authors_wrote_and_no_second_value()
    {
        // The authoring pin fixes what may be HANDED IN; this fixes what the object then CARRIES. A provider's raw
        // message parked in a field, a URL kept "for diagnostics" — either would sit on an object the ledger writer
        // already reads, one line away from being written into a column every operator can read. Adding a second
        // value has to fail here so that serializing it later is an argument someone has, not a line someone adds.
        Carriers(typeof(ArtifactLocationAbandonment)).ShouldBe([nameof(ArtifactLocationAbandonment.Observed)], ignoreOrder: true,
            customMessage: "a second value on an abandonment is a second thing a later author can hand to the ledger");
    }

    [Fact]
    public void An_object_key_is_carried_whole_however_much_of_a_url_it_looks_like()
    {
        // '?' and '#' are legal in an object key, and a key is the one thing this observation exists to hand back. An
        // observation that stopped at the first of them would name a key that is not there, and send an operator
        // hunting stranded bytes to a coordinate the destination has never heard of.
        const string awkward = "reports/q3?draft#v2 final";

        Detail(Abandoned(ArtifactLocationAbandonment.Unservable(ArtifactStorageErrorCode.Missing, awkward)), "observed")
            .ShouldContain(awkward);
    }

    [Fact]
    public void Every_closure_detail_is_the_json_object_the_ledger_constrains_it_to()
    {
        // ck_artifact_location_event_details: jsonb_typeof(details_jsonb) = 'object'. A bare string would be rejected
        // by the database at the end of a purge that already touched the bytes.
        Root(ArtifactLocationClosureDetails.Deleted()).ValueKind.ShouldBe(JsonValueKind.Object);
        Root(Abandoned(Unservable())).ValueKind.ShouldBe(JsonValueKind.Object);
    }

    /// <summary>Every value an instance carries, at any accessibility — the compiler's own backing storage is not one.</summary>
    private static List<string> Carriers(Type type)
    {
        var declared = type.GetFields(AnyInstanceValue).Where(field => !field.IsDefined(typeof(CompilerGeneratedAttribute)));

        return declared.Select(field => field.Name).Concat(type.GetProperties(AnyInstanceValue).Select(value => value.Name)).ToList();
    }

    /// <summary>Whether this parameter is one a caller writes the ledger through, rather than one it merely points at.</summary>
    private static bool Authored(ParameterInfo parameter) =>
        !parameter.ParameterType.IsEnum && !(parameter.ParameterType == typeof(string) && parameter.Name == "objectKey");

    private static ArtifactLocationAbandonment Unservable() =>
        ArtifactLocationAbandonment.Unservable(ArtifactStorageErrorCode.Missing, ObjectKey);

    private static string Abandoned(ArtifactLocationAbandonment abandonment) => ArtifactLocationClosureDetails.Abandoned(abandonment);

    private static JsonElement Root(string detailsJson) => JsonDocument.Parse(detailsJson).RootElement;

    private static string? Detail(string detailsJson, string name) =>
        Root(detailsJson).TryGetProperty(name, out var value) ? value.GetString() : null;
}
