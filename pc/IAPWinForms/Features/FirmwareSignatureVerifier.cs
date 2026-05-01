using System.Security.Cryptography;

namespace IAPWinForms.Features;

public interface IFirmwareSignatureVerifier
{
    bool Verify(byte[] firmwareData, byte[] signature, string hashAlgorithm = "SHA256");
    bool Verify(byte[] firmwareData, byte[] signature, FirmwareSignatureVerificationOptions? options);
}

public sealed class FirmwareSignatureVerificationOptions
{
    public string SignatureAlgorithm { get; init; } = "RSA";
    public string HashAlgorithm { get; init; } = "SHA256";
    public string? PublicKeyPem { get; init; }
    public string? PublicKeyPath { get; init; }
}

internal sealed class FirmwareSignatureVerifier : IFirmwareSignatureVerifier
{
    private const string EmbeddedPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA1t9CF5DeLK+zxJvfkZw3
+oSpx5XyVDO7UARDBk6w8PdqS2CFBpLmC1+kRh2J9okckofElfFZBJ+C0hAxk+8t
CGEl00KWj1+LBEVAr3i/rU52/dH0reOiG+UH+/uGV4GmKaxH00k35haNrAREalfJ
kq9TL2IF+oHXMkPn2CeQVZAHULz7m3cVwuQo1LC7+T3kuO0ygrQPz0M2fUOCOs6Z
0fmEAR3XzxV6QVHvpbUPeHbM3Fn7odySlKTHCfs5O833A4/ZWPPEIBsZs7Tbz/Qi
S01FvGBJOlkdBR4A/+ItgprdeXJwzLBOoAuWiulP5ORiyhzQ94bRBF/c3Bikf1jC
awIDAQAB
-----END PUBLIC KEY-----
""";

    public bool Verify(byte[] firmwareData, byte[] signature, string hashAlgorithm = "SHA256")
    {
        return Verify(firmwareData, signature, new FirmwareSignatureVerificationOptions
        {
            HashAlgorithm = hashAlgorithm
        });
    }

    public bool Verify(byte[] firmwareData, byte[] signature, FirmwareSignatureVerificationOptions? options)
    {
        ArgumentNullException.ThrowIfNull(firmwareData);
        ArgumentNullException.ThrowIfNull(signature);
        if (firmwareData.Length == 0 || signature.Length == 0)
        {
            return false;
        }

        FirmwareSignatureVerificationOptions effectiveOptions = options ?? new FirmwareSignatureVerificationOptions();
        string publicKeyPem = LoadPublicKeyPem(effectiveOptions);

        return effectiveOptions.SignatureAlgorithm.ToUpperInvariant() switch
        {
            "RSA" => VerifyRsa(firmwareData, signature, publicKeyPem, effectiveOptions.HashAlgorithm),
            "ECDSA" => VerifyEcdsa(firmwareData, signature, publicKeyPem, effectiveOptions.HashAlgorithm),
            _ => throw new FirmwareValidationException($"Unsupported signature algorithm: {effectiveOptions.SignatureAlgorithm}")
        };
    }

    private static HashAlgorithmName ResolveHashAlgorithm(string? hashAlgorithm)
    {
        return hashAlgorithm?.ToUpperInvariant() switch
        {
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA512" => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256
        };
    }

    private static bool VerifyRsa(byte[] firmwareData, byte[] signature, string publicKeyPem, string? hashAlgorithm)
    {
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return rsa.VerifyData(
            firmwareData,
            signature,
            ResolveHashAlgorithm(hashAlgorithm),
            RSASignaturePadding.Pkcs1);
    }

    private static bool VerifyEcdsa(byte[] firmwareData, byte[] signature, string publicKeyPem, string? hashAlgorithm)
    {
        using ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        return ecdsa.VerifyData(
            firmwareData,
            signature,
            ResolveHashAlgorithm(hashAlgorithm));
    }

    private static string LoadPublicKeyPem(FirmwareSignatureVerificationOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicKeyPem))
        {
            return options.PublicKeyPem;
        }

        if (!string.IsNullOrWhiteSpace(options.PublicKeyPath))
        {
            if (!File.Exists(options.PublicKeyPath))
            {
                throw new FirmwareValidationException($"Public key file not found: {options.PublicKeyPath}");
            }

            return File.ReadAllText(options.PublicKeyPath);
        }

        return EmbeddedPublicKeyPem;
    }
}
