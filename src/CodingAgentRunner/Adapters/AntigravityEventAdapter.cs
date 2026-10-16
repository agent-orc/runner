using System.Text.Json;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Adapters;

/// <summary>Maps <c>agy --output-format stream-json</c> frames to run events.</summary>
public static class AntigravityEventAdapter
{
    /// <summary>Map one stdout or stderr line. Stderr permission notices are diagnostics; the result frame decides the outcome.</summary>
    public static IEnumerable<CliRunEvent> Map(string line, string runId, CliStreamKind stream = CliStreamKind.Stdout)
    {
        if (stream == CliStreamKind.Stderr)
        {
            if (line.Contains("auto-denied", StringComparison.OrdinalIgnoreCase))
                return [new CliRunEvent.Diagnostic
                {
                    RunId = runId,
                    Severity = DiagnosticSeverity.Error,
                    Subsystem = "agy",
                    Code = "permission_denied",
                    Category = "permission",
                    Summary = "Antigravity denied a tool permission in headless mode.",
                    RawDetail = line,
                    DedupeKey = "agy:permission_denied",
                }];
            return [];
        }
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return [];
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (String(root, "event"))
            {
                case "init":
                    {
                        var init = Object(root, "init");
                        return [new CliRunEvent.SessionStarted(String(root, "conversation_id"))
                    {
                        RunId = runId,
                        Model = String(init, "model"),
                        Cwd = String(init, "cwd"),
                        PermissionMode = String(init, "permission_mode"),
                    }];
                    }
                case "step_update":
                    {
                        var step = Object(root, "step_update");
                        var type = String(step, "step_type");
                        var state = String(step, "state");
                        if (type == "agent_response")
                        {
                            var delta = String(step, "text_delta");
                            return string.IsNullOrEmpty(delta) ? [] : [new CliRunEvent.OutputDelta(delta) { RunId = runId }];
                        }
                        if (type == "tool")
                        {
                            var name = String(step, "tool_name") ?? String(Object(step, "tool_info"), "name") ?? "tool";
                            var tool = Object(step, "tool_info");
                            if (state == "ACTIVE")
                                return [new CliRunEvent.ToolStarted(name, ToolArgument(Object(tool, "parameters"))) { RunId = runId }];
                            if (state == "DONE")
                            {
                                var error = Object(tool, "error");
                                var failed = error.ValueKind == JsonValueKind.Object
                                    || string.Equals(String(step, "status"), "ERROR", StringComparison.OrdinalIgnoreCase);
                                return [new CliRunEvent.ToolCompleted(name, failed, FirstLine(String(tool, "output") ?? String(error, "message"))) { RunId = runId }];
                            }
                        }
                        return [];
                    }
                case "result":
                    {
                        var result = Object(root, "result");
                        var denied = Array(result, "denied_actions")
                            .Select(action => new DeniedAction(String(action, "action") ?? "", String(action, "display_name")))
                            .ToArray();
                        if (denied.Length > 0)
                        {
                            var names = string.Join(", ", denied.Select(action => action.Action));
                            return [new CliRunEvent.TurnFailed($"permission denied: {names}")
                        {
                            RunId = runId,
                            DeniedActions = denied,
                        }];
                        }
                        var status = String(result, "status");
                        if (string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                            return [new CliRunEvent.TurnCompleted(Usage(Object(result, "usage"))) { RunId = runId }];
                        return [new CliRunEvent.TurnFailed(String(result, "error") ?? status ?? "agy result missing status") { RunId = runId }];
                    }
                default:
                    return [new CliRunEvent.Unknown(line.Length <= 200 ? line : line[..200], line) { RunId = runId }];
            }
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Usage(JsonElement usage)
        => $"input={Number(usage, "input_tokens")} output={Number(usage, "output_tokens")} reasoning={Number(usage, "thinking_tokens")} cache_read={Number(usage, "cache_read_tokens")}";

    private static long Number(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.TryGetInt64(out var n) ? n : 0;

    private static string? ToolArgument(JsonElement parameters)
    {
        foreach (var key in new[] { "CommandLine", "TargetFile", "url", "Url", "FilePath", "path" })
            if (String(parameters, key) is { } value) return value;
        return null;
    }

    private static string? FirstLine(string? value)
        => value?.Split('\n', 2)[0].TrimEnd('\r');

    private static JsonElement Object(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;

    private static IEnumerable<JsonElement> Array(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];

    private static string? String(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
