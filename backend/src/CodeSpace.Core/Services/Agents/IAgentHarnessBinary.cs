namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// SIBLING capability (Rule 7) for a harness that shells out to an EXTERNAL executable: it can name the command it
/// would run, so a caller that must freeze what actually executed can observe those bytes. Deliberately not a member
/// of <see cref="IAgentHarness"/> — a future in-process or HTTP-backed harness drives no local binary at all, and
/// widening the identity interface would force it to answer a question it has no answer to.
///
/// <para>The one member returns the SAME string <see cref="IAgentHarness.BuildInvocation"/> stamps onto
/// <c>SandboxSpec.Command</c>. That identity is the whole contract: an observer that resolved the binary any other
/// way would freeze bytes the run never executed.</para>
/// </summary>
public interface IAgentHarnessBinary
{
    /// <summary>The executable this harness runs — an absolute path when the operator repointed it, else a bare name the OS resolves on PATH.</summary>
    string ResolveCommand();
}
