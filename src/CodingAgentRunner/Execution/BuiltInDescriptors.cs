using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Adapters;
using CodingAgentRunner.Attachments;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution.Hardening;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Execution;

/// <summary>
/// The built-in <see cref="CliDescriptor"/>s — one per supported CLI — plus the
/// default catalog that registers them. Each descriptor is pure data + delegates,
/// lifted from what used to be a per-CLI driver subclass; the engine USES them and
/// nothing subclasses anything. A new built-in CLI is one more entry here.
/// </summary>
internal static class BuiltInDescriptors
{
    private static readonly Regex Uuid = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    /// <summary>A fresh catalog with every built-in descriptor registered.</summary>
    public static CliCatalog DefaultCatalog() => new CliCatalog()
        .Register(Claude).Register(Codex).Register(Gemini).Register(Antigravity);

    /// <summary>Resolve a built-in descriptor by CLI type (used by the runner + tests).</summary>
    public static CliDescriptor Get(string cliType) => DefaultCatalog().Get(cliType);

    // ── Claude ──────────────────────────────────────────────────────────

    /// <summary>Claude Code: <c>claude -p &lt;prompt&gt; --output-format stream-json --verbose</c>, CLAUDE_CONFIG_DIR clean home, npm-shim heal.</summary>
    public static readonly CliDescriptor Claude = new()
    {
        CliType = CliTypes.Claude,
        GetCliPath = o => o.ClaudePath ?? "claude",
        CleanContext = new CleanContextSpec("CLAUDE_CONFIG_DIR", ".claude", [".credentials.json", "settings.json"]),
        Capabilities = m => Capabilities(CliTypes.Claude, m, supportsCleanContext: true, supportsResume: true),
        CanResumeSessionId = static _ => true,
        InterruptClassifier = InterruptClassifiers.None,
        Liveness = LivenessSpec.InBandDefault,
        Parse = (line, runId, stream) => stream == CliStreamKind.Stdout ? ClaudeEventAdapter.Map(line, runId) : Array.Empty<CliRunEvent>(),
        EnsureHealthy = HealClaudeAsync,
        BuildLaunch = ClaudeLaunch,
    };

    private static LaunchSpec ClaudeLaunch(CliLaunchContext ctx)
    {
        var r = ctx.Request;
        var argv = new List<string> { "-p" };
        if (!string.IsNullOrWhiteSpace(r.ResumeSessionId)) { argv.Add("-r"); argv.Add(r.ResumeSessionId!); }
        if (!string.IsNullOrWhiteSpace(ctx.ResolvedModel)) { argv.Add("--model"); argv.Add(ctx.ResolvedModel!); }
        foreach (var flag in CliReasoningFlags.For(CliTypes.Claude, ctx.ResolvedModel, ctx.ResolvedThinkingLevel)) argv.Add(flag);
        argv.Add("--output-format"); argv.Add("stream-json"); argv.Add("--verbose");
        foreach (var flag in CliPermissionFlags.For(CliTypes.Claude, r.PermissionMode)) argv.Add(flag);
        if (!string.IsNullOrEmpty(r.Prompt)) argv.Add(r.Prompt);   // prompt is the LAST positional argv
        return new LaunchSpec
        {
            Executable = ResolveClaudeBinary(ctx.CliPath, ctx.Logger),
            Argv = argv,
            WorkingDirectory = r.WorkingDirectory,
            NormalizedModel = ctx.ResolvedModel,
        };
    }

    private static async Task<(bool Ok, string? Error)> HealClaudeAsync(PreSpawnHealthContext ctx, CancellationToken ct)
    {
        var probe = ctx.Probe();
        if (probe.Available) return (true, null);
        ctx.Logger.LogWarning("claude --version failed pre-spawn at '{Path}'; running the npm-shim healer", probe.Path);
        var outcome = await NpmShimHealer.TryHealClaudeAsync(ctx.Logger, ct).ConfigureAwait(false);
        if (outcome.Actions.Count > 0)
            ctx.Logger.LogInformation("npm-shim healer actions for claude: {Actions}", string.Join("; ", outcome.Actions));
        if (!outcome.Available)
            return (false, outcome.Error ?? "npm-shim healer reported claude unavailable after repair");
        var verify = ctx.Probe();
        return verify.Available ? (true, null) : (false, $"claude --version still failing after heal at '{verify.Path}'");
    }

    // ── Codex ───────────────────────────────────────────────────────────

