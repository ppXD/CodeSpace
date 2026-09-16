using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeSpace.Core.Persistence.Db;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Fail-closed floor for the shape that broke three sweeps: a service that opens its own transaction on the SCOPED
/// <c>CodeSpaceDbContext</c> without asking whether one is already open. <c>TransactionalBehavior</c> opens one per
/// <c>ICommand</c> on that same context, and Npgsql refuses a second — so such a service threw the moment any caller
/// reached it through the mediator, and the diff that made a caller transactional never mentioned the service at all.
///
/// <para>So every <c>BeginTransactionAsync(</c> under <c>CodeSpace.Core/Services</c> must go through
/// <see cref="ScopedTransaction.OwnOrJoinAsync"/>, or be named in <see cref="OwnsByDesign"/> with the reason it must
/// own its transaction. Each entry pins a COUNT as well as a reason: adding a raw call to an already-exempt file is
/// exactly the way a new instance of this bug would arrive unnoticed.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ScopedTransactionInventoryTests
{
    private const string ScannedRoot = "backend/src/CodeSpace.Core/Services";
    private const string RawCall = "BeginTransactionAsync(";

    /// <summary>
    /// Files whose transactions must stay owned, with how many such calls each has. Every entry names WHY joining
    /// would be wrong — "it has always done it this way" is not a reason, since that described all three sweeps too.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int Calls, string Reason)> OwnsByDesign = new Dictionary<string, (int, string)>(StringComparer.Ordinal)
    {
        ["Agents/AgentRunLogging/AgentRunLogCaptureRecoveryService.cs"] = (3, "every transaction is on a private CreateDb() context, never the scoped one — no caller's transaction can be ambient on it."),
        ["Agents/AgentRunLogging/AgentRunLogService.cs"] = (4, "the same private CreateDb() context; nothing ambient can reach it."),
        ["Agents/AgentRunLogging/AgentRunLogService.Verification.cs"] = (1, "the same private CreateDb() context; nothing ambient can reach it."),
        ["Agents/AgentRunLogging/AgentRunLogStorageReadiness.cs"] = (1, "rolls back and CONTINUES on a unique violation; joined, that rollback would discard the caller's work instead of its own."),
        ["Agents/AgentRunService.Ownership.cs"] = (1, "EnsureIndependentOwnershipTransaction refuses an ambient transaction outright — an execution claim must be durable before a worker acts on it."),
        ["Agents/AgentRunService.Review.cs"] = (1, "the same ownership guard, asserted two lines above this call."),
        ["Agents/Mcp/IToolCallLedgerService.cs"] = (1, "rolls back and CONTINUES on a lost claim race, then re-reads the winner; a joined rollback would take the caller's transaction with it."),
        ["Agents/Publish/ArtifactManifestStore.cs"] = (1, "already joins, but through a SAVEPOINT: a failed pointer replacement must discard only its own writes, which a plain join cannot do."),
        ["Agents/Publish/IPublishManifestStore.cs"] = (1, "rolls back and CONTINUES into the fenced-update fallback when the first insert loses its race."),
        ["Supervisor/Executors/RealSupervisorActionExecutor.Spawn.cs"] = (1, "the authorization wave must roll back to zero residue when the supervisor catches the fault and carries on; joined, that discard would be a no-op and replay would see a partial wave."),
        ["Supervisor/Observation/SupervisorDecisionObservationMetadataReader.cs"] = (1, "already joins; the RepeatableRead snapshot it opens is only meaningful for the transaction it owns."),
        ["Supervisor/Observation/SupervisorPlanObservationLeafReader.cs"] = (1, "already joins, same RepeatableRead snapshot."),
        ["Tasks/RoutePreview/TaskRouteSnapshotService.cs"] = (1, "already joins through a savepoint branch taken before this line; this arm runs only when nothing is ambient."),
        ["Workflows/Artifacts/Defaults/StorageDefaultMaterializer.cs"] = (1, "already joins, but through a SAVEPOINT: a refused destination must not leave the profile and credential it wrote behind in the caller's commit."),
        ["Workflows/Artifacts/Retention/ArtifactRetentionReaper.cs"] = (3, "the same private CreateDb() context; nothing ambient can reach it."),
        ["Workflows/Artifacts/Runtime/ArtifactCasRuntimeCoordinator.cs"] = (1, "the same private CreateDb() context; nothing ambient can reach it."),
        ["Workflows/Artifacts/Runtime/ArtifactCasRuntimeCoordinator.Purge.cs"] = (1, "the same private CreateDb() context; nothing ambient can reach it."),
        ["Workflows/Artifacts/Runtime/ArtifactLocationVerifier.cs"] = (1, "the same private CreateDb() context; nothing ambient can reach it."),
        ["Workflows/Artifacts/Runtime/LegacyPlacementAdopter.cs"] = (8, "the same private CreateDb() context; nothing ambient can reach it."),
        ["Workflows/Artifacts/StorageProfileHeadLock.cs"] = (1, "already joins, and hands the caller back the transaction it had to open so the advisory lock outlives the call."),
        ["Workflows/Budget/BudgetLedger.PhysicalLlm.cs"] = (1, "AdmitPhysicalAsync: the receipt authorizing the physical POST must be COMMITTED before the caller sends it."),
        ["Workflows/Engine/WorkflowEngine.cs"] = (1, "the redaction fallback re-writes the record through a fresh scope, which is only correct because disposing this transaction discards the first write."),
        ["Workflows/ModelCalls/WorkflowRunModelCallBodyMaterializer.cs"] = (2, "the same private CreateDb() context; nothing ambient can reach it."),
    };

    [Fact]
    public void Every_service_transaction_joins_an_ambient_one_or_is_allow_listed()
    {
        var found = RawCallSites();

        found.ShouldNotBeEmpty("the source scan found no BeginTransactionAsync at all — every check here would pass vacuously");

        var offenders = found
            .Where(file => !OwnsByDesign.ContainsKey(file.Key) || OwnsByDesign[file.Key].Calls != file.Value.Count)
            .Select(file => $"{file.Key}:{string.Join(",", file.Value)} — {(OwnsByDesign.TryGetValue(file.Key, out var e) ? $"allow-listed for {e.Calls}, found {file.Value.Count}" : "not allow-listed")}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            $"these open their own transaction on the scoped {nameof(CodeSpaceDbContext)} without asking whether one " +
            $"is already open, which throws the moment the caller arrives through the mediator. Use " +
            $"{nameof(ScopedTransaction)}.{nameof(ScopedTransaction.OwnOrJoinAsync)}, or add the file to " +
            $"{nameof(OwnsByDesign)} with the reason it must own its transaction:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_allow_list_does_not_rot()
    {
        var found = RawCallSites();

        foreach (var (file, entry) in OwnsByDesign)
        {
            found.ShouldContainKey(file, $"allow-listed file '{file}' no longer opens its own transaction — remove it from {nameof(OwnsByDesign)}");
            entry.Reason.ShouldNotBeNullOrWhiteSpace($"allow-listed file '{file}' must name why it has to own its transaction");
        }
    }

    [Fact]
    public void The_helper_is_the_idiom_the_converted_services_use()
    {
        var callers = SourceFiles()
            .Where(file => File.ReadAllText(file).Contains($"{nameof(ScopedTransaction)}.{nameof(ScopedTransaction.OwnOrJoinAsync)}(", StringComparison.Ordinal))
            .ToList();

        callers.Count.ShouldBeGreaterThan(1,
            customMessage: $"{nameof(ScopedTransaction)} has no callers left under {ScannedRoot}. Either the conversions were " +
                           "reverted — in which case the sweeps that need them are broken again — or the helper is dead code.");
    }

    /// <summary>Every source line under the scanned root that calls <c>BeginTransactionAsync(</c> for real, grouped by repo-relative file. Comment lines are text about the call, not a call.</summary>
    private static Dictionary<string, List<int>> RawCallSites()
    {
        var sites = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file)
                .Select((text, index) => (Text: text.TrimStart(), Number: index + 1))
                .Where(line => line.Text.Contains(RawCall, StringComparison.Ordinal))
                .Where(line => !line.Text.StartsWith("//", StringComparison.Ordinal) && !line.Text.StartsWith('*'))
                .Select(line => line.Number)
                .ToList();

            if (lines.Count > 0) sites[Relative(file)] = lines;
        }

        return sites;
    }

    private static IEnumerable<string> SourceFiles() => Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), ScannedRoot), "*.cs", SearchOption.AllDirectories);

    private static string Relative(string file) => Path.GetRelativePath(Path.Combine(RepositoryRoot(), ScannedRoot), file).Replace(Path.DirectorySeparatorChar, '/');

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException($"repository root not found walking up from {AppContext.BaseDirectory}");
    }
}
