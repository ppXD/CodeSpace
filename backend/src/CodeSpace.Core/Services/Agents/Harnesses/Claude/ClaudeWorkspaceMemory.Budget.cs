namespace CodeSpace.Core.Services.Agents.Harnesses.Claude;

/// <summary>
/// What one build may spend finding and checking memory (<see cref="Budget"/>), and the guard that checks each directory
/// against it (<see cref="Guard"/>).
/// </summary>
internal static partial class ClaudeWorkspaceMemory
{
    /// <summary>The most distinct paths one build resolves to find and check memory, the walk and every directory's check together. Pinned by a test.</summary>
    internal const int MaxBuildLookups = 65536;

    /// <summary>The most path components one build walks to resolve them, every link target's own included. Pinned by a test.</summary>
    internal const int MaxBuildComponents = 1048576;

    /// <summary>The most bytes of memory one build reads, the walk's and every directory's check's together. Pinned by a test.</summary>
    internal const long MaxBuildBytes = 64L * 1024 * 1024;

    /// <summary>Why a directory the guard never got to check is left out; such directories share <see cref="BudgetNotice"/> rather than a notice each.</summary>
    private const string Unchecked = "the build's budget ran out before its memory was checked";

    /// <summary>Said once when a build spent its budget, whoever spent it.</summary>
    private static readonly string BudgetNotice = $"Left any memory the runner had not yet found or checked out of this run: finding and checking it would take more than one build spends ({MaxBuildLookups} paths, {MaxBuildComponents} path components, {MaxBuildBytes} bytes).";

    /// <summary>
    /// What one build may spend finding and checking memory, shared by the walk below the cwd and every directory the guard
    /// checks. The bounds of one walk or one directory's check (<see cref="MaxWalkedEntries"/>, <see cref="MaxLookups"/>,
    /// <see cref="MaxScannedBytes"/>, <see cref="PhysicalPath.MaxLinkHops"/>) do not bound their sum, nor how long each
    /// link target a lookup follows is: a few links that each climb a long chain cost milliseconds per lookup, and a build
    /// checks many directories. So every distinct path the build resolves, every component walked to resolve it and every
    /// byte read counts against this one budget, and each path is resolved once per build — nothing writes the workspace
    /// while a build runs. Once any bound is passed the budget is spent: every further lookup, read or walk step throws
    /// <see cref="MemoryBudgetSpentException"/>, and the timeline says so once (<see cref="BudgetNotice"/>).
    /// </summary>
    private sealed class Budget
    {
        private readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);
        private readonly PhysicalPath.Allowance _components = new(MaxBuildComponents);
        private int _lookups;
        private long _bytes;

        /// <summary>Whether the build has wanted more than it may spend.</summary>
        public bool Spent { get; private set; }

        /// <summary>Where <paramref name="path"/> really is (<see cref="PhysicalPath.File"/>), resolved once per build.</summary>
        public string? Resolve(string path)
        {
            EnsureLeft();

            if (_resolved.TryGetValue(path, out var physical)) return physical;

            Spend(++_lookups > MaxBuildLookups);

            physical = PhysicalPath.File(path, _components);

            Spend(_components.Spent);

            return _resolved[path] = physical;
        }

        /// <summary>Counts <paramref name="bytes"/> the build just read.</summary>
        public void Read(long bytes) => Spend((_bytes += bytes) > MaxBuildBytes);

        /// <summary>Throws once the budget is spent, so a walk stops at its next step.</summary>
        public void EnsureLeft() => Spend(false);

        private void Spend(bool over)
        {
            Spent |= over;

            if (Spent) throw new MemoryBudgetSpentException();
        }
    }

    /// <summary>Thrown by every <see cref="Budget"/> call once the build has spent it.</summary>
    private sealed class MemoryBudgetSpentException() : Exception("The build spent what it may on finding and checking memory.");

    /// <summary>What the guard found of one directory's memory: why it must be left out (null when it stays inside the workspace, <see cref="Unchecked"/> when the budget ran out first), and how many bytes the files it imports hold — which load beside it.</summary>
    private sealed record Check(string? Escape, long ImportedBytes);

    /// <summary>
    /// The outside-link guard of one build: whether a directory's memory reaches outside the physical
    /// <paramref name="workspace"/> or cannot be checked, and why — each directory checked once against the build's
    /// <paramref name="budget"/>, whichever route asks, an <c>--add-dir</c> or a pointer. The workspace is resolved by the
    /// same walker as everything its memory reaches, so the two sides of the comparison cannot disagree on a link.
    /// </summary>
    private sealed class Guard(string workspace, Budget budget)
    {
        private readonly Dictionary<string, Check> _checks = new(StringComparer.Ordinal);

        public Budget Budget => budget;

        public Check Of(string directory)
        {
            var key = Path.TrimEndingDirectorySeparator(directory);

            if (!_checks.TryGetValue(key, out var check)) _checks[key] = check = Run(directory);

            return check;
        }

        /// <summary>One directory's check. A directory the guard cannot finish reading is left out rather than failing the launch.</summary>
        private Check Run(string directory)
        {
            var closure = new Closure(workspace, directory, budget);

            try
            {
                return new Check(closure.FirstEscape(), closure.ImportedBytes);
            }
            catch (MemoryBudgetSpentException)
            {
                return new Check(Unchecked, 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return new Check("its memory could not be checked", 0);
            }
        }
    }
}
