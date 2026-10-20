using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AskKey.Guard;

/// <summary>
/// Creates the service pipe with PIPE_REJECT_REMOTE_CLIENTS. .NET's PipeOptions
/// does not expose that Windows flag, so this is the one Win32 call needed.
/// </summary>
internal static class GuardPipe
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint BufferSize = 4096;
    private const uint NoTimeout = 0;

    public static NamedPipeServerStream Create(string pipeName, int maxInstances, PipeSecurity security)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        SafePipeHandle? handle = null;
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            handle = CreateNamedPipe(
                @"\\.\pipe\" + pipeName,
                PipeAccessDuplex | FileFlagOverlapped,
                PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                (uint)maxInstances,
                BufferSize,
                BufferSize,
                NoTimeout,
                ref attributes);
            if (handle.IsInvalid)
                throw new IOException($"CreateNamedPipe failed: {Marshal.GetLastWin32Error()}.");

            var stream = new NamedPipeServerStream(
                PipeDirection.InOut, isAsync: true, isConnected: false, handle);
            handle = null;
            return stream;
        }
        finally
        {
            handle?.Dispose();
            pinned.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);
}