    /// <summary>Codex: <c>codex exec --experimental-json</c>, prompt via stdin (<c>-</c>), CODEX_HOME clean home, UUID resume.</summary>
    public static readonly CliDescriptor Codex = new()
    {
        CliType = CliTypes.Codex,
        GetCliPath = o => o.CodexPath ?? "codex",
        CleanContext = new CleanContextSpec("CODEX_HOME", ".codex", ["auth.json", "config.toml"]),
        Capabilities = m => Capabilities(CliTypes.Codex, m, supportsCleanContext: true, supportsResume: true),
        CanResumeSessionId = static s => !string.IsNullOrWhiteSpace(s) && Uuid.IsMatch(s),
        InterruptClassifier = InterruptClassifiers.None,
        Liveness = LivenessSpec.InBandDefault,
        Parse = (line, runId, stream) => stream is CliStreamKind.Stdout or CliStreamKind.Stderr ? CodexEventAdapter.Map(line, runId, stream) : Array.Empty<CliRunEvent>(),
        BuildLaunch = CodexLaunch,
    };

    private static LaunchSpec CodexLaunch(CliLaunchContext ctx)
    {
        var r = ctx.Request;
        // ALL exec-level options must precede `resume`: --sandbox is not a global flag.
        var argv = new List<string> { "exec", "--experimental-json" };
        foreach (var flag in CliPermissionFlags.For(CliTypes.Codex, r.PermissionMode)) argv.Add(flag);
        if (!string.IsNullOrWhiteSpace(ctx.ResolvedModel)) { argv.Add("-m"); argv.Add(ctx.ResolvedModel!); }
        foreach (var flag in CliReasoningFlags.For(CliTypes.Codex, ctx.ResolvedModel, ctx.ResolvedThinkingLevel)) argv.Add(flag);
        if (r.Tuning is { Count: > 0 })
            foreach (var kv in r.Tuning) { argv.Add("-c"); argv.Add($"{kv.Key}={kv.Value}"); }
        foreach (var attachment in ctx.Attachments.Where(IsImage))
        {
            argv.Add("--image");
            argv.Add(attachment.AbsolutePath);
        }
        if (!string.IsNullOrWhiteSpace(r.ResumeSessionId) && Uuid.IsMatch(r.ResumeSessionId!))
        {
            argv.Add("resume"); argv.Add(r.ResumeSessionId!);
        }
        if (!string.IsNullOrEmpty(r.Prompt)) argv.Add("-");   // `-` reads the prompt from stdin
        return new LaunchSpec
        {
            Executable = SafeResolve(ctx.CliPath),
            Argv = argv,
            WorkingDirectory = r.WorkingDirectory,
            StdinPayload = string.IsNullOrEmpty(r.Prompt) ? null : r.Prompt,
            NormalizedModel = ctx.ResolvedModel,
        };
    }

    // ── Gemini (deprecated, shared-only) ────────────────────────────────

#pragma warning disable CS0618 // The deprecated Gemini driver stays registered until its pre-1.0 removal.
    /// <summary>Gemini: <c>gemini -o stream-json</c>, always <c>--skip-trust</c>; shared-only, no resume. <b>Deprecated</b>.</summary>
    public static readonly CliDescriptor Gemini = new()
    {
        CliType = CliTypes.Gemini,
        GetCliPath = o => o.GeminiPath ?? "gemini",
        Capabilities = m => Capabilities(CliTypes.Gemini, m, supportsCleanContext: false, supportsResume: false),
        CanResumeSessionId = static _ => true,
        InterruptClassifier = InterruptClassifiers.None,
        Liveness = LivenessSpec.InBandDefault,
        Parse = (line, runId, stream) => stream == CliStreamKind.Stdout ? GeminiEventAdapter.Map(line, runId) : Array.Empty<CliRunEvent>(),
        BuildLaunch = GeminiLaunch,
    };

    private static LaunchSpec GeminiLaunch(CliLaunchContext ctx)
    {
        var r = ctx.Request;
        var argv = new List<string> { "-o", "stream-json" };
        foreach (var flag in CliPermissionFlags.For(CliTypes.Gemini, r.PermissionMode)) argv.Add(flag);
        if (!string.IsNullOrWhiteSpace(ctx.ResolvedModel)) { argv.Add("-m"); argv.Add(ctx.ResolvedModel!); }
        if (!string.IsNullOrEmpty(r.Prompt)) { argv.Add("-p"); argv.Add(r.Prompt); }
        return new LaunchSpec
        {
            Executable = SafeResolve(ctx.CliPath),
            Argv = argv,
            WorkingDirectory = r.WorkingDirectory,
            NormalizedModel = ctx.ResolvedModel,
        };
    }
#pragma warning restore CS0618

    // ── Antigravity (agentapi) ──────────────────────────────────────────

