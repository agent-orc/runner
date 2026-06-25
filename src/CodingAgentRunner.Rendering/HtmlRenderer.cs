using System.Text;

namespace CodingAgentRunner.Rendering;

/// <summary>
/// An opt-in HTML materializer over the span model. It consumes the consumer's
/// <see cref="LinkResolver"/> directly and builds the <c>&lt;a&gt;</c> from the WHOLE
/// <see cref="ResolvedLink"/> — href + target + rel + data-attributes — so app
/// behaviours (in-app task nav via <c>data-task-key</c>, open-in-editor via
/// <c>data-editor-path</c>) survive. A consumer that renders to a different surface
/// (Angular, ANSI) writes its own materializer over the same model.
/// </summary>
public static class HtmlRenderer
{
    /// <summary>Render one inline span to HTML, applying <paramref name="resolver"/> to a link span (default: <see cref="LinkExtractor.WebDefault"/>).</summary>
    public static string SpanToHtml(RenderedSpan span, LinkResolver? resolver = null)
    {
        if ((span.Kind is SpanKind.Link or SpanKind.PathRef) && span.Link is { } link)
        {
            var resolved = (resolver ?? LinkExtractor.WebDefault)(link);
            var sb = new StringBuilder("<a href=\"").Append(Escape(resolved.Href)).Append('"');
            if (!string.IsNullOrEmpty(resolved.Target)) sb.Append(" target=\"").Append(Escape(resolved.Target!)).Append('"');
            if (!string.IsNullOrEmpty(resolved.Rel)) sb.Append(" rel=\"").Append(Escape(resolved.Rel!)).Append('"');
            if (resolved.DataAttributes is { } data)
                foreach (var kv in data)
                    sb.Append(' ').Append(Escape(kv.Key)).Append("=\"").Append(Escape(kv.Value)).Append('"');
            sb.Append('>').Append(Escape(span.Text)).Append("</a>");
            return sb.ToString();
        }

        var inner = Escape(span.Text);
        return span.Kind switch
        {
            SpanKind.Bold => $"<strong>{inner}</strong>",
            SpanKind.Italic => $"<em>{inner}</em>",
            SpanKind.Code => $"<code>{inner}</code>",
            _ => inner,
        };
    }

    /// <summary>Render a whole line's spans to an HTML fragment (no block wrapper — the caller chooses one from <see cref="RenderedLine.Kind"/>).</summary>
    public static string SpansToHtml(RenderedLine line, LinkResolver? resolver = null)
    {
        var sb = new StringBuilder();
        foreach (var span in line.Spans) sb.Append(SpanToHtml(span, resolver));
        return sb.ToString();
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
