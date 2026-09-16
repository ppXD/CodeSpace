using Xunit;

// Every test class in this assembly runs one at a time.
//
// xUnit parallelises ACROSS collections only, and this suite's collection map made that the wrong default. 45 of the
// 54 Surface=Engine classes and all of the heavy real-model classes already sit in PostgresCollection, so they were
// serial anyway — but 30 of the 34 Surface=Http classes carry no collection at all, and each of them creates a GUID
// database and runs DbUp's 230-odd migrations in its fixture. Run concurrently, that is far more than a developer
// Postgres will take: measured on this suite at Category=E2E&Surface=Http, 101 of 172 reported results failed with
// `53200: out of shared memory` (max_locks_per_transaction exhausted by the concurrent DDL), with no test in the
// filter doing anything wrong. CI hides it because every lane gets its own Postgres container and a smaller
// runner.
//
// Serialised, the same filter plus the Worker lane is 160/160 green in 1m50s — thirty seconds more wall time than
// the broken parallel run, in exchange for a suite that a developer can actually run. The cost lands almost
// entirely on the Http lane: the Engine lane's 41-class block is already serial, the 13 classes outside it are
// DB-less evidence and vocabulary readers, and the real-model lanes are collection-serial to begin with.
//
// It is also the only lever xUnit offers for the property RecurringJobWorkerSmokeE2ETests needs. That class boots
// the one fixture in the suite that runs as HangfireHosting=Worker, so it holds ControlWorkerCount +
// ProcessorCount * 2 Postgres connections for its lifetime and its ticks execute real filesystem sweeps. Serialising
// against a collection would only have covered the 4 Http classes that are in one; the other 30 would still have
// raced it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
