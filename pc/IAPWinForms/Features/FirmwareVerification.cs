using System.Text.Json;

namespace IAPWinForms.Features;

public sealed class FirmwareVerificationOptions
{
    public string? ManifestPath { get; init; }
    public bool RequireManifest { get; init; }
    public bool RequireSignature { get; init; }
    public bool DeleteOutputOnFailure { get; init; } = true;
    public string? ExpectedCrc32 { get; init; }
    public string? SignatureBase64 { get; init; }
    public string? SignatureFilePath { get; init; }
    public string HashAlgorithm { get; init; } = "SHA256";
    public string SignatureAlgorithm { get; init; } = "RSA";
    public string? PublicKeyPem { get; init; }
    public string? PublicKeyPath { get; init; }
}

public sealed class FirmwareVerificationManifest
{
    public string ExpectedCrc32 { get; set; } = string.Empty;
    public string SignatureBase64 { get; set; } = string.Empty;
    public string HashAlgorithm { get; set; } = "SHA256";
    public string SignatureAlgorithm { get; set; } = "RSA";
}

public sealed class FirmwareVerificationResult
{
    public bool VerificationPerformed { get; init; }
    public bool Success { get; init; }
    public string ActualCrc32Hex { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? ManifestPath { get; init; }
}

public sealed class FirmwareValidationException : Exception
{
    public FirmwareValidationException(string message) : base(message)
    {
    }
}

public interface IFirmwareVerificationService
{
    FirmwareVerificationResult Validate(byte[] firmwareData, string outputPath, FirmwareVerificationOptions? options = null);
    string? ResolveManifestPath(string outputPath, FirmwareVerificationOptions? options = null);
}

internal sealed class FirmwareVerificationService : IFirmwareVerificationService
{
    private const string CompanionManifestSuffix = ".verify.json";

    private readonly IFirmwareSignatureVerifier _signatureVerifier;

    public FirmwareVerificationService()
        : this(new FirmwareSignatureVerifier())
    {
    }

    public FirmwareVerificationService(IFirmwareSignatureVerifier signatureVerifier)
    {
        _signatureVerifier = signatureVerifier;
    }

    public FirmwareVerificationResult Validate(byte[] firmwareData, string outputPath, FirmwareVerificationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(firmwareData);
        if (firmwareData.Length == 0)
        {
            throw new FirmwareValidationException("Received firmware is empty.");
        }

        string actualCrc32Hex = Crc32Utility.ComputeHex(firmwareData);
        string? manifestPath = ResolveManifestPath(outputPath, options);
        FirmwareVerificationManifest? manifest = null;
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            if (!File.Exists(manifestPath))
            {
                throw new FirmwareValidationException($"Verification manifest not found: {manifestPath}");
            }

            manifest = LoadManifest(manifestPath);
        }

        if (manifest == null && options?.RequireManifest == true)
        {
            throw new FirmwareValidationException("Verification manifest is required but was not found.");
        }

        string expectedCrc32Hex = ResolveExpectedCrc32(options, manifest);
        byte[]? signatureBytes = ResolveSignatureBytes(options, manifest);
        string signatureAlgorithm = ResolveSignatureAlgorithm(options, manifest);
        string hashAlgorithm = ResolveHashAlgorithm(options, manifest);

        bool hasExpectedCrc = expectedCrc32Hex.Length == 8;
        bool hasSignature = signatureBytes is { Length: > 0 };

        if (!hasExpectedCrc && !hasSignature)
        {
            return new FirmwareVerificationResult
            {
                VerificationPerformed = false,
                Success = true,
                ActualCrc32Hex = actualCrc32Hex,
                ManifestPath = manifestPath,
                Message = $"YModem receive completed. Verification skipped. CRC32={actualCrc32Hex}."
            };
        }

        if (hasExpectedCrc && !string.Equals(expectedCrc32Hex, actualCrc32Hex, StringComparison.OrdinalIgnoreCase))
        {
            throw new FirmwareValidationException(
                $"CRC32 mismatch. Expected={expectedCrc32Hex}, Actual={actualCrc32Hex}.");
        }

