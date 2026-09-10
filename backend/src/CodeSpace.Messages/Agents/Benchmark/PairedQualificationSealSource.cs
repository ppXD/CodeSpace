namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>How the sealing caller came by the observations it is sealing. A FACT about the path, not a switch: it decides whether this seal must re-verify the live runtime, and getting it wrong in either direction is a real failure (a paid campaign sealed on a substituted runtime, or a fully-paid one stranded).</summary>
public enum PairedQualificationSealSource
{
    /// <summary>The caller executed this campaign's paid cells in this process. The live runtime is re-verified: the campaign could have been redeployed underneath its own last cell, and the seal is what mints the capability claim.</summary>
    Execution = 0,

    /// <summary>The caller only replayed durable observation rows — no model call, no new measurement. NOT re-verified: every row it reduces was already gated at admission and execution when it was produced, and the seal binds the protocol digest those cells ran under, so refusing here would strand a fully-paid campaign on a host whose runtime moved after its last cell.</summary>
    Replay = 1,
}
