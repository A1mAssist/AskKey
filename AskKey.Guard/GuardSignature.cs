using System.Security.Cryptography;

namespace AskKey.Guard;

public static class GuardSignature
{
    /// <summary>
    /// Verifies a DER SubjectPublicKeyInfo key over the raw nonce. Windows Hello
    /// emits PKCS#1 v1.5 today; PSS is accepted for compatible providers.
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> publicKeyDer,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signature)
    {
        if (publicKeyDer.IsEmpty || data.IsEmpty || signature.IsEmpty) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKeyDer, out var read);
            if (read != publicKeyDer.Length) return false;
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                || rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