        if (!hasSignature)
        {
            if (options?.RequireSignature == true)
            {
                throw new FirmwareValidationException("Firmware signature is required but was not provided.");
            }

            return new FirmwareVerificationResult
            {
                VerificationPerformed = hasExpectedCrc,
                Success = true,
                ActualCrc32Hex = actualCrc32Hex,
                ManifestPath = manifestPath,
                Message = $"YModem receive completed. CRC32={actualCrc32Hex}, signature verification skipped."
            };
        }

        FirmwareSignatureVerificationOptions signatureOptions = new()
        {
            SignatureAlgorithm = signatureAlgorithm,
            HashAlgorithm = hashAlgorithm,
            PublicKeyPem = options?.PublicKeyPem,
            PublicKeyPath = options?.PublicKeyPath
        };
        bool verified = _signatureVerifier.Verify(firmwareData, signatureBytes!, signatureOptions);
        if (!verified)
        {
            throw new FirmwareValidationException("Signature verification failed.");
        }

        return new FirmwareVerificationResult
        {
            VerificationPerformed = true,
            Success = true,
            ActualCrc32Hex = actualCrc32Hex,
            ManifestPath = manifestPath,
            Message = $"YModem receive completed. CRC32={actualCrc32Hex}, signature verification passed."
        };
    }

    public string? ResolveManifestPath(string outputPath, FirmwareVerificationOptions? options = null)
    {
        if (!string.IsNullOrWhiteSpace(options?.ManifestPath))
        {
            return options.ManifestPath;
        }

        string companionPath = outputPath + CompanionManifestSuffix;
        return File.Exists(companionPath) ? companionPath : null;
    }

    private static FirmwareVerificationManifest LoadManifest(string manifestPath)
    {
        string json = File.ReadAllText(manifestPath);
        FirmwareVerificationManifest? manifest = JsonSerializer.Deserialize<FirmwareVerificationManifest>(json);
        if (manifest == null)
        {
            throw new FirmwareValidationException("Verification manifest is empty or invalid.");
        }

        return manifest;
    }

    private static string NormalizeCrc32(string? crc32)
    {
        if (string.IsNullOrWhiteSpace(crc32))
        {
            return string.Empty;
        }

        return crc32
            .Trim()
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static string ResolveExpectedCrc32(FirmwareVerificationOptions? options, FirmwareVerificationManifest? manifest)
    {
        string expectedCrc32Hex = NormalizeCrc32(options?.ExpectedCrc32);
        if (expectedCrc32Hex.Length == 0 && manifest != null)
        {
            expectedCrc32Hex = NormalizeCrc32(manifest.ExpectedCrc32);
        }

        if (expectedCrc32Hex.Length != 0 && expectedCrc32Hex.Length != 8)
        {
            throw new FirmwareValidationException("Expected CRC32 is missing or invalid.");
        }

        return expectedCrc32Hex;
    }

    private static string ResolveSignatureAlgorithm(FirmwareVerificationOptions? options, FirmwareVerificationManifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(options?.SignatureAlgorithm))
        {
            return options.SignatureAlgorithm;
        }

        if (!string.IsNullOrWhiteSpace(manifest?.SignatureAlgorithm))
        {
            return manifest.SignatureAlgorithm;
        }

        return "RSA";
    }

    private static string ResolveHashAlgorithm(FirmwareVerificationOptions? options, FirmwareVerificationManifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(options?.HashAlgorithm))
        {
            return options.HashAlgorithm;
        }

        if (!string.IsNullOrWhiteSpace(manifest?.HashAlgorithm))
        {
            return manifest.HashAlgorithm;
        }

        return "SHA256";
    }

    private static byte[]? ResolveSignatureBytes(FirmwareVerificationOptions? options, FirmwareVerificationManifest? manifest)
    {
        string? signatureBase64 = options?.SignatureBase64;
        if (string.IsNullOrWhiteSpace(signatureBase64) && manifest != null)
        {
            signatureBase64 = manifest.SignatureBase64;
        }

        if (!string.IsNullOrWhiteSpace(signatureBase64))
        {
            try
            {
                return Convert.FromBase64String(signatureBase64.Trim());
            }
            catch (FormatException ex)
            {
                throw new FirmwareValidationException($"SignatureBase64 is invalid: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(options?.SignatureFilePath))
        {
            if (!File.Exists(options.SignatureFilePath))
            {
                throw new FirmwareValidationException($"Signature file not found: {options.SignatureFilePath}");
            }

            return File.ReadAllBytes(options.SignatureFilePath);
        }

        return null;
    }
}
