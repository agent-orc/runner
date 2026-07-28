using CodingAgentRunner.Delegation;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Delegation;

/// <summary>
/// Pins the two rules that make it safe to materialize agent definitions into
/// somebody's checkout: a repo-provided definition is never touched, and everything
/// the runner writes is taken back out when the run ends.
/// </summary>
public class SubagentMaterializationTests
{
    private static string NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "car-test-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void Prepare_WritesDefaultAgents_AndRemovesThemOnDispose()
    {
        var workspace = NewWorkspace();
        try
        {
            var agentsDir = Path.Combine(workspace, ".claude", "agents");
            SubagentMaterialization? prep;
            using (prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, new DelegationOptions()))
            {
                Assert.NotNull(prep);
                Assert.Equal(2, prep!.Available.Count);
                Assert.All(prep.Available, a => Assert.Equal(SubagentSources.Runner, a.Source));

                var mechanical = Path.Combine(agentsDir, "mechanical.md");
                Assert.True(File.Exists(mechanical));
                var text = File.ReadAllText(mechanical);
                Assert.Contains("name: mechanical", text);
                Assert.Contains("model: haiku", text);
                Assert.Contains(SubagentMaterializer.GeneratedMarker, text);
                Assert.True(File.Exists(Path.Combine(agentsDir, "checker.md")));
            }

            // Disposal takes the whole tree the runner created back out, so the run
            // leaves no files for the host's commit to pick up.
            Assert.False(File.Exists(Path.Combine(agentsDir, "mechanical.md")));
            Assert.False(Directory.Exists(agentsDir));
            Assert.False(Directory.Exists(Path.Combine(workspace, ".claude")));
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Prepare_LeavesARepoProvidedDefinitionAlone()
    {
        var workspace = NewWorkspace();
        try
        {
            var agentsDir = Path.Combine(workspace, ".claude", "agents");
            Directory.CreateDirectory(agentsDir);
            var repoOwned = Path.Combine(agentsDir, "mechanical.md");
            const string repoText = "---\nname: mechanical\ndescription: the project's own sweeper\nmodel: sonnet\n---\n\nProject rules.\n";
            File.WriteAllText(repoOwned, repoText);

            using var prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, new DelegationOptions());

            Assert.NotNull(prep);
            Assert.Equal(repoText, File.ReadAllText(repoOwned));                       // untouched
            Assert.DoesNotContain(prep!.WrittenFiles, f => f == repoOwned);

            var mechanical = Assert.Single(prep.Available, a => a.Name == "mechanical");
            Assert.Equal(SubagentSources.Repo, mechanical.Source);
            Assert.Equal("the project's own sweeper", mechanical.Description);

            // The runner still contributes the agents the project did not define.
            Assert.Equal(SubagentSources.Runner, Assert.Single(prep.Available, a => a.Name == "checker").Source);

            prep.Dispose();
            Assert.True(File.Exists(repoOwned));                                       // survives the run
            Assert.False(File.Exists(Path.Combine(agentsDir, "checker.md")));
            Assert.True(Directory.Exists(agentsDir));                                  // the repo's directory stays
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Prepare_RefreshesAStaleRunnerFileFromACrashedRun()
    {
        var workspace = NewWorkspace();
        try
        {
            var agentsDir = Path.Combine(workspace, ".claude", "agents");
            Directory.CreateDirectory(agentsDir);
            var stale = Path.Combine(agentsDir, "mechanical.md");
            File.WriteAllText(stale, $"---\nname: mechanical\n---\n\n<!-- {SubagentMaterializer.GeneratedMarker} -->\n\nold body\n");

            using var prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, new DelegationOptions());

            Assert.NotNull(prep);
            Assert.Contains(prep!.WrittenFiles, f => f == stale);
            Assert.DoesNotContain("old body", File.ReadAllText(stale));
            Assert.Equal(SubagentSources.Runner, Assert.Single(prep.Available, a => a.Name == "mechanical").Source);
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Prepare_ReturnsNull_WhenTheProjectOptedOut()
    {
        var workspace = NewWorkspace();
        try
        {
            var agentsDir = Path.Combine(workspace, ".claude", "agents");
            Directory.CreateDirectory(agentsDir);
            File.WriteAllText(Path.Combine(agentsDir, SubagentMaterializer.OptOutFileName), "");

            var prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, new DelegationOptions());

            Assert.Null(prep);
            Assert.False(File.Exists(Path.Combine(agentsDir, "mechanical.md")));
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Prepare_ReturnsNull_WhenDelegationIsDisabled()
    {
        var workspace = NewWorkspace();
        try
        {
            var prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, new DelegationOptions { Enabled = false });

            Assert.Null(prep);
            Assert.False(Directory.Exists(Path.Combine(workspace, ".claude")));
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Prepare_SkipsADefinitionWhoseNameIsNotAFileName()
    {
        var workspace = NewWorkspace();
        try
        {
            var options = new DelegationOptions
            {
                Agents =
                [
                    SubagentDefaults.Mechanical with { Name = "../escape" },
                    SubagentDefaults.Checker,
                ],
            };

            using var prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, options);

            Assert.NotNull(prep);
            Assert.Equal("checker", Assert.Single(prep!.Available).Name);
            Assert.False(File.Exists(Path.Combine(workspace, ".claude", "escape.md")));
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void CodexDefinitionsAreRenderedAsToml_WithTheLightModel()
    {
        var workspace = NewWorkspace();
        try
        {
            using var prep = SubagentMaterializer.Prepare(
                CliTypes.Codex, SubagentRenderers.Codex, workspace, new DelegationOptions());

            Assert.NotNull(prep);
            var file = Path.Combine(workspace, ".codex", "agents", "mechanical.toml");
            Assert.True(File.Exists(file));

            var text = File.ReadAllText(file);
            Assert.Contains("name = \"mechanical\"", text);
            Assert.Contains($"model = \"{SubagentDefaults.CodexLightModel}\"", text);
            Assert.Contains("developer_instructions = \"\"\"", text);
            Assert.StartsWith("# " + SubagentMaterializer.GeneratedMarker, text);
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Sources_ReportBothOrigins()
    {
        var workspace = NewWorkspace();
        try
        {
            var agentsDir = Path.Combine(workspace, ".claude", "agents");
            Directory.CreateDirectory(agentsDir);
            File.WriteAllText(Path.Combine(agentsDir, "checker.md"), "---\nname: checker\ndescription: ours\n---\n");

            using var prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, new DelegationOptions());

            Assert.NotNull(prep);
            Assert.All(prep!.Sources, s => Assert.Equal(CliContextSourceKinds.InstructionFile, s.Kind));
            Assert.Contains(prep.Sources, s => s.Detail == "provided by the repository");
            Assert.Contains(prep.Sources, s => s.Detail!.StartsWith("materialized by the runner", StringComparison.Ordinal));
        }
        finally { Cleanup(workspace); }
    }

    [Theory]
    [InlineData("mechanical", true)]
    [InlineData("code-checker", true)]
    [InlineData("", false)]
    [InlineData("../escape", false)]
    [InlineData("Mechanical", false)]
    [InlineData("with space", false)]
    [InlineData("-leading", false)]
    public void IsValidName_AcceptsOnlyFileSafeIds(string name, bool expected)
        => Assert.Equal(expected, SubagentDefinition.IsValidName(name));
}
