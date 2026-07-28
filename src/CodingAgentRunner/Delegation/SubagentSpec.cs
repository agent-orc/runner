using System.Text;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Delegation;

/// <summary>
/// A CLI's agent-definition convention, declared as data on its
/// <see cref="Execution.CliDescriptor"/>: where the CLI looks for project-scoped agent
/// files, what extension they carry, and how one <see cref="SubagentDefinition"/> is
/// rendered into that file format. Declaring it (rather than running it) keeps the
/// descriptor a pure value and the file mechanics in
/// <see cref="SubagentMaterializer"/>. Null on a descriptor means the CLI has no
/// subagent convention the runner can wire up.
/// </summary>
/// <param name="DirectoryRelativePath">Agents directory, relative to the run's working directory (e.g. <c>.claude/agents</c>).</param>
/// <param name="FileExtension">Extension of one agent definition file, including the dot (e.g. <c>.md</c>).</param>
/// <param name="Render">Renders one definition into the CLI's file format, including the runner's generated-by marker.</param>
/// <param name="ReadDescription">Best-effort read of an existing (repo-provided) definition's description, for the run's agent inventory.</param>
/// <param name="AdvertiseInPrompt">Whether the run prompt should tell the agent these workers exist. False for a CLI whose non-interactive mode does not actually run a subagent.</param>
public sealed record SubagentSpec(
    string DirectoryRelativePath,
    string FileExtension,
    Func<SubagentDefinition, string> Render,
    Func<string, string?> ReadDescription,
    bool AdvertiseInPrompt = true);

/// <summary>
/// The per-CLI renderers behind <see cref="SubagentSpec"/>. Claude Code reads
/// markdown with YAML frontmatter; Codex reads TOML. Both formats accept comments,
/// so every generated file carries
/// <see cref="SubagentMaterializer.GeneratedMarker"/> — that marker is what makes
/// "did the runner write this file?" decidable, and it is why a repo's own agent
/// definition is never overwritten or deleted.
/// </summary>
public static class SubagentRenderers
{
    /// <summary>Claude Code's convention: <c>.claude/agents/&lt;name&gt;.md</c> with <c>name</c> / <c>description</c> / <c>model</c> / <c>tools</c> frontmatter.</summary>
    public static readonly SubagentSpec Claude = new(
        ".claude/agents",
        ".md",
        RenderClaude,
        ReadClaudeDescription);

    /// <summary>
    /// Codex's convention: <c>.codex/agents/&lt;name&gt;.toml</c> with <c>name</c> /
    /// <c>description</c> / <c>model</c> / <c>developer_instructions</c>.
    /// <para>
    /// The definitions are written but <b>not advertised in the prompt</b>. The runner
    /// spawns Codex through <c>codex exec</c>, and that mode does not actually run a
    /// subagent: probed against codex-cli 0.145.0, a prompt that asks for one produces
    /// a <c>collab_tool_call</c> frame with an empty <c>receiver_thread_ids</c> and an
    /// empty <c>agents_states</c> — no child thread is ever created — after which the
    /// model reports an answer it invented. Telling the primary agent to delegate would
    /// therefore buy fabricated results, not cheaper ones. The files still ship so the
    /// set is in place the day exec runs subagents; see
    /// <c>docs/delegation.md</c> for the probe.
    /// </para>
    /// </summary>
    public static readonly SubagentSpec Codex = new(
        ".codex/agents",
        ".toml",
        RenderCodex,
        ReadCodexDescription,
        AdvertiseInPrompt: false);

    private static string RenderClaude(SubagentDefinition agent)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(OneLine(agent.Name)).Append('\n');
        sb.Append("description: ").Append(YamlScalar(agent.Description)).Append('\n');
        var model = agent.ModelFor(CliTypes.Claude);
        if (!string.IsNullOrWhiteSpace(model))
            sb.Append("model: ").Append(OneLine(model!)).Append('\n');
        if (agent.Tools is { Count: > 0 })
            sb.Append("tools: ").Append(string.Join(", ", agent.Tools.Select(OneLine))).Append('\n');
        sb.Append("---\n\n");
        sb.Append("<!-- ").Append(SubagentMaterializer.GeneratedMarker).Append(" -->\n\n");
        sb.Append(agent.Instructions.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    private static string RenderCodex(SubagentDefinition agent)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(SubagentMaterializer.GeneratedMarker).Append('\n');
        sb.Append("name = ").Append(TomlString(agent.Name)).Append('\n');
        sb.Append("description = ").Append(TomlString(agent.Description)).Append('\n');
        var model = agent.ModelFor(CliTypes.Codex);
        if (!string.IsNullOrWhiteSpace(model))
            sb.Append("model = ").Append(TomlString(model!)).Append('\n');
        sb.Append("developer_instructions = ").Append(TomlString(agent.Instructions.Trim())).Append('\n');
        return sb.ToString();
    }

    private static string? ReadClaudeDescription(string text)
    {
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;
            if (!line.StartsWith("description:", StringComparison.OrdinalIgnoreCase)) continue;
            return Unquote(line["description:".Length..].Trim());
        }
        return null;
    }

    private static string? ReadCodexDescription(string text)
    {
        foreach (var line in Lines(text))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("description", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            return Unquote(trimmed[(eq + 1)..].Trim());
        }
        return null;
    }

    // Only the head of a definition file is scanned: the description lives in the
    // frontmatter / table header, and a long agent body should not cost a full read.
    private static IEnumerable<string> Lines(string text)
        => text.Split('\n', 40).Take(30).Select(l => l.TrimEnd('\r'));

    private static string Unquote(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
            v = v[1..^1];
        return v.Replace("\\\"", "\"").Trim();
    }

    private static string OneLine(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>Render a description as a YAML double-quoted scalar so a colon or a quote in it cannot break the frontmatter.</summary>
    private static string YamlScalar(string value)
        => "\"" + OneLine(value).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Render a TOML string: a basic string on one line, a multi-line literal when the value has newlines.</summary>
    private static string TomlString(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        if (!escaped.Contains('\n')) return "\"" + escaped.Replace("\r", "") + "\"";
        return "\"\"\"\n" + escaped.Replace("\r", "") + "\n\"\"\"";
    }
}
