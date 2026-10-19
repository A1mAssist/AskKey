using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace AskKey.Guard;

internal static class GuardProcess
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 4;
    private const int ErrorInsufficientBuffer = 122;

    public static bool TryGetClientProcessId(NamedPipeServerStream pipe, out uint processId)
        => GetNamedPipeClientProcessId(pipe.SafePipeHandle, out processId);

    public static bool TryGetServerProcessId(NamedPipeClientStream pipe, out uint processId)
        => GetNamedPipeServerProcessId(pipe.SafePipeHandle, out processId);

    /// <summary>
    /// A standard user cannot reliably open a LocalService process, but SCM status
    /// exposes its PID. Matching that PID prevents a local process from squatting
    /// the fixed pipe even when process-image queries are denied.
    /// </summary>
    public static bool TryGetServiceProcessId(string serviceName, out uint processId)
    {
        processId = 0;
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return false;
        var service = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        try
        {
            service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return false;
            var size = Marshal.SizeOf<ServiceStatusProcess>();
            buffer = Marshal.AllocHGlobal(size);
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, (uint)size, out _))
                return false;
            var status = Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
            if (status.CurrentState != ServiceRunning || status.ProcessId == 0) return false;
            processId = status.ProcessId;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    public static string? ImagePath(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var capacity = 1024u;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var path = new StringBuilder((int)capacity);
                var size = capacity;
                if (QueryFullProcessImageNameW(process, 0, path, ref size)) return path.ToString();
                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return null;
                capacity = size;
            }
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static string? UserSid(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (process == IntPtr.Zero) return null;
        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token)) return null;
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr processHandle, uint flags, StringBuilder executableName, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(
        string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(
        IntPtr serviceManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service, int infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }
}
