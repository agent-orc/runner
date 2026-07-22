using System.Diagnostics;
using System.Text.Json;
using CodingAgentRunner.Execution;
using Microsoft.Extensions.Logging;

namespace CodingAgentRunner.Attachments;

internal sealed record PreparedAttachments(
    CliRunRequest Request,
    IReadOnlyList<ResolvedAttachment> Files);

internal static class AttachmentPreparation
{
    private static readonly EventId ResolutionStarted = new(2100, "AttachmentResolutionStarted");
    private static readonly EventId ResolutionCompleted = new(2101, "AttachmentResolutionCompleted");
    private static readonly EventId ResolutionFailed = new(2102, "AttachmentResolutionFailed");

    public static async Task<(PreparedAttachments? Prepared, string? Error)> PrepareAsync(
        CliRunRequest request,
        IAttachmentResolver? resolver,
        ILogger logger,
        CancellationToken ct)
    {
        if (request.Attachments is not { Count: > 0 })
            return (new PreparedAttachments(request, []), null);

        var timer = Stopwatch.StartNew();
        logger.LogInformation(
            ResolutionStarted,
            "Resolving {AttachmentCount} attachment(s) for {RunId}",
            request.Attachments.Count,
            request.RunId);

        if (resolver is null)
            return Failed(
                request,
                request.Attachments[0],
                "CliOptions.AttachmentResolver is not configured. Configure a resolver for the chat attachment storage before starting this run.",
                timer,
                logger);

        var files = new List<ResolvedAttachment>(request.Attachments.Count);
        foreach (var attachment in request.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.Reference))
                return Failed(request, attachment, "the attachment reference is empty", timer, logger);

            ResolvedAttachment? resolved;
            try
            {
                resolved = await resolver.ResolveAsync(attachment, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed(request, attachment, $"the resolver failed: {ex.Message}", timer, logger, ex);
            }

            if (resolved is null)
                return Failed(request, attachment, "the resolver returned no file; verify that the reference still exists in durable attachment storage", timer, logger);
            if (string.IsNullOrWhiteSpace(resolved.AbsolutePath))
                return Failed(request, attachment, "the resolver returned an empty path", timer, logger);
            if (!Path.IsPathRooted(resolved.AbsolutePath))
                return Failed(request, attachment, $"the resolver returned a relative path ('{resolved.AbsolutePath}'); it must return an absolute path", timer, logger);

            string fullPath;
            try { fullPath = Path.GetFullPath(resolved.AbsolutePath); }
            catch (Exception ex)
            {
                return Failed(request, attachment, $"the resolver returned an invalid path: {ex.Message}", timer, logger, ex);
            }

            if (fullPath.IndexOfAny(['\r', '\n']) >= 0)
                return Failed(request, attachment, "the resolved path contains a line break and cannot be passed safely to the CLI", timer, logger);

            if (!File.Exists(fullPath))
                return Failed(request, attachment, $"the resolved file does not exist at '{fullPath}'; restore or re-upload the attachment", timer, logger);

            try
            {
                using var readable = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex)
            {
                return Failed(request, attachment, $"the resolved file is not readable at '{fullPath}': {ex.Message}", timer, logger, ex);
            }

            files.Add(resolved with
            {
                AbsolutePath = fullPath,
                FileName = FirstNonBlank(resolved.FileName, attachment.FileName, Path.GetFileName(fullPath)),
                MediaType = FirstNonBlank(resolved.MediaType, attachment.MediaType),
            });
        }

        timer.Stop();
        logger.LogInformation(
            ResolutionCompleted,
            "Resolved {AttachmentCount} attachment(s) for {RunId} in {ElapsedMilliseconds} ms",
            files.Count,
            request.RunId,
            timer.ElapsedMilliseconds);

        var prompt = AddAttachmentContext(request.Prompt, request.Attachments, files);
        return (new PreparedAttachments(request with { Prompt = prompt }, files), null);
    }

    private static (PreparedAttachments? Prepared, string? Error) Failed(
        CliRunRequest request,
        AttachmentReference attachment,
        string detail,
        Stopwatch timer,
        ILogger logger,
        Exception? exception = null)
    {
        timer.Stop();
        var reference = string.IsNullOrWhiteSpace(attachment.Reference) ? "<empty>" : attachment.Reference;
        var suffix = detail.EndsWith('.') ? "" : ".";
        var error = $"Attachment '{reference}' could not be resolved: {detail}{suffix}";
        logger.LogError(
            ResolutionFailed,
            exception,
            "Attachment resolution failed for {RunId} reference {AttachmentReference} after {ElapsedMilliseconds} ms: {Error}",
            request.RunId,
            reference,
            timer.ElapsedMilliseconds,
            error);
        return (null, error);
    }

    private static string AddAttachmentContext(
        string prompt,
        IReadOnlyList<AttachmentReference> references,
        IReadOnlyList<ResolvedAttachment> files)
    {
        var lines = new List<string>(files.Count + 4)
        {
            prompt,
            "",
            "<chat-attachments>",
            "The chat message includes these resolved local files. Open them when they are relevant to the request:",
        };

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var reference = references[i];
            var label = FirstNonBlank(file.FileName, reference.AltText, reference.FileName, "attachment")!;
            var media = string.IsNullOrWhiteSpace(file.MediaType) ? "unknown media type" : file.MediaType!;
            lines.Add($"- {JsonSerializer.Serialize(OneLine(label))} ({OneLine(media)}): {file.AbsolutePath}");
        }

        lines.Add("</chat-attachments>");
        return string.Join(Environment.NewLine, lines);
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string OneLine(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
