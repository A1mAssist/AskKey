using global::System.Runtime.InteropServices;
using global::System.Security.Cryptography;
using global::System.Security.Cryptography.X509Certificates;

namespace AskKey.Integrations;

/// <summary>Signature facts for one PE file. A null subject means the file is unsigned
/// or its signature does not validate, so callers must treat it as untrusted.</summary>
public interface IAuthenticodeVerifier
{
    string? SignerSubject(string path);
}

/// <summary>
/// WinVerifyTrust + signer-certificate extraction. The chain is checked against the
/// machine's trust store; revocation lookups use the local cache only so a config
/// write never blocks on the network.
/// </summary>
public sealed class AuthenticodeVerifier : IAuthenticodeVerifier
{
    public static readonly AuthenticodeVerifier Shared = new();

    public string? SignerSubject(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (!TrustNative.Verify(path)) return null;
#pragma warning disable SYSLIB0057 // Signed-file extraction has no X509CertificateLoader equivalent.
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static class TrustNative
    {
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

        private static readonly Guid GenericVerifyV2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        private static extern int WinVerifyTrust(
            IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr data);

        public static bool Verify(string path)
        {
            var filePath = Marshal.StringToHGlobalUni(path);
            var fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            var data = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            try
            {
                Marshal.StructureToPtr(new WinTrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    pcwszFilePath = filePath,
                }, fileInfo, false);

                var trustData = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = fileInfo,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
                };
                Marshal.StructureToPtr(trustData, data, false);
                var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, data);
                // The API writes the state handle back into the struct; close it.
                trustData = Marshal.PtrToStructure<WinTrustData>(data);
                trustData.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(trustData, data, false);
                _ = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, data);
                return result == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(fileInfo);
                Marshal.FreeHGlobal(filePath);
            }
        }
    }
}
