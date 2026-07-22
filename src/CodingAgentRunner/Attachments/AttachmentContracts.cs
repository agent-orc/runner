namespace CodingAgentRunner.Attachments;

/// <summary>
/// A durable attachment reference supplied by a chat or task message. The reference
/// is opaque to the runner; the host resolves it through <see cref="IAttachmentResolver"/>.
/// </summary>
/// <param name="Reference">The stable storage id or resolvable URL from the chat attachment contract.</param>
public sealed record AttachmentReference(string Reference)
{
    /// <summary>Original display/file name, when the chat host recorded one.</summary>
    public string? FileName { get; init; }

    /// <summary>Recorded media type, for example <c>image/png</c>.</summary>
    public string? MediaType { get; init; }

    /// <summary>Display label or alt text from the chat message.</summary>
    public string? AltText { get; init; }
}

/// <summary>
/// The local file produced by an <see cref="IAttachmentResolver"/>. The file must
/// already exist and <see cref="AbsolutePath"/> must be rooted. The resolver owns
/// its storage lifetime; it must keep the file readable for the duration of the run.
/// </summary>
/// <param name="AbsolutePath">Absolute path to the durable attachment file.</param>
public sealed record ResolvedAttachment(string AbsolutePath)
{
    /// <summary>Resolved file name, or null to use the name from the reference/path.</summary>
    public string? FileName { get; init; }

    /// <summary>Resolved media type, or null to use the type from the reference.</summary>
    public string? MediaType { get; init; }
}

/// <summary>
/// Resolves the chat library's durable attachment reference to a local file that a
/// coding-agent CLI can read. Implement this at the host/storage boundary; the runner
/// does not download URLs or interpret storage ids itself.
/// </summary>
public interface IAttachmentResolver
{
    /// <summary>
    /// Resolve one reference. Return null only when the reference is unknown; throw
    /// with a storage-specific message for other failures so the run error is actionable.
    /// </summary>
    ValueTask<ResolvedAttachment?> ResolveAsync(
        AttachmentReference attachment,
        CancellationToken cancellationToken = default);
}
