using System;
using System.Collections.Generic;
using System.Linq;
using CodeSpace.Messages.Commands.Chat;
using CodeSpace.Messages.Mediation;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Enforces the write-side transaction contract: every MUTATING request (a type under a
/// <c>*.Commands.*</c> namespace) MUST be an <see cref="ICommand{T}"/>, so the
/// <c>TransactionalBehavior</c> wraps it in one transaction. Queries stay <c>IRequest&lt;T&gt;</c>.
///
/// <para>Why pin it: when the marker is left to per-author choice, you can't tell from the type whether
/// a request is transactional — and a bare-<c>IRequest</c> mutating command commits per-<c>SaveChanges</c>
/// instead of once at the end, which is exactly the fuzzy commit boundary behind the pre-/post-commit
/// dispatch races. This test makes a new bare-<c>IRequest</c> command fail CI until it's made
/// transactional (or explicitly excepted below).</para>
/// </summary>
[Trait("Category", "Unit")]
public class CommandContractConventionTests
{
    /// <summary>
    /// name → reason. Keep MINIMAL. The only legitimate exception is a command that self-manages
    /// concurrency in a way that REQUIRES statement-level autocommit — a "try insert / catch
    /// unique-violation / re-read the winner" find-or-create. A wrapping transaction is aborted by the
    /// failed insert, so the recovery read can't run; such a command must stay a bare IRequest.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NonTransactionalByDesign = new Dictionary<string, string>
    {
        ["OpenDirectConversationCommand"] =
            "ConversationService.GetOrCreateDirectAsync re-reads the dm_key winner after a unique-violation; " +
            "a wrapping transaction would be aborted by the failed insert, breaking the recovery read.",
    };

    [Fact]
    public void Every_mutating_command_is_ICommand_unless_explicitly_excepted()
    {
        var offenders = MutatingCommandTypes()
            .Where(t => !ImplementsICommand(t))
            .Where(t => !NonTransactionalByDesign.ContainsKey(t.Name))
            .Select(t => t.FullName)
            .OrderBy(n => n)
            .ToList();

        offenders.ShouldBeEmpty(
            "these *.Commands.* types are mutating requests but not ICommand<T>, so TransactionalBehavior won't " +
            "wrap them. Make them ICommand<T>, or — ONLY if they require statement-level autocommit — add them to " +
            "NonTransactionalByDesign with a concrete reason:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NonTransactional_allow_list_does_not_rot()
    {
        // Every excepted name must still be a real command that is intentionally NOT ICommand. If one is
        // converted to ICommand or deleted, this fails so the stale entry gets removed.
        foreach (var name in NonTransactionalByDesign.Keys)
        {
            var type = MutatingCommandTypes().SingleOrDefault(t => t.Name == name);
            type.ShouldNotBeNull($"allow-listed command '{name}' no longer exists — remove it from NonTransactionalByDesign");
            ImplementsICommand(type!).ShouldBeFalse($"allow-listed command '{name}' is now ICommand — remove it from NonTransactionalByDesign");
        }
    }

    private static IEnumerable<Type> MutatingCommandTypes() =>
        typeof(RespondToMessageCommand).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.Namespace != null && t.Namespace.Contains(".Commands."))
            .Where(t => typeof(IBaseRequest).IsAssignableFrom(t));

    private static bool ImplementsICommand(Type t) =>
        t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>));
}
