using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Tests.Model;

public class AntigravityDiscoveryTests
{
    [Fact]
    public void ModelsTsv_ParsesIdsAndLabels()
    {
        var rows = AntigravityCliModelDiscovery.ParseModels("gemini-4-argon-low\tGemini 4 Argon (Low)\r\ngemini-4-argon-high\tGemini 4 Argon (High)\ninvalid\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("gemini-4-argon-low", rows[0].Id);
        Assert.Equal("Gemini 4 Argon (Low)", rows[0].Label);
        Assert.Equal("google", rows[0].Vendor);
    }

    [Fact]
    public void VersionProbe_UsesAgyVersionExitAndOutput()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "agy-probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "#!/bin/sh\n[ \"$1\" = \"--version\" ] || exit 9\nprintf '1.4.0\\n'\n");
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var engine = new CliRunEngine(BuiltInDescriptors.Antigravity, new CliOptions { AntigravityPath = path });
            var result = engine.TestCliPath();
            Assert.True(result.Available);
            Assert.Equal("1.4.0", result.Version);
            Assert.Equal(path, result.Path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Discovery_InvokesModelsCommand()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "agy-models-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path,
            "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then printf '1.4.0\\n'; exit 0; fi\n" +
            "if [ \"$1\" = \"models\" ]; then printf 'gemini-4-argon-low\\tGemini 4 Argon (Low)\\n'; exit 0; fi\nexit 9\n");
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var discovery = new AntigravityCliModelDiscovery(new CliOptions { AntigravityPath = path });
            var catalog = await discovery.DiscoverAsync();
            Assert.Equal("1.4.0", catalog.CliVersion);
            Assert.Equal("gemini-4-argon-low", Assert.Single(catalog.Models).Id);
        }
        finally { File.Delete(path); }
    }
}
