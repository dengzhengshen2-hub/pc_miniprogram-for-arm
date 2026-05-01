using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IAPWinForms.Features;

public sealed class IapPackageException : Exception
{
    public IapPackageException(string message) : base(message)
    {
    }
}

public sealed class IapPackageManifest
{
    [JsonPropertyName("packageFormatVersion")]
    public int PackageFormatVersion { get; set; } = 3;

    [JsonPropertyName("firmwareFileName")]
    public string FirmwareFileName { get; set; } = string.Empty;

    [JsonPropertyName("firmwareSize")]
    public long FirmwareSize { get; set; }

    [JsonPropertyName("firmwareCrc32")]
    public string FirmwareCrc32 { get; set; } = string.Empty;

    [JsonPropertyName("firmwareSha256")]
    public string FirmwareSha256 { get; set; } = string.Empty;

    [JsonPropertyName("signatureAlgorithm")]
    public string SignatureAlgorithm { get; set; } = "RSA";

    [JsonPropertyName("hashAlgorithm")]
    public string HashAlgorithm { get; set; } = "SHA256";

    [JsonPropertyName("signatureBase64")]
    public string SignatureBase64 { get; set; } = string.Empty;

    [JsonPropertyName("requiresEncryption")]
    public bool RequiresEncryption { get; set; } = true;

    [JsonPropertyName("encryptionAlgorithm")]
    public string EncryptionAlgorithm { get; set; } = string.Empty;

    [JsonPropertyName("encryptionKeyId")]
    public string EncryptionKeyId { get; set; } = string.Empty;

    [JsonPropertyName("encryptionIvHex")]
    public string EncryptionIvHex { get; set; } = string.Empty;

    [JsonPropertyName("transferEncoding")]
    public string TransferEncoding { get; set; } = "RAW_CTR";

    [JsonPropertyName("manifestSignatureBase64")]
    public string ManifestSignatureBase64 { get; set; } = string.Empty;
}

public sealed class IapPackageBuildRequest
{
    public string FirmwarePath { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
    public string? PrivateKeyPem { get; init; }
    public string OutputPackagePath { get; init; } = string.Empty;
    public string SignatureAlgorithm { get; init; } = "RSA";
    public string HashAlgorithm { get; init; } = "SHA256";
    public OtaSecurityProfile? SecurityProfile { get; init; }
    public string? VerificationPublicKeyPem { get; init; }
    public string? VerificationPublicKeyPath { get; init; }
    public string FirmwareVersion { get; init; } = "0.0.0";
    public string MinAllowedVersion { get; init; } = "0.0.0";
    public OtaTargetSlot? TargetSlot { get; init; }
}

public sealed class IapPackageBuildResult
{
    public string FirmwarePath { get; init; } = string.Empty;
    public string OutputPackagePath { get; init; } = string.Empty;
    public long FirmwareSize { get; init; }
    public string FirmwareCrc32 { get; init; } = string.Empty;
    public string SignatureAlgorithm { get; init; } = string.Empty;
    public string HashAlgorithm { get; init; } = string.Empty;
    public IapPackageManifest Manifest { get; init; } = new();
    public byte[] ImageHeaderBytes { get; init; } = [];
    public OtaImageHeaderInfo ImageHeader { get; init; } = new();
}

public sealed class IapPackageContent
{
    public string PackagePath { get; init; } = string.Empty;
    public byte[] FirmwareBytes { get; init; } = [];
    public IapPackageManifest Manifest { get; init; } = new();
    public byte[] ImageHeaderBytes { get; init; } = [];
    public OtaImageHeaderInfo? ImageHeader { get; init; }
}

public interface IIapPackageService
{
    string BuildDefaultPackagePath(string firmwarePath);
    IapPackageBuildResult CreatePackage(IapPackageBuildRequest request);
    IapPackageContent LoadPackage(string packagePath);
}

public sealed class IapPackageService : IIapPackageService
{
    private const string FirmwareEntryName = "firmware.bin";
    private const string ManifestEntryName = "manifest.json";
    private const string ImageHeaderEntryName = "image-header.bin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private const string LegacyZeroIvHex = "00000000000000000000000000000000";
    private const string LegacyTransferEncoding = "IV_PREFIXED";

