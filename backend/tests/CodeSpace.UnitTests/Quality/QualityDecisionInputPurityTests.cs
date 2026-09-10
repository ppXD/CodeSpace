using System.Collections;
using System.Reflection;
using CodeSpace.Core.Services.Quality;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Quality;
using Shouldly;

namespace CodeSpace.UnitTests.Quality;

/// <summary>
/// ENFORCES the P22 purity invariant that makes "chosen by evidence, never by a task-name switch" a property of the
/// TYPE rather than a promise in a doc-comment: <see cref="QualityDecisionInput"/>'s transitive shape cannot carry
/// an identity at all. No string, no string collection, no <see cref="Guid"/>, and no member name containing an
/// identity noun — so a task phrasing, a provider, a model, a repository or an artifact path is not merely unused
/// by the policy, it is UNREPRESENTABLE in the policy's input.
///
/// <para>That is what makes the refutation airtight. "Permuting identity-like context never changes the decision"
/// cannot be tested by varying an identity field, because there is no identity field to vary — the honest proof is
/// (1) the shape admits none, (2) the member inventory is pinned so adding one is a test-visible decision, and
/// (3) the decision is a pure function of the recorded values, so two separately-built inputs carrying equal facts
/// decide identically. All three are pinned below.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class QualityDecisionInputPurityTests
{
    /// <summary>The nouns that name a THING rather than measure one. A member whose name contains any of these is naming an identity, which the quality surface must never see.</summary>
    private static readonly string[] IdentityNouns = { "Task", "Goal", "Provider", "Model", "Repo", "Path", "Name" };

    [Fact]
    public void The_decision_input_carries_no_identity_bearing_type_anywhere_in_its_shape()
    {
        foreach (var (owner, property, carried) in TransitiveProperties())
        {
            carried.ShouldNotBe(typeof(string), $"{owner.Name}.{property} is a string — a name, a path, a task phrasing or a model id can ride in on it, and the policy would then be able to switch on identity");
            carried.ShouldNotBe(typeof(Guid), $"{owner.Name}.{property} is a Guid — an identity in numeric clothing, which the no-string rule alone would not catch");
            carried.ShouldNotBe(typeof(object), $"{owner.Name}.{property} is an object — anything at all can ride in on it");
        }
    }

    [Fact]
    public void No_member_of_the_decision_input_is_named_after_an_identity()
    {
        foreach (var (owner, property, _) in TransitiveProperties())
        {
            var offending = IdentityNouns.FirstOrDefault(noun => property.Contains(noun, StringComparison.OrdinalIgnoreCase));

            offending.ShouldBeNull($"{owner.Name}.{property} is named after the identity noun '{offending}'. A count of things is scale and is welcome; the NAME of a thing is identity and is not. Rename to the scale axis (as WorkspaceUnitCount does) rather than adding an exemption — an exemption list is the crack a real identity field slips through.");
        }
    }

    [Fact]
    public void The_recorded_fact_inventory_is_pinned()
    {
        // The settable members ARE the decision surface. Pinned so that widening it — the one way a task-name
        // switch could ever come back — is a visible, reviewed decision rather than an additive-looking edit.
        var recorded = typeof(QualityDecisionInput).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal);

        recorded.ShouldBe(new[]
        {
            "Attempts",
            "BudgetCapUsd",
            "ChangedFileCount",
            "CheckDeclared",
            "EstimatedNextAttemptCostUsd",
            "IndependentReviewDisapproved",
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
    public void The_decision_is_a_pure_function_of_the_recorded_values_alone()
    {
        // Value-determinism: two SEPARATELY CONSTRUCTED inputs carrying equal facts must decide identically, and a
        // repeated call must not drift. With identity unrepresentable (above), this is the whole of "no ambient
        // context can change the decision" — there is no other channel by which context could reach the policy.
        foreach (var facts in FactShapes())
        {
            var rebuilt = facts with { };

            rebuilt.ShouldBe(facts, "the input is a value record — equal facts must be equal inputs");
            QualityPolicy.Decide(rebuilt).ShouldBe(QualityPolicy.Decide(facts));
            QualityPolicy.Decide(facts).ShouldBe(QualityPolicy.Decide(facts), "the policy must be free of state — the same facts decide the same way every time");
        }
    }

    [Fact]
    public void The_decision_OUTPUT_carries_its_evidence_as_prose_the_input_deliberately_cannot()
    {
        // The ban is on the INPUT, not on strings as such: a decision must be able to explain itself in words.
        // This pins the asymmetry so nobody "fixes" the guard by making the reason non-textual.
        typeof(QualityDecision).GetProperty(nameof(QualityDecision.Reason))!.PropertyType.ShouldBe(typeof(string));
    }

    /// <summary>Every public property reachable from the decision input, unwrapping nullables and collection elements, and recursing into the quality surface's own nested types.</summary>
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

                if (carried.Namespace == typeof(QualityDecisionInput).Namespace) pending.Enqueue(carried);
            }
        }
    }

    /// <summary>The type a member actually CARRIES — <c>T</c> for <c>T?</c> and for any sequence of <c>T</c> — so a string hidden inside a nullable or a collection is still caught.</summary>
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

    /// <summary>A spread of fact shapes covering each ordered row's trigger, used for the value-determinism sweep.</summary>
    private static IEnumerable<QualityDecisionInput> FactShapes()
    {
        yield return new QualityDecisionInput();
        yield return new QualityDecisionInput { BudgetCapUsd = 1m, SpendSoFarUsd = 1m, EstimatedNextAttemptCostUsd = 0.5m };
        yield return new QualityDecisionInput { NoProgressDecisions = 4, MaxNoProgressDecisions = 4 };
        yield return new QualityDecisionInput { CheckDeclared = true, Attempts = One(VerificationDisposition.InfraUnknown) };
        yield return new QualityDecisionInput { CheckDeclared = true, Attempts = One(VerificationDisposition.Unknown) };
        yield return new QualityDecisionInput { CheckDeclared = false, Attempts = One(VerificationDisposition.Unknown) };
        yield return new QualityDecisionInput { CheckDeclared = true, Attempts = One(VerificationDisposition.Passed), RecordedReviewScore = 12 };
        yield return new QualityDecisionInput { CheckDeclared = true, Attempts = One(VerificationDisposition.Passed) };
        yield return new QualityDecisionInput { CheckDeclared = true, WorkspaceUnitCount = 4, Attempts = Failed(3, FailureKind.Unprocessable) };
        yield return new QualityDecisionInput { CheckDeclared = true, ChangedFileCount = 2, Attempts = Failed(3, FailureKind.Invalid) };
    }

    private static QualityAttemptFact[] One(VerificationDisposition disposition) =>
        new[] { new QualityAttemptFact { Disposition = disposition } };

    private static QualityAttemptFact[] Failed(int count, FailureKind failure) =>
        Enumerable.Range(0, count).Select(_ => new QualityAttemptFact { Disposition = VerificationDisposition.Failed, Failure = failure }).ToArray();
}
