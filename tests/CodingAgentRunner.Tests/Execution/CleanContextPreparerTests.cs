using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Execution;

public class CleanContextPreparerTests
{
    private static string MakeFakeClaudeHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "car-test-home-" + Guid.NewGuid().ToString("N"));
        var claudeDir = Path.Combine(home, ".claude");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(Path.Combine(claudeDir, ".credentials.json"), "{\"token\":\"x\"}");
        File.WriteAllText(Path.Combine(claudeDir, "settings.json"), "{}");
        // A file that must NOT be seeded into the clean home:
        File.WriteAllText(Path.Combine(claudeDir, "CLAUDE.md"), "secret memory");
        return home;
    }

    [Fact]
    public void PrepareClaude_CreatesIsolatedHome_SeedsAllowlist_AndRedirectsEnv()
    {
        var home = MakeFakeClaudeHome();
        try
        {
            using var prep = CleanContextPreparer.PrepareClaude(home);
            Assert.NotNull(prep);
            Assert.Equal(CliTypes.Claude, prep!.CliType);

            // The env redirect points the CLI at the temp home.
            Assert.True(prep.EnvOverrides.TryGetValue("CLAUDE_CONFIG_DIR", out var dir));
            Assert.Equal(prep.TempHome, dir);
            Assert.True(Directory.Exists(prep.TempHome));

            // Allowlisted files were seeded; user memory was NOT.
            Assert.True(File.Exists(Path.Combine(prep.TempHome, ".credentials.json")));
            Assert.True(File.Exists(Path.Combine(prep.TempHome, "settings.json")));
            Assert.False(File.Exists(Path.Combine(prep.TempHome, "CLAUDE.md")));

            // Credential refreshes in the clean home reach the source through the hardlink.
            var sourceCredentials = Path.Combine(home, ".claude", ".credentials.json");
            var cleanCredentials = Path.Combine(prep.TempHome, ".credentials.json");
            Assert.Contains(
                prep.Sources,
                s => s.Path == cleanCredentials && s.Detail?.StartsWith("hard-linked from ") == true);
            File.WriteAllText(cleanCredentials, "{\"token\":\"refreshed\"}");
            Assert.Equal("{\"token\":\"refreshed\"}", File.ReadAllText(sourceCredentials));

            // Base settings remain an independent copy.
            var sourceSettings = Path.Combine(home, ".claude", "settings.json");
            var cleanSettings = Path.Combine(prep.TempHome, "settings.json");
            File.WriteAllText(cleanSettings, "{\"clean\":true}");
            Assert.Equal("{}", File.ReadAllText(sourceSettings));

            // Sources: the env entry + the two seeded files.
            Assert.Contains(prep.Sources, s => s.Kind == CliContextSourceKinds.Env);
            Assert.Equal(2, prep.Sources.Count(s => s.Kind == CliContextSourceKinds.GlobalConfig));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Dispose_TearsDownTheTempHome()
    {
        var home = MakeFakeClaudeHome();
        string tempHome;
        try
        {
            var prep = CleanContextPreparer.PrepareClaude(home);
            Assert.NotNull(prep);
            tempHome = prep!.TempHome;
            Assert.True(Directory.Exists(tempHome));
            prep.Dispose();
            Assert.False(Directory.Exists(tempHome));
            prep.Dispose(); // idempotent
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void PrepareClaude_WithNoUserHome_StillCreatesHome_WithNoSeeds()
    {
        using var prep = CleanContextPreparer.PrepareClaude(null);
        Assert.NotNull(prep);
        Assert.True(Directory.Exists(prep!.TempHome));
        // Only the env source; nothing to seed.
        Assert.DoesNotContain(prep.Sources, s => s.Kind == CliContextSourceKinds.GlobalConfig);
    }

    [Fact]
    public void LinkOrCopy_WhenLinksAreUnavailable_FallsBackToIndependentCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "car-link-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.json");
            var destination = Path.Combine(root, "destination.json");
            File.WriteAllText(source, "{\"token\":\"source\"}");
            var attempts = new List<string>();

            var method = CleanContextPreparer.LinkOrCopy(
                source,
                destination,
                createHardLink: (_, _) =>
                {
                    attempts.Add("hardlink");
                    throw new PlatformNotSupportedException();
                },
                createSymbolicLink: (_, _) =>
                {
                    attempts.Add("symlink");
                    throw new UnauthorizedAccessException();
                });

            Assert.Equal(SeedFileMethod.Copy, method);
            Assert.Equal(["hardlink", "symlink"], attempts);
            Assert.Equal(File.ReadAllText(source), File.ReadAllText(destination));

            File.WriteAllText(destination, "{\"token\":\"clean-home\"}");
            Assert.Equal("{\"token\":\"source\"}", File.ReadAllText(source));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuiltInSpecs_SeparateRefreshableCredentialsFromCopiedConfig()
    {
        var defaultSpec = new CleanContextSpec("CLI_HOME", ".cli", ["auth.json"]);
        Assert.Equal(["auth.json"], defaultSpec.LinkedSeedFiles);
        Assert.Empty(defaultSpec.CopiedSeedFiles);

        var claude = BuiltInDescriptors.Get(CliTypes.Claude).CleanContext;
        Assert.NotNull(claude);
        Assert.Equal([".credentials.json"], claude!.LinkedSeedFiles);
        Assert.Equal(["settings.json"], claude.CopiedSeedFiles);

        var codex = BuiltInDescriptors.Get(CliTypes.Codex).CleanContext;
        Assert.NotNull(codex);
        Assert.Equal(["auth.json"], codex!.LinkedSeedFiles);
        Assert.Equal(["config.toml"], codex.CopiedSeedFiles);
    }
}
