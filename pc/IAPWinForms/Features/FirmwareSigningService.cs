using System.Security.Cryptography;

namespace IAPWinForms.Features;

public sealed class FirmwareSigningOptions
{
    public string SignatureAlgorithm { get; init; } = "RSA";
    public string HashAlgorithm { get; init; } = "SHA256";
    public string? PrivateKeyPem { get; init; }
    public string? PrivateKeyPath { get; init; }
    public OtaSecurityProfile? SecurityProfile { get; init; }
    public string ProductionPrivateKeyEnvironmentVariableName { get; init; } = OtaSecurityEnvironment.ProductionSigningPemEnvironmentVariable;
}

public interface IFirmwareSigningService
{
    byte[] Sign(byte[] firmwareData, string privateKeyPath, string hashAlgorithm = "SHA256");
    byte[] Sign(byte[] firmwareData, FirmwareSigningOptions? options);
}

public sealed class RsaFirmwareSigningService : IFirmwareSigningService
{
    private readonly ISigningKeyMaterialProvider _keyMaterialProvider;

    public RsaFirmwareSigningService()
        : this(new EnvironmentAwareSigningKeyMaterialProvider())
    {
    }

    internal RsaFirmwareSigningService(ISigningKeyMaterialProvider keyMaterialProvider)
    {
        _keyMaterialProvider = keyMaterialProvider;
    }

    public byte[] Sign(byte[] firmwareData, string privateKeyPath, string hashAlgorithm = "SHA256")
    {
        return Sign(firmwareData, new FirmwareSigningOptions
        {
            PrivateKeyPath = privateKeyPath,
            HashAlgorithm = hashAlgorithm
        });
    }

    public byte[] Sign(byte[] firmwareData, FirmwareSigningOptions? options)
    {
        ArgumentNullException.ThrowIfNull(firmwareData);
        if (firmwareData.Length == 0)
        {
            throw new ArgumentException("Firmware data cannot be empty.", nameof(firmwareData));
        }

        FirmwareSigningOptions effectiveOptions = options ?? new FirmwareSigningOptions();
        if (!string.Equals(effectiveOptions.SignatureAlgorithm, "RSA", StringComparison.OrdinalIgnoreCase))
        {
            throw new FirmwareValidationException(
                $"Unsupported signing algorithm: {effectiveOptions.SignatureAlgorithm}");
        }

        string privateKeyPem = _keyMaterialProvider.ResolvePrivateKey(effectiveOptions).PrivateKeyPem;
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return rsa.SignData(
            firmwareData,
            ResolveHashAlgorithm(effectiveOptions.HashAlgorithm),
            RSASignaturePadding.Pkcs1);
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
}
