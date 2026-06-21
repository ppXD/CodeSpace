namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Serializes every test class that exercises the C3 stall watchdog. <c>LocalProcessRunner.IdleTimeout()</c> reads the
/// process-global <c>CODESPACE_AGENT_STDOUT_IDLE_TIMEOUT_SECONDS</c> live, once at the start of each streaming/durable
/// run; the watchdog tests mutate-then-restore that global. Without this collection, xUnit would run these classes in
/// parallel and one class's restore-to-default could land in the instant another's run reads the env — flaking it.
/// Sharing one collection makes them run serially relative to each other (the batch <c>RunAsync</c> path never reads
/// the env, so its class is deliberately excluded).
/// </summary>
[CollectionDefinition("LocalProcessIdleWatchdog", DisableParallelization = true)]
public sealed class LocalProcessIdleWatchdogCollection;
