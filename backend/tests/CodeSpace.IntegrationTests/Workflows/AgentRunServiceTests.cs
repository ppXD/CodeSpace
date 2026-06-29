using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Drives the REAL AgentRunService (resolved through CodeSpaceModule's DI, proving it's registered)
/// against real Postgres across a full lifecycle — create → running → append events → complete, then
/// reads back run + events via the live cursor — plus the guards: an illegal transition and a
/// non-terminal completion status each throw, and re-running an already-running run is rejected.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunServiceTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunServiceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task CreateAsync_persists_the_owning_cell_iteration_key()
    {
        // D4: the iteration key passed at creation is the agent run's owning workflow CELL — it must round-trip
        // verbatim (a map/loop branch key) and default to "" for a top-level / standalone run.
        var teamId = await SeedTeamAsync();

        Guid branchRunId, topLevelRunId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            branchRunId = (await svc.CreateAsync(BuildTask(), teamId, Guid.NewGuid(), "agent", iterationKey: "map#2", cancellationToken: CancellationToken.None)).Id;
            topLevelRunId = (await svc.CreateAsync(BuildTask(), teamId, null, null, cancellationToken: CancellationToken.None)).Id;
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == branchRunId)).IterationKey
            .ShouldBe("map#2", "a branch agent run round-trips its owning cell key verbatim");
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == topLevelRunId)).IterationKey
            .ShouldBe("", "a top-level / standalone run defaults to the empty (NoIteration) cell key");
    }

    [Fact]
    public async Task Full_lifecycle_create_run_append_events_complete()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Queued);
            runId = run.Id;
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.CommandExecuted, Text = "npm test", Data = JsonSerializer.SerializeToElement(new { command = "npm test", exitCode = 0 }) }, CancellationToken.None);
            await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "Fixed the failing tests." }, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "Fixed.", ChangedFiles = new[] { "src/a.ts" } }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            var run = await svc.GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded);
            run.StartedAt.ShouldNotBeNull();
            run.CompletedAt.ShouldNotBeNull();
            run.ResultJson.ShouldNotBeNull();
            run.ResultJson!.ShouldContain("completed");

            var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
            events.Select(e => e.Kind).ShouldBe(new[] { AgentEventKind.CommandExecuted, AgentEventKind.AssistantMessage });
            events[0].DataJson.ShouldNotBeNull();
            events[0].DataJson!.ShouldContain("npm test");

            // cursor: events strictly after the first
            var tail = await svc.GetEventsAsync(runId, teamId, events[0].Sequence, CancellationToken.None);
            tail.Select(e => e.Kind).ShouldBe(new[] { AgentEventKind.AssistantMessage });
        }
    }

    [Fact]
    public async Task AppendEventsAsync_persists_a_batch_in_strict_emission_order()
    {
        // D1: the buffered writer flushes many events in ONE batched call. The per-run BIGSERIAL `sequence`
        // (the canonical order the live log + replay cursor read by) MUST be assigned in emission order — even
        // though the random-Guid PK would let EF's change tracker scramble a plain AddRange. Mixed payloads
        // (some with data_json, some without) exercise the NULL element in the jsonb array.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        var batch = new[]
        {
            new AgentEvent { Kind = AgentEventKind.Started, Text = "one" },
            new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "two", Data = JsonSerializer.SerializeToElement(new { step = 2 }) },
            new AgentEvent { Kind = AgentEventKind.CommandExecuted, Text = "three" },
            new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "four", Data = JsonSerializer.SerializeToElement(new { step = 4 }) },
            new AgentEvent { Kind = AgentEventKind.Completed, Text = "five" },
        };

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().AppendEventsAsync(runId, batch, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var events = await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, 0, CancellationToken.None);

            events.Select(e => e.Text).ShouldBe(new[] { "one", "two", "three", "four", "five" }, "the batch reads back in EXACT emission order");
            events.Select(e => e.Sequence).ShouldBe(events.Select(e => e.Sequence).OrderBy(s => s), "the BIGSERIAL sequence is strictly ascending in emission order");
            events[1].DataJson!.ShouldContain("\"step\": 2");   // jsonb round-trips reformatted (space after colon)
            events[2].DataJson.ShouldBeNull("an event with no payload persists NULL data_json");
        }
    }

    [Fact]
    public async Task AppendEventsAsync_keeps_global_order_across_multiple_batches()
    {
        // D1: the writer flushes in several batches over the run's life (checkpoint flushes + a final flush).
        // Each batch is its own call; the global sequence must stay monotonic ACROSS batches so a cursor read
        // (sequence > N) never skips or reorders an event from an earlier batch.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            await svc.AppendEventsAsync(runId, new[] { Ev("a1"), Ev("a2") }, CancellationToken.None);
            await svc.AppendEventsAsync(runId, new[] { Ev("b1"), Ev("b2"), Ev("b3") }, CancellationToken.None);
            await svc.AppendEventsAsync(runId, new[] { Ev("c1") }, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
        {
            var events = await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, 0, CancellationToken.None);

            events.Select(e => e.Text).ShouldBe(new[] { "a1", "a2", "b1", "b2", "b3", "c1" }, "every batch appends after the previous one, in order");

            // The incremental live cursor: reading strictly after the 2nd event yields exactly the tail, in order.
            var tail = await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, events[1].Sequence, CancellationToken.None);
            tail.Select(e => e.Text).ShouldBe(new[] { "b1", "b2", "b3", "c1" });
        }

        static AgentEvent Ev(string text) => new() { Kind = AgentEventKind.AssistantMessage, Text = text };
    }

    [Fact]
    public async Task AppendEventsAsync_persists_a_large_batch_in_order_in_one_statement()
    {
        // D1 scale: a busy run can flush hundreds of events at once. The single unnest INSERT must carry an
        // arbitrarily large batch (the parameter count is FIXED at four — one array per column — so there is no
        // placeholder explosion / 65535-parameter ceiling) and still serial-stamp every row in emission order.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        const int n = 300;
        var batch = Enumerable.Range(0, n)
            .Select(i => new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = $"line-{i:D4}" })
            .ToArray();

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().AppendEventsAsync(runId, batch, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var events = await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, 0, CancellationToken.None);

            events.Count.ShouldBe(n, "every event in the large batch landed");
            events.Select(e => e.Text).ShouldBe(Enumerable.Range(0, n).Select(i => $"line-{i:D4}"), "the whole batch reads back in exact emission order");
        }
    }

    [Fact]
    public async Task AppendEventsAsync_concurrent_runs_keep_per_run_order_and_isolation()
    {
        // D1 concurrency + multi-agent: two runs append batches CONCURRENTLY (the real fan-out shape — many agents
        // streaming at once). The global BIGSERIAL interleaves across runs, but each run's read MUST return exactly
        // its own events, complete and in per-run emission order — no loss, no cross-run leakage, no reorder.
        var teamId = await SeedTeamAsync();

        Guid runA, runB;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runA = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            runB = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runA, CancellationToken.None);
            await svc.MarkRunningAsync(runB, CancellationToken.None);
        }

        const int batches = 20;
        const int perBatch = 5;

        // Each run drives its own DbContext scope (a real worker = its own connection); the two interleave freely.
        async Task DriveAsync(Guid runId, string prefix)
        {
            for (var b = 0; b < batches; b++)
            {
                using var scope = _fixture.BeginScope();
                var events = Enumerable.Range(0, perBatch)
                    .Select(i => new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = $"{prefix}-{b:D2}-{i}" })
                    .ToArray();
                await scope.Resolve<IAgentRunService>().AppendEventsAsync(runId, events, CancellationToken.None);
            }
        }

        await Task.WhenAll(DriveAsync(runA, "A"), DriveAsync(runB, "B"));

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            var a = await svc.GetEventsAsync(runA, teamId, 0, CancellationToken.None);
            var bEvents = await svc.GetEventsAsync(runB, teamId, 0, CancellationToken.None);

            var expectedA = Enumerable.Range(0, batches).SelectMany(b => Enumerable.Range(0, perBatch).Select(i => $"A-{b:D2}-{i}"));
            var expectedB = Enumerable.Range(0, batches).SelectMany(b => Enumerable.Range(0, perBatch).Select(i => $"B-{b:D2}-{i}"));

            a.Select(e => e.Text).ShouldBe(expectedA, "run A reads back exactly its own events, in per-run emission order");
            bEvents.Select(e => e.Text).ShouldBe(expectedB, "run B reads back exactly its own events, in per-run emission order");
            a.ShouldAllBe(e => e.Text.StartsWith("A-"), "no run-B event leaked into run A");
            bEvents.ShouldAllBe(e => e.Text.StartsWith("B-"), "no run-A event leaked into run B");
        }
    }

    [Fact]
    public async Task AppendEventsAsync_with_an_empty_batch_is_a_noop()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().AppendEventsAsync(runId, Array.Empty<AgentEvent>(), CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, 0, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AppendEventsAsync_rejects_the_whole_batch_atomically_when_it_violates_a_constraint()
    {
        // D1 atomicity: the entire offset-reasoning rests on each flush being ONE all-or-nothing statement. A
        // constraint-violating batch (here: a foreign-key violation — the run was never created) must commit ZERO
        // rows — never a torn prefix. If a future refactor split the batch into per-row inserts without a
        // transaction, a mid-batch failure could leave a partial prefix the flush-before-offset invariant no longer
        // protects (the cursor would point into a gap with orphaned later events, and the append-only log could
        // never repair it). We pin the single-statement guarantee.
        var teamId = await SeedTeamAsync();

        Guid goodRun;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            goodRun = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(goodRun, CancellationToken.None);
            await svc.AppendEventsAsync(goodRun, new[] { Ev("baseline-1"), Ev("baseline-2"), Ev("baseline-3") }, CancellationToken.None);
        }

        var orphanRun = Guid.NewGuid();   // never created → agent_run_id FK has nothing to reference
        var rejected = new[] { Ev("torn-1"), Ev("torn-2"), Ev("torn-3"), Ev("torn-4"), Ev("torn-5") };

        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<Exception>(() => scope.Resolve<IAgentRunService>().AppendEventsAsync(orphanRun, rejected, CancellationToken.None));

        using (var scope = _fixture.BeginScope())
        {
            // Zero rows landed for the violating batch — the INSERT was rejected wholesale, not row-by-row.
            (await scope.Resolve<CodeSpaceDbContext>().AgentRunEvent.AsNoTracking().CountAsync(e => e.AgentRunId == orphanRun))
                .ShouldBe(0, "a constraint-violating batch commits NONE of its rows (single atomic statement)");

            // The unrelated good run is untouched — exactly its baseline, still in order.
            (await scope.Resolve<IAgentRunService>().GetEventsAsync(goodRun, teamId, 0, CancellationToken.None)).Select(e => e.Text)
                .ShouldBe(new[] { "baseline-1", "baseline-2", "baseline-3" });
        }

        static AgentEvent Ev(string text) => new() { Kind = AgentEventKind.AssistantMessage, Text = text };
    }

    [Fact]
    public async Task AppendEventsAsync_under_heavy_concurrent_fan_out_keeps_per_run_order_and_global_uniqueness()
    {
        // D1 concurrency at fan-out scale (6 real agents streaming at once): STRONGER than the 2-run test —
        // it also asserts the GLOBAL BIGSERIAL stream has NO duplicate sequence across all runs and NO loss
        // (total persisted == every event), and that the raw global stream filtered to one run matches that
        // run's per-run cursor order. Catches a dropped ORDER BY, an AddRange scramble, or a serial-allocation
        // collision that only manifests under real contention.
        var teamId = await SeedTeamAsync();

        const int runCount = 6, batches = 30, perBatch = 8;
        const int perRun = batches * perBatch;   // 240

        var runIds = new Guid[runCount];
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            for (var r = 0; r < runCount; r++)
            {
                runIds[r] = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
                await svc.MarkRunningAsync(runIds[r], CancellationToken.None);
            }
        }

        async Task DriveAsync(int r)
        {
            var letter = (char)('A' + r);
            for (var b = 0; b < batches; b++)
            {
                using var scope = _fixture.BeginScope();   // own connection per batch (a real worker's own scope)
                var events = Enumerable.Range(0, perBatch)
                    .Select(i => new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = $"{letter}-{b:D2}-{i}" })
                    .ToArray();
                await scope.Resolve<IAgentRunService>().AppendEventsAsync(runIds[r], events, CancellationToken.None);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, runCount).Select(DriveAsync));

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            var allSequences = new List<long>();
            for (var r = 0; r < runCount; r++)
            {
                var letter = (char)('A' + r);
                var events = await svc.GetEventsAsync(runIds[r], teamId, 0, CancellationToken.None);

                var expected = Enumerable.Range(0, batches).SelectMany(b => Enumerable.Range(0, perBatch).Select(i => $"{letter}-{b:D2}-{i}"));
                events.Select(e => e.Text).ShouldBe(expected, $"run {letter} reads back its {perRun} events in exact per-run emission order");
                events.Select(e => e.Sequence).ShouldBe(events.Select(e => e.Sequence).OrderBy(s => s), $"run {letter} sequences strictly ascending");
                allSequences.AddRange(events.Select(e => e.Sequence));
            }

            allSequences.Count.ShouldBe(runCount * perRun, "no event lost across the fan-out");
            allSequences.Distinct().Count().ShouldBe(allSequences.Count, "every event got a UNIQUE global BIGSERIAL sequence — no collision under contention");
        }
    }

    [Fact]
    public async Task GetEventsAsync_live_cursor_drains_a_growing_log_exactly_once_under_a_contended_global_sequence()
    {
        // D1 live cursor (the operator/supervisor stream polled WHILE the run appends). The per-run cursor reads
        // `agent_run_id = A AND sequence > N ORDER BY sequence`. The hazard: a row whose global BIGSERIAL is
        // assigned-then-committed out of order relative to the cursor's position would be SKIPPED forever (silent
        // loss invisible to any read-after-settle test). To make the global serial space genuinely CONTENDED while
        // run A streams, TWO other "noise" runs commit concurrently throughout — so A's serials interleave with
        // theirs and a noise commit lands between two A commits. The reader (cursor over A) must still observe
        // every A row EXACTLY ONCE, in order, never tripped by a sibling run's interleaved serial.
        var teamId = await SeedTeamAsync();

        Guid runId, noise1, noise2;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            noise1 = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            noise2 = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
            await svc.MarkRunningAsync(noise1, CancellationToken.None);
            await svc.MarkRunningAsync(noise2, CancellationToken.None);
        }

        const int batches = 40, perBatch = 5;
        const int total = batches * perBatch;   // 200
        var expected = Enumerable.Range(0, batches).SelectMany(b => Enumerable.Range(0, perBatch).Select(i => $"c-{b:D2}-{i}")).ToList();

        // Noise writers hammer the OTHER two runs (own scopes) until told to stop — contending the global serial.
        using var noiseStop = new CancellationTokenSource();
        async Task NoiseAsync(Guid id)
        {
            var n = 0;
            while (!noiseStop.IsCancellationRequested)
            {
                using var scope = _fixture.BeginScope();
                var events = Enumerable.Range(0, 3).Select(i => new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = $"noise-{n}-{i}" }).ToArray();
                try { await scope.Resolve<IAgentRunService>().AppendEventsAsync(id, events, noiseStop.Token); }
                catch (OperationCanceledException) { break; }
                n++;
            }
        }
        var noiseTasks = new[] { Task.Run(() => NoiseAsync(noise1)), Task.Run(() => NoiseAsync(noise2)) };

        var writer = Task.Run(async () =>
        {
            for (var b = 0; b < batches; b++)
            {
                using var scope = _fixture.BeginScope();
                var events = Enumerable.Range(0, perBatch).Select(i => new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = $"c-{b:D2}-{i}" }).ToArray();
                await scope.Resolve<IAgentRunService>().AppendEventsAsync(runId, events, CancellationToken.None);
                await Task.Delay(1);
            }
        });

        var seenTexts = new List<string>();
        var seenSeqs = new List<long>();
        var reader = Task.Run(async () =>
        {
            long cursor = 0;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (seenTexts.Count < total && DateTimeOffset.UtcNow <= deadline)
            {
                int got;
                using (var scope = _fixture.BeginScope())
                {
                    var page = await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, cursor, CancellationToken.None);
                    got = page.Count;
                    if (got > 0)
                    {
                        seenTexts.AddRange(page.Select(e => e.Text));
                        seenSeqs.AddRange(page.Select(e => e.Sequence));
                        cursor = page[^1].Sequence;
                    }
                }
                if (got == 0) await Task.Delay(5);   // pace the poll only when caught up
            }
        });

        await Task.WhenAll(writer, reader);
        noiseStop.Cancel();
        await Task.WhenAll(noiseTasks);

        seenTexts.Count.ShouldBe(total, $"the cursor stream observed every committed row exactly once (saw {seenTexts.Count}/{total} — a skip means a sibling run's interleaved serial tripped the per-run cursor)");
        seenSeqs.SequenceEqual(seenSeqs.OrderBy(s => s)).ShouldBeTrue("observed sequences strictly ascending — no row re-read out of order");
        seenSeqs.Distinct().Count().ShouldBe(seenSeqs.Count, "no row observed twice");
        seenTexts.ShouldBe(expected, "the live stream reconstructs the exact emission order");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Text)
                .ShouldBe(expected, "a post-settle full read matches the live-observed order");
    }

    [Fact]
    public async Task AppendEventsAsync_round_trips_structured_payloads_keeping_the_null_data_array_row_aligned()
    {
        // D1 data fidelity at scale: every 3rd event carries a DISTINCT structured payload, the rest carry none —
        // exercising interleaved NULL / non-NULL elements through unnest({3}::text[]) + CAST(e.data AS jsonb). A
        // parallel-array desync would mis-attribute payloads to the wrong row (invisible at 5 rows, corrupting the
        // replay/observability read once D3 makes structured payloads the norm). Also pins unicode / escapes /
        // empty-object / json-null / large-blob / SQL-injection fidelity through the CAST + text binding.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        const int n = 300;
        const string bigBlob = "X";
        var batch = new AgentEvent[n];
        for (var i = 0; i < n; i++)
            batch[i] = (i % 3 == 0)
                ? new AgentEvent { Kind = AgentEventKind.ToolCall, Text = $"row-{i:D4}", Data = JsonSerializer.SerializeToElement(new { idx = i, phase = "build" }) }
                : new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = $"row-{i:D4}" };   // no payload → NULL data_json

        // Splice in edge-case fidelity rows (each with its own index so misattribution is detectable).
        batch[30] = new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "row-0030", Data = JsonSerializer.SerializeToElement(new { idx = 30, nested = new[] { 1, 2, 3 }, who = "café-🚀-naïve" }) };
        batch[60] = new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "row-0060", Data = JsonSerializer.SerializeToElement(new { idx = 60, q = "he said \"hi\"\tand\\back", line = "a\nb" }) };
        batch[90] = new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "row-0090", Data = JsonSerializer.SerializeToElement(new { }) };                       // empty object
        batch[120] = new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "row-0120", Data = JsonSerializer.Deserialize<JsonElement>("null") };                 // json null literal
        batch[150] = new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "row-0150", Data = JsonSerializer.SerializeToElement(new { idx = 150, blob = new string('X', 100_000), end = "SENTINEL" }) };
        batch[180] = new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "row-0180'; DROP TABLE agent_run_event; --\nsecond line\twith\ttabs" };          // injection + meta chars in text

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().AppendEventsAsync(runId, batch, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var events = await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, 0, CancellationToken.None);

            events.Count.ShouldBe(n, "every row landed (table intact — the injection string was bound, not executed)");
            events.Select(e => e.Text).ShouldBe(Enumerable.Range(0, n).Select(i => i == 180 ? "row-0180'; DROP TABLE agent_run_event; --\nsecond line\twith\ttabs" : $"row-{i:D4}"), "text round-trips verbatim incl. newlines/tabs/quotes");

            // Each payload-bearing row carries ITS OWN index — proves the data[] NULL/non-NULL elements stayed row-aligned.
            for (var i = 0; i < n; i++)
            {
                if (i is 90 or 120 or 150 or 180) continue;   // empty-object / json-null / 100KB-offloaded / text-only injection — handled below
                if (i % 3 == 0)
                {
                    events[i].DataJson.ShouldNotBeNull($"row {i} carries a structured payload");
                    JsonSerializer.Deserialize<JsonElement>(events[i].DataJson!).GetProperty("idx").GetInt32().ShouldBe(i, $"row {i}'s payload was NOT shifted onto another row");
                }
                else
                {
                    events[i].DataJson.ShouldBeNull($"row {i} has no payload → NULL data_json (no column shift)");
                }
            }

            events[30].DataJson!.ShouldContain("🚀", Case.Sensitive);   // unicode/emoji survive
            events[60].DataJson!.ShouldContain("\\t");                   // escapes survive (jsonb keeps the escape)
            events[90].DataJson.ShouldBe("{}", "empty object round-trips");
            events[120].DataJson.ShouldBe("null", "json null literal round-trips as a jsonb null, distinct from a missing payload");

            // Row 150's ~100KB payload exceeds the 8KiB inline threshold → D2 #1 offloads it: the row keeps only
            // the ref, and the full untruncated payload is recoverable from the artifact store.
            events[150].DataJson.ShouldBeNull("the 100KB payload was offloaded, not kept inline");
            events[150].DataArtifactId.ShouldNotBeNull();
            var offloaded = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, events[150].DataArtifactId!.Value, CancellationToken.None);
            System.Text.Encoding.UTF8.GetString(offloaded!.Bytes).ShouldContain("SENTINEL", customMessage: "the 100KB payload is recoverable + untruncated from the artifact store");
        }
    }

    [Fact]
    public async Task AppendEventsAsync_offloads_a_large_data_json_payload_keeping_only_the_ref()
    {
        // D2 #1: a large structured event payload (data_json) is offloaded to the artifact store and the event row
        // keeps only data_artifact_id — so the append-only log stays bounded even when D3 emits big tool_result /
        // reasoning blocks. Small payloads stay inline (common case). Mixed batch proves per-row routing + order.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        var bigPayload = JsonSerializer.SerializeToElement(new { tag = "big-tool-result", blob = new string('x', ArtifactStoreConfig.DefaultInlineThresholdBytes + 500) });
        var batch = new[]
        {
            new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "small", Data = JsonSerializer.SerializeToElement(new { k = "v" }) },   // small → inline
            new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "big", Data = bigPayload },                                            // large → offload
            new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "no-data" },                                                   // no payload
        };

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().AppendEventsAsync(runId, batch, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var events = await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, 0, CancellationToken.None);

            events.Select(e => e.Text).ShouldBe(new[] { "small", "big", "no-data" }, "order preserved through the offload");

            events[0].DataJson.ShouldNotBeNull("a small payload stays inline");
            events[0].DataArtifactId.ShouldBeNull();

            events[1].DataJson.ShouldBeNull("the large payload was moved out of the row");
            events[1].DataArtifactId.ShouldNotBeNull("the row keeps only the ref");

            events[2].DataJson.ShouldBeNull("no payload");
            events[2].DataArtifactId.ShouldBeNull();

            // The full payload round-trips from the artifact store as valid JSON.
            var artifact = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, events[1].DataArtifactId!.Value, CancellationToken.None);
            artifact.ShouldNotBeNull();
            artifact!.ContentType.ShouldBe("application/json");
            var recovered = System.Text.Encoding.UTF8.GetString(artifact.Bytes);
            JsonDocument.Parse(recovered).RootElement.GetProperty("tag").GetString().ShouldBe("big-tool-result", "the offloaded structured payload is recoverable in full");
        }
    }

    [Fact]
    public async Task Completing_with_a_large_patch_offloads_it_to_an_artifact_and_keeps_only_the_ref()
    {
        // D2: a large unified diff must NOT bloat result_jsonb — it's offloaded to the content-addressed artifact
        // store (team-scoped) and the result keeps only PatchArtifactId. The full diff round-trips from the store.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        // ~40 KiB diff — comfortably over the 8 KiB inline threshold. Distinctive content so we can verify fidelity.
        var bigPatch = string.Concat(Enumerable.Range(0, 1000).Select(i => $"+added line {i:D4} to the file\n"));
        bigPatch.Length.ShouldBeGreaterThan(ArtifactStoreConfig.DefaultInlineThresholdBytes, "the diff must exceed the inline threshold to exercise offload");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = bigPatch, ChangedFiles = new[] { "src/a.ts" } },
                CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var stored = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

            stored.Patch.ShouldBe("", "the large diff was moved out of result_jsonb");
            stored.PatchArtifactId.ShouldNotBeNull("the result keeps a reference to the offloaded diff");
            run.ResultJson!.Length.ShouldBeLessThan(bigPatch.Length, "result_jsonb no longer carries the full diff");

            // The full diff round-trips from the artifact store, byte-for-byte.
            var artifact = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, stored.PatchArtifactId!.Value, CancellationToken.None);
            artifact.ShouldNotBeNull();
            System.Text.Encoding.UTF8.GetString(artifact!.Bytes).ShouldBe(bigPatch, "the offloaded diff is recoverable in full");
            artifact.ContentType.ShouldBe("text/x-diff");
        }
    }

    [Fact]
    public async Task Completing_with_a_small_patch_keeps_it_inline_with_no_artifact()
    {
        // A small diff stays inline in result_jsonb (no offload, no artifact ref) — the common case is unchanged.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        const string smallPatch = "+one small change\n-one removed line\n";

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = smallPatch },
                CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var stored = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

            stored.Patch.ShouldBe(smallPatch, "a small diff stays inline");
            stored.PatchArtifactId.ShouldBeNull("no artifact is created for an inline diff");
        }
    }

    [Fact]
    public async Task Completing_with_a_large_per_repo_patch_offloads_it_keeping_only_the_ref()
    {
        // S7-C0: a MULTI-repo run's per-repo diff must not bloat result_jsonb any more than the top-level one — each
        // per-repo patch over the inline threshold is offloaded (Patch cleared + PatchArtifactId set), the small one
        // stays inline, and both round-trip from the team-scoped artifact store.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        var bigPatch = string.Concat(Enumerable.Range(0, 1000).Select(i => $"+web line {i:D4}\n"));
        bigPatch.Length.ShouldBeGreaterThan(ArtifactStoreConfig.DefaultInlineThresholdBytes, "the per-repo diff must exceed the inline threshold to exercise offload");
        const string smallPatch = "+one api change\n";

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId, new AgentRunResult
            {
                Status = AgentRunStatus.Succeeded, ExitReason = "completed", ChangeSetId = "cs-test",
                RepositoryResults = new[]
                {
                    new RepositoryRunResult { Alias = "web", RepositoryId = Guid.NewGuid(), Patch = bigPatch, BaseSha = "base-web", Access = WorkspaceAccess.Write },
                    new RepositoryRunResult { Alias = "api", RepositoryId = Guid.NewGuid(), Patch = smallPatch, BaseSha = "base-api", Access = WorkspaceAccess.Write },
                },
            }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var stored = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

            var web = stored.RepositoryResults.Single(r => r.Alias == "web");
            web.Patch.ShouldBe("", "the large per-repo diff was moved out of result_jsonb");
            web.PatchArtifactId.ShouldNotBeNull("the per-repo result keeps a reference to the offloaded diff");
            run.ResultJson!.Length.ShouldBeLessThan(bigPatch.Length, "result_jsonb no longer carries the full per-repo diff");

            var api = stored.RepositoryResults.Single(r => r.Alias == "api");
            api.Patch.ShouldBe(smallPatch, "a small per-repo diff stays inline");
            api.PatchArtifactId.ShouldBeNull("no artifact is created for an inline per-repo diff");

            // The full per-repo diff round-trips from the artifact store, byte-for-byte.
            var artifact = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, web.PatchArtifactId!.Value, CancellationToken.None);
            artifact.ShouldNotBeNull();
            System.Text.Encoding.UTF8.GetString(artifact!.Bytes).ShouldBe(bigPatch, "the offloaded per-repo diff is recoverable in full");
            artifact.ContentType.ShouldBe("text/x-diff");
        }
    }

    [Fact]
    public async Task Completing_with_a_large_transcript_offloads_it_to_an_artifact_and_keeps_only_the_ref()
    {
        // D3a: the faithful raw transcript must NOT bloat result_jsonb — it's offloaded to the content-addressed
        // artifact store (team-scoped) and the result keeps only TranscriptArtifactId. The full transcript
        // round-trips from the store, byte-for-byte — the durable "replay the exact session" record.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        // ~50 KiB transcript — comfortably over the 8 KiB inline threshold. Distinctive content for fidelity.
        var bigTranscript = string.Concat(Enumerable.Range(0, 1000).Select(i => $"[raw] harness stream line {i:D4}\n"));
        bigTranscript.Length.ShouldBeGreaterThan(ArtifactStoreConfig.DefaultInlineThresholdBytes, "the transcript must exceed the inline threshold to exercise offload");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Transcript = bigTranscript },
                CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var stored = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

            stored.Transcript.ShouldBe("", "the large transcript was moved out of result_jsonb");
            stored.TranscriptArtifactId.ShouldNotBeNull("the result keeps a reference to the offloaded transcript");
            run.ResultJson!.Length.ShouldBeLessThan(bigTranscript.Length, "result_jsonb no longer carries the full transcript");

            var artifact = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, stored.TranscriptArtifactId!.Value, CancellationToken.None);
            artifact.ShouldNotBeNull();
            System.Text.Encoding.UTF8.GetString(artifact!.Bytes).ShouldBe(bigTranscript, "the offloaded transcript is recoverable in full");
            artifact.ContentType.ShouldBe("text/plain");
        }
    }

    [Fact]
    public async Task Completing_with_a_large_session_transcript_offloads_it_to_an_artifact_and_keeps_only_the_ref()
    {
        // P3: the RESUMABLE session transcript (the harness-native session file a CONTINUE restores) is offloaded exactly
        // like the stream-json transcript — a large one moves to the team-scoped artifact store and result_jsonb keeps only
        // SessionTranscriptArtifactId, recoverable byte-for-byte (the 3.2c producer fetches it to restore the conversation).
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        // ~40 KiB of session jsonl — comfortably over the inline threshold. Distinctive content for fidelity.
        var bigSession = string.Concat(Enumerable.Range(0, 1000).Select(i => $"{{\"role\":\"assistant\",\"turn\":{i}}}\n"));
        bigSession.Length.ShouldBeGreaterThan(ArtifactStoreConfig.DefaultInlineThresholdBytes, "the session transcript must exceed the inline threshold to exercise offload");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", SessionTranscript = bigSession },
                CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var stored = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

            stored.SessionTranscript.ShouldBe("", "the large session transcript was moved out of result_jsonb");
            stored.SessionTranscriptArtifactId.ShouldNotBeNull("the result keeps a reference to the offloaded session transcript");

            var artifact = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, stored.SessionTranscriptArtifactId!.Value, CancellationToken.None);
            artifact.ShouldNotBeNull();
            System.Text.Encoding.UTF8.GetString(artifact!.Bytes).ShouldBe(bigSession, "the offloaded session transcript is recoverable in full — the conversation a continue restores");
        }
    }

    [Fact]
    public async Task Completing_with_a_small_transcript_keeps_it_inline_with_no_artifact()
    {
        // A small transcript stays inline in result_jsonb (no offload, no artifact ref).
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        const string smallTranscript = "[raw] started\n[raw] done\n";

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Transcript = smallTranscript },
                CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var stored = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

            stored.Transcript.ShouldBe(smallTranscript, "a small transcript stays inline");
            stored.TranscriptArtifactId.ShouldBeNull("no artifact is created for an inline transcript");
        }
    }

    [Fact]
    public async Task Reading_events_for_another_teams_run_returns_empty()
    {
        // The events read is team-scoped: a foreign run id leaks neither events nor the run's existence.
        var ownerTeam = await SeedTeamAsync();
        var otherTeam = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            var run = await svc.CreateAsync(BuildTask(), ownerTeam, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
            runId = run.Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
            await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "owner-only" }, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            (await svc.GetEventsAsync(runId, otherTeam, 0, CancellationToken.None)).ShouldBeEmpty("a foreign team sees no events");
            (await svc.GetEventsAsync(runId, ownerTeam, 0, CancellationToken.None)).ShouldHaveSingleItem();
        }
    }

    [Fact]
    public async Task Completing_a_queued_run_is_illegal()
    {
        var teamId = await SeedTeamAsync();
        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);

        // Queued → Succeeded is illegal — a run can't succeed without running.
        await Should.ThrowAsync<AgentRunTransitionException>(() =>
            svc.CompleteAsync(run.Id, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, CancellationToken.None));
    }

    [Fact]
    public async Task Completing_with_a_nonterminal_status_is_rejected()
    {
        var teamId = await SeedTeamAsync();
        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        await svc.MarkRunningAsync(run.Id, CancellationToken.None);

        await Should.ThrowAsync<AgentRunTransitionException>(() =>
            svc.CompleteAsync(run.Id, new AgentRunResult { Status = AgentRunStatus.Running, ExitReason = "still going" }, CancellationToken.None));
    }

    [Fact]
    public async Task Re_running_an_already_running_run_is_rejected()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
            runId = run.Id;
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<AgentRunTransitionException>(() =>
                scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None));
    }

    [Fact]
    public async Task Claim_returns_the_bumped_fence_epoch_and_completion_under_it_succeeds()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = (await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;

        long epoch;
        using (var scope = _fixture.BeginScope())
            epoch = await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        epoch.ShouldBe(1, "the claim bumps the fence epoch from its 0 default");

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            await svc.CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, epoch, CancellationToken.None);
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
        }
    }

    [Fact]
    public async Task Completing_under_a_stale_epoch_is_fenced_out()
    {
        // Simulates a reclaim: the run's epoch is bumped (a lease-expiry reclaim / restart re-claim) AFTER this
        // worker claimed it, so the original worker's epoch-fenced completion must lose — no double-completion.
        var teamId = await SeedTeamAsync();

        Guid runId;
        long claimedEpoch;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            claimedEpoch = await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<CodeSpaceDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET fence_epoch = fence_epoch + 1 WHERE id = {runId}");

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            await Should.ThrowAsync<AgentRunTransitionException>(() =>
                svc.CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, claimedEpoch, CancellationToken.None));

            // The run was NOT completed — it stays Running for the reclaimer to finish (no double-completion).
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running);
        }
    }

    [Fact]
    public async Task Claim_stamps_a_lease_and_heartbeat_renews_it()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = (await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        DateTimeOffset claimedLease;
        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.LeaseExpiresAt.ShouldNotBeNull("the claim stamps a lease");
            claimedLease = run.LeaseExpiresAt!.Value;
            claimedLease.ShouldBeGreaterThan(DateTimeOffset.UtcNow, "the lease is in the future (now + window)");
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().HeartbeatAsync(runId, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.LeaseExpiresAt!.Value.ShouldBeGreaterThanOrEqualTo(claimedLease, "the heartbeat pushes the lease forward");
        }
    }

    [Fact]
    public async Task Reclaim_for_reattach_bumps_the_epoch_and_re_leases_a_running_run()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        long claimedEpoch;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            claimedEpoch = await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        // Lapse the lease (the claiming worker stopped renewing it — it died) so the reclaim mirrors the real path.
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<CodeSpaceDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE id = {runId}");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None))
                .ShouldBeTrue("reclaiming a Running run wins the CAS");

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Running, "the reclaim keeps the run Running — it's re-claimed, not completed");
            run.FenceEpoch.ShouldBe(claimedEpoch + 1, "the reclaim bumps the fence epoch so a revived original observer is fenced out");
            run.LeaseExpiresAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow, "the reclaim re-leases into the future so the run drops out of the stale sweep");
        }
    }

    [Fact]
    public async Task Reclaim_for_reattach_is_a_noop_on_a_terminal_run()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        long epoch;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            epoch = await svc.MarkRunningAsync(runId, CancellationToken.None);
            await svc.CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, epoch, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None))
                .ShouldBeFalse("a terminal run can't be reclaimed for re-attach");

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded);
            run.FenceEpoch.ShouldBe(epoch, "a lost reclaim leaves the epoch untouched");
        }
    }

    [Fact]
    public async Task Completing_under_the_pre_reclaim_epoch_is_fenced_out_after_a_reattach_reclaim()
    {
        // The double-completion fence: a reclaim bumps the epoch for the re-attaching worker, so the ORIGINAL
        // worker (if it revives and tries to complete under its old epoch) loses — no double-completion.
        var teamId = await SeedTeamAsync();

        Guid runId;
        long originalEpoch;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            originalEpoch = await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            await Should.ThrowAsync<AgentRunTransitionException>(() =>
                svc.CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, originalEpoch, CancellationToken.None));

            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running, "the original worker lost the epoch-fenced CAS; the run stays Running for the re-attacher");
        }
    }

    [Fact]
    public async Task CreateAsync_rejects_the_run_that_would_breach_the_per_team_cap()
    {
        // The D4a chokepoint over real Postgres: with the per-team cap pinned to 2 (via the env override), seed
        // 2 in-flight runs for the team, then the 3rd CreateAsync must throw AgentRunAdmissionException —
        // fail-closed BEFORE the row is persisted.
        using var caps = WithCaps(perTeam: 2, global: 1000);

        var teamId = await SeedTeamAsync();

        await SeedInflightRunsAsync(teamId, queued: 1, running: 1);   // 2 in flight → AT the cap

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var ex = await Should.ThrowAsync<AgentRunAdmissionException>(() =>
            svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None));

        ex.Message.ShouldContain(AdmissionController.MaxInflightPerTeamEnvVar);   // names the env var to raise

        // The rejected run never touched the table — still exactly the 2 we seeded.
        (await CountInflightForTeamAsync(teamId)).ShouldBe(2, "the over-cap run was refused pre-persist, not inserted");
    }

    [Fact]
    public async Task CreateAsync_admits_a_run_while_under_the_per_team_cap()
    {
        using var caps = WithCaps(perTeam: 5, global: 1000);

        var teamId = await SeedTeamAsync();

        await SeedInflightRunsAsync(teamId, queued: 2, running: 1);   // 3 in flight, cap 5 → headroom

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Queued, "a sub-cap run is admitted + persisted Queued");
        (await CountInflightForTeamAsync(teamId)).ShouldBe(4);
    }

    [Fact]
    public async Task The_per_team_cap_isolates_teams_a_full_team_does_not_block_a_different_team()
    {
        // Per-team isolation: one team being AT its cap must not starve another team — the cap counts only the
        // requesting team's in-flight runs.
        using var caps = WithCaps(perTeam: 2, global: 1000);

        var fullTeam = await SeedTeamAsync();
        var otherTeam = await SeedTeamAsync();

        await SeedInflightRunsAsync(fullTeam, queued: 1, running: 1);   // fullTeam is AT its cap
        await SeedInflightRunsAsync(otherTeam, queued: 1, running: 0);  // otherTeam has plenty of headroom

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        await Should.ThrowAsync<AgentRunAdmissionException>(() =>
            svc.CreateAsync(BuildTask(), fullTeam, null, null, iterationKey: "", cancellationToken: CancellationToken.None));

        // The OTHER team, well under its own cap, is admitted normally despite the first team being full.
        var run = await svc.CreateAsync(BuildTask(), otherTeam, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Queued, "a different team under its own cap is unaffected by a full team");
    }

    [Fact]
    public async Task The_global_cap_trips_across_teams_even_when_each_team_is_under_its_own_cap()
    {
        // The deployment-wide ceiling: spread the in-flight runs across two teams so NEITHER is at its per-team
        // cap, but TOGETHER they hit the global cap — the next create (for a third team well under its own cap)
        // is still refused by the global gate.
        using var caps = WithCaps(perTeam: 100, global: 3);

        var teamA = await SeedTeamAsync();
        var teamB = await SeedTeamAsync();
        var teamC = await SeedTeamAsync();

        await SeedInflightRunsAsync(teamA, queued: 1, running: 1);   // 2
        await SeedInflightRunsAsync(teamB, queued: 1, running: 0);   // +1 = 3 global → AT the global cap

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var ex = await Should.ThrowAsync<AgentRunAdmissionException>(() =>
            svc.CreateAsync(BuildTask(), teamC, null, null, iterationKey: "", cancellationToken: CancellationToken.None));

        ex.Message.ShouldContain(AdmissionController.MaxInflightGlobalEnvVar, customMessage: "the global gate, not the per-team gate, is the binding limit here");
    }

    // ─── Completion contract (Slice A1): no run lands Succeeded while a decision it raised is unanswered ─────────

    [Theory]
    [InlineData(ToolCallLedgerStatus.AwaitingApproval)]   // parked, waiting for an answer — the common case
    [InlineData(ToolCallLedgerStatus.Pending)]            // claimed but not yet parked (a crash stranded it before the park CAS) — still unanswered, so it must still block a clean success
    public async Task Completing_a_run_with_an_unanswered_decision_re_grades_to_needs_review(ToolCallLedgerStatus decisionStatus)
    {
        // The core invariant: a would-be Succeeded run that left a decision.request unanswered is re-graded to
        // NeedsReview(NeedsDecision) carrying the decision id — the ask isn't buried under "success", and the captured
        // work (the summary) is preserved for a reviewer. BOTH unanswered statuses (Pending + AwaitingApproval) block,
        // pinning each half of FindBlockingDecisionIdAsync's status predicate.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        var decisionId = await SeedLedgerRowAsync(teamId, runId, decisionStatus, DecisionToolKinds.DecisionRequest);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did the work" }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.NeedsReview, "a would-be success with a pending decision is re-graded — never silently Succeeded");

            var stored = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            stored.Status.ShouldBe(AgentRunStatus.NeedsReview);
            stored.CompletionDisposition.ShouldBe(CompletionDisposition.NeedsDecision);
            stored.PendingDecisionId.ShouldBe(decisionId, "the result carries the unanswered decision's id for a reviewer / the queue to resolve");
            stored.Summary.ShouldBe("did the work", "the captured work is preserved — only the verdict changed");
        }
    }

    [Fact]
    public async Task Completing_a_run_with_no_pending_decision_stays_a_clean_success()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded);

            var stored = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            stored.CompletionDisposition.ShouldBe(CompletionDisposition.Completed, "a clean success carries the Completed disposition");
            stored.PendingDecisionId.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Completing_a_run_whose_decision_was_already_answered_stays_a_clean_success()
    {
        // Precision: the gate fires ONLY on an UNANSWERED decision. An answered one (its row flipped to Succeeded) is
        // no longer outstanding, so the run lands a clean Succeeded.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        await SeedLedgerRowAsync(teamId, runId, ToolCallLedgerStatus.Succeeded, DecisionToolKinds.DecisionRequest);   // answered

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status
                .ShouldBe(AgentRunStatus.Succeeded, "an answered decision is not outstanding — the run succeeds cleanly");
    }

    [Fact]
    public async Task Completing_a_run_with_only_a_pending_side_effecting_approval_stays_succeeded()
    {
        // Precision: the completion contract is DECISION-specific. A pending side-effecting approval row (git.open_pr —
        // the approve_action gate's concern) is NOT a decision.request, so it does not re-grade the run to NeedsReview.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        await SeedLedgerRowAsync(teamId, runId, ToolCallLedgerStatus.AwaitingApproval, "git.open_pr");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status
                .ShouldBe(AgentRunStatus.Succeeded, "a pending approval is not a decision — the completion contract leaves it alone");
    }

    [Fact]
    public async Task Completing_a_failed_run_with_a_pending_decision_stays_failed()
    {
        // The gate re-grades ONLY a would-be success. A genuinely Failed run with a leftover pending decision stays
        // Failed — its status is already the final word.
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        await SeedLedgerRowAsync(teamId, runId, ToolCallLedgerStatus.AwaitingApproval, DecisionToolKinds.DecisionRequest);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = "boom" }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status
                .ShouldBe(AgentRunStatus.Failed, "the contract re-grades only a would-be success, never a failure");
    }

    [Fact]
    public async Task Completing_a_run_with_an_answered_and_an_unanswered_decision_re_grades_to_the_unanswered_one()
    {
        // A run can raise several decisions over its life. An ANSWERED sibling must neither suppress the re-grade nor
        // leak its own id — the run lands NeedsReview carrying the STILL-unanswered decision's id (not the answered one).
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        await SeedLedgerRowAsync(teamId, runId, ToolCallLedgerStatus.Succeeded, DecisionToolKinds.DecisionRequest);                       // an earlier decision, already answered
        var unansweredId = await SeedLedgerRowAsync(teamId, runId, ToolCallLedgerStatus.AwaitingApproval, DecisionToolKinds.DecisionRequest);   // still open

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.NeedsReview, "an answered sibling must not let the run succeed while another decision is open");
            JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!.PendingDecisionId
                .ShouldBe(unansweredId, "the re-grade carries the UNANSWERED decision's id, never the answered sibling's");
        }
    }

    // ─── A2 final-output review (best-effort net, opt-in): a question-ending success → NeedsReview(NeedsReview) ────

    [Fact]
    public async Task With_review_enabled_completing_a_run_whose_final_output_asks_a_question_re_grades_to_needs_review()
    {
        // Slice A2 (opt-in best-effort net): a would-be success whose FINAL message ends on an unresolved question is
        // re-graded to NeedsReview(NeedsReview) — carrying NO decision id (it's a heuristic, not a raised decision).
        using var review = WithFinalOutputReview(true);

        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "I drafted the migration. Should I apply it?" }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.NeedsReview, "a question-ending success is re-graded when the net is enabled");

            var stored = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            stored.CompletionDisposition.ShouldBe(CompletionDisposition.NeedsReview);
            stored.PendingDecisionId.ShouldBeNull("A2 is a heuristic — it carries no decision id");
        }
    }

    [Fact]
    public async Task With_review_disabled_a_question_ending_success_stays_succeeded()
    {
        // Default-OFF: the heuristic must not change behaviour unless an operator opts in.
        using var review = WithFinalOutputReview(false);

        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "Should I apply it?" }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status
                .ShouldBe(AgentRunStatus.Succeeded, "with the net off (default), a question-ending success is unchanged");
    }

    [Fact]
    public async Task A_pending_decision_outranks_the_final_output_heuristic()
    {
        // Precedence: when BOTH a real unanswered decision AND a question-ending summary are present, A1 wins — the run
        // is NeedsReview(NeedsDecision) carrying the decision id, not the weaker NeedsReview(NeedsReview).
        using var review = WithFinalOutputReview(true);

        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        var decisionId = await SeedLedgerRowAsync(teamId, runId, ToolCallLedgerStatus.AwaitingApproval, DecisionToolKinds.DecisionRequest);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId,
                new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "Should I apply it?" }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var stored = JsonSerializer.Deserialize<AgentRunResult>(
                (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).ResultJson!, AgentJson.Options)!;
            stored.Status.ShouldBe(AgentRunStatus.NeedsReview);
            stored.CompletionDisposition.ShouldBe(CompletionDisposition.NeedsDecision, "the raised decision (A1) outranks the final-output heuristic (A2)");
            stored.PendingDecisionId.ShouldBe(decisionId);
        }
    }

    private static IDisposable WithFinalOutputReview(bool enabled) => new FinalOutputReviewFlag(enabled);

    /// <summary>Scope the A2 opt-in flag for one test; restores the prior value on Dispose (the shared-process env stays isolated — the PostgresCollection runs these sequentially).</summary>
    private sealed class FinalOutputReviewFlag : IDisposable
    {
        private readonly string? _prior = Environment.GetEnvironmentVariable(FinalOutputReview.EnabledEnvVar);
        public FinalOutputReviewFlag(bool enabled) => Environment.SetEnvironmentVariable(FinalOutputReview.EnabledEnvVar, enabled ? "true" : null);
        public void Dispose() => Environment.SetEnvironmentVariable(FinalOutputReview.EnabledEnvVar, _prior);
    }

    private const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Plant a ledger row for a run directly (bypassing the MCP park path) so the completion gate sees a real decision.request / approval row in the given state.</summary>
    private async Task<Guid> SeedLedgerRowAsync(Guid teamId, Guid runId, ToolCallLedgerStatus status, string toolKind)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = id, TeamId = teamId, AgentRunId = runId, ToolKind = toolKind,
            IdempotencyKey = $"{toolKind}:{id:N}", InputHash = ZeroHash, Status = status,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return id;
    }

    // ─── Admission helpers ──────────────────────────────────────────────────────

    /// <summary>Pin the in-flight caps for one test via the env overrides; restores the prior values on Dispose so the shared-process env stays isolated (mirrors AdmissionControllerTests).</summary>
    private static IDisposable WithCaps(int perTeam, int global) => new CapOverride(perTeam, global);

    private sealed class CapOverride : IDisposable
    {
        private readonly string? _perTeam = Environment.GetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar);
        private readonly string? _global = Environment.GetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar);

        public CapOverride(int perTeam, int global)
        {
            Environment.SetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar, perTeam.ToString());
            Environment.SetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar, global.ToString());
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar, _perTeam);
            Environment.SetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar, _global);
        }
    }

    // Insert raw in-flight (Queued/Running) AgentRun rows directly — bypassing CreateAsync (the gate under test),
    // so the seed itself is never refused. The whole-test isolation comes from each team being a fresh GUID.
    private async Task SeedInflightRunsAsync(Guid teamId, int queued, int running)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        for (var i = 0; i < queued; i++)
            db.AgentRun.Add(new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Queued, TaskJson = "{}" });

        for (var i = 0; i < running; i++)
            db.AgentRun.Add(new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, TaskJson = "{}" });

        await db.SaveChangesAsync();
    }

    private async Task<int> CountInflightForTeamAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .CountAsync(r => r.TeamId == teamId && (r.Status == AgentRunStatus.Queued || r.Status == AgentRunStatus.Running));
    }

    [Fact]
    public async Task CreateAsync_promotes_the_tasks_agent_definition_id_onto_the_run()
    {
        var teamId = await SeedTeamAsync();
        var agentDefinitionId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            BuildTask() with { AgentDefinitionId = agentDefinitionId }, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);

        run.AgentDefinitionId.ShouldBe(agentDefinitionId,
            customMessage: "the persona is promoted from the task onto the agent_run column at creation — the key the runs index filters by");
    }

    private static AgentTask BuildTask(string goal = "Fix the failing billing tests") =>
        new() { Goal = goal, Harness = "codex-cli", Model = "gpt-5.3-codex" };

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
