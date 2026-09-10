using System.Collections;
using System.Reflection;
using CodeSpace.Core.Services.Quality;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Quality;
using Shouldly;

namespace CodeSpace.UnitTests.Quality;

/// <summary>
/// ENFORCES the P22 purity invariant that makes "chosen by evidence, never by a task-name switch" a property of the
/// TYPE rather than a promise in a doc-comment: <see cref="QualityDecisionInput"/>'s transitive shape cannot carry
/// an identity at all.
///
/// <para><b>An ALLOW-list, not a deny-list.</b> A ban on <c>string</c>, <c>Guid</c> and <c>object</c> would refuse
/// only the identity carriers somebody already thought of. A <c>Uri</c>, a <c>byte[]</c> content digest, a
/// <c>KeyValuePair&lt;string, int&gt;</c>, an enum minted per repository, or a nested options record would all
/// pass such a ban. So the rule is inverted: a member may carry ONLY a type on
/// <see cref="CarriedTypeAllowList"/>, the walk recurses into every non-primitive it finds, and both member names
/// AND carried type names are checked against the identity nouns. Widening the surface then means editing this
/// list, in the open.</para>
///
/// <para>That is also what makes the refutation airtight. "Permuting identity-like context never changes the
/// decision" cannot be tested by varying an identity field, because there is no identity field to vary — the
/// honest proof is (1) the shape admits none, (2) the member inventories are pinned so adding one is a
/// test-visible decision, and (3) the decision is a pure function of the recorded values, so two SEPARATELY
/// CONSTRUCTED inputs carrying equal facts decide identically. All three are pinned below.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class QualityDecisionInputPurityTests
{
    /// <summary>The ONLY types the decision surface may carry, transitively. Everything else — including anything that could hold text, bytes, a URI, or a nested shape of its own — is refused by default rather than by enumeration.</summary>
    private static readonly Type[] CarriedTypeAllowList = { typeof(bool), typeof(int), typeof(decimal), typeof(VerificationDisposition), typeof(QualityAttemptFact) };

    /// <summary>The nouns that name a THING rather than measure one. A member — or a carried type — whose name contains any of these is naming an identity, which the quality surface must never see.</summary>
    private static readonly string[] IdentityNouns = { "Task", "Goal", "Provider", "Model", "Repo", "Path", "Name", "Id", "Kind", "Hash", "Digest", "Harness", "Agent", "Vendor", "Key" };

    [Fact]
    public void Every_type_carried_anywhere_in_the_decision_input_is_on_the_allow_list()
    {
        foreach (var (owner, property, carried) in TransitiveProperties())
        {
            CarriedTypeAllowList.ShouldContain(carried,
                $"{owner.Name}.{property} carries {carried.FullName}, which is not on the quality surface's allow-list. " +
                "The list is short on purpose: a number, a bool, a typed classification, or the attempt-fact row. Anything else can carry an identity — " +
                "a name, a path, a task phrasing, a model id, a digest — and the policy would then be able to switch on identity. " +
                "If the new fact is real, reduce it to one of these; if the list must grow, that is the reviewed edit this failure demands.");
        }
    }

    [Fact]
    public void No_member_or_carried_type_of_the_decision_input_is_named_after_an_identity()
    {
        foreach (var (owner, property, carried) in TransitiveProperties())
        {
            OffendingNoun(property).ShouldBeNull($"{owner.Name}.{property} is named after an identity noun. A count of things is scale and is welcome; the NAME of a thing is identity and is not. Rename to the scale axis (as WorkspaceUnitCount does) rather than adding an exemption — an exemption list is the crack a real identity field slips through.");
            OffendingNoun(carried.Name).ShouldBeNull($"{owner.Name}.{property} carries the type {carried.Name}, which is itself named after an identity noun — a type name is as much a declaration of intent as a member name.");
        }
    }

    [Fact]
    public void The_recorded_fact_inventory_is_pinned()
    {
        // The settable members ARE the decision surface. Pinned so that widening it — the one way a task-name
        // switch could ever come back — is a visible, reviewed decision rather than an additive-looking edit.
        SettableMembersOf(typeof(QualityDecisionInput)).ShouldBe(new[]
        {
            "Attempts",
            "BudgetCapUsd",
            "ChangedFileCount",
            "CheckDeclared",
            "EstimatedNextAttemptCostUsd",
            "IndependentReviewDisapproved",
            "IndependentReviewRecorded",
            "MaxNoProgressDecisions",
            "NoProgressDecisions",
            "RecordedReviewScore",
            "SelfClaimContradictedTheCheck",
            "SpendIsUndercounted",
            "SpendSoFarUsd",
            "WorkspaceUnitCount",
        });
    }

    [Fact]
    public void The_nested_attempt_fact_inventory_is_pinned()
    {
        // The nested row is where an identity would be easiest to smuggle in — it is per-attempt, so "which model
        // ran it" and "what did it output" both feel local there. One recorded classification, pinned.
        SettableMembersOf(typeof(QualityAttemptFact)).ShouldBe(new[] { "Disposition" });
    }

    [Fact]
    public void Two_separately_constructed_inputs_carrying_equal_facts_decide_identically()
    {
        // Value-determinism, built from two INDEPENDENT constructions rather than one `with { }` copy: a copy
        // reuses the original's own attempt array, so it can only ever prove that the same object decides the same
        // way. With identity unrepresentable (above), this is the whole of "no ambient context can change the
        // decision" — there is no other channel by which context could reach the policy.
        foreach (var build in FactShapes())
        {
            var first = build();
            var second = build();

            ReferenceEquals(first, second).ShouldBeFalse("the shapes must be built twice, or this test proves nothing");
            QualityPolicy.Decide(first).ShouldBe(QualityPolicy.Decide(second), "equal recorded facts must decide equally, however the input was assembled");
            QualityPolicy.Decide(first).ShouldBe(QualityPolicy.Decide(first), "the policy must be free of state — the same facts decide the same way every time");
        }
    }

    [Fact]
    public void Record_equality_does_not_reach_into_the_attempt_list_so_9b_must_not_key_a_cache_on_the_input()
    {
        // A trap worth pinning rather than discovering: the input is a record, but IReadOnlyList compares by
        // REFERENCE, so two inputs carrying identical facts are unequal whenever their attempt arrays differ by
        // instance. The DECISION is still identical — determinism lives in the values, not in record equality — so
        // 9b may memoize on the decided facts, never on the input record.
        var facts = new QualityDecisionInput { Attempts = One(VerificationDisposition.Failed) };
        var equalFacts = new QualityDecisionInput { Attempts = One(VerificationDisposition.Failed) };

        equalFacts.ShouldNotBe(facts, "record equality sees the list REFERENCE, not the attempts — do not treat input equality as fact equality");
        QualityPolicy.Decide(equalFacts).ShouldBe(QualityPolicy.Decide(facts), "…while the decision is a pure function of the values and must agree");
    }

    [Fact]
    public void The_decision_OUTPUT_carries_its_evidence_as_prose_the_input_deliberately_cannot()
    {
        // The ban is on the INPUT, not on strings as such: a decision must be able to explain itself in words.
        // This pins the asymmetry so nobody "fixes" the guard by making the reason non-textual.
        typeof(QualityDecision).GetProperty(nameof(QualityDecision.Reason))!.PropertyType.ShouldBe(typeof(string));
    }

    /// <summary>The identity noun a name contains, or null when it names a measurement rather than a thing.</summary>
    private static string? OffendingNoun(string name) => IdentityNouns.FirstOrDefault(noun => name.Contains(noun, StringComparison.OrdinalIgnoreCase));

    /// <summary>The settable (<c>init</c>) member names of <paramref name="type"/>, ordinally ordered — the part of a shape a caller populates.</summary>
    private static IEnumerable<string> SettableMembersOf(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is not null).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>
    /// Every public property reachable from the decision input, unwrapping nullables and collection elements, and
    /// recursing into EVERY carried type that is not a primitive or an enum — not merely the ones in the quality
    /// namespace. A nested type from anywhere is exactly how a shape grows an identity without any member of the
    /// root record looking suspicious.
    /// </summary>
    private static IEnumerable<(Type Owner, string Property, Type Carried)> TransitiveProperties()
    {
        var pending = new Queue<Type>(new[] { typeof(QualityDecisionInput) });
        var seen = new HashSet<Type>();

        while (pending.Count > 0)
        {
            var owner = pending.Dequeue();

            if (!seen.Add(owner)) continue;

            foreach (var property in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var carried = Unwrap(property.PropertyType);

                yield return (owner, property.Name, carried);

                if (!carried.IsPrimitive && !carried.IsEnum && carried != typeof(decimal) && carried != typeof(string)) pending.Enqueue(carried);
            }
        }
    }

    /// <summary>The type a member actually CARRIES — <c>T</c> for <c>T?</c> and for any sequence of <c>T</c> — so a type hidden inside a nullable or a collection is still caught.</summary>
    private static Type Unwrap(Type type)
    {
        var withoutNullable = Nullable.GetUnderlyingType(type) ?? type;

        if (withoutNullable == typeof(string)) return withoutNullable;

        if (!typeof(IEnumerable).IsAssignableFrom(withoutNullable)) return withoutNullable;

        var element = withoutNullable.IsArray
            ? withoutNullable.GetElementType()
            : withoutNullable.GetInterfaces().Append(withoutNullable).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];

        return element is null ? withoutNullable : Unwrap(element);
    }

    /// <summary>A spread of fact shapes covering each ordered row's trigger, as BUILDERS so the determinism sweep can construct each one twice.</summary>
    private static IEnumerable<Func<QualityDecisionInput>> FactShapes()
    {
        yield return () => new QualityDecisionInput();
        yield return () => new QualityDecisionInput { BudgetCapUsd = 1m, SpendSoFarUsd = 1m, EstimatedNextAttemptCostUsd = 0.5m };
        yield return () => new QualityDecisionInput { NoProgressDecisions = 4, MaxNoProgressDecisions = 4 };
        yield return () => new QualityDecisionInput { CheckDeclared = true, Attempts = One(VerificationDisposition.InfraUnknown) };
        yield return () => new QualityDecisionInput { CheckDeclared = true, Attempts = One(VerificationDisposition.Waived) };
        yield return () => new QualityDecisionInput { CheckDeclared = true, Attempts = One(VerificationDisposition.Unknown) };
        yield return () => new QualityDecisionInput { CheckDeclared = false, Attempts = One(VerificationDisposition.Unknown) };
        yield return () => new QualityDecisionInput { CheckDeclared = true, Attempts = One(VerificationDisposition.NotApplicable) };
        yield return () => new QualityDecisionInput { CheckDeclared = true, Attempts = One(VerificationDisposition.Passed), RecordedReviewScore = 12 };
        yield return () => new QualityDecisionInput { CheckDeclared = true, Attempts = One(VerificationDisposition.Passed) };
        yield return () => new QualityDecisionInput { CheckDeclared = false, IndependentReviewRecorded = true, Attempts = One(VerificationDisposition.Unknown) };
        yield return () => new QualityDecisionInput { CheckDeclared = true, WorkspaceUnitCount = 4, Attempts = Failed(3) };
        yield return () => new QualityDecisionInput { CheckDeclared = true, ChangedFileCount = 2, Attempts = Failed(3) };
    }

    private static QualityAttemptFact[] One(VerificationDisposition disposition) =>
        new[] { new QualityAttemptFact { Disposition = disposition } };

    private static QualityAttemptFact[] Failed(int count) =>
        Enumerable.Range(0, count).Select(_ => new QualityAttemptFact { Disposition = VerificationDisposition.Failed }).ToArray();
}
