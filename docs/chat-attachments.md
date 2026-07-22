# Chat attachments

Chat UIs stage pasted files in the browser, but an agent run may start later or on
another process. The runner therefore accepts durable attachment references rather
than browser `File` objects or `blob:` URLs.

## Contract

The application maps its stored chat attachment onto `AttachmentReference` and
configures one `IAttachmentResolver` in `CliOptions`. A resolver returns a
`ResolvedAttachment` whose `AbsolutePath` points to an existing local file:

```csharp
using CodingAgentRunner.Attachments;

sealed class ProjectAttachmentResolver(ProjectAttachmentStore store)
    : IAttachmentResolver
{
    public async ValueTask<ResolvedAttachment?> ResolveAsync(
        AttachmentReference attachment,
        CancellationToken ct = default)
    {
        var stored = await store.ResolveToLocalFileAsync(attachment.Reference, ct);
        return stored is null
            ? null
            : new ResolvedAttachment(stored.AbsolutePath)
            {
                FileName = stored.FileName,
                MediaType = stored.MediaType,
            };
    }
}
```

The runner treats `AttachmentReference.Reference` as opaque. It may be a storage
id or an authenticated, resolvable reference from the chat host. The runner does
not fetch arbitrary URLs. This keeps authentication, retention, and durable
storage policy in the application that owns the chat.

When using `coding-agent-chat`, map the persisted attachment's resolvable reference
to `AttachmentReference.Reference` and its display label to `AltText`. Do not submit
a pending browser `blob:` URL: upload it first and store the durable reference with
the chat message.

The resolver owns the resolved file and must keep it readable for the duration of
the run. It should return `null` when a reference does not exist and throw with a
storage-specific message for other resolution failures.

## Delivery to the CLI

Resolution happens before the CLI process starts. For each attachment, the runner:

1. calls the configured resolver;
2. requires a rooted path and verifies that the file exists;
3. adds the absolute path, file name, and media type to a delimited
   `<chat-attachments>` block after the prompt;
4. passes image files to Codex with `--image`, including resumed runs.

The prompt block lets file-reading CLIs open the durable file by absolute path.
Codex's native image option additionally sends image content as model input.
Non-image files are path context only.

## Failures and logs

Attachment resolution is all-or-nothing. If one reference cannot be resolved, the
runner returns an error from `StartAsync` and does not spawn the CLI. Errors name
the failed reference and state whether the resolver is missing, returned no file,
returned a relative/invalid path, or pointed at a missing file. A resolver exception
keeps its message in the returned error.

The runner emits structured log events `AttachmentResolutionStarted`,
`AttachmentResolutionCompleted`, and `AttachmentResolutionFailed`. Completion and
failure events include elapsed milliseconds; failure events include the run id and
attachment reference.
