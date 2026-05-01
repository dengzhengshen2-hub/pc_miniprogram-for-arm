using System.Security.Cryptography;
using System.Text;

namespace IAPWinForms.Features;

public sealed record FirmwareEncryptionResult(
    string InputPath,
    string OutputPath,
    long InputSize,
    long OutputSize,
    string IvHex,
    bool IvPrefixed,
    string KeyId);

public sealed class FirmwareEncryptionOptions
{
    public OtaSecurityProfile? SecurityProfile { get; init; }
    public string? IvHex { get; init; }
    public bool PrefixIvToCiphertext { get; init; } = false;
}

public interface IFirmwareEncryptionService
{
    string AlgorithmName { get; }
    string KeyHex { get; }
    string IvHex { get; }
    string CreateRandomIvHex();
    string ResolveKeyId(OtaSecurityProfile? requestedProfile = null);
    string BuildDefaultOutputPath(string inputPath);
    byte[] EncryptBytes(byte[] plainBytes);
    byte[] EncryptBytes(byte[] plainBytes, FirmwareEncryptionOptions? options);
    FirmwareEncryptionResult EncryptFile(string inputPath, string outputPath);
    FirmwareEncryptionResult EncryptFile(string inputPath, string outputPath, FirmwareEncryptionOptions? options);
    bool SelfTest();
}

internal sealed class FirmwareEncryptionService : IFirmwareEncryptionService
{
    private const string RandomPerPackageIvText = "PER_PACKAGE_RANDOM_IV_16B";
    private const string SelfTestIvHex = "00000000000000000000000000000000";
    private const string SelfTestCipherHex = "225D1E09230FC3BEF289E71360796ABD58BCCE777E7F8DD465E072367C";
    private const string InputPathRequiredMessage = "\u8bf7\u8f93\u5165\u5f85\u52a0\u5bc6\u7684\u56fa\u4ef6\u6587\u4ef6\u8def\u5f84\u3002";
    private const string InputMissingMessage = "\u5f85\u52a0\u5bc6\u56fa\u4ef6\u4e0d\u5b58\u5728\u3002";
    private const string OutputPathRequiredMessage = "\u8bf7\u8f93\u5165\u52a0\u5bc6\u540e\u7684\u8f93\u51fa\u6587\u4ef6\u8def\u5f84\u3002";
    private const string InputOutputSameMessage = "\u8f93\u51fa\u6587\u4ef6\u4e0d\u80fd\u4e0e\u8f93\u5165\u56fa\u4ef6\u76f8\u540c\u3002";

    private readonly IOtaAesKeyProvider _keyProvider;

    public FirmwareEncryptionService()
        : this(new EnvironmentAwareOtaAesKeyProvider())
    {
    }

    internal FirmwareEncryptionService(IOtaAesKeyProvider keyProvider)
    {
        _keyProvider = keyProvider;
    }

    public string AlgorithmName => "AES-256-CTR";
    public string KeyHex => _keyProvider.ResolveKeyMaterial().DisplayText;
    public string IvHex => RandomPerPackageIvText;

    public string CreateRandomIvHex()
    {
        byte[] iv = new byte[16];
        RandomNumberGenerator.Fill(iv);
        return Convert.ToHexString(iv);
    }

    public string ResolveKeyId(OtaSecurityProfile? requestedProfile = null)
    {
        return _keyProvider.ResolveKeyMaterial(requestedProfile).KeyId;
    }

