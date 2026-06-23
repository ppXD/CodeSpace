namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// A fake agent CLI (real-scenario coverage B2) that RAISES a real <c>decision.request</c> over the run's in-process
/// MCP endpoint, BLOCKS on the answer, then RESUMES — the piece no existing fake CLI provides (the others are
/// file/stdout-only). The REAL <see cref="CodeSpace.Core.Services.Agents.AgentRunExecutor"/> runs this script as the
/// agent process (<c>/bin/sh -c</c>) while the endpoint is open, so a passing test proves the WHOLE interaction edge: a
/// real agent process → the per-run UDS endpoint → the decision substrate (park + answer + resume) → process exit.
///
/// <para><b>How it speaks MCP.</b> Pure <c>/bin/sh</c> can't open an <c>AF_UNIX</c> socket, so the script execs the REAL
/// <c>codespace-mcp</c> proxy (the same stdio↔UDS bridge a Codex/Claude CLI launches) and pipes newline-delimited
/// JSON-RPC through it. The per-run socket + token (minted only AFTER the endpoint opens, so they can't be pre-injected)
/// and the proxy dll path are handed to the script by the TEST via a creds file it writes once it has them — a
/// test-fixture detail that does not touch the substrate (the raise → park → answer → resume all run through the real
/// endpoint + real proxy + real ledger).</para>
///
/// <para><b>The five acceptance gates this enables.</b> (1) the durable decision ledger row is never lost; (2) the
/// answer is exactly-once; (3) the agent does NOT re-run a completed side effect on resume — it writes <see cref="PreMarker"/>
/// ONCE before the decision and <see cref="PostMarker"/> ONCE after the reply; (4) it exits 0 after resuming; (5) the
/// full raise→park→answer→resume sequence is observable. POSIX <c>/bin/sh</c> only (no bashisms / no <c>coproc</c>), so
/// it runs on every host the integration suite targets; the test skips when the proxy dll is absent.</para>
/// </summary>
public sealed class DecisionRaisingFakeCli : IDisposable
{
    private readonly string _dir;

    /// <summary>The decision the fake agent raises — a low-risk choose-one the test answers with option "a".</summary>
    public const string Question = "Which migration path?";
    public static readonly IReadOnlyList<string> OptionIds = new[] { "a", "b" };

    public DecisionRaisingFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-b2-decisionraising-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        CredsFile = Path.Combine(_dir, "creds");
        PreMarker = Path.Combine(_dir, "pre");
        PostMarker = Path.Combine(_dir, "post");
        SeenFile = Path.Combine(_dir, "seen");
        DebugLog = Path.Combine(_dir, "debug.log");
    }

    /// <summary>A progress + proxy-stderr log the script appends to, so a failing test can surface exactly where the agent stalled.</summary>
    public string DebugLog { get; }

    /// <summary>The debug log's content (empty if the script never started), for a diagnostic on a failed assertion.</summary>
    public string ReadDebug() { try { return File.ReadAllText(DebugLog); } catch { return "(no debug log)"; } }

    /// <summary>The file the TEST writes (3 lines: socket path, run token, proxy dll path) once the endpoint has minted them. The script polls it before connecting.</summary>
    public string CredsFile { get; }

    /// <summary>Written ONCE by the script BEFORE it raises the decision — the "already-completed side effect" gate 3 asserts is not re-run.</summary>
    public string PreMarker { get; }

    /// <summary>Written ONCE by the script AFTER the answer reply arrives — proof the agent process RESUMED past the blocked call (gate 4).</summary>
    public string PostMarker { get; }

    private string SeenFile { get; }

    /// <summary>Hand the script the per-run socket + token (from the opened endpoint) and the resolved proxy dll path. Atomic write so the script's <c>-s</c> poll never reads a half-written file.</summary>
    public void WriteCreds(string socketPath, string token, string proxyDllPath)
    {
        var tmp = CredsFile + ".tmp";
        File.WriteAllText(tmp, $"{socketPath}\n{token}\n{proxyDllPath}\n");
        File.Move(tmp, CredsFile, overwrite: true);
    }

    /// <summary>The <c>/bin/sh</c> body the test hands a <c>ScriptedHarness</c> — it runs as the real agent process.</summary>
    public string Script => string.Join('\n',
        "# B2: raise a real decision.request over the per-run MCP endpoint via the real codespace-mcp proxy, block, resume.",
        $"CREDS='{CredsFile}'; PRE='{PreMarker}'; POST='{PostMarker}'; SEEN='{SeenFile}'; LOG='{DebugLog}'",
        "echo raised >> \"$PRE\"   # gate 3: the pre-decision side effect, APPENDED so a re-run would show >1 line",
        "echo \"pre-written; sh=$0; dotnet=$(command -v dotnet); sed=$(command -v sed)\" >> \"$LOG\"",
        "n=0; while [ ! -s \"$CREDS\" ]; do n=$((n+1)); if [ $n -gt 600 ]; then echo 'no creds' >> \"$LOG\"; exit 21; fi; sleep 0.1; done",
        "SOCK=`sed -n 1p \"$CREDS\"`; TOK=`sed -n 2p \"$CREDS\"`; DLL=`sed -n 3p \"$CREDS\"`",
        "[ -n \"$SOCK\" ] && [ -n \"$TOK\" ] && [ -n \"$DLL\" ] || { echo \"bad creds sock=[$SOCK] dll=[$DLL]\" >> \"$LOG\"; exit 22; }",
        "echo \"creds ok sock=$SOCK dll=$DLL tok_len=${#TOK}\" >> \"$LOG\"",
        "export CODESPACE_MCP_SOCKET=\"$SOCK\" CODESPACE_RUN_TOKEN=\"$TOK\"",
        "{",
        "  printf '%s\\n' '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}'",
        "  printf '%s\\n' '{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"decision.request\",\"arguments\":{\"question\":\"" + Question + "\",\"decisionType\":\"choose_one\",\"options\":[{\"id\":\"a\",\"label\":\"additive\"},{\"id\":\"b\",\"label\":\"destructive\"}],\"recommendedOption\":\"a\"}}}'",
        "  # keep stdin open (so the proxy does not EOF) until we have seen the decision reply",
        "  m=0; while [ ! -f \"$SEEN\" ]; do m=$((m+1)); if [ $m -gt 1800 ]; then break; fi; sleep 0.1; done",
        "} | dotnet \"$DLL\" --proxy 2>>\"$LOG\" | while IFS= read -r line; do",
        "  echo \"reply: $line\" >> \"$LOG\"",
        "  case \"$line\" in",
        "    *'\"id\":2'*) : > \"$SEEN\"; echo resumed >> \"$POST\"; break ;;   # gate 4: answer arrived → resume; APPEND so a double-resume would show >1 line (symmetric with PRE)",
        "  esac",
        "done",
        "[ -f \"$POST\" ] || { echo 'no decision reply' >> \"$LOG\"; exit 23; }",
        "exit 0");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
