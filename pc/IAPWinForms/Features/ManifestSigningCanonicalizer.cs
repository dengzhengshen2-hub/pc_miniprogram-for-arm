using System.Globalization;
using System.Text;

namespace IAPWinForms.Features;

internal static class ManifestSigningCanonicalizer
{
    public static byte[] BuildIapPackageManifestPayload(IapPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        StringBuilder builder = new();
        Append(builder, "packageFormatVersion", manifest.PackageFormatVersion.ToString(CultureInfo.InvariantCulture));
        Append(builder, "firmwareFileName", manifest.FirmwareFileName);
        Append(builder, "firmwareSize", manifest.FirmwareSize.ToString(CultureInfo.InvariantCulture));
        Append(builder, "firmwareCrc32", NormalizeHex(manifest.FirmwareCrc32));
        Append(builder, "firmwareSha256", NormalizeHex(manifest.FirmwareSha256));
        Append(builder, "signatureAlgorithm", NormalizeUpperInvariant(manifest.SignatureAlgorithm));
        Append(builder, "hashAlgorithm", NormalizeUpperInvariant(manifest.HashAlgorithm));
        Append(builder, "signatureBase64", NormalizeBase64(manifest.SignatureBase64));
        Append(builder, "requiresEncryption", manifest.RequiresEncryption ? "true" : "false");
        Append(builder, "encryptionAlgorithm", manifest.EncryptionAlgorithm);
        Append(builder, "encryptionKeyId", manifest.EncryptionKeyId);
        Append(builder, "encryptionIvHex", NormalizeHex(manifest.EncryptionIvHex));
        Append(builder, "transferEncoding", NormalizeUpperInvariant(manifest.TransferEncoding));
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static byte[] BuildReleaseManifestPayload(ReleaseManifestDocument manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        StringBuilder builder = new();
        Append(builder, "releaseId", manifest.ReleaseId);
        Append(builder, "manifestVersion", manifest.ManifestVersion);
        Append(builder, "productId", manifest.ProductId);
        Append(builder, "hwRev", manifest.HwRev);
        Append(builder, "latestVersion", manifest.LatestVersion);
        Append(builder, "minVersion", manifest.MinVersion);
        Append(builder, "forceUpdate", manifest.ForceUpdate ? "true" : "false");
        Append(builder, "signatureAlgorithm", NormalizeUpperInvariant(manifest.SignatureAlgorithm));
        Append(builder, "hashAlgorithm", NormalizeUpperInvariant(manifest.HashAlgorithm));
        Append(builder, "signedAt", manifest.SignedAt);

        for (int index = 0; index < manifest.Packages.Count; index++)
        {
            ReleaseManifestPackage package = manifest.Packages[index];
            Append(builder, $"packages[{index}].partition", package.Partition);
            Append(builder, $"packages[{index}].version", package.Version);
            Append(builder, $"packages[{index}].url", package.Url);
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void Append(StringBuilder builder, string name, string? value)
    {
        _ = builder.Append(name)
            .Append('=')
            .Append(value?.Trim() ?? string.Empty)
            .Append('\n');
    }

    private static string NormalizeUpperInvariant(string? value)
    {
        return value?.Trim().ToUpperInvariant() ?? string.Empty;
    }

    private static string NormalizeBase64(string? value)
    {
        return value?.Trim() ?? string.Empty;
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