    private readonly IFirmwareSigningService _signingService;
    private readonly IFirmwareSignatureVerifier _signatureVerifier;
    private readonly IFirmwareEncryptionService _encryptionService;

    public IapPackageService()
        : this(new RsaFirmwareSigningService(), new FirmwareSignatureVerifier(), new FirmwareEncryptionService())
    {
    }

    public IapPackageService(
        IFirmwareSigningService signingService,
        IFirmwareSignatureVerifier signatureVerifier,
        IFirmwareEncryptionService encryptionService)
    {
        _signingService = signingService;
        _signatureVerifier = signatureVerifier;
        _encryptionService = encryptionService;
    }

    public string BuildDefaultPackagePath(string firmwarePath)
    {
        if (string.IsNullOrWhiteSpace(firmwarePath))
        {
            return string.Empty;
        }

        string directory = Path.GetDirectoryName(firmwarePath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(firmwarePath);
        return Path.Combine(directory, $"{name}.iap");
    }

    public IapPackageBuildResult CreatePackage(IapPackageBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string firmwarePath = GetRequiredExistingPath(request.FirmwarePath, "Firmware");
        string outputPackagePath = GetRequiredOutputPath(request.OutputPackagePath);
        string firmwareFullPath = Path.GetFullPath(firmwarePath);
        string outputFullPath = Path.GetFullPath(outputPackagePath);

        if (string.Equals(firmwareFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IapPackageException("Output package path cannot be the same as the firmware path.");
        }

        OtaImageHeaderCodec.ValidateVersionText(request.FirmwareVersion, nameof(request.FirmwareVersion));
        OtaImageHeaderCodec.ValidateVersionText(request.MinAllowedVersion, nameof(request.MinAllowedVersion));

        byte[] firmwareBytes = File.ReadAllBytes(firmwareFullPath);
        if (firmwareBytes.Length == 0)
        {
            throw new IapPackageException("Firmware file is empty.");
        }

        OtaTargetSlot targetSlot = request.TargetSlot ?? OtaImageHeaderCodec.InferTargetSlot(firmwareFullPath, outputFullPath);
        EmbeddedFirmwareVersionInfo embeddedVersion = EmbeddedFirmwareVersionReader.ExtractAndValidate(
            firmwareBytes,
            targetSlot,
            request.FirmwareVersion);

        byte[] firmwareSignature = _signingService.Sign(firmwareBytes, new FirmwareSigningOptions
        {
            SignatureAlgorithm = request.SignatureAlgorithm,
            HashAlgorithm = request.HashAlgorithm,
            PrivateKeyPem = request.PrivateKeyPem,
            PrivateKeyPath = request.PrivateKeyPath,
            SecurityProfile = request.SecurityProfile
        });

        bool firmwareVerified = _signatureVerifier.Verify(firmwareBytes, firmwareSignature, new FirmwareSignatureVerificationOptions
        {
            SignatureAlgorithm = request.SignatureAlgorithm,
            HashAlgorithm = request.HashAlgorithm,
            PublicKeyPem = request.VerificationPublicKeyPem,
            PublicKeyPath = request.VerificationPublicKeyPath
        });
        if (!firmwareVerified)
        {
            throw new IapPackageException(
                "Package signing self-verification failed. The selected private key does not match the configured public key.");
        }

        string firmwareCrc32 = Crc32Utility.ComputeHex(firmwareBytes);
        string firmwareSha256 = ComputeSha256Hex(firmwareBytes);
        string encryptionIvHex = _encryptionService.CreateRandomIvHex();
        string encryptionKeyId = _encryptionService.ResolveKeyId(request.SecurityProfile);

        IapPackageManifest manifest = new()
        {
            PackageFormatVersion = 3,
            FirmwareFileName = Path.GetFileName(firmwareFullPath),
            FirmwareSize = firmwareBytes.LongLength,
            FirmwareCrc32 = firmwareCrc32,
            FirmwareSha256 = firmwareSha256,
            SignatureAlgorithm = request.SignatureAlgorithm.ToUpperInvariant(),
            HashAlgorithm = request.HashAlgorithm.ToUpperInvariant(),
            SignatureBase64 = Convert.ToBase64String(firmwareSignature),
            RequiresEncryption = true,
            EncryptionAlgorithm = _encryptionService.AlgorithmName,
            EncryptionKeyId = encryptionKeyId,
            EncryptionIvHex = encryptionIvHex,
            TransferEncoding = "RAW_CTR"
        };

        byte[] imageHeaderPayload = OtaImageHeaderCodec.BuildPayload(
            targetSlot,
            embeddedVersion.Version,
            firmwareBytes.Length,
            Convert.FromHexString(firmwareSha256),
            Convert.FromHexString(encryptionIvHex),
            request.MinAllowedVersion);

        byte[] imageHeaderSignature = _signingService.Sign(imageHeaderPayload, new FirmwareSigningOptions
        {
            SignatureAlgorithm = request.SignatureAlgorithm,
            HashAlgorithm = request.HashAlgorithm,
            PrivateKeyPem = request.PrivateKeyPem,
            PrivateKeyPath = request.PrivateKeyPath,
            SecurityProfile = request.SecurityProfile
        });

        bool imageHeaderVerified = _signatureVerifier.Verify(imageHeaderPayload, imageHeaderSignature, new FirmwareSignatureVerificationOptions
        {
            SignatureAlgorithm = request.SignatureAlgorithm,
            HashAlgorithm = request.HashAlgorithm,
            PublicKeyPem = request.VerificationPublicKeyPem,
            PublicKeyPath = request.VerificationPublicKeyPath
        });
        if (!imageHeaderVerified)
        {
            throw new IapPackageException("image-header signing self-verification failed.");
        }

        byte[] imageHeaderBytes = OtaImageHeaderCodec.BuildBinary(imageHeaderPayload, imageHeaderSignature);
        OtaImageHeaderInfo imageHeader = OtaImageHeaderCodec.Parse(imageHeaderBytes);
        OtaImageHeaderCodec.VerifyManifestConsistency(imageHeader, manifest, firmwareBytes);

        byte[] manifestPayload = ManifestSigningCanonicalizer.BuildIapPackageManifestPayload(manifest);
        byte[] manifestSignature = _signingService.Sign(manifestPayload, new FirmwareSigningOptions
        {
            SignatureAlgorithm = manifest.SignatureAlgorithm,
            HashAlgorithm = manifest.HashAlgorithm,
            PrivateKeyPem = request.PrivateKeyPem,
            PrivateKeyPath = request.PrivateKeyPath,
            SecurityProfile = request.SecurityProfile
        });
        manifest.ManifestSignatureBase64 = Convert.ToBase64String(manifestSignature);

        bool manifestVerified = _signatureVerifier.Verify(
            ManifestSigningCanonicalizer.BuildIapPackageManifestPayload(manifest),
            manifestSignature,
            new FirmwareSignatureVerificationOptions
            {
                SignatureAlgorithm = request.SignatureAlgorithm,
                HashAlgorithm = request.HashAlgorithm,
                PublicKeyPem = request.VerificationPublicKeyPem,
                PublicKeyPath = request.VerificationPublicKeyPath
            });
        if (!manifestVerified)
        {
            throw new IapPackageException(
                "Manifest signing self-verification failed. The selected private key does not match the configured public key.");
        }

        string? outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using FileStream stream = new(outputFullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        WriteEntry(archive, FirmwareEntryName, firmwareBytes);
        WriteEntry(archive, ImageHeaderEntryName, imageHeaderBytes);
        WriteEntry(archive, ManifestEntryName, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));

        return new IapPackageBuildResult
        {
            FirmwarePath = firmwareFullPath,
            OutputPackagePath = outputFullPath,
            FirmwareSize = firmwareBytes.LongLength,
            FirmwareCrc32 = firmwareCrc32,
            SignatureAlgorithm = manifest.SignatureAlgorithm,
            HashAlgorithm = manifest.HashAlgorithm,
            Manifest = manifest,
            ImageHeaderBytes = imageHeaderBytes,
            ImageHeader = imageHeader
        };
    }

    public IapPackageContent LoadPackage(string packagePath)
    {
        string packageFullPath = GetRequiredExistingPath(packagePath, ".iap package");
        using FileStream stream = new(packageFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);

        byte[] firmwareBytes = ReadRequiredEntry(archive, FirmwareEntryName);
        byte[] manifestBytes = ReadRequiredEntry(archive, ManifestEntryName);
        IapPackageManifest? manifest = JsonSerializer.Deserialize<IapPackageManifest>(manifestBytes, JsonOptions);
        if (manifest == null)
        {
            throw new IapPackageException("manifest.json is empty or invalid.");
        }

        ApplyBackwardCompatibleDefaults(manifest, firmwareBytes);
        ValidateManifest(manifest, firmwareBytes.LongLength);

        byte[] imageHeaderBytes = [];
        OtaImageHeaderInfo? imageHeader = null;
        if (manifest.PackageFormatVersion >= 3)
        {
            imageHeaderBytes = ReadRequiredEntry(archive, ImageHeaderEntryName);
            imageHeader = OtaImageHeaderCodec.Parse(imageHeaderBytes);
            OtaImageHeaderCodec.VerifyManifestConsistency(imageHeader, manifest, firmwareBytes);
        }
        else
        {
            byte[]? optionalImageHeader = ReadOptionalEntry(archive, ImageHeaderEntryName);
            if (optionalImageHeader is { Length: > 0 })
            {
                imageHeaderBytes = optionalImageHeader;
                imageHeader = OtaImageHeaderCodec.Parse(imageHeaderBytes);
                OtaImageHeaderCodec.VerifyManifestConsistency(imageHeader, manifest, firmwareBytes);
            }
        }

        return new IapPackageContent
        {
            PackagePath = packageFullPath,
            FirmwareBytes = firmwareBytes,
            Manifest = manifest,
            ImageHeaderBytes = imageHeaderBytes,
            ImageHeader = imageHeader
        };
    }

    private static string GetRequiredExistingPath(string path, string displayName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new IapPackageException($"{displayName} path is required.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{displayName} file not found.", path);
        }

        return path;
    }

    private static string GetRequiredOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new IapPackageException("Output package path is required.");
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string entryName, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream entryStream = entry.Open();
        entryStream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] ReadRequiredEntry(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName, entryName, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            throw new IapPackageException($"Required package entry not found: {entryName}");
        }

