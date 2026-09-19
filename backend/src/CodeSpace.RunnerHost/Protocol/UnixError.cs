namespace CodeSpace.NativeLaunch;

/// <summary>
/// A <c>errno</c> in words. The bootstrap's failures are read by an operator, in a run's error text, long after the
/// process that produced them is gone — and "errno 2" tells that reader nothing about what to go and fix, while
/// "ENOENT: the executable does not exist at that path" tells them where to look. The handful below are the ones a
/// launch can actually end on: everything this bootstrap asks the kernel for is an exec or a file descriptor.
///
/// <para>TOTAL by construction, by the rule <see cref="NativeProcess.Explain"/> states: this is only ever called to
/// explain a failure that has already happened, so it is a pure lookup with no IO and nothing to throw. An errno it
/// does not know is reported as the number it is rather than guessed at.</para>
/// </summary>
internal static class UnixError
{
    /// <summary><paramref name="error"/> rendered as <c>errno 2 (ENOENT: the executable does not exist at that path)</c>, or <c>errno 99 (unknown)</c> for one not listed.</summary>
    public static string Describe(int error) => Known(error) is { } known ? $"errno {error} ({known.Name}: {known.Meaning})" : $"errno {error} (unknown)";

    /// <summary>
    /// The errno values this bootstrap can end on, with what each one means HERE. Numbers that differ between the two
    /// supported platforms are keyed by platform rather than pinned to Linux's: the same symbol is 40 on Linux and 62
    /// on macOS, and a table that answered Linux's value on a dev box would name the wrong failure.
    /// </summary>
    private static (string Name, string Meaning)? Known(int error) => error switch
    {
        1 => ("EPERM", "the kernel refused this process permission"),
        2 => ("ENOENT", "the executable does not exist at that path"),
        5 => ("EIO", "the filesystem could not read it"),
        7 => ("E2BIG", "the argument and environment block is larger than the kernel accepts"),
        8 => ("ENOEXEC", "the file is not a format this kernel can execute"),
        9 => ("EBADF", "the file descriptor is not open"),
        12 => ("ENOMEM", "the kernel could not allocate for the new image"),
        13 => ("EACCES", "the file is not executable, or a directory on its path is not searchable"),
        20 => ("ENOTDIR", "a component of the path is not a directory"),
        24 => ("EMFILE", "this process is at its open-file limit"),
        26 => ("ETXTBSY", "the executable is open for writing"),
        _ when error == (OperatingSystem.IsMacOS() ? 62 : 40) => ("ELOOP", "too many symbolic links on the path"),
        _ when error == (OperatingSystem.IsMacOS() ? 63 : 36) => ("ENAMETOOLONG", "the path is longer than the kernel accepts"),
        _ => null,
    };
}
