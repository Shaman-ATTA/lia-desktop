using System.Runtime.InteropServices;
using System.Text;

namespace LiaDesktop.Services;

/// <summary>Start a process outside the current KillOnClose job (CREATE_BREAKAWAY_FROM_JOB).</summary>
public static class DetachedProcess
{
    const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    const uint CREATE_NO_WINDOW = 0x08000000;

    public static bool Start(string fileName, string arguments, string? workingDirectory = null)
    {
        var cmd = new StringBuilder();
        cmd.Append('"').Append(fileName).Append('"');
        if (!string.IsNullOrWhiteSpace(arguments))
            cmd.Append(' ').Append(arguments);

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var pi = new PROCESS_INFORMATION();
        var dir = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        var ok = CreateProcessW(
            null,
            cmd,
            IntPtr.Zero,
            IntPtr.Zero,
            bInheritHandles: false,
            dwCreationFlags: CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: dir,
            ref si,
            out pi);
        if (!ok) return false;
        if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
        if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
        return true;
    }

    /// <summary>Run a .cmd/.bat after a short delay, fully detached from our job.</summary>
    public static bool StartDelayedCmd(string cmdPath, string? workingDirectory = null)
    {
        // cmd.exe /c "<wrapper>" — wrapper itself sleeps then runs setup
        return Start(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/c \"" + cmdPath + "\"",
            workingDirectory);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);
}
