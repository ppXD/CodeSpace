namespace CodeSpace.Messages.Health;

/// <summary>What <c>git --version</c> said on this host: the release when it could be read, and the sentence an operator reads either way — git's own output, or why there was none.</summary>
public sealed record GitReading(GitVersion? Version, string Detail);
