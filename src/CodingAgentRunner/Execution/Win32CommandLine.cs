using System.Text;

namespace CodingAgentRunner.Execution;

/// <summary>
/// Builds a single Win32 command-line string from an executable + argv, applying
/// Microsoft's documented argv-quoting algorithm (the same rules
/// <c>ProcessStartInfo.ArgumentList</c> uses, so a hand-built
/// <c>CreateProcessW</c> line round-trips identically through
/// <c>CommandLineToArgvW</c>).
///
/// <para>
/// Pure and platform-agnostic so the escaping — the genuinely bug-prone part of a
/// raw spawn — can be unit-tested anywhere, even though the only consumer
/// (<see cref="Win.WindowsHandleScrubSpawner"/>) is Windows-only.
/// </para>
/// </summary>
internal static class Win32CommandLine
{
    /// <summary>Build a command line: the quoted exe followed by each quoted arg.</summary>
    public static string Build(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        AppendArg(sb, exe);
        foreach (var a in args)
        {
            sb.Append(' ');
            AppendArg(sb, a);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Append one argument with Win32 argv-quoting (from Raymond Chen's
    /// "Everyone quotes command line arguments the wrong way").
    /// </summary>
    public static void AppendArg(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        for (int i = 0; ; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(arg[i]);
            }
        }
        sb.Append('"');
    }
}
