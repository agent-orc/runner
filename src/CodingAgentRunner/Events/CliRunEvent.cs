namespace CodingAgentRunner.Events;

/// <summary>
/// One observation about a CLI run. Each per-CLI adapter (Claude / Codex /
/// Copilot / Gemini) maps the CLI's native protocol onto this vocabulary; the
/// runner and any policy layer consume only this contract — they never parse a
/// CLI's raw frames directly.
///
/// <para>
/// <b>Why a closed sum type, not an open string-keyed bag.</b> A watchdog or a
/// completion policy decides what to do based on event kind and phase
/// transitions. A typo or a missing case in those policies should be a compile
/// error, not a silent fallback. Roslyn's switch-expression exhaustiveness check
/// is the load-bearing guard — a new kind is added in one place and the compiler
/// points at every site that must handle it.
/// </para>
/// <para>
/// <b>What this is NOT.</b> Not a 1:1 representation of any one CLI's frames —
/// adapters compress / synthesise where it produces clearer policy. Renaming a
/// kind, adding a field, or splitting one event into two is fine as long as the
/// adapters and consumers move together.
/// </para>
/// </summary>
public abstract record CliRunEvent
{
    /// <summary>UTC timestamp the runner observed the underlying byte that produced this event.</summary>
    public DateTime ObservedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Correlation id of the run this event belongs to (consumer-assigned).</summary>
    public string RunId { get; init; } = "";

    /// <summary>Process spawned. First event from any adapter.</summary>
    public sealed record RunStarted(int ProcessId, string CliType, string? Model) : CliRunEvent;

    /// <summary>Adapter saw the CLI's first protocol frame; the session is being initialized.</summary>
    public sealed record SessionInitializing : CliRunEvent;

    /// <summary>Session is open. <see cref="SessionId"/> is the CLI-assigned id when available; null when the CLI has not surfaced one yet.</summary>
    public sealed record SessionStarted(string? SessionId) : CliRunEvent;

    /// <summary>The prompt the runner sent has been acknowledged by the CLI; a turn is starting.</summary>
    public sealed record TurnStarted : CliRunEvent;

    /// <summary>The model produced visible output text. <see cref="Text"/> may be a token, a chunk, or a whole line — adapters do not need to be uniform.</summary>
    public sealed record OutputDelta(string Text) : CliRunEvent;

    /// <summary>The agent invoked a tool. <see cref="ToolName"/> is normalized (Read / Edit / Bash / ...).</summary>
    public sealed record ToolStarted(string ToolName, string? Argument) : CliRunEvent;

    /// <summary>A tool call returned. <see cref="IsError"/> reflects what the CLI reports for the call, not whether the result satisfies the user.</summary>
    public sealed record ToolCompleted(string ToolName, bool IsError, string? FirstLine) : CliRunEvent;

    /// <summary>
    /// The agent emitted its own internal task plan (Claude <c>TodoWrite</c>,
    /// Codex <c>update_plan</c>). One event per plan frame. Read-only
    /// observability: this parses telemetry the CLI already streams, never a
    /// second model call.
    /// </summary>
    public sealed record PlanUpdated(string Source, IReadOnlyList<PlanFrameItem> Items) : CliRunEvent;

    /// <summary>Liveness ping from a structured channel (e.g. Codex App Server's heartbeat). Pure adapter signal; the runner uses it to reset the silence clock without a real <see cref="OutputDelta"/>.</summary>
    public sealed record Heartbeat : CliRunEvent;

    /// <summary>The current turn finished. <see cref="UsageSummary"/> is a one-line free-form summary the adapter constructs.</summary>
    public sealed record TurnCompleted(string? UsageSummary) : CliRunEvent;

    /// <summary>The current turn failed. <see cref="Reason"/> is the adapter's best one-line explanation.</summary>
    public sealed record TurnFailed(string Reason) : CliRunEvent;

    /// <summary>The CLI emitted an explicit "I need user input" signal (a provider-specific marker, or a consumer-defined sentinel).</summary>
    public sealed record NeedsInput(string Reason) : CliRunEvent;

    /// <summary>An interactive CLI is asking for tool/edit approval. The runner does not auto-approve at this layer; it forwards to the consumer when running in a non-bypass mode.</summary>
    public sealed record ApprovalRequested(string Description) : CliRunEvent;

    /// <summary>Per-turn rate-limit info from CLIs that surface it (e.g. Claude). Drives a live usage indicator.</summary>
    public sealed record RateLimitObserved(
        string? Window,
        string? Status,
        long ResetsAt,
        string? OverageStatus,
        bool IsUsingOverage) : CliRunEvent;

    /// <summary>The process exited. <see cref="ExitCode"/> is the OS exit code; <see cref="Status"/> is the adapter's best-effort classification (completed / failed / stopped / cancelled). The runner reconciles it with the real exit code and any stop reason.</summary>
    public sealed record ProcessExited(int? ExitCode, string Status, double DurationSeconds) : CliRunEvent;

    /// <summary>The runner killed the process tree (watchdog timeout, user stop, cancellation).</summary>
    public sealed record Killed(string Reason) : CliRunEvent;

    /// <summary>Adapter could not classify a chunk of output. <see cref="Sample"/> is a short prefix of the unclassified payload, capped to 200 chars.</summary>
    public sealed record Unknown(string Sample) : CliRunEvent;
}

/// <summary>
/// One top-level item inside a <see cref="CliRunEvent.PlanUpdated"/> frame,
/// normalized across CLIs. <see cref="Id"/> is stable across snapshots within
/// the same run so a sub-action attributed while the item was active still maps
/// back to it after it completes. <see cref="Status"/> is one of
/// <c>pending</c> / <c>active</c> / <c>done</c> (the CLI's
/// <c>in_progress</c> / <c>completed</c> are normalized at ingest).
/// </summary>
public sealed record PlanFrameItem(string Id, string Title, string Status);

/// <summary>
/// Derives a stable, snapshot-independent id for a plan item from its title.
/// Claude's <c>TodoWrite</c> and Codex's <c>update_plan</c> items carry no id;
/// the title (the imperative <c>content</c> / <c>step</c>) is the one field that
/// stays constant as the item walks <c>pending &#8594; active &#8594; done</c>, so
/// we hash a normalized form of it. Normalization (trim + lowercase + collapse
/// internal whitespace) keeps trivial reformatting from minting a new id.
/// </summary>
public static class PlanItemId
{
    /// <summary>Derive the stable id for a plan item from its title.</summary>
    public static string From(string? title)
    {
        var normalized = string.Join(' ',
            (title ?? string.Empty).Trim().ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));
        // 8 hex chars is plenty to disambiguate the handful of items in a plan.
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}

/// <summary>
/// Normalizes a CLI-native plan-item status string onto the
/// <c>pending</c> / <c>active</c> / <c>done</c> vocabulary. Unknown values fall
/// back to <c>pending</c> so an unexpected status never renders as an active item
/// the user would read as "the agent is here right now".
/// </summary>
public static class PlanItemStatus
{
    /// <summary>Map a CLI-native status onto <c>pending</c> / <c>active</c> / <c>done</c>.</summary>
    public static string Normalize(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "in_progress" or "in-progress" or "active" or "running" => "active",
        "completed" or "complete" or "done" => "done",
        _ => "pending",
    };
}
