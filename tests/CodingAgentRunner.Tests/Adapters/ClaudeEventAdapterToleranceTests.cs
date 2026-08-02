using System.Linq;
using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;
using Xunit;

namespace CodingAgentRunner.Tests.Adapters;

/// <summary>
/// Adapter tolerance for frames whose field casing and value types have drifted
/// across Claude Code releases (CAR-E): <c>rate_limit_event</c> in camelCase and
/// snake_case, unix-seconds and ISO-8601 reset timestamps, stringified booleans,
/// and the init frame's execution context. A frame the parser cannot fully read
/// must degrade field-by-field, never lose the event or fail the read loop.
/// </summary>
public class ClaudeEventAdapterToleranceTests
{
    private static System.Collections.Generic.List<CliRunEvent> Map(string line)
        => ClaudeEventAdapter.Map(line, "run-1").ToList();

    [Fact]
    public void RateLimit_CamelCase_KeepsItsHistoricalReading()
    {
        var evt = Assert.Single(Map(
            """{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","status":"allowed_warning","resetsAt":1753600000,"overageStatus":"enabled","isUsingOverage":true}}"""));
        var rl = Assert.IsType<CliRunEvent.RateLimitObserved>(evt);
        Assert.Equal("5-hour", rl.Window);
        Assert.Equal("allowed_warning", rl.Status);
        Assert.Equal(1753600000L, rl.ResetsAt);
        Assert.Equal("enabled", rl.OverageStatus);
        Assert.True(rl.IsUsingOverage);
    }

    [Fact]
    public void RateLimit_SnakeCase_WithIsoResetAndStringBool_ReadsTheSameFields()
    {
        var evt = Assert.Single(Map(
            """{"type":"rate_limit_event","rate_limit_info":{"rate_limit_type":"seven_day","status":"allowed","resets_at":"2026-07-27T06:26:40Z","overage_status":"disabled","is_using_overage":"true"}}"""));
        var rl = Assert.IsType<CliRunEvent.RateLimitObserved>(evt);
        Assert.Equal("weekly", rl.Window);
        Assert.Equal("allowed", rl.Status);
        Assert.Equal(1785133600L, rl.ResetsAt); // 2026-07-27T06:26:40Z as unix seconds
        Assert.Equal("disabled", rl.OverageStatus);
        Assert.True(rl.IsUsingOverage);
    }

    [Fact]
    public void RateLimit_CamelCasedInfoObject_IsAccepted()
    {
        var evt = Assert.Single(Map(
            """{"type":"rate_limit_event","rateLimitInfo":{"rateLimitType":"five_hour","status":"allowed","resetsAt":"1753600000"}}"""));
        var rl = Assert.IsType<CliRunEvent.RateLimitObserved>(evt);
        Assert.Equal("5-hour", rl.Window);
        Assert.Equal(1753600000L, rl.ResetsAt); // numeric string parses as unix seconds
        Assert.False(rl.IsUsingOverage);
    }

    [Fact]
    public void RateLimit_MissingOrUnreadableFields_DegradeToDefaultsNotToADroppedEvent()
    {
        var evt = Assert.Single(Map(
            """{"type":"rate_limit_event","rate_limit_info":{"resets_at":"not-a-timestamp"}}"""));
        var rl = Assert.IsType<CliRunEvent.RateLimitObserved>(evt);
        Assert.Null(rl.Window);
        Assert.Null(rl.Status);
        Assert.Equal(0L, rl.ResetsAt);
        Assert.False(rl.IsUsingOverage);
    }

    [Fact]
    public void InitFrame_CarriesTheExecutionContext_OnSessionStarted()
    {
        var evt = Assert.Single(Map(
            """{"type":"system","subtype":"init","session_id":"abc-123","model":"claude-opus-4-8","permissionMode":"bypassPermissions","cwd":"/srv/work/AGT-1","apiKeySource":"none"}"""));
        var started = Assert.IsType<CliRunEvent.SessionStarted>(evt);
        Assert.Equal("abc-123", started.SessionId);
        Assert.Equal("claude-opus-4-8", started.Model);
        Assert.Equal("bypassPermissions", started.PermissionMode);
        Assert.Equal("/srv/work/AGT-1", started.Cwd);
        Assert.Equal("none", started.ApiKeySource);
    }

    [Fact]
    public void InitFrame_WithoutContextFields_StillStartsTheSession()
    {
        var evt = Assert.Single(Map("""{"type":"system","subtype":"init","session_id":"abc-123"}"""));
        var started = Assert.IsType<CliRunEvent.SessionStarted>(evt);
        Assert.Equal("abc-123", started.SessionId);
        Assert.Null(started.Model);
        Assert.Null(started.PermissionMode);
        Assert.Null(started.Cwd);
        Assert.Null(started.ApiKeySource);
    }
}
