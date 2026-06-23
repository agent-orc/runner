using System.Diagnostics;
using System.Text;
using CodingAgentRunner.Abstractions;

namespace CodingAgentRunner.Execution.Hardening;

/// <summary>
/// Applies the environment-variable hardening every coding-agent CLI needs to run
/// reliably and produce a parseable, leak-free stream — especially on Windows
/// under a long-lived host process.
/// </summary>
internal static class EnvironmentHardening
{
    /// <summary>
    /// Stamp the hardening environment onto <paramref name="psi"/>. Consumer
    /// <paramref name="environmentOverrides"/> are applied last and win.
    /// </summary>
    public static void Apply(
        ProcessStartInfo psi,
        CliHardeningOptions? options = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        options ??= new CliHardeningOptions();

        if (options.EnforceUtf8)
        {
            // Windows defaults the redirected streams to the system code page,
            // which corrupts non-ASCII bytes from the CLI and has caused silent
            // crashes on prompts/output containing umlauts. Force UTF-8 on both
            // ends, and tell the (Node/Python-based) CLI to emit UTF-8 too.
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["LC_ALL"] = "C.UTF-8";
            psi.Environment["LANG"] = "C.UTF-8";
            psi.Environment["NODE_NO_WARNINGS"] = "1";
        }

        // Suppress interactive auto-update prompts, colour escape sequences that
        // confuse stream parsers, and tip-of-the-day banners that would otherwise
        // dominate the output. CLI-specific but harmless when set for all.
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["FORCE_COLOR"] = "0";
        psi.Environment["CLAUDE_CODE_DISABLE_AUTOUPDATER"] = "1";
        psi.Environment["GEMINI_NO_UPDATE_NOTIFIER"] = "1";
        psi.Environment["CODEX_DISABLE_TIP_OF_THE_DAY"] = "1";
        // CI=1 is the conventional non-interactive marker most npm CLIs respect.
        psi.Environment["CI"] = "1";

        // .NET build-server suppression. An agent that runs `dotnet build`/`test`
        // otherwise leaves PERSISTENT detached MSBuild worker nodes and a build
        // server behind; those re-parent away from the CLI's process tree, evade
        // a process-tree kill, and accumulate into a host-starving process leak.
        // Disabling node-reuse + the MSBuild server makes each agent `dotnet`
        // invocation tear its build processes down with the build.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        // Consumer overrides win.
        if (environmentOverrides is not null)
            foreach (var kv in environmentOverrides)
                psi.Environment[kv.Key] = kv.Value;
    }
}
