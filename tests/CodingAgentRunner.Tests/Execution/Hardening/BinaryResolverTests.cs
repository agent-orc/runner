using CodingAgentRunner.Execution.Hardening;
using Xunit;

namespace CodingAgentRunner.Tests.Execution.Hardening;

public class BinaryResolverTests
{
    [Fact]
    public void ResolveShimToExe_NpmStyleCmd_ResolvesToBundledExe()
    {
        // A fake npm .cmd shim that launches a bundled .exe (the claude.cmd shape).
        var dir = Directory.CreateTempSubdirectory("car-shim-").FullName;
        try
        {
            var binDir = Path.Combine(dir, "node_modules", "@vendor", "tool", "bin");
            Directory.CreateDirectory(binDir);
            var exe = Path.Combine(binDir, "tool.exe");
            File.WriteAllText(exe, "stub");

            var cmd = Path.Combine(dir, "tool.cmd");
            File.WriteAllText(cmd,
                "@ECHO off\r\nSET dp0=%~dp0\r\n" +
                "\"%dp0%\\node_modules\\@vendor\\tool\\bin\\tool.exe\"   %*\r\n");

            var resolved = BinaryResolver.ResolveShimToExe(cmd);

            // .cmd shims are a Windows concept; off-Windows the input is returned.
            if (OperatingSystem.IsWindows())
                Assert.Equal(exe, resolved, ignoreCase: true);
            else
                Assert.Equal(cmd, resolved);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ResolveShimToExe_NodeScriptShim_ReturnsInputUnchanged()
    {
        var dir = Directory.CreateTempSubdirectory("car-shim2-").FullName;
        try
        {
            var cmd = Path.Combine(dir, "tool.cmd");
            File.WriteAllText(cmd, "@ECHO off\r\nnode  \"%~dp0\\..\\tool\\cli.js\" %*\r\n");
            // No .exe is launched -> nothing to unwrap.
            Assert.Equal(cmd, BinaryResolver.ResolveShimToExe(cmd));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ResolveShimToExe_NonShimInput_ReturnsInputUnchanged()
    {
        Assert.Equal("claude", BinaryResolver.ResolveShimToExe("claude"));
        Assert.Equal(@"C:\tools\claude.exe", BinaryResolver.ResolveShimToExe(@"C:\tools\claude.exe"));
        Assert.Equal("", BinaryResolver.ResolveShimToExe(""));
    }

    [Fact]
    public void ResolveExecutable_RootedExistingFile_ReturnedUnchanged()
    {
        var file = Path.GetTempFileName(); // has an extension and exists
        try { Assert.Equal(file, BinaryResolver.ResolveExecutable(file)); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ResolveExecutable_EmptyOrWhitespace_ReturnedUnchanged()
    {
        Assert.Equal("", BinaryResolver.ResolveExecutable(""));
        Assert.Equal("   ", BinaryResolver.ResolveExecutable("   "));
    }
}
