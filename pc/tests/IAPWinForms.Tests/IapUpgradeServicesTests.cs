using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using IAPWinForms.Features;

namespace IAPWinForms.Tests;

[TestClass]
public sealed class IapUpgradeServicesTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [TestMethod]
    public void CreatePackage_AndPrepareForSend_Succeeds()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            byte[] firmwareBytes = CreateFirmwareBytes();
            string firmwarePath = WriteFirmware(tempDirectory, "payload.bin", firmwareBytes);
            (string privateKeyPath, _, string publicKeyPem) = CreateKeyPair(tempDirectory, "valid");

            IIapPackageService packageService = new IapPackageService();
            IapPackageBuildResult packageResult = packageService.CreatePackage(CreateBuildRequest(
                firmwarePath,
                privateKeyPath,
                Path.Combine(tempDirectory, "payload.iap"),
                publicKeyPem));

            Assert.IsTrue(File.Exists(packageResult.OutputPackagePath));
            Assert.AreEqual(Crc32Utility.ComputeHex(firmwareBytes), packageResult.FirmwareCrc32);
            Assert.AreEqual(3, packageResult.Manifest.PackageFormatVersion);
            Assert.AreEqual("RSA", packageResult.Manifest.SignatureAlgorithm);
            Assert.AreEqual("SHA256", packageResult.Manifest.HashAlgorithm);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(firmwareBytes)), packageResult.Manifest.FirmwareSha256);
            Assert.AreEqual("RAW_CTR", packageResult.Manifest.TransferEncoding);
            Assert.AreEqual(32, packageResult.Manifest.EncryptionIvHex.Length);
            Assert.IsFalse(string.IsNullOrWhiteSpace(packageResult.Manifest.ManifestSignatureBase64));
            Assert.AreEqual(OtaTargetSlot.App1, packageResult.ImageHeader.TargetSlot);

            IapPackageContent package = packageService.LoadPackage(packageResult.OutputPackagePath);
            CollectionAssert.AreEqual(firmwareBytes, package.FirmwareBytes);
            Assert.AreEqual("payload.bin", package.Manifest.FirmwareFileName);
            Assert.IsNotNull(package.ImageHeader);

            IFirmwareUpgradePreparationService preparationService = new FirmwareUpgradePreparationService();
            FirmwareUpgradePreparationResult prepared = preparationService.PrepareForYModemSend(new FirmwareUpgradePreparationRequest
            {
                PackagePath = packageResult.OutputPackagePath,
                VerificationPublicKeyPem = publicKeyPem
            });

            Assert.AreEqual("payload_ctr.bin", prepared.TransferFileName);
            Assert.AreEqual(packageResult.FirmwareCrc32, prepared.ActualCrc32Hex);
            Assert.AreEqual("AES-256-CTR", prepared.EncryptionAlgorithm);
            Assert.AreEqual(package.Manifest.EncryptionIvHex, prepared.EncryptionIvHex);
            Assert.AreEqual("RAW_CTR", prepared.TransferEncoding);
            StringAssert.Contains(prepared.VerificationMessage, "signature verification passed");
            StringAssert.Contains(prepared.VerificationMessage, "image-header verified");

            IFirmwareEncryptionService encryptionService = new FirmwareEncryptionService();
            byte[] expectedTransferBytes = encryptionService.EncryptBytes(BuildTransferPlainBytes(package), new FirmwareEncryptionOptions
            {
                IvHex = package.Manifest.EncryptionIvHex,
                PrefixIvToCiphertext = false
            });

            CollectionAssert.AreEqual(expectedTransferBytes, prepared.TransferBytes);
            Assert.AreEqual(package.FirmwareBytes.Length, prepared.TransferBytes.Length);
        }
        finally
        {
            DeleteDirectory(tempDirectory);
        }
    }

    [TestMethod]
    public void PrepareForSend_DetectsFirmwareTamperBySignature()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            byte[] firmwareBytes = CreateFirmwareBytes();
            string firmwarePath = WriteFirmware(tempDirectory, "tamper.bin", firmwareBytes);
            (string privateKeyPath, string privateKeyPem, string publicKeyPem) = CreateKeyPair(tempDirectory, "tamper");

            IIapPackageService packageService = new IapPackageService();
            IapPackageBuildResult packageResult = packageService.CreatePackage(CreateBuildRequest(
                firmwarePath,
                privateKeyPath,
                Path.Combine(tempDirectory, "tamper.iap"),
                publicKeyPem));

            byte[] tamperedFirmware = [.. firmwareBytes];
            tamperedFirmware[0] ^= 0x5A;
            MutatePackageAndResign(packageResult.OutputPackagePath, privateKeyPem, publicKeyPem, (manifest, _) =>
            {
                manifest.FirmwareCrc32 = Crc32Utility.ComputeHex(tamperedFirmware);
                manifest.FirmwareSha256 = Convert.ToHexString(SHA256.HashData(tamperedFirmware));
                return (tamperedFirmware, manifest);
            }, requireFirmwareSignatureMatch: false, rebuildImageHeader: true);

            IFirmwareUpgradePreparationService preparationService = new FirmwareUpgradePreparationService();
            FirmwareValidationException ex = AssertThrows<FirmwareValidationException>(() =>
                preparationService.PrepareForYModemSend(new FirmwareUpgradePreparationRequest
                {
                    PackagePath = packageResult.OutputPackagePath,
                    VerificationPublicKeyPem = publicKeyPem
                }));

            StringAssert.Contains(ex.Message, "Signature verification failed");
        }
        finally
        {
            DeleteDirectory(tempDirectory);
        }
    }

    [TestMethod]
    public void PrepareForSend_DetectsManifestTamperBySignature()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            byte[] firmwareBytes = CreateFirmwareBytes();
            string firmwarePath = WriteFirmware(tempDirectory, "manifest.bin", firmwareBytes);
            (string privateKeyPath, _, string publicKeyPem) = CreateKeyPair(tempDirectory, "manifest");

            IIapPackageService packageService = new IapPackageService();
            IapPackageBuildResult packageResult = packageService.CreatePackage(CreateBuildRequest(
                firmwarePath,
                privateKeyPath,
                Path.Combine(tempDirectory, "manifest.iap"),
                publicKeyPem));

            MutatePackage(packageResult.OutputPackagePath, (manifest, currentFirmware) =>
            {
                manifest.FirmwareFileName = "manifest-renamed.bin";
                return (currentFirmware, manifest);
            });

            IFirmwareUpgradePreparationService preparationService = new FirmwareUpgradePreparationService();
            FirmwareValidationException ex = AssertThrows<FirmwareValidationException>(() =>
                preparationService.PrepareForYModemSend(new FirmwareUpgradePreparationRequest
                {
                    PackagePath = packageResult.OutputPackagePath,
                    VerificationPublicKeyPem = publicKeyPem
                }));

            StringAssert.Contains(ex.Message, "Manifest signature verification failed");
        }
        finally
        {
            DeleteDirectory(tempDirectory);
        }
    }

    [TestMethod]
    public void PrepareForSend_DetectsManifestCrcTamper()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            byte[] firmwareBytes = CreateFirmwareBytes();
            string firmwarePath = WriteFirmware(tempDirectory, "crc.bin", firmwareBytes);
            (string privateKeyPath, string privateKeyPem, string publicKeyPem) = CreateKeyPair(tempDirectory, "crc");

            IIapPackageService packageService = new IapPackageService();
            IapPackageBuildResult packageResult = packageService.CreatePackage(CreateBuildRequest(
                firmwarePath,
                privateKeyPath,
                Path.Combine(tempDirectory, "crc.iap"),
                publicKeyPem));

            MutatePackageAndResign(packageResult.OutputPackagePath, privateKeyPem, publicKeyPem, (manifest, currentFirmware) =>
            {
                manifest.FirmwareCrc32 = "00000000";
                return (currentFirmware, manifest);
            });

            IFirmwareUpgradePreparationService preparationService = new FirmwareUpgradePreparationService();
            FirmwareValidationException ex = AssertThrows<FirmwareValidationException>(() =>
                preparationService.PrepareForYModemSend(new FirmwareUpgradePreparationRequest
                {
                    PackagePath = packageResult.OutputPackagePath,
                    VerificationPublicKeyPem = publicKeyPem
                }));

            StringAssert.Contains(ex.Message, "CRC32 mismatch");
        }
        finally
        {
            DeleteDirectory(tempDirectory);
        }
    }

    [TestMethod]
    public void CreatePackage_FailsWhenSelfVerificationUsesDifferentPublicKey()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            byte[] firmwareBytes = CreateFirmwareBytes();
            string firmwarePath = WriteFirmware(tempDirectory, "wrongkey.bin", firmwareBytes);
            (_, _, string verificationPublicKeyPem) = CreateKeyPair(tempDirectory, "verify");
            (string privateKeyPath, _, _) = CreateKeyPair(tempDirectory, "sign");

            IIapPackageService packageService = new IapPackageService();
            IapPackageException ex = AssertThrows<IapPackageException>(() =>
                packageService.CreatePackage(CreateBuildRequest(
                    firmwarePath,
                    privateKeyPath,
                    Path.Combine(tempDirectory, "wrongkey.iap"),
                    verificationPublicKeyPem)));

            StringAssert.Contains(ex.Message, "self-verification failed");
        }
        finally
        {
            DeleteDirectory(tempDirectory);
        }
    }

    [TestMethod]
    public void PrepareForSend_ProducesSameBytesAsEncryptionService()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            byte[] firmwareBytes = CreateFirmwareBytes();
            string firmwarePath = WriteFirmware(tempDirectory, "encrypt.bin", firmwareBytes);
            (string privateKeyPath, _, string publicKeyPem) = CreateKeyPair(tempDirectory, "encrypt");

            IIapPackageService packageService = new IapPackageService();
            IapPackageBuildResult packageResult = packageService.CreatePackage(CreateBuildRequest(
                firmwarePath,
                privateKeyPath,
                Path.Combine(tempDirectory, "encrypt.iap"),
                publicKeyPem));

            IFirmwareUpgradePreparationService preparationService = new FirmwareUpgradePreparationService();
            FirmwareUpgradePreparationResult prepared = preparationService.PrepareForYModemSend(new FirmwareUpgradePreparationRequest
            {
                PackagePath = packageResult.OutputPackagePath,
                VerificationPublicKeyPem = publicKeyPem
            });

            IFirmwareEncryptionService encryptionService = new FirmwareEncryptionService();
            IapPackageContent package = packageService.LoadPackage(packageResult.OutputPackagePath);
            byte[] expectedEncrypted = encryptionService.EncryptBytes(BuildTransferPlainBytes(package), new FirmwareEncryptionOptions
            {
                IvHex = packageResult.Manifest.EncryptionIvHex,
                PrefixIvToCiphertext = false
            });

            CollectionAssert.AreEqual(expectedEncrypted, prepared.TransferBytes);
            Assert.IsTrue(encryptionService.SelfTest());
        }
        finally
        {
            DeleteDirectory(tempDirectory);
        }
    }

    [TestMethod]
    public void ReleaseManifestService_CreatesAndVerifiesSignedManifest()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            (string privateKeyPath, _, string publicKeyPem) = CreateKeyPair(tempDirectory, "release");
            IReleaseManifestService manifestService = new ReleaseManifestService();

            ReleaseManifestBuildResult result = manifestService.CreateSignedManifest(new ReleaseManifestBuildRequest
            {
                ReleaseId = "lcd-a1-1.0.3",
                ProductId = "LCD",
                HwRev = "A1",
                LatestVersion = "1.0.3",
                MinVersion = "1.0.2",
                ForceUpdate = true,
                PrivateKeyPath = privateKeyPath,
                VerificationPublicKeyPem = publicKeyPem,
                OutputPath = Path.Combine(tempDirectory, "release-manifest.v2.json"),
                Packages =
                [
                    new ReleaseManifestPackage
                    {
                        Partition = "app1",
                        Version = "1.0.3",
                        Url = "https://example.com/LCD_app1.iap"
                    },
                    new ReleaseManifestPackage
                    {
                        Partition = "app2",
                        Version = "1.0.3",
                        Url = "https://example.com/LCD_app2.iap"
                    }
                ]
            });

            Assert.IsTrue(File.Exists(result.OutputPath));
            Assert.AreEqual("2", result.Manifest.ManifestVersion);
            Assert.IsTrue(manifestService.Verify(result.Manifest, new FirmwareSignatureVerificationOptions
            {
                PublicKeyPem = publicKeyPem,
                SignatureAlgorithm = result.Manifest.SignatureAlgorithm,
                HashAlgorithm = result.Manifest.HashAlgorithm
            }));
        }
        finally
        {
            DeleteDirectory(tempDirectory);
        }
    }

    [TestMethod]
    public void CreatePackage_FailsWhenEmbeddedFirmwareVersionDoesNotMatchRequestedVersion()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            byte[] firmwareBytes = CreateFirmwareBytes(version: "1.0.2");
            string firmwarePath = WriteFirmware(tempDirectory, "embedded-mismatch.bin", firmwareBytes);
            (string privateKeyPath, _, string publicKeyPem) = CreateKeyPair(tempDirectory, "embedded-mismatch");

            IIapPackageService packageService = new IapPackageService();
            IapPackageException ex = AssertThrows<IapPackageException>(() =>
                packageService.CreatePackage(CreateBuildRequest(
                    firmwarePath,
                    privateKeyPath,
                    Path.Combine(tempDirectory, "embedded-mismatch.iap"),
                    publicKeyPem)));

            StringAssert.Contains(ex.Message, "Embedded firmware version mismatch");
        }
        finally
        {
            DeleteDirectory(tempDirectory);
        }
    }

    private static byte[] CreateFirmwareBytes(string slot = "APP1", string version = "1.0.3")
    {
        byte[] payload = Enumerable.Range(0, 73).Select(index => (byte)(index * 3 + 11)).ToArray();
        byte[] markerBytes = System.Text.Encoding.ASCII.GetBytes($"IAPFWV1|{slot}|{version}|");
        byte[] firmwareBytes = new byte[payload.Length + markerBytes.Length + 8];
        Buffer.BlockCopy(payload, 0, firmwareBytes, 0, payload.Length);
        Buffer.BlockCopy(markerBytes, 0, firmwareBytes, payload.Length + 4, markerBytes.Length);
        return firmwareBytes;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "IAPWinFormsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFirmware(string directory, string fileName, byte[] firmwareBytes)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, firmwareBytes);
        return path;
    }

    private static IapPackageBuildRequest CreateBuildRequest(
        string firmwarePath,
        string privateKeyPath,
        string outputPackagePath,
        string verificationPublicKeyPem)
    {
        return new IapPackageBuildRequest
        {
            FirmwarePath = firmwarePath,
            PrivateKeyPath = privateKeyPath,
            OutputPackagePath = outputPackagePath,
            VerificationPublicKeyPem = verificationPublicKeyPem,
            TargetSlot = OtaTargetSlot.App1,
            FirmwareVersion = "1.0.3",
            MinAllowedVersion = "1.0.0"
        };
    }

    private static (string privateKeyPath, string privateKeyPem, string publicKeyPem) CreateKeyPair(string directory, string prefix)
    {
        using RSA rsa = RSA.Create(2048);
        string privateKeyPem = rsa.ExportRSAPrivateKeyPem();
        string publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        string privateKeyPath = Path.Combine(directory, $"{prefix}_private.pem");
        File.WriteAllText(privateKeyPath, privateKeyPem);
        return (privateKeyPath, privateKeyPem, publicKeyPem);
    }

    private static void MutatePackage(
        string packagePath,
        Func<IapPackageManifest, byte[], (byte[] firmwareBytes, IapPackageManifest manifest)> mutator)
    {
        using FileStream stream = new(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Update, leaveOpen: false);

        byte[] currentFirmware = ReadEntry(archive, "firmware.bin");
        IapPackageManifest manifest = JsonSerializer.Deserialize<IapPackageManifest>(ReadEntry(archive, "manifest.json"), JsonOptions)
            ?? throw new AssertFailedException("Failed to load manifest.json from package.");

        (byte[] firmwareBytes, IapPackageManifest nextManifest) = mutator(manifest, currentFirmware);
        ReplaceEntry(archive, "firmware.bin", firmwareBytes);
        ReplaceEntry(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(nextManifest, JsonOptions));
    }

    private static void MutatePackageAndResign(
        string packagePath,
        string privateKeyPem,
        string verificationPublicKeyPem,
        Func<IapPackageManifest, byte[], (byte[] firmwareBytes, IapPackageManifest manifest)> mutator,
        bool requireFirmwareSignatureMatch = true,
        bool rebuildImageHeader = false)
    {
        using FileStream stream = new(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Update, leaveOpen: false);

        byte[] currentFirmware = ReadEntry(archive, "firmware.bin");
        byte[] currentImageHeaderBytes = ReadEntry(archive, "image-header.bin");
        IapPackageManifest manifest = JsonSerializer.Deserialize<IapPackageManifest>(ReadEntry(archive, "manifest.json"), JsonOptions)
            ?? throw new AssertFailedException("Failed to load manifest.json from package.");

        (byte[] firmwareBytes, IapPackageManifest nextManifest) = mutator(manifest, currentFirmware);

        IFirmwareSignatureVerifier verifier = new FirmwareSignatureVerifier();
        bool firmwareSignatureMatches = verifier.Verify(firmwareBytes, Convert.FromBase64String(nextManifest.SignatureBase64), new FirmwareSignatureVerificationOptions
        {
            SignatureAlgorithm = nextManifest.SignatureAlgorithm,
            HashAlgorithm = nextManifest.HashAlgorithm,
            PublicKeyPem = verificationPublicKeyPem
        });
        if (requireFirmwareSignatureMatch && !firmwareSignatureMatches)
        {
            throw new AssertFailedException("Mutated firmware no longer matches original signature.");
        }

        IFirmwareSigningService signingService = new RsaFirmwareSigningService();
        nextManifest.ManifestSignatureBase64 = Convert.ToBase64String(signingService.Sign(
            ManifestSigningCanonicalizer.BuildIapPackageManifestPayload(nextManifest),
            new FirmwareSigningOptions
            {
                PrivateKeyPem = privateKeyPem,
                SignatureAlgorithm = nextManifest.SignatureAlgorithm,
                HashAlgorithm = nextManifest.HashAlgorithm
            }));

        ReplaceEntry(archive, "firmware.bin", firmwareBytes);
        if (rebuildImageHeader)
        {
            OtaImageHeaderInfo currentImageHeader = OtaImageHeaderCodec.Parse(currentImageHeaderBytes);
            byte[] imageHeaderPayload = OtaImageHeaderCodec.BuildPayload(
                currentImageHeader.TargetSlot,
                currentImageHeader.FirmwareVersion,
                firmwareBytes.Length,
                SHA256.HashData(firmwareBytes),
                Convert.FromHexString(nextManifest.EncryptionIvHex),
                currentImageHeader.MinAllowedVersion);
            byte[] imageHeaderSignature = signingService.Sign(imageHeaderPayload, new FirmwareSigningOptions
            {
                PrivateKeyPem = privateKeyPem,
                SignatureAlgorithm = nextManifest.SignatureAlgorithm,
                HashAlgorithm = nextManifest.HashAlgorithm
            });
            ReplaceEntry(archive, "image-header.bin", OtaImageHeaderCodec.BuildBinary(imageHeaderPayload, imageHeaderSignature));
        }
        ReplaceEntry(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(nextManifest, JsonOptions));
    }

    private static byte[] ReadEntry(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName)
            ?? throw new AssertFailedException($"Missing zip entry: {entryName}");
        using Stream entryStream = entry.Open();
        using MemoryStream memoryStream = new();
        entryStream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static void ReplaceEntry(ZipArchive archive, string entryName, byte[] bytes)
    {
        archive.GetEntry(entryName)?.Delete();
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream entryStream = entry.Open();
        entryStream.Write(bytes, 0, bytes.Length);
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

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new AssertFailedException($"Expected exception of type {typeof(TException).Name}.");
    }
}
