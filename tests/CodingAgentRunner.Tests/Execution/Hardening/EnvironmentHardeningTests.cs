using System.Diagnostics;
using System.Text;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Execution.Hardening;
using Xunit;

namespace CodingAgentRunner.Tests.Execution.Hardening;

public class EnvironmentHardeningTests
{
    [Fact]
    public void Apply_StampsTheHardeningVariables()
    {
        var psi = new ProcessStartInfo();

        EnvironmentHardening.Apply(psi);

        Assert.Equal("1", psi.Environment["NO_COLOR"]);
        Assert.Equal("0", psi.Environment["FORCE_COLOR"]);
        Assert.Equal("1", psi.Environment["CLAUDE_CODE_DISABLE_AUTOUPDATER"]);
        Assert.Equal("1", psi.Environment["GEMINI_NO_UPDATE_NOTIFIER"]);
        Assert.Equal("1", psi.Environment["CODEX_DISABLE_TIP_OF_THE_DAY"]);
        Assert.Equal("1", psi.Environment["CI"]);
        Assert.Equal("1", psi.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Equal("1", psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("1", psi.Environment["DOTNET_NOLOGO"]);
    }

    [Fact]
    public void Apply_WithUtf8_SetsEncodingAndLocaleVars()
    {
        var psi = new ProcessStartInfo();

        EnvironmentHardening.Apply(psi, new CliHardeningOptions { EnforceUtf8 = true });

        Assert.Equal(Encoding.UTF8, psi.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, psi.StandardErrorEncoding);
        Assert.Equal("utf-8", psi.Environment["PYTHONIOENCODING"]);
        Assert.Equal("C.UTF-8", psi.Environment["LC_ALL"]);
        Assert.Equal("1", psi.Environment["NODE_NO_WARNINGS"]);
    }

    [Fact]
    public void Apply_WithoutUtf8_LeavesEncodingUnset()
    {
        var psi = new ProcessStartInfo();
        // ProcessStartInfo.Environment starts as a copy of *this* process's
        // environment, so on a host that already exports PYTHONIOENCODING the key is
        // present before Apply is called. Clear it first: the assertion is about what
        // Apply sets, not about what the test host happens to inherit.
        psi.Environment.Remove("PYTHONIOENCODING");

        EnvironmentHardening.Apply(psi, new CliHardeningOptions { EnforceUtf8 = false });

        Assert.Null(psi.StandardOutputEncoding);
        Assert.False(psi.Environment.ContainsKey("PYTHONIOENCODING"));
    }

    [Fact]
    public void Apply_ConsumerOverridesWin()
    {
        var psi = new ProcessStartInfo();

        EnvironmentHardening.Apply(psi, null,
            new Dictionary<string, string> { ["NO_COLOR"] = "0", ["MY_VAR"] = "x" });

        Assert.Equal("0", psi.Environment["NO_COLOR"]); // override wins
        Assert.Equal("x", psi.Environment["MY_VAR"]);
    }
}
