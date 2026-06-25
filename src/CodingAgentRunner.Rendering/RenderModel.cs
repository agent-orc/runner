namespace CodingAgentRunner.Rendering;

/// <summary>Inline span styles — presentation-agnostic (no HTML, no ANSI).</summary>
public enum SpanKind
{
    /// <summary>Plain text.</summary>
    Text,
    /// <summary>Bold.</summary>
    Bold,
    /// <summary>Italic.</summary>
    Italic,
    /// <summary>Inline code.</summary>
    Code,
    /// <summary>A link — carries a <see cref="LinkSpec"/> the consumer's resolver materializes.</summary>
    Link,
    /// <summary>A file/path reference — a link whose target is a path.</summary>
    PathRef,
}

/// <summary>
/// One inline run of rendered content. A <see cref="SpanKind.Link"/> / <see cref="SpanKind.PathRef"/>
/// span carries a <see cref="LinkSpec"/> (the raw target + kind); every other span is
/// just styled text. The model never holds a resolved href — that is the consumer's
/// <see cref="LinkResolver"/> applied at materialization.
/// </summary>
/// <param name="Kind">The inline style.</param>
/// <param name="Text">The display text.</param>
/// <param name="Link">The link spec when <see cref="Kind"/> is a link/path-ref; otherwise null.</param>
public sealed record RenderedSpan(SpanKind Kind, string Text, LinkSpec? Link = null);

/// <summary>The block role of a rendered line.</summary>
public enum LineKind
{
    /// <summary>A paragraph of prose.</summary>
    Prose,
    /// <summary>A heading (see <c>Level</c>).</summary>
    Heading,
    /// <summary>A list item.</summary>
    ListItem,
    /// <summary>A code block (see <c>Language</c>).</summary>
    CodeBlock,
    /// <summary>A tool/agent activity line (see <c>Activity</c>).</summary>
    Activity,
    /// <summary>A plan / todo item.</summary>
    PlanItem,
    /// <summary>A meta / diagnostic line (session, rate-limit, …).</summary>
    Meta,
}

/// <summary>Typed agent activity — replaces re-bucketing an emitted <c>● Read</c> marker string with a regex.</summary>
public enum ActivityKind
{
    /// <summary>Not an activity line.</summary>
    None,
    /// <summary>Reading a file.</summary>
    Read,
    /// <summary>Editing / writing a file.</summary>
    Edit,
    /// <summary>Searching.</summary>
    Search,
    /// <summary>Running a command.</summary>
    Run,
    /// <summary>Spawning a sub-task.</summary>
    Task,
    /// <summary>Updating a todo / plan.</summary>
    Todo,
    /// <summary>A session event.</summary>
    Session,
    /// <summary>A rate-limit observation.</summary>
    RateLimit,
    /// <summary>An activity that does not fit the named set.</summary>
    Other,
}

/// <summary>
/// One materialized line: its block role, its inline spans, and the optional facets
/// a surface needs (heading level, code language, typed activity, error/status). A UI
/// maps this onto its own surface — Angular DOM, an ANSI terminal, server HTML — none
/// of which leak into the model.
/// </summary>
/// <param name="Kind">The block role.</param>
/// <param name="Spans">The inline spans.</param>
/// <param name="Level">Heading level (1..6) when <see cref="LineKind.Heading"/>; else 0.</param>
/// <param name="Language">Code language when <see cref="LineKind.CodeBlock"/>; else null.</param>
/// <param name="Activity">Typed activity when <see cref="LineKind.Activity"/>; else <see cref="ActivityKind.None"/>.</param>
/// <param name="IsError">True for an error activity / result.</param>
/// <param name="Status">Optional status text (e.g. a plan item state).</param>
public sealed record RenderedLine(
    LineKind Kind,
    IReadOnlyList<RenderedSpan> Spans,
    int Level = 0,
    string? Language = null,
    ActivityKind Activity = ActivityKind.None,
    bool IsError = false,
    string? Status = null);
