using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Model;

/// <summary>
/// The pure (cliType, mode) → flags mapper every driver uses on spawn. Asserting it
/// here covers "right flags per mode" without driving a real CLI. (Consolidated from the
/// consuming app — the library is the canonical owner.)
/// </summary>
public sealed class CliPermissionFlagsTests
{
    [Fact]
    public void Claude_Yolo_SkipsPermissions()
        => Assert.Equal(["--dangerously-skip-permissions"], CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.Yolo));

    [Fact]
    public void Claude_WorkspaceWrite_AcceptEdits()
        => Assert.Equal(["--permission-mode", "acceptEdits"], CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.WorkspaceWrite));

    [Fact]
    public void Claude_ReadOnly_PlanMode()
        => Assert.Equal(["--permission-mode", "plan"], CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.ReadOnly));

    [Fact]
    public void Claude_Custom_InjectsNothing()
        => Assert.Empty(CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.Custom));

    [Fact]
    public void Codex_Yolo_DangerFullAccess()
        => Assert.Equal(["--sandbox", "danger-full-access"], CliPermissionFlags.For(CliTypes.Codex, CliPermissionModes.Yolo));

    [Fact]
    public void Codex_WorkspaceWrite_SandboxWorkspaceWrite()
        => Assert.Equal(["--sandbox", "workspace-write"], CliPermissionFlags.For(CliTypes.Codex, CliPermissionModes.WorkspaceWrite));

    [Fact]
    public void Codex_ReadOnly_SandboxReadOnly()
        => Assert.Equal(["--sandbox", "read-only"], CliPermissionFlags.For(CliTypes.Codex, CliPermissionModes.ReadOnly));

    [Fact]
    public void Codex_Custom_InjectsNothing()
        => Assert.Empty(CliPermissionFlags.For(CliTypes.Codex, CliPermissionModes.Custom));

    [Fact]
    public void Gemini_Yolo_SkipTrustY()
        => Assert.Equal(["--skip-trust", "-y"], CliPermissionFlags.For(CliTypes.Gemini, CliPermissionModes.Yolo));

    [Fact]
    public void Gemini_WorkspaceWrite_AutoEdit_KeepsSkipTrust()
        => Assert.Equal(["--skip-trust", "--approval-mode", "auto_edit"], CliPermissionFlags.For(CliTypes.Gemini, CliPermissionModes.WorkspaceWrite));

    [Fact]
    public void Gemini_Custom_StillSkipsTrustToAvoidHang()
        => Assert.Equal(["--skip-trust"], CliPermissionFlags.For(CliTypes.Gemini, CliPermissionModes.Custom));

    [Fact]
    public void Copilot_Yolo_AllowAll()
        => Assert.Equal(["--allow-all"], CliPermissionFlags.For(CliTypes.Copilot, CliPermissionModes.Yolo));

    [Theory]
    [InlineData(CliPermissionModes.WorkspaceWrite)]
    [InlineData(CliPermissionModes.ReadOnly)]
    [InlineData(CliPermissionModes.Custom)]
    public void Copilot_NonYolo_InjectsNothing(string mode)
        => Assert.Empty(CliPermissionFlags.For(CliTypes.Copilot, mode));

    [Fact]
    public void NullMode_NormalizesToYolo()
    {
        Assert.Equal(CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.Yolo), CliPermissionFlags.For(CliTypes.Claude, null));
        Assert.Equal(CliPermissionFlags.For(CliTypes.Codex, CliPermissionModes.Yolo), CliPermissionFlags.For(CliTypes.Codex, null));
    }

    [Fact]
    public void UnknownCliType_NormalizesViaCliTypesContract()   // unknown id → Copilot, never throws
        => Assert.Equal(CliPermissionFlags.For(CliTypes.Copilot, CliPermissionModes.Yolo), CliPermissionFlags.For("totally-made-up", CliPermissionModes.Yolo));
}
