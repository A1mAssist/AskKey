using System.Security.Cryptography;

namespace AskKey.Guard.Tests;

public sealed class GuardSignatureTests
{
    [Fact]
    public void AcceptsPkcs1AndPss()
    {
        using var rsa = RSA.Create(2048);
        var data = RandomNumberGenerator.GetBytes(32);
        var publicKey = rsa.ExportSubjectPublicKeyInfo();

        Assert.True(GuardSignature.Verify(
            publicKey, data, rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
        Assert.True(GuardSignature.Verify(
            publicKey, data, rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    }

    [Fact]
    public void RejectsWrongDataSignatureAndKey()
    {
        using var signer = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var data = RandomNumberGenerator.GetBytes(32);
        var signature = signer.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var otherData = data.ToArray();
        otherData[0] ^= 0xFF;
        var tampered = signature.ToArray();
        tampered[0] ^= 0xFF;

        Assert.False(GuardSignature.Verify(signer.ExportSubjectPublicKeyInfo(), otherData, signature));
        Assert.False(GuardSignature.Verify(signer.ExportSubjectPublicKeyInfo(), data, tampered));
        Assert.False(GuardSignature.Verify(other.ExportSubjectPublicKeyInfo(), data, signature));
        Assert.False(GuardSignature.Verify("not a key"u8, data, signature));
    }
}
