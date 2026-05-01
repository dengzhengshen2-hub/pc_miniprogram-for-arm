using System.Text.Json;
using System.Text.Json.Serialization;

namespace IAPWinForms.Features;

public sealed class ReleaseManifestPackage
{
    [JsonPropertyName("partition")]
    public string Partition { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public sealed class ReleaseManifestDocument
{
    [JsonPropertyName("releaseId")]
    public string ReleaseId { get; set; } = string.Empty;

    [JsonPropertyName("manifestVersion")]
    public string ManifestVersion { get; set; } = "2";

    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("hwRev")]
    public string HwRev { get; set; } = string.Empty;

    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = string.Empty;

    [JsonPropertyName("minVersion")]
    public string MinVersion { get; set; } = string.Empty;

    [JsonPropertyName("forceUpdate")]
    public bool ForceUpdate { get; set; }

    [JsonPropertyName("signatureAlgorithm")]
    public string SignatureAlgorithm { get; set; } = "RSA";

    [JsonPropertyName("hashAlgorithm")]
    public string HashAlgorithm { get; set; } = "SHA256";

    [JsonPropertyName("signedAt")]
    public string SignedAt { get; set; } = string.Empty;

    [JsonPropertyName("packages")]
    public List<ReleaseManifestPackage> Packages { get; set; } = [];

    [JsonPropertyName("signatureBase64")]
    public string SignatureBase64 { get; set; } = string.Empty;
}

public sealed class ReleaseManifestBuildRequest
{
    public string ReleaseId { get; init; } = string.Empty;
    public string ProductId { get; init; } = string.Empty;
    public string HwRev { get; init; } = string.Empty;
    public string LatestVersion { get; init; } = string.Empty;
    public string MinVersion { get; init; } = string.Empty;
    public bool ForceUpdate { get; init; }
    public IReadOnlyList<ReleaseManifestPackage> Packages { get; init; } = [];
    public string SignatureAlgorithm { get; init; } = "RSA";
    public string HashAlgorithm { get; init; } = "SHA256";
    public OtaSecurityProfile? SecurityProfile { get; init; }
    public string? PrivateKeyPem { get; init; }
    public string? PrivateKeyPath { get; init; }
    public string? VerificationPublicKeyPem { get; init; }
    public string? VerificationPublicKeyPath { get; init; }
    public string? OutputPath { get; init; }
    public DateTimeOffset? SignedAtUtc { get; init; }
}

public sealed class ReleaseManifestBuildResult
{
    public string? OutputPath { get; init; }
    public ReleaseManifestDocument Manifest { get; init; } = new();
}

public interface IReleaseManifestService
{
    ReleaseManifestBuildResult CreateSignedManifest(ReleaseManifestBuildRequest request);
    ReleaseManifestDocument Load(string path);
    bool Verify(ReleaseManifestDocument manifest, FirmwareSignatureVerificationOptions? options = null);
}

public sealed class ReleaseManifestService : IReleaseManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IFirmwareSigningService _signingService;
    private readonly IFirmwareSignatureVerifier _signatureVerifier;

    public ReleaseManifestService()
        : this(new RsaFirmwareSigningService(), new FirmwareSignatureVerifier())
    {
    }

    public ReleaseManifestService(
        IFirmwareSigningService signingService,
        IFirmwareSignatureVerifier signatureVerifier)
    {
        _signingService = signingService;
        _signatureVerifier = signatureVerifier;
    }

    public ReleaseManifestBuildResult CreateSignedManifest(ReleaseManifestBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePackages(request.Packages);

        ReleaseManifestDocument manifest = new()
        {
            ReleaseId = GetRequiredValue(request.ReleaseId, nameof(request.ReleaseId)),
            ManifestVersion = "2",
            ProductId = GetRequiredValue(request.ProductId, nameof(request.ProductId)),
            HwRev = GetRequiredValue(request.HwRev, nameof(request.HwRev)),
            LatestVersion = GetRequiredValue(request.LatestVersion, nameof(request.LatestVersion)),
            MinVersion = GetRequiredValue(request.MinVersion, nameof(request.MinVersion)),
            ForceUpdate = request.ForceUpdate,
            SignatureAlgorithm = request.SignatureAlgorithm.ToUpperInvariant(),
            HashAlgorithm = request.HashAlgorithm.ToUpperInvariant(),
            SignedAt = (request.SignedAtUtc ?? DateTimeOffset.UtcNow).ToString("O"),
            Packages = request.Packages
                .Select(package => new ReleaseManifestPackage
                {
                    Partition = package.Partition.Trim(),
                    Version = package.Version.Trim(),
                    Url = package.Url.Trim()
                })
                .ToList()
        };

        byte[] payload = ManifestSigningCanonicalizer.BuildReleaseManifestPayload(manifest);
        byte[] signatureBytes = _signingService.Sign(payload, new FirmwareSigningOptions
        {
            SignatureAlgorithm = manifest.SignatureAlgorithm,
            HashAlgorithm = manifest.HashAlgorithm,
            PrivateKeyPem = request.PrivateKeyPem,
            PrivateKeyPath = request.PrivateKeyPath,
            SecurityProfile = request.SecurityProfile
        });
        manifest.SignatureBase64 = Convert.ToBase64String(signatureBytes);

        bool verified = Verify(manifest, new FirmwareSignatureVerificationOptions
        {
            SignatureAlgorithm = manifest.SignatureAlgorithm,
            HashAlgorithm = manifest.HashAlgorithm,
            PublicKeyPem = request.VerificationPublicKeyPem,
            PublicKeyPath = request.VerificationPublicKeyPath
        });
        if (!verified)
        {
            throw new IapPackageException("Release manifest signing self-verification failed.");
        }

        string? outputPath = null;
        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            outputPath = Path.GetFullPath(request.OutputPath);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            File.WriteAllBytes(outputPath, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        }

        return new ReleaseManifestBuildResult
        {
            OutputPath = outputPath,
            Manifest = manifest
        };
    }

    public ReleaseManifestDocument Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new IapPackageException("Release manifest path is required.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Release manifest file not found.", path);
        }

        ReleaseManifestDocument? manifest = JsonSerializer.Deserialize<ReleaseManifestDocument>(File.ReadAllBytes(path), JsonOptions);
        if (manifest == null)
        {
            throw new IapPackageException("Release manifest is empty or invalid.");
        }

        return manifest;
    }

    public bool Verify(ReleaseManifestDocument manifest, FirmwareSignatureVerificationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.SignatureBase64))
        {
            return false;
        }

        byte[] payload = ManifestSigningCanonicalizer.BuildReleaseManifestPayload(manifest);
        byte[] signatureBytes = Convert.FromBase64String(manifest.SignatureBase64.Trim());
        return _signatureVerifier.Verify(payload, signatureBytes, options ?? new FirmwareSignatureVerificationOptions
        {
            SignatureAlgorithm = manifest.SignatureAlgorithm,
            HashAlgorithm = manifest.HashAlgorithm
        });
    }

    private static string GetRequiredValue(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new IapPackageException($"{fieldName} is required.");
        }

        return value.Trim();
    }

    private static void ValidatePackages(IReadOnlyList<ReleaseManifestPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        if (packages.Count == 0)
        {
            throw new IapPackageException("At least one package is required.");
        }

        foreach (ReleaseManifestPackage package in packages)
        {
            if (string.IsNullOrWhiteSpace(package.Partition) ||
                string.IsNullOrWhiteSpace(package.Version) ||
                string.IsNullOrWhiteSpace(package.Url))
            {
                throw new IapPackageException("Each release manifest package must define partition, version, and url.");
            }
        }
    }
}
