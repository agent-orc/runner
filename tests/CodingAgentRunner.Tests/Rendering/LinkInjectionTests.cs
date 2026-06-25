using CodingAgentRunner.Rendering;
using Xunit;

namespace CodingAgentRunner.Tests.Rendering;

/// <summary>
/// The link-injection design: ONE renderer + a consumer-supplied <see cref="LinkResolver"/>
/// serve every surface. The four §13 scenarios (HTML doc, file links, task-refs, web
/// URLs) are just different resolvers over the same <see cref="LinkSpec"/> model — and
/// the materializer keeps the WHOLE <see cref="ResolvedLink"/> (target + data-attrs),
/// not just the href.
/// </summary>
public class LinkInjectionTests
{
    private static RenderedSpan Link(LinkKind kind, string target, string text)
        => new(SpanKind.Link, text, new LinkSpec(kind, target));

    // ── Scenario D: plain web URL, default resolver ─────────────────────

    [Fact]
    public void WebUrl_DefaultResolver_OpensBlank_NoOpener()
    {
        var html = HtmlRenderer.SpanToHtml(Link(LinkKind.Url, "https://example.com/x", "docs"));
        Assert.Contains("href=\"https://example.com/x\"", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
        Assert.Contains(">docs</a>", html);
    }

    [Fact]
    public void WebUrl_Unsafe_IsNeutralizedToHash()
    {
        var html = HtmlRenderer.SpanToHtml(Link(LinkKind.Url, "javascript:alert(1)", "x"));
        Assert.Contains("href=\"#\"", html);
        Assert.DoesNotContain("javascript:", html);
    }

    // ── Scenario C: task-refs (in-app nav) — the data-attrs MUST survive ─

    [Fact]
    public void TaskRef_AppResolver_ProducesInAppNav_WithDataAttributes()
    {
        LinkResolver appResolver = spec => spec.Kind == LinkKind.TaskRef
            ? new ResolvedLink($"#task:{spec.RawTarget}",
                DataAttributes: new Dictionary<string, string>
                {
                    ["data-task-ref"] = "true",
                    ["data-task-key"] = spec.RawTarget,
                })
            : LinkExtractor.WebDefault(spec);

        var html = HtmlRenderer.SpanToHtml(Link(LinkKind.TaskRef, "ASS-738", "ASS-738"), appResolver);
        Assert.Contains("href=\"#task:ASS-738\"", html);
        Assert.Contains("data-task-ref=\"true\"", html);
        Assert.Contains("data-task-key=\"ASS-738\"", html);   // §13 fix: data-attrs are not dropped
    }

    // ── Scenario B: file links (open-in-editor) ─────────────────────────

    [Fact]
    public void FileLink_EditorResolver_BuildsOpenFileHref_AndEditorPath()
    {
        LinkResolver editorResolver = spec => spec.Kind == LinkKind.FilePath
            ? new ResolvedLink($"/open-file?path={Uri.EscapeDataString(spec.RawTarget)}",
                DataAttributes: new Dictionary<string, string> { ["data-editor-path"] = spec.RawTarget })
            : LinkExtractor.WebDefault(spec);

        var html = HtmlRenderer.SpanToHtml(Link(LinkKind.FilePath, "src/Parser.cs", "Parser.cs"), editorResolver);
        Assert.Contains("/open-file?path=src%2FParser.cs", html);
        Assert.Contains("data-editor-path=\"src/Parser.cs\"", html);
    }

    // ── Scenario A: a standalone HTML document (static targets) ──────────

    [Fact]
    public void HtmlDoc_Resolver_GivesStaticTargets_PerKind()
    {
        LinkResolver docResolver = spec => spec.Kind switch
        {
            LinkKind.Url => new ResolvedLink(LinkExtractor.IsSafeUrl(spec.RawTarget) ? spec.RawTarget : "#", Target: "_blank"),
            LinkKind.FilePath => new ResolvedLink($"file:///{spec.RawTarget}"),
            LinkKind.TaskRef => new ResolvedLink("#"),   // no in-app nav in a static doc
            _ => new ResolvedLink(spec.RawTarget),
        };

        Assert.Contains("href=\"file:///docs/x.md\"", HtmlRenderer.SpanToHtml(Link(LinkKind.FilePath, "docs/x.md", "x"), docResolver));
        Assert.Contains("href=\"#\"", HtmlRenderer.SpanToHtml(Link(LinkKind.TaskRef, "ASS-1", "t"), docResolver));
        Assert.Contains("target=\"_blank\"", HtmlRenderer.SpanToHtml(Link(LinkKind.Url, "https://x.dev", "u"), docResolver));
    }

    // ── LinkExtractor policy ────────────────────────────────────────────

    [Theory]
    [InlineData("https://x.com", LinkKind.Url)]
    [InlineData("mailto:a@b.com", LinkKind.Url)]
    [InlineData("#section", LinkKind.Anchor)]
    [InlineData("src/Parser.cs", LinkKind.FilePath)]
    [InlineData("C:\\repo\\x.cs", LinkKind.FilePath)]
    [InlineData("file:///x", LinkKind.FilePath)]
    public void Classify_MapsRawTargets(string target, LinkKind expected)
        => Assert.Equal(expected, LinkExtractor.Classify(target));

    [Theory]
    [InlineData("https://x.com", true)]
    [InlineData("http://x.com", true)]
    [InlineData("mailto:a@b.com", true)]
    [InlineData("/relative", true)]
    [InlineData("#frag", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("vbscript:msgbox", false)]
    [InlineData("", false)]
    public void IsSafeUrl_AllowlistsOnlyWebAndRelative(string url, bool safe)
        => Assert.Equal(safe, LinkExtractor.IsSafeUrl(url));

    [Fact]
    public void NonLinkSpans_RenderWithInlineStyle_AndEscape()
    {
        Assert.Equal("<strong>a &amp; b</strong>", HtmlRenderer.SpanToHtml(new RenderedSpan(SpanKind.Bold, "a & b")));
        Assert.Equal("<code>x&lt;y</code>", HtmlRenderer.SpanToHtml(new RenderedSpan(SpanKind.Code, "x<y")));
        Assert.Equal("plain", HtmlRenderer.SpanToHtml(new RenderedSpan(SpanKind.Text, "plain")));
    }
}
