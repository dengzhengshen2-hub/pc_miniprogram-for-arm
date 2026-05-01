using System.Buffers.Binary;
using System.Text;

namespace IAPWinForms.Features;

public enum OtaTargetSlot
{
    App1 = 0,
    App2 = 1
}

public sealed class OtaImageHeaderInfo
{
    public int FormatVersion { get; init; }
    public OtaTargetSlot TargetSlot { get; init; }
    public string FirmwareVersion { get; init; } = string.Empty;
    public int FirmwareSize { get; init; }
    public string FirmwareSha256 { get; init; } = string.Empty;
    public string IvHex { get; init; } = string.Empty;
    public int SignatureAlgorithm { get; init; }
    public string MinAllowedVersion { get; init; } = string.Empty;
    public byte[] PayloadBytes { get; init; } = [];
    public byte[] SignatureBytes { get; init; } = [];
    public byte[] BinaryBytes { get; init; } = [];
}

internal static class OtaImageHeaderCodec
{
    public const uint Magic = 0x4F544132U;
    public const ushort HeaderVersion = 1;
    public const int HeaderEnvelopeSize = 20;
    public const int PayloadSize = 96;
    public const int SignatureSize = 256;
    public const int TotalSize = HeaderEnvelopeSize + PayloadSize + SignatureSize;
    public const int SignatureAlgorithmRsa2048Sha256Pkcs1 = 1;