        using Stream entryStream = entry.Open();
        using MemoryStream memoryStream = new();
        entryStream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static byte[]? ReadOptionalEntry(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName, entryName, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            return null;
        }

        using Stream entryStream = entry.Open();
        using MemoryStream memoryStream = new();
        entryStream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static void ValidateManifest(IapPackageManifest manifest, long firmwareLength)
    {
        if (manifest.PackageFormatVersion is not 1 and not 2 and not 3)
        {
            throw new IapPackageException(
                $"Unsupported packageFormatVersion: {manifest.PackageFormatVersion}");
        }

        if (string.IsNullOrWhiteSpace(manifest.FirmwareFileName))
        {
            throw new IapPackageException("manifest.json is missing firmwareFileName.");
        }

        if (manifest.FirmwareSize <= 0)
        {
            throw new IapPackageException("manifest.json is missing firmwareSize.");
        }

        if (manifest.FirmwareSize != firmwareLength)
        {
            throw new IapPackageException(
                $"Firmware size mismatch. Manifest={manifest.FirmwareSize}, Package={firmwareLength}.");
        }

        manifest.FirmwareCrc32 = NormalizeCrc32(manifest.FirmwareCrc32);
        if (manifest.FirmwareCrc32.Length != 8)
        {
            throw new IapPackageException("manifest.json contains an invalid firmwareCrc32.");
        }

        manifest.FirmwareSha256 = NormalizeSha256(manifest.FirmwareSha256);
        if (manifest.FirmwareSha256.Length != 64)
        {
            throw new IapPackageException("manifest.json contains an invalid firmwareSha256.");
        }

        if (string.IsNullOrWhiteSpace(manifest.SignatureBase64))
        {
            throw new IapPackageException("manifest.json is missing signatureBase64.");
        }

        try
        {
            _ = Convert.FromBase64String(manifest.SignatureBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new IapPackageException($"manifest.json signatureBase64 is invalid: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(manifest.SignatureAlgorithm))
        {
            throw new IapPackageException("manifest.json is missing signatureAlgorithm.");
        }

        if (string.IsNullOrWhiteSpace(manifest.HashAlgorithm))
        {
            throw new IapPackageException("manifest.json is missing hashAlgorithm.");
        }

        if (!manifest.RequiresEncryption)
        {
            throw new IapPackageException("The selected package is not marked for encrypted delivery.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EncryptionAlgorithm))
        {
            throw new IapPackageException("manifest.json is missing encryptionAlgorithm.");
        }

        manifest.EncryptionIvHex = NormalizeIvHex(manifest.EncryptionIvHex);
        if (manifest.EncryptionIvHex.Length != 32)
        {
            throw new IapPackageException("manifest.json contains an invalid encryptionIvHex.");
        }

        if (string.IsNullOrWhiteSpace(manifest.TransferEncoding))
        {
            throw new IapPackageException("manifest.json is missing transferEncoding.");
        }

        if (manifest.PackageFormatVersion >= 2)
        {
            if (string.IsNullOrWhiteSpace(manifest.ManifestSignatureBase64))
            {
                throw new IapPackageException("manifest.json is missing manifestSignatureBase64.");
            }

            try
            {
                _ = Convert.FromBase64String(manifest.ManifestSignatureBase64.Trim());
            }
            catch (FormatException ex)
            {
                throw new IapPackageException($"manifest.json manifestSignatureBase64 is invalid: {ex.Message}");
            }
        }

        manifest.SignatureAlgorithm = manifest.SignatureAlgorithm.ToUpperInvariant();
        manifest.HashAlgorithm = manifest.HashAlgorithm.ToUpperInvariant();
        manifest.TransferEncoding = manifest.TransferEncoding.Trim().ToUpperInvariant();
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

    private static string NormalizeSha256(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return string.Empty;
        }

        return sha256
            .Trim()
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static string NormalizeIvHex(string? ivHex)
    {
        if (string.IsNullOrWhiteSpace(ivHex))
        {
            return string.Empty;
        }

        return ivHex
            .Trim()
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static void ApplyBackwardCompatibleDefaults(IapPackageManifest manifest, byte[] firmwareBytes)
    {
        if (manifest.PackageFormatVersion <= 1)
        {
            if (string.IsNullOrWhiteSpace(manifest.FirmwareSha256))
            {
                manifest.FirmwareSha256 = ComputeSha256Hex(firmwareBytes);
            }

            if (string.IsNullOrWhiteSpace(manifest.EncryptionIvHex))
            {
                manifest.EncryptionIvHex = LegacyZeroIvHex;
            }

            if (string.IsNullOrWhiteSpace(manifest.EncryptionKeyId))
            {
                manifest.EncryptionKeyId = "legacy-fixed-aes256";
            }

            if (string.IsNullOrWhiteSpace(manifest.TransferEncoding))
            {
                manifest.TransferEncoding = LegacyTransferEncoding;
            }
        }
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