    /// <summary>Antigravity: <c>agentapi new-conversation --model=&lt;tier&gt; "&lt;prompt&gt;"</c> / <c>send-message &lt;uuid&gt;</c>; reuses the Gemini frame adapter.</summary>
    public static readonly CliDescriptor Antigravity = new()
    {
        CliType = CliTypes.Antigravity,
        GetCliPath = o => o.AntigravityPath ?? "agentapi",
        Capabilities = m => Capabilities(CliTypes.Antigravity, m, supportsCleanContext: false, supportsResume: true),
        CanResumeSessionId = static s => !string.IsNullOrWhiteSpace(s) && Uuid.IsMatch(s),
        InterruptClassifier = InterruptClassifiers.None,
        Liveness = LivenessSpec.InBandDefault,
        Parse = (line, runId, stream) => stream == CliStreamKind.Stdout ? GeminiEventAdapter.Map(line, runId) : Array.Empty<CliRunEvent>(),
        ProbeCliPath = ProbeAntigravity,
        BuildLaunch = AntigravityLaunch,
    };

    private static LaunchSpec AntigravityLaunch(CliLaunchContext ctx)
    {
        var r = ctx.Request;
        var argv = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.ResumeSessionId))
        {
            argv.Add("send-message"); argv.Add(r.ResumeSessionId!);
        }
        else
        {
            argv.Add("new-conversation");
            var mapped = MapAntigravityModel(ctx.ResolvedModel);
            if (!string.IsNullOrEmpty(mapped)) argv.Add($"--model={mapped}");
        }
        // The prompt is the last positional argument (a space when empty, so argv stays well-formed).
        argv.Add(string.IsNullOrEmpty(r.Prompt) ? " " : r.Prompt);
        return new LaunchSpec
        {
            Executable = SafeResolve(ctx.CliPath),
            Argv = argv,
            WorkingDirectory = r.WorkingDirectory,
            NormalizedModel = ctx.ResolvedModel,
        };
    }

    private static string? MapAntigravityModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var m = model.ToLowerInvariant();
        if (m.Contains("lite")) return "flash_lite";
        if (m.Contains("pro")) return "pro";
        if (m.Contains("flash")) return "flash";
        return "flash";
    }

    private static (bool Available, string? Version, string Path) ProbeAntigravity(CliOptions options, string? path)
    {
        var resolved = SafeResolve(string.IsNullOrWhiteSpace(path) ? (options.AntigravityPath ?? "agentapi") : path.Trim());
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = resolved,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            var o = proc.StandardOutput.ReadToEnd().Trim();
            var e = proc.StandardError.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            // agentapi has no --version: a non-zero exit that reports an unknown command
            // (or prints its usage banner) still means the CLI is present.
            var ok = proc.ExitCode == 0
                || o.Contains("unknown command: --version") || e.Contains("unknown command: --version")
                || o.Contains("Usage: agentapi") || e.Contains("Usage: agentapi");
            return (ok, "agentapi", resolved);
        }
        catch { return (false, null, resolved); }
    }

    // ── shared helpers ──────────────────────────────────────────────────

    private static CliCapabilities Capabilities(string cliType, string? model, bool supportsCleanContext, bool supportsResume) => new()
    {
        CliType = cliType,
        Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
        ThinkingLevels = CliThinkingLevels.For(cliType, model),
        DefaultThinkingLevel = CliThinkingLevels.DefaultFor(cliType, model),
        SupportsCleanContext = supportsCleanContext,
        SupportsResume = supportsResume,
        EmitsHeartbeatDuringThinking = cliType == CliTypes.Codex,
    };

    private static string SafeResolve(string path)
    {
        try { return BinaryResolver.ResolveExecutable(path); }
        catch { return path; }
    }

    private static bool IsImage(ResolvedAttachment attachment)
    {
        if (attachment.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return Path.GetExtension(attachment.AbsolutePath).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff";
    }

    private static string ResolveClaudeBinary(string nameOrPath, ILogger logger)
    {
        string resolved;
        try { resolved = BinaryResolver.ResolveExecutable(nameOrPath); }
        catch { return nameOrPath; }
        try
        {
            // Rewrite an npm `.cmd` shim to the bundled exe it calls — spawning the
            // `.cmd` with a multi-line argv on Windows truncates the prompt at the
            // first newline.
            var probed = BinaryResolver.ResolveShimToExe(resolved);
            if (!string.Equals(probed, resolved, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("[claude-bin] Rewrote .cmd shim {Shim} -> bundled exe {Exe}", resolved, probed);
                return probed;
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "[claude-bin] shim->exe probe failed; using resolved path {Path}", resolved); }
        return resolved;
    }
}