    public static byte[] BuildPayload(
        OtaTargetSlot targetSlot,
        string firmwareVersion,
        int firmwareSize,
        byte[] firmwareSha256,
        byte[] iv,
        string minAllowedVersion)
    {
        if (firmwareSha256.Length != 32)
        {
            throw new IapPackageException("image-header requires a 32-byte SHA256 digest.");
        }

        if (iv.Length != 16)
        {
            throw new IapPackageException("image-header requires a 16-byte IV.");
        }

        ValidateVersionText(firmwareVersion, nameof(firmwareVersion));
        ValidateVersionText(minAllowedVersion, nameof(minAllowedVersion));

        byte[] payload = new byte[PayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 1U);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)targetSlot);
        WriteFixedAscii(payload.AsSpan(8, 16), firmwareVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24, 4), (uint)firmwareSize);
        firmwareSha256.CopyTo(payload, 28);
        iv.CopyTo(payload, 60);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(76, 4), SignatureAlgorithmRsa2048Sha256Pkcs1);
        WriteFixedAscii(payload.AsSpan(80, 16), minAllowedVersion);
        return payload;
    }

    public static byte[] BuildBinary(byte[] payload, byte[] signature)
    {
        if (payload.Length != PayloadSize)
        {
            throw new IapPackageException($"image-header payload must be {PayloadSize} bytes.");
        }

        if (signature.Length != SignatureSize)
        {
            throw new IapPackageException($"image-header signature must be {SignatureSize} bytes.");
        }

        byte[] binary = new byte[TotalSize];
        BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(4, 2), HeaderVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(6, 2), (ushort)0U);
        BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(8, 4), TotalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(12, 4), SignatureSize);
        BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(16, 4), 0U);
        payload.CopyTo(binary, HeaderEnvelopeSize);
        signature.CopyTo(binary, HeaderEnvelopeSize + PayloadSize);
        return binary;
    }

    public static OtaImageHeaderInfo Parse(byte[] binary)
    {
        ArgumentNullException.ThrowIfNull(binary);
        if (binary.Length != TotalSize)
        {
            throw new IapPackageException($"image-header.bin size mismatch. Expected {TotalSize}, actual {binary.Length}.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(0, 4));
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(4, 2));
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(8, 4));
        uint signatureLen = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(12, 4));
        if (magic != Magic || version != HeaderVersion || headerSize != TotalSize || signatureLen != SignatureSize)
        {
            throw new IapPackageException("image-header.bin envelope is invalid.");
        }

        byte[] payload = binary.AsSpan(HeaderEnvelopeSize, PayloadSize).ToArray();
        byte[] signature = binary.AsSpan(HeaderEnvelopeSize + PayloadSize, SignatureSize).ToArray();
        int formatVersion = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)));
        int targetSlotValue = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4)));
        int firmwareSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(24, 4)));
        int signatureAlgorithm = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(76, 4)));

        if (formatVersion != 1)
        {
            throw new IapPackageException($"Unsupported image-header format_version: {formatVersion}");
        }

        if (targetSlotValue is < 0 or > 1)
        {
            throw new IapPackageException($"Invalid image-header target_slot: {targetSlotValue}");
        }

        return new OtaImageHeaderInfo
        {
            FormatVersion = formatVersion,
            TargetSlot = (OtaTargetSlot)targetSlotValue,
            FirmwareVersion = ReadFixedAscii(payload.AsSpan(8, 16)),
            FirmwareSize = firmwareSize,
            FirmwareSha256 = Convert.ToHexString(payload.AsSpan(28, 32)),
            IvHex = Convert.ToHexString(payload.AsSpan(60, 16)),
            SignatureAlgorithm = signatureAlgorithm,
            MinAllowedVersion = ReadFixedAscii(payload.AsSpan(80, 16)),
            PayloadBytes = payload,
            SignatureBytes = signature,
            BinaryBytes = binary.ToArray()
        };
    }

    public static OtaTargetSlot InferTargetSlot(string firmwarePath, string outputPackagePath)
    {
        string combined = $"{firmwarePath}|{outputPackagePath}".ToLowerInvariant();
        if (combined.Contains("app1", StringComparison.Ordinal))
        {
            return OtaTargetSlot.App1;
        }

        if (combined.Contains("app2", StringComparison.Ordinal))
        {
            return OtaTargetSlot.App2;
        }

        throw new IapPackageException("TargetSlot is required and could not be inferred from the package path.");
    }

    public static void ValidateVersionText(string? value, string fieldName)
    {
        if (!IsValidVersionText(value))
        {
            throw new IapPackageException($"{fieldName} must be a semantic version like 1.0.3.");
        }
    }

    public static bool IsValidVersionText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();
        int dots = 0;
        bool hasDigit = false;
        foreach (char ch in text)
        {
            if (ch is >= '0' and <= '9')
            {
                hasDigit = true;
                continue;
            }

            if (ch == '.')
            {
                if (!hasDigit || dots >= 2)
                {
                    return false;
                }

                dots++;
                hasDigit = false;
                continue;
            }

            return false;
        }

        return hasDigit && dots == 2 && text.Length < 16;
    }

    public static void VerifyManifestConsistency(OtaImageHeaderInfo header, IapPackageManifest manifest, byte[] firmwareBytes)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(firmwareBytes);

        if (header.SignatureAlgorithm != SignatureAlgorithmRsa2048Sha256Pkcs1)
        {
            throw new IapPackageException($"Unsupported image-header signature_algorithm: {header.SignatureAlgorithm}");
        }

        if (!string.Equals(header.FirmwareSha256, NormalizeHex(manifest.FirmwareSha256), StringComparison.Ordinal))
        {
            throw new IapPackageException("image-header firmware_sha256 does not match manifest.json.");
        }

        if (!string.Equals(header.IvHex, NormalizeHex(manifest.EncryptionIvHex), StringComparison.Ordinal))
        {
            throw new IapPackageException("image-header IV does not match manifest.json.");
        }

        if (header.FirmwareSize != firmwareBytes.Length)
        {
            throw new IapPackageException("image-header firmware_size does not match firmware.bin.");
        }

        string actualSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(firmwareBytes));
        if (!string.Equals(header.FirmwareSha256, actualSha256, StringComparison.Ordinal))
        {
            throw new IapPackageException("image-header firmware_sha256 does not match the actual firmware bytes.");
        }

        ValidateVersionText(header.FirmwareVersion, "image-header firmware_version");
        ValidateVersionText(header.MinAllowedVersion, "image-header min_allowed_version");
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        Encoding.ASCII.GetBytes(value.AsSpan(), destination);
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> source)
    {
        int length = source.IndexOf((byte)0);
        if (length < 0)
        {
            length = source.Length;
        }

        return Encoding.ASCII.GetString(source[..length]).Trim();
    }

    private static string NormalizeHex(string? value)
    {
        return value?
            .Trim()
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant() ?? string.Empty;
    }
}
