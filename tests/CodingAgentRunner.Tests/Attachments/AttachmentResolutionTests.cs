using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Attachments;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodingAgentRunner.Tests.Attachments;

public class AttachmentResolutionTests
{
    private sealed class DelegateResolver(
        Func<AttachmentReference, ResolvedAttachment?> resolve) : IAttachmentResolver
    {
        public ValueTask<ResolvedAttachment?> ResolveAsync(
            AttachmentReference attachment,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(resolve(attachment));
    }

    private sealed class CountingSpawner : ICliProcessSpawner
    {
        public int Count { get; private set; }

        public CliSpawn Spawn(System.Diagnostics.ProcessStartInfo startInfo)
        {
            Count++;
            throw new InvalidOperationException("The process must not be spawned.");
        }
    }

    private sealed class TestLogPaths(string root) : IRunLogPathProvider
    {
        public string GetRunLogDirectory(string runId) => Path.Combine(root, "logs", runId);
        public string GetActiveJobsFile() => Path.Combine(root, "logs", "active.json");
    }

    private static CliDescriptor Descriptor(Action<CliLaunchContext>? observe = null) => new()
    {
        CliType = CliTypes.Codex,
        GetCliPath = _ => "dotnet",
        BuildLaunch = context =>
        {
            observe?.Invoke(context);
            return new LaunchSpec
            {
                Executable = "dotnet",
                Argv = ["--version"],
                WorkingDirectory = context.Request.WorkingDirectory,
            };
        },
        Parse = (_, _, _) => Array.Empty<CliRunEvent>(),
        InterruptClassifier = InterruptClassifiers.None,
        Liveness = LivenessSpec.InBandDefault,
        Capabilities = model => new CliCapabilities { CliType = CliTypes.Codex, Model = model },
    };

    [Fact]
    public async Task StartAsync_ResolvesAttachmentBeforeBuildingTheLaunch()
    {
        var root = Path.Combine(Path.GetTempPath(), "car-attachments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var imagePath = Path.Combine(root, "pasted screenshot.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47]);

        try
        {
            CliLaunchContext? observed = null;
            var resolver = new DelegateResolver(attachment => attachment.Reference == "attachment:42"
                ? new ResolvedAttachment(imagePath) { MediaType = "image/png" }
                : null);
            var engine = new CliRunEngine(
                Descriptor(context => observed = context),
                new CliOptions { AllowAgentGitMutation = true, AttachmentResolver = resolver },
                logPaths: new TestLogPaths(root));

            var (run, error) = await engine.StartAsync(new CliRunRequest
            {
                RunId = "attachment-ok",
                Prompt = "Explain the error in the screenshot.",
                WorkingDirectory = root,
                Attachments =
                [
                    new AttachmentReference("attachment:42")
                    {
                        FileName = "screenshot.png",
                        MediaType = "image/png",
                        AltText = "build error",
                    },
                ],
            });

            Assert.Null(error);
            Assert.NotNull(run);
            Assert.NotNull(observed);
            var attachment = Assert.Single(observed!.Attachments);
            Assert.Equal(Path.GetFullPath(imagePath), attachment.AbsolutePath);
            Assert.Contains("Explain the error in the screenshot.", observed.Request.Prompt);
            Assert.Contains(Path.GetFullPath(imagePath), observed.Request.Prompt);
            Assert.Contains("<chat-attachments>", observed.Request.Prompt);

            await WaitUntilAsync(() => engine.GetExecution("attachment-ok")?.Status != "running");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StartAsync_UnresolvableReferenceIsActionableAndDoesNotSpawn()
    {
        var spawner = new CountingSpawner();
        var engine = new CliRunEngine(
            Descriptor(),
            new CliOptions { AllowAgentGitMutation = true, Spawner = spawner });

        var (run, error) = await engine.StartAsync(new CliRunRequest
        {
            RunId = "attachment-missing",
            Prompt = "Inspect this image.",
            WorkingDirectory = Path.GetTempPath(),
            Attachments = [new AttachmentReference("attachment:missing")],
        });

        Assert.Null(run);
        Assert.Contains("Attachment 'attachment:missing' could not be resolved", error);
        Assert.Contains("CliOptions.AttachmentResolver", error);
        Assert.Equal(0, spawner.Count);
    }

    [Fact]
    public async Task StartAsync_MissingResolvedFileNamesTheRecoveryAction()
    {
        var path = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"), "shot.png");
        var resolver = new DelegateResolver(_ => new ResolvedAttachment(path));
        var engine = new CliRunEngine(
            Descriptor(),
            new CliOptions { AllowAgentGitMutation = true, AttachmentResolver = resolver });

        var (run, error) = await engine.StartAsync(new CliRunRequest
        {
            RunId = "attachment-gone",
            Prompt = "Inspect this image.",
            WorkingDirectory = Path.GetTempPath(),
            Attachments = [new AttachmentReference("attachment:gone")],
        });

        Assert.Null(run);
        Assert.Contains("resolved file does not exist", error);
        Assert.Contains("restore or re-upload", error);
        Assert.Contains(path, error);
    }

    [Fact]
    public void CodexLaunch_PassesResolvedImagesThroughNativeImageInput()
    {
        var imagePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "screen.png"));
        var request = new CliRunRequest
        {
            RunId = "codex-image",
            Prompt = "Inspect it.",
            WorkingDirectory = Path.GetTempPath(),
            ResumeSessionId = "12345678-1234-1234-1234-123456789abc",
        };
        var context = new CliLaunchContext(request, "codex", null, null, NullLogger.Instance)
        {
            Attachments = [new ResolvedAttachment(imagePath) { MediaType = "image/png" }],
        };

        var launch = BuiltInDescriptors.Codex.BuildLaunch(context);
        var imageFlag = launch.Argv.ToList().IndexOf("--image");

        Assert.True(imageFlag >= 0);
        Assert.Equal(imagePath, launch.Argv[imageFlag + 1]);
        Assert.True(imageFlag < launch.Argv.ToList().IndexOf("resume"));
        Assert.Equal("-", launch.Argv[^1]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
            await Task.Delay(25, timeout.Token);
    }
}
