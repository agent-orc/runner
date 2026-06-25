namespace CodingAgentRunner.Rendering;

/// <summary>What kind of target a link points at — the axis a consumer's policy switches on.</summary>
public enum LinkKind
{
    /// <summary>An external web URL (http/https/mailto).</summary>
    Url,
    /// <summary>A file path (open-in-editor, file://, an app route — the consumer decides).</summary>
    FilePath,
    /// <summary>An app-specific entity reference (e.g. a task key like <c>ASS-738</c>) — never auto-detected, always tagged by the producer.</summary>
    TaskRef,
    /// <summary>An in-document fragment (<c>#section</c>).</summary>
    Anchor,
}

/// <summary>
/// A link the renderer recognised, carrying the RAW target + its kind — but NOT a
/// resolved URL. The consumer's <see cref="LinkResolver"/> turns it into a
/// <see cref="ResolvedLink"/> at materialization time. This is the seam that keeps a
/// single renderer serving every consumer: the lib classifies, the host decides.
/// </summary>
/// <param name="Kind">What the target is.</param>
/// <param name="RawTarget">The unresolved target string (a URL, a path, a task key, a fragment).</param>
/// <param name="DisplayLabel">Optional display text override; null uses the span's own text.</param>
public sealed record LinkSpec(LinkKind Kind, string RawTarget, string? DisplayLabel = null);

/// <summary>
/// A consumer's resolution of a <see cref="LinkSpec"/> into a concrete link. The
/// full record is consumed when materializing (href + target + rel + data-attributes)
/// — none of it is dropped, so app behaviours like in-app task navigation
/// (<c>data-task-key</c>) survive.
/// </summary>
/// <param name="Href">The final href.</param>
/// <param name="Target">Optional anchor target (e.g. <c>_blank</c>).</param>
/// <param name="Rel">Optional rel (e.g. <c>noopener noreferrer</c>).</param>
/// <param name="DataAttributes">Optional <c>data-*</c> attributes the consumer's click handler reads.</param>
public sealed record ResolvedLink(
    string Href,
    string? Target = null,
    string? Rel = null,
    IReadOnlyDictionary<string, string>? DataAttributes = null);

/// <summary>
/// The one injected policy that turns a recognised <see cref="LinkSpec"/> into a
/// <see cref="ResolvedLink"/>. A consumer supplies one; the renderer never bakes in
/// app-specific link semantics. Default: <see cref="LinkExtractor.WebDefault"/>.
/// </summary>
public delegate ResolvedLink LinkResolver(LinkSpec spec);

/// <summary>
/// The one URL policy, shared by prose and tool-arg linkification: classify a raw
/// target, vet a URL against an allowlist, and provide the safe web default. App
/// concerns (task-ref nav, file-open, lightbox) live in the consumer's
/// <see cref="LinkResolver"/>, never here.
/// </summary>
public static class LinkExtractor
{
    /// <summary>
    /// True when <paramref name="url"/> is safe to emit as an <c>href</c>: http/https/
    /// mailto, or a relative / anchor / fragment target. Everything else — notably
    /// <c>javascript:</c>, <c>data:</c>, <c>vbscript:</c>, scheme-less hosts — is rejected.
    /// </summary>
    public static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var u = url.Trim();
        if (u[0] is '/' or '.' or '#') return true;          // relative / anchor / fragment
        var lower = u.ToLowerInvariant();
        return lower.StartsWith("http://", StringComparison.Ordinal)
            || lower.StartsWith("https://", StringComparison.Ordinal)
            || lower.StartsWith("mailto:", StringComparison.Ordinal);
    }

    /// <summary>
    /// Best-effort classification of a raw target where the kind is not already known.
    /// http/https/mailto → <see cref="LinkKind.Url"/>; a leading <c>#</c> →
    /// <see cref="LinkKind.Anchor"/>; a <c>file:</c> scheme or a path separator →
    /// <see cref="LinkKind.FilePath"/>; otherwise <see cref="LinkKind.Url"/>.
    /// <see cref="LinkKind.TaskRef"/> is never inferred — it is app-specific and the
    /// producer tags it explicitly.
    /// </summary>
    public static LinkKind Classify(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return LinkKind.Url;
        var t = target.Trim();
        if (t[0] == '#') return LinkKind.Anchor;
        var lower = t.ToLowerInvariant();
        if (lower.StartsWith("http://", StringComparison.Ordinal)
            || lower.StartsWith("https://", StringComparison.Ordinal)
            || lower.StartsWith("mailto:", StringComparison.Ordinal))
            return LinkKind.Url;
        if (lower.StartsWith("file:", StringComparison.Ordinal) || t.Contains('/') || t.Contains('\\'))
            return LinkKind.FilePath;
        return LinkKind.Url;
    }

    /// <summary>
    /// The safe web default resolver: a vetted URL opens in a new tab with
    /// <c>rel="noopener noreferrer"</c>; an unsafe URL is neutralized to <c>#</c>.
    /// A consumer overrides this per <see cref="LinkKind"/> for task-refs / file links.
    /// </summary>
    public static ResolvedLink WebDefault(LinkSpec spec)
        => new(IsSafeUrl(spec.RawTarget) ? spec.RawTarget : "#", Target: "_blank", Rel: "noopener noreferrer");
}
