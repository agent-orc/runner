using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace CodingAgentRunner.Execution.Win;

/// <summary>
/// Win32-native child-process spawn that curates which parent handles are
/// inheritable. The default <see cref="Process"/> API on Windows sets
/// <c>bInheritHandles=TRUE</c> and inherits ALL inheritable handles in the
/// parent's table — sockets, file watchers, ConPTY consoles, ETW listeners, and
/// any random library's global state. A Node-based CLI (Claude / Codex / Gemini)
/// inherits those handles and may stat / read them during init, blocking on a
/// handle it has no business owning.
///
/// <para>
/// The fix: spawn via <c>CreateProcessW</c> + <c>STARTUPINFOEX</c> +
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>, passing exactly the three pipe
/// handles the CLI expects (stdin, stdout, stderr) and nothing else. The OSS
/// reference for the same shape is in
/// <c>openai/codex/codex-rs/windows-sandbox-rs/src/proc_thread_attr.rs</c>; this
/// is the .NET adaptation.
/// </para>
/// <para>Windows-only by attribute. Callers fall through to the standard <see cref="Process.Start()"/> path on non-Windows.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsHandleScrubSpawner
{
    /// <summary>
    /// Result of a curated spawn. Stdin / Stdout / Stderr are owned by the caller
    /// and must be disposed; <see cref="Process"/> wraps the child by PID for the
    /// runner's watchdog / kill flow.
    /// </summary>
    public sealed record Result(
        Process Process,
        FileStream? Stdin,
        FileStream Stdout,
        FileStream Stderr,
        Action KillTree);

    /// <summary>
    /// Spawn <paramref name="exePath"/> with <paramref name="argList"/> as argv
    /// (escaped per CommandLineToArgvW), <paramref name="cwd"/> as the working
    /// directory, and <paramref name="envBlock"/> as the child's environment. Only
    /// the child-side ends of the stdout + stderr (and optional stdin) pipes are
    /// marked inheritable; nothing else from the parent leaks in.
    /// </summary>
    public static Result Spawn(
        string exePath,
        IReadOnlyList<string> argList,
        string cwd,
        IReadOnlyDictionary<string, string?> envBlock,
        bool wantStdin,
        ILogger? logger = null)
    {
        // 1. Create stdout / stderr / stdin pipes / NUL handle.
        //    The PARENT keeps the read ends (stdout/stderr) and (when a payload is
        //    needed) the write end of stdin. The CHILD gets the write ends of
        //    stdout/stderr and either the read end of a stdin pipe or a handle to
        //    \\.\NUL when no payload is needed. Only the child-side handles are
        //    flagged inheritable; parent-side handles stay non-inheritable.
        //
        //    Why NUL when wantStdin=false: STARTF_USESTDHANDLES with hStdInput=NULL
        //    gives the child INVALID_HANDLE_VALUE for stdin, which Node CLIs treat
        //    as a hard error during init (claude exits with code 1 immediately). A
        //    real handle to \\.\NUL gives the child an immediate-EOF stdin, which is
        //    what Python's stdin=DEVNULL and Node's stdio:'ignore' wire up.
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        IntPtr stdoutRead = IntPtr.Zero, stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero, stderrWrite = IntPtr.Zero;
        IntPtr stdinRead = IntPtr.Zero, stdinWrite = IntPtr.Zero;
        IntPtr nullHandle = IntPtr.Zero;
        IntPtr lpAttributeList = IntPtr.Zero;
        IntPtr handleListPtr = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        var success = false;
        // Everything that allocates a kernel handle lives inside this try so the
        // finally can close anything that fails partway. On the success path each
        // handle is either transferred (to a FileStream / killTree) or closed and
        // then zeroed, so the finally's cleanup is a no-op for it.
        try
        {
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref sa, 0)) ThrowLastError("CreatePipe(stdout)");
            if (!SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0)) ThrowLastError("SetHandleInformation(stdoutRead)");
            if (!CreatePipe(out stderrRead, out stderrWrite, ref sa, 0)) ThrowLastError("CreatePipe(stderr)");
            if (!SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0)) ThrowLastError("SetHandleInformation(stderrRead)");
            if (wantStdin)
            {
                if (!CreatePipe(out stdinRead, out stdinWrite, ref sa, 0)) ThrowLastError("CreatePipe(stdin)");
                if (!SetHandleInformation(stdinWrite, HANDLE_FLAG_INHERIT, 0)) ThrowLastError("SetHandleInformation(stdinWrite)");
            }
            else
            {
                // Open \\.\NUL with inheritable security attributes so the child
                // receives a real, immediately-EOF stdin handle.
                nullHandle = CreateFileW(
                    "NUL",
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    ref sa,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);
                if (nullHandle == INVALID_HANDLE_VALUE) ThrowLastError("CreateFileW(NUL)");
            }

            // 2. Build the PROC_THREAD_ATTRIBUTE_LIST holding ONLY our pipe handles.
            //    InitializeProcThreadAttributeList is called twice: once with NULL to
            //    size the buffer, once with the buffer.
            UIntPtr size = UIntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            lpAttributeList = Marshal.AllocHGlobal((int)size);
            if (!InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref size))
                ThrowLastError("InitializeProcThreadAttributeList");

            // Pack the inheritable handles into a heap buffer. Stdin's child-side
            // handle is either the pipe read end (payload path) or the NUL device
            // handle (default-deny path).
            var stdinChildHandle = wantStdin ? stdinRead : nullHandle;
            var handles = new[] { stdinChildHandle, stdoutWrite, stderrWrite };
            var bytes = handles.Length * IntPtr.Size;
            handleListPtr = Marshal.AllocHGlobal(bytes);
            for (int i = 0; i < handles.Length; i++)
                Marshal.WriteIntPtr(handleListPtr, i * IntPtr.Size, handles[i]);

            if (!UpdateProcThreadAttribute(
                    lpAttributeList,
                    0,
                    (UIntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    handleListPtr,
                    (UIntPtr)bytes,
                    IntPtr.Zero,
                    IntPtr.Zero))
                ThrowLastError("UpdateProcThreadAttribute");

            // 3. Build the command line. CreateProcessW wants a single string
            //    parsed by CommandLineToArgvW; we apply standard Win32 escaping.
            var cmdLine = Win32CommandLine.Build(exePath, argList);

            // 4. Build the environment block. Win32 wants a sorted null-terminated
            //    list of VAR=VALUE entries followed by a final null.
            //    CREATE_UNICODE_ENVIRONMENT is required when we pass UTF-16.
            var envPtr = BuildEnvironmentBlock(envBlock);

            // 5. STARTUPINFOEX wraps the regular STARTUPINFO and adds the attribute
            //    list. STARTF_USESTDHANDLES makes CreateProcess use our handles.
            var siEx = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = (uint)Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = wantStdin ? stdinRead : nullHandle,
                    hStdOutput = stdoutWrite,
                    hStdError = stderrWrite
                },
                lpAttributeList = lpAttributeList
            };

            // 6. CreateProcessW. EXTENDED_STARTUPINFO_PRESENT tells CreateProcess to
            //    honour the attribute list. bInheritHandles=TRUE is REQUIRED for the
            //    handle-list attribute to take effect; with the attribute list it
            //    only inherits the handles we listed.
            const uint creationFlags = CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW;
            if (!CreateProcessW(
                    null,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    creationFlags,
                    envPtr,
                    cwd,
                    ref siEx,
                    out pi))
                ThrowLastError($"CreateProcessW({exePath})");

            // We don't need the thread handle. Zero it so the failure cleanup below
            // never double-closes it.
            CloseHandle(pi.hThread); pi.hThread = IntPtr.Zero;

            // Close the child-side pipe ends in the parent so EOF propagates when the
            // child closes its end; zero each so the finally treats it as handled.
            CloseHandle(stdoutWrite); stdoutWrite = IntPtr.Zero;
            CloseHandle(stderrWrite); stderrWrite = IntPtr.Zero;
            if (wantStdin) { CloseHandle(stdinRead); stdinRead = IntPtr.Zero; }
            else if (nullHandle != IntPtr.Zero) { CloseHandle(nullHandle); nullHandle = IntPtr.Zero; }

            // Wrap the parent-side pipe ends as FileStreams (they now own the handle);
            // zero each so the finally does not close a handle a FileStream owns.
            var stdoutStream = new FileStream(new SafeFileHandle(stdoutRead, ownsHandle: true), FileAccess.Read); stdoutRead = IntPtr.Zero;
            var stderrStream = new FileStream(new SafeFileHandle(stderrRead, ownsHandle: true), FileAccess.Read); stderrRead = IntPtr.Zero;
            FileStream? stdinStream = null;
            if (wantStdin)
            {
                stdinStream = new FileStream(new SafeFileHandle(stdinWrite, ownsHandle: true), FileAccess.Write);
                stdinWrite = IntPtr.Zero;
            }

            // Wrap the process by PID for HasExited / WaitForExitAsync; the raw
            // handle stays with our PROCESS_INFORMATION until Kill closes it.
            var managed = Process.GetProcessById((int)pi.dwProcessId);
            var rawHandle = pi.hProcess;
            var rawPid = (int)pi.dwProcessId;
            Action killTree = () =>
            {
                try { TerminateProcessTree(rawPid, logger); }
                catch (Exception ex) { logger?.LogDebug(ex, "WindowsHandleScrubSpawner: best-effort kill failed"); }
                finally { CloseHandle(rawHandle); }
            };

            success = true; // the process handle is now owned by killTree
            return new Result(managed, stdinStream, stdoutStream, stderrStream, killTree);
        }
        finally
        {
            if (lpAttributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(lpAttributeList);
                Marshal.FreeHGlobal(lpAttributeList);
            }
            if (handleListPtr != IntPtr.Zero) Marshal.FreeHGlobal(handleListPtr);

            if (!success)
            {
                // Spawn failed partway. Close every kernel handle we created that was
                // not transferred to a FileStream / killTree (those were zeroed above).
                foreach (var h in new[] { stdoutRead, stdoutWrite, stderrRead, stderrWrite, stdinRead, stdinWrite, nullHandle, pi.hProcess, pi.hThread })
                    if (h != IntPtr.Zero && h != INVALID_HANDLE_VALUE) CloseHandle(h);
            }
        }
    }

    /// <summary>Best-effort kill of the spawned process and its children via taskkill /T /F.</summary>
    private static void TerminateProcessTree(int pid, ILogger? logger)
    {
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {pid} /T /F",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            killer?.WaitForExit(2000);
        }
        catch (Exception ex) { logger?.LogDebug(ex, "WindowsHandleScrubSpawner: taskkill failed"); }
    }

    /// <summary>Convert a dictionary to a Win32 Unicode environment block (sorted, null-terminated, double-null-terminated).</summary>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> env)
    {
        // CreateProcess wants entries sorted by key (case-insensitive on Windows)
        // followed by an empty string. Each entry is "KEY=VALUE\0", terminated by
        // an additional "\0" at the end.
        var sb = new StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            sb.Append(kv.Key).Append('=').Append(kv.Value ?? string.Empty).Append('\0');
        }
        sb.Append('\0');
        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
        // Note: we do NOT free this; CreateProcess copies it. (Per docs.)
    }

    private static void ThrowLastError(string what)
    {
        var err = Marshal.GetLastWin32Error();
        throw new System.ComponentModel.Win32Exception(err, $"{what} failed: Win32 error {err}");
    }

    // ── Win32 P/Invoke surface ──────────────────────────────────────────

    private const int HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const ulong PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, uint dwFlags, ref UIntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, UIntPtr Attribute, IntPtr lpValue, UIntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
