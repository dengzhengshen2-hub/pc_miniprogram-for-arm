using System.Globalization;

namespace IAPWinForms.Features;

public enum OtaSecurityProfile
{
    Development = 0,
    Production = 1
}

public static class OtaSecurityEnvironment
{
    public const string SecurityProfileEnvironmentVariable = "IAP_SECURITY_PROFILE";
    public const string ProductionSigningPemEnvironmentVariable = "IAP_SIGNING_PRIVATE_KEY_PEM";
    public const string ProductionAesKeyEnvironmentVariable = "IAP_AES_KEY_HEX";

    public static OtaSecurityProfile ResolveProfile(OtaSecurityProfile? requestedProfile)
    {
        if (requestedProfile.HasValue)
        {
            return requestedProfile.Value;
        }

        string? raw = Environment.GetEnvironmentVariable(SecurityProfileEnvironmentVariable);
        string normalized = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "production", StringComparison.OrdinalIgnoreCase))
        {
            return OtaSecurityProfile.Production;
        }

        if (string.Equals(normalized, "development", StringComparison.OrdinalIgnoreCase))
        {
            return OtaSecurityProfile.Development;
        }

        throw new InvalidOperationException(
            $"Unsupported {SecurityProfileEnvironmentVariable} value: {normalized}. Expected development or production.");
    }
}

public sealed record SigningKeyMaterial(
    string PrivateKeyPem,
    OtaSecurityProfile SecurityProfile,
    string SourceDescription);

public sealed record OtaAesKeyMaterial(
    byte[] KeyBytes,
    string KeyId,
    OtaSecurityProfile SecurityProfile,
    string DisplayText);

public interface ISigningKeyMaterialProvider
{
    SigningKeyMaterial ResolvePrivateKey(FirmwareSigningOptions options);
}

public interface IOtaAesKeyProvider
{
    OtaAesKeyMaterial ResolveKeyMaterial(OtaSecurityProfile? requestedProfile = null);
}

internal sealed class EnvironmentAwareSigningKeyMaterialProvider : ISigningKeyMaterialProvider
{
    public SigningKeyMaterial ResolvePrivateKey(FirmwareSigningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        OtaSecurityProfile profile = OtaSecurityEnvironment.ResolveProfile(options.SecurityProfile);
        return profile switch
        {
            OtaSecurityProfile.Development => ResolveDevelopmentPrivateKey(options),
            OtaSecurityProfile.Production => ResolveProductionPrivateKey(options),
            _ => throw new InvalidOperationException($"Unsupported security profile: {profile}.")
        };
    }

    private static SigningKeyMaterial ResolveDevelopmentPrivateKey(FirmwareSigningOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPem))
        {
            return new SigningKeyMaterial(
                options.PrivateKeyPem,
                OtaSecurityProfile.Development,
                "development:inline-pem");
        }

        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            if (!File.Exists(options.PrivateKeyPath))
            {
                throw new FileNotFoundException("Private key file not found.", options.PrivateKeyPath);
            }

            return new SigningKeyMaterial(
                File.ReadAllText(options.PrivateKeyPath),
                OtaSecurityProfile.Development,
                $"development:file:{Path.GetFileName(options.PrivateKeyPath)}");
        }

        throw new ArgumentException(
            "Development signing requires a PEM private key or private key path.");
    }

    private static SigningKeyMaterial ResolveProductionPrivateKey(FirmwareSigningOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPem) ||
            !string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            throw new InvalidOperationException(
                "Production signing profile does not allow local PEM strings or private key files. " +
                $"Provide the key material through {options.ProductionPrivateKeyEnvironmentVariableName} or a custom ISigningKeyMaterialProvider.");
        }

        string environmentVariableName = string.IsNullOrWhiteSpace(options.ProductionPrivateKeyEnvironmentVariableName)
            ? OtaSecurityEnvironment.ProductionSigningPemEnvironmentVariable
            : options.ProductionPrivateKeyEnvironmentVariableName;
        string? privateKeyPem = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new InvalidOperationException(
                $"Production signing key is not available. Expected environment variable: {environmentVariableName}.");
        }

        return new SigningKeyMaterial(
            privateKeyPem,
            OtaSecurityProfile.Production,
            $"production:env:{environmentVariableName}");
    }
}

internal sealed class EnvironmentAwareOtaAesKeyProvider : IOtaAesKeyProvider
{
    private static readonly byte[] DevelopmentKey =
    [
        0x9D, 0xD2, 0x00, 0x24, 0x84, 0x60, 0x2E, 0xDA,
        0x0C, 0xDD, 0x52, 0x7B, 0x05, 0xC1, 0x6B, 0x01,
        0xFF, 0x17, 0xCD, 0x6F, 0x8C, 0x1E, 0x3E, 0x09,
        0xCF, 0x1F, 0x0C, 0x78, 0x87, 0xEF, 0x8A, 0xEC
    ];

    public OtaAesKeyMaterial ResolveKeyMaterial(OtaSecurityProfile? requestedProfile = null)
    {
        OtaSecurityProfile profile = OtaSecurityEnvironment.ResolveProfile(requestedProfile);
        return profile switch
        {
            OtaSecurityProfile.Development => new OtaAesKeyMaterial(
                [.. DevelopmentKey],
                "dev-fixed-aes256",
                OtaSecurityProfile.Development,
                Convert.ToHexString(DevelopmentKey)),
            OtaSecurityProfile.Production => ResolveProductionKeyMaterial(),
            _ => throw new InvalidOperationException($"Unsupported security profile: {profile}.")
        };
    }

    private static OtaAesKeyMaterial ResolveProductionKeyMaterial()
    {
        string? keyHex = Environment.GetEnvironmentVariable(OtaSecurityEnvironment.ProductionAesKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(keyHex))
        {
            throw new InvalidOperationException(
                $"Production AES key is not available. Expected environment variable: {OtaSecurityEnvironment.ProductionAesKeyEnvironmentVariable}.");
        }

        string normalizedHex = NormalizeHex(keyHex, 64);
        byte[] keyBytes = Convert.FromHexString(normalizedHex);
        string maskedTail = normalizedHex[^8..];

        return new OtaAesKeyMaterial(
            keyBytes,
            "prod-env-aes256",
            OtaSecurityProfile.Production,
            $"[hidden via env, tail={maskedTail}]");
    }

    private static string NormalizeHex(string hex, int expectedLength)
    {
        string normalized = hex
            .Trim()
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (normalized.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"AES key must be {expectedLength / 2} bytes ({expectedLength} hex chars), actual={normalized.Length.ToString(CultureInfo.InvariantCulture)}.");
        }

        _ = Convert.FromHexString(normalized);
        return normalized;
    }
}