    public string BuildDefaultOutputPath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return string.Empty;
        }

        string directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string extension = Path.GetExtension(inputPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        return Path.Combine(directory, $"{name}_ctr{extension}");
    }

    public byte[] EncryptBytes(byte[] plainBytes)
    {
        return EncryptBytes(plainBytes, options: null);
    }

    public byte[] EncryptBytes(byte[] plainBytes, FirmwareEncryptionOptions? options)
    {
        ArgumentNullException.ThrowIfNull(plainBytes);

        EncryptionExecutionPlan plan = BuildExecutionPlan(options);
        byte[] encryptedBytes = EncryptBytesCore(plainBytes, plan.KeyMaterial.KeyBytes, plan.IvBytes);
        if (!plan.PrefixIvToCiphertext)
        {
            return encryptedBytes;
        }

        byte[] output = new byte[plan.IvBytes.Length + encryptedBytes.Length];
        Buffer.BlockCopy(plan.IvBytes, 0, output, 0, plan.IvBytes.Length);
        Buffer.BlockCopy(encryptedBytes, 0, output, plan.IvBytes.Length, encryptedBytes.Length);
        return output;
    }

    public FirmwareEncryptionResult EncryptFile(string inputPath, string outputPath)
    {
        return EncryptFile(inputPath, outputPath, options: null);
    }

    public FirmwareEncryptionResult EncryptFile(string inputPath, string outputPath, FirmwareEncryptionOptions? options)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException(InputPathRequiredMessage, nameof(inputPath));
        }

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException(InputMissingMessage, inputPath);
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException(OutputPathRequiredMessage, nameof(outputPath));
        }

        string inputFullPath = Path.GetFullPath(inputPath);
        string outputFullPath = Path.GetFullPath(outputPath);
        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(InputOutputSameMessage);
        }

        string? outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        byte[] plainBytes = File.ReadAllBytes(inputFullPath);
        EncryptionExecutionPlan plan = BuildExecutionPlan(options);
        byte[] encryptedBytes = EncryptBytes(plainBytes, new FirmwareEncryptionOptions
        {
            SecurityProfile = plan.KeyMaterial.SecurityProfile,
            IvHex = Convert.ToHexString(plan.IvBytes),
            PrefixIvToCiphertext = plan.PrefixIvToCiphertext
        });
        File.WriteAllBytes(outputFullPath, encryptedBytes);

        return new FirmwareEncryptionResult(
            inputFullPath,
            outputFullPath,
            plainBytes.LongLength,
            encryptedBytes.LongLength,
            Convert.ToHexString(plan.IvBytes),
            plan.PrefixIvToCiphertext,
            plan.KeyMaterial.KeyId);
    }

    public bool SelfTest()
    {
        byte[] sample = Encoding.ASCII.GetBytes("Hello_OTA_CTR_Test_1234567890");
        byte[] encrypted = EncryptBytes(sample, new FirmwareEncryptionOptions
        {
            SecurityProfile = OtaSecurityProfile.Development,
            IvHex = SelfTestIvHex,
            PrefixIvToCiphertext = false
        });
        return string.Equals(Convert.ToHexString(encrypted), SelfTestCipherHex, StringComparison.OrdinalIgnoreCase);
    }

    private EncryptionExecutionPlan BuildExecutionPlan(FirmwareEncryptionOptions? options)
    {
        OtaAesKeyMaterial keyMaterial = _keyProvider.ResolveKeyMaterial(options?.SecurityProfile);
        byte[] ivBytes;

        if (string.IsNullOrWhiteSpace(options?.IvHex))
        {
            ivBytes = Convert.FromHexString(CreateRandomIvHex());
        }
        else
        {
            string ivHex = NormalizeIvHex(options.IvHex);
            ivBytes = Convert.FromHexString(ivHex);
        }

        return new EncryptionExecutionPlan(
            keyMaterial,
            ivBytes,
            options?.PrefixIvToCiphertext ?? true);
    }

    private static byte[] EncryptBytesCore(byte[] plainBytes, byte[] keyBytes, byte[] ivBytes)
    {
        using Aes aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = keyBytes;
        aes.IV = new byte[16];

        using ICryptoTransform encryptor = aes.CreateEncryptor();
        byte[] output = new byte[plainBytes.Length];
        byte[] counter = new byte[16];
        byte[] keystream = new byte[16];
        int offset = 0;
        uint blockIndex = 0;

        while (offset < plainBytes.Length)
        {
            BuildCtrCounter(counter, ivBytes, blockIndex);
            int transformed = encryptor.TransformBlock(counter, 0, counter.Length, keystream, 0);
            if (transformed != keystream.Length)
            {
                throw new InvalidOperationException("AES-CTR keystream generation failed.");
            }

            int chunk = Math.Min(keystream.Length, plainBytes.Length - offset);
            for (int index = 0; index < chunk; index++)
            {
                output[offset + index] = (byte)(plainBytes[offset + index] ^ keystream[index]);
            }

            offset += chunk;
            blockIndex++;
        }

        return output;
    }

    private static void BuildCtrCounter(byte[] counter, byte[] ivBytes, uint blockIndex)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(ivBytes);
        if (counter.Length != 16 || ivBytes.Length != 16)
        {
            throw new InvalidOperationException("AES-CTR counter requires a 16-byte IV.");
        }

        Buffer.BlockCopy(ivBytes, 0, counter, 0, ivBytes.Length);
        uint carry = blockIndex;
        for (int index = counter.Length - 1; index >= 0; index--)
        {
            carry += counter[index];
            counter[index] = (byte)(carry & 0xFFU);
            carry >>= 8;
        }
    }

    private static string NormalizeIvHex(string ivHex)
    {
        string normalized = ivHex
            .Trim()
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (normalized.Length != 32)
        {
            throw new InvalidOperationException("IV must be 16 bytes (32 hex chars).");
        }

        _ = Convert.FromHexString(normalized);
        return normalized;
    }

    private sealed record EncryptionExecutionPlan(
        OtaAesKeyMaterial KeyMaterial,
        byte[] IvBytes,
        bool PrefixIvToCiphertext);
}
