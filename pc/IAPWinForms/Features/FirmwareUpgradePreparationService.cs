namespace IAPWinForms.Features;

public sealed class FirmwareUpgradePreparationRequest
{
    public string PackagePath { get; init; } = string.Empty;
    public string? VerificationPublicKeyPem { get; init; }
    public string? VerificationPublicKeyPath { get; init; }
}

public sealed class FirmwareUpgradePreparationResult
{
    public string PackagePath { get; init; } = string.Empty;
    public string OriginalFirmwareFileName { get; init; } = string.Empty;
    public long OriginalFirmwareSize { get; init; }
    public string ActualCrc32Hex { get; init; } = string.Empty;
    public string TransferFileName { get; init; } = string.Empty;
    public byte[] TransferBytes { get; init; } = [];
    public string VerificationMessage { get; init; } = string.Empty;
    public string EncryptionAlgorithm { get; init; } = string.Empty;
    public string EncryptionIvHex { get; init; } = string.Empty;
    public string TransferEncoding { get; init; } = string.Empty;
}

public interface IFirmwareUpgradePreparationService
{
    FirmwareUpgradePreparationResult PrepareForYModemSend(
        FirmwareUpgradePreparationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class FirmwareUpgradePreparationService : IFirmwareUpgradePreparationService
{
    private readonly IIapPackageService _iapPackageService;
    private readonly IFirmwareVerificationService _verificationService;
    private readonly IFirmwareEncryptionService _encryptionService;
    private readonly IFirmwareSignatureVerifier _signatureVerifier;

    public FirmwareUpgradePreparationService()
        : this(new IapPackageService(), new FirmwareVerificationService(), new FirmwareEncryptionService(), new FirmwareSignatureVerifier())
    {
    }

    public FirmwareUpgradePreparationService(
        IIapPackageService iapPackageService,
        IFirmwareVerificationService verificationService,
        IFirmwareEncryptionService encryptionService,
        IFirmwareSignatureVerifier signatureVerifier)
    {
        _iapPackageService = iapPackageService;
        _verificationService = verificationService;
        _encryptionService = encryptionService;
        _signatureVerifier = signatureVerifier;
    }

    public FirmwareUpgradePreparationResult PrepareForYModemSend(
        FirmwareUpgradePreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PackagePath))
        {
            throw new IapPackageException("Package path is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_encryptionService.SelfTest())
        {
            throw new InvalidOperationException("AES self-test failed. Please check the configured key and IV before upgrading.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        IapPackageContent package = _iapPackageService.LoadPackage(request.PackagePath);
        if (!string.Equals(
                package.Manifest.EncryptionAlgorithm,
                _encryptionService.AlgorithmName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IapPackageException(
                $"Unsupported encryption algorithm. Package={package.Manifest.EncryptionAlgorithm}, Current={_encryptionService.AlgorithmName}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        VerifyManifestSignatureIfPresent(package, request);

        cancellationToken.ThrowIfCancellationRequested();
        string imageHeaderVerificationMessage = VerifyImageHeaderIfPresent(package, request);

        cancellationToken.ThrowIfCancellationRequested();
        FirmwareVerificationResult verificationResult = _verificationService.Validate(package.FirmwareBytes, request.PackagePath, new FirmwareVerificationOptions
        {
            ExpectedCrc32 = package.Manifest.FirmwareCrc32,
            SignatureBase64 = package.Manifest.SignatureBase64,
            SignatureAlgorithm = package.Manifest.SignatureAlgorithm,
            HashAlgorithm = package.Manifest.HashAlgorithm,
            RequireSignature = true,
            PublicKeyPem = request.VerificationPublicKeyPem,
            PublicKeyPath = request.VerificationPublicKeyPath
        });

        cancellationToken.ThrowIfCancellationRequested();
        byte[] transferPlainBytes = BuildTransferPlainBytes(package);
        byte[] encryptedBytes = _encryptionService.EncryptBytes(transferPlainBytes, new FirmwareEncryptionOptions
        {
            IvHex = package.Manifest.EncryptionIvHex,
            PrefixIvToCiphertext = string.Equals(package.Manifest.TransferEncoding, "IV_PREFIXED", StringComparison.OrdinalIgnoreCase)
        });
        cancellationToken.ThrowIfCancellationRequested();

        return new FirmwareUpgradePreparationResult
        {
            PackagePath = Path.GetFullPath(request.PackagePath),
            OriginalFirmwareFileName = package.Manifest.FirmwareFileName,
            OriginalFirmwareSize = package.Manifest.FirmwareSize,
            ActualCrc32Hex = verificationResult.ActualCrc32Hex,
            TransferFileName = BuildTransferFileName(package.Manifest.FirmwareFileName),
            TransferBytes = encryptedBytes,
            VerificationMessage = BuildVerificationMessage(verificationResult.Message, imageHeaderVerificationMessage),
            EncryptionAlgorithm = _encryptionService.AlgorithmName,
            EncryptionIvHex = package.Manifest.EncryptionIvHex,
            TransferEncoding = package.Manifest.TransferEncoding
        };
    }

    private void VerifyManifestSignatureIfPresent(IapPackageContent package, FirmwareUpgradePreparationRequest request)
    {
        if (package.Manifest.PackageFormatVersion < 2 ||
            string.IsNullOrWhiteSpace(package.Manifest.ManifestSignatureBase64))
        {
            return;
        }

        byte[] payload = ManifestSigningCanonicalizer.BuildIapPackageManifestPayload(package.Manifest);
        byte[] signature = Convert.FromBase64String(package.Manifest.ManifestSignatureBase64.Trim());
        bool verified = _signatureVerifier.Verify(payload, signature, new FirmwareSignatureVerificationOptions
        {
            SignatureAlgorithm = package.Manifest.SignatureAlgorithm,
            HashAlgorithm = package.Manifest.HashAlgorithm,
            PublicKeyPem = request.VerificationPublicKeyPem,
            PublicKeyPath = request.VerificationPublicKeyPath
        });
        if (!verified)
        {
            throw new FirmwareValidationException("Manifest signature verification failed.");
        }
    }

    private string VerifyImageHeaderIfPresent(IapPackageContent package, FirmwareUpgradePreparationRequest request)
    {
        if (package.ImageHeader == null)
        {
            return string.Empty;
        }

        bool verified = _signatureVerifier.Verify(package.ImageHeader.PayloadBytes, package.ImageHeader.SignatureBytes, new FirmwareSignatureVerificationOptions
        {
            SignatureAlgorithm = package.Manifest.SignatureAlgorithm,
            HashAlgorithm = package.Manifest.HashAlgorithm,
            PublicKeyPem = request.VerificationPublicKeyPem,
            PublicKeyPath = request.VerificationPublicKeyPath
        });
        if (!verified)
        {
            throw new FirmwareValidationException("image-header signature verification failed.");
        }

        return $"image-header verified: slot={package.ImageHeader.TargetSlot}, version={package.ImageHeader.FirmwareVersion}";
    }

    private static byte[] BuildTransferPlainBytes(IapPackageContent package)
    {
        if (!string.Equals(package.Manifest.TransferEncoding, "IV_PREFIXED", StringComparison.OrdinalIgnoreCase) ||
            package.ImageHeaderBytes.Length == 0)
        {
            return package.FirmwareBytes;
        }

        byte[] plainBytes = new byte[package.ImageHeaderBytes.Length + package.FirmwareBytes.Length];
        Buffer.BlockCopy(package.ImageHeaderBytes, 0, plainBytes, 0, package.ImageHeaderBytes.Length);
        Buffer.BlockCopy(package.FirmwareBytes, 0, plainBytes, package.ImageHeaderBytes.Length, package.FirmwareBytes.Length);
        return plainBytes;
    }

    private static string BuildVerificationMessage(string firmwareMessage, string imageHeaderMessage)
    {
        if (string.IsNullOrWhiteSpace(imageHeaderMessage))
        {
            return firmwareMessage;
        }

        if (string.IsNullOrWhiteSpace(firmwareMessage))
        {
            return imageHeaderMessage;
        }

        return $"{firmwareMessage}; {imageHeaderMessage}";
    }

    private static string BuildTransferFileName(string originalFileName)
    {
        string safeFileName = Path.GetFileName(originalFileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            return "firmware_ctr.bin";
        }

        string name = Path.GetFileNameWithoutExtension(safeFileName);
        string extension = Path.GetExtension(safeFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        return $"{name}_ctr{extension}";
    }
}
