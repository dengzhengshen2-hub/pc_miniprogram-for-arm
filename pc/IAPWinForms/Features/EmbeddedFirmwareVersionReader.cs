using System.Text;

namespace IAPWinForms.Features;

internal sealed class EmbeddedFirmwareVersionInfo
{
    public OtaTargetSlot TargetSlot { get; init; }
    public string Version { get; init; } = string.Empty;
    public int Offset { get; init; }
}

internal static class EmbeddedFirmwareVersionReader
{
    private const string MarkerPrefix = "IAPFWV1|";

    public static EmbeddedFirmwareVersionInfo Extract(byte[] firmwareBytes, OtaTargetSlot targetSlot)
    {
        ArgumentNullException.ThrowIfNull(firmwareBytes);

        string slotText = targetSlot == OtaTargetSlot.App1 ? "APP1" : "APP2";
        byte[] markerPrefixBytes = Encoding.ASCII.GetBytes($"{MarkerPrefix}{slotText}|");
        EmbeddedFirmwareVersionInfo? match = null;

        for (int index = 0; index <= firmwareBytes.Length - markerPrefixBytes.Length; index++)
        {
            if (!MatchesAt(firmwareBytes, index, markerPrefixBytes))
            {
                continue;
            }

            int versionStart = index + markerPrefixBytes.Length;
            int versionEnd = versionStart;
            while (versionEnd < firmwareBytes.Length &&
                   firmwareBytes[versionEnd] != (byte)'|' &&
                   firmwareBytes[versionEnd] != 0)
            {
                versionEnd++;
            }

            if (versionEnd >= firmwareBytes.Length || firmwareBytes[versionEnd] != (byte)'|')
            {
                throw new IapPackageException(
                    $"Embedded firmware version marker is malformed near offset 0x{index:X} for {slotText}.");
            }

            string version = Encoding.ASCII.GetString(firmwareBytes, versionStart, versionEnd - versionStart).Trim();
            OtaImageHeaderCodec.ValidateVersionText(version, $"embedded firmware version for {slotText}");

            if (match != null &&
                !string.Equals(match.Version, version, StringComparison.Ordinal))
            {
                throw new IapPackageException(
                    $"Multiple embedded firmware version markers were found for {slotText}: {match.Version} vs {version}.");
            }

            match = new EmbeddedFirmwareVersionInfo
            {
                TargetSlot = targetSlot,
                Version = version,
                Offset = index
            };

            index = versionEnd;
        }

        if (match == null)
        {
            throw new IapPackageException(
                $"Embedded firmware version marker not found for {slotText}. Rebuild the STM32 APP before packaging.");
        }

        return match;
    }

    public static EmbeddedFirmwareVersionInfo ExtractAndValidate(
        byte[] firmwareBytes,
        OtaTargetSlot targetSlot,
        string expectedVersion)
    {
        EmbeddedFirmwareVersionInfo info = Extract(firmwareBytes, targetSlot);
        OtaImageHeaderCodec.ValidateVersionText(expectedVersion, nameof(expectedVersion));

        if (!string.Equals(info.Version, expectedVersion.Trim(), StringComparison.Ordinal))
        {
            throw new IapPackageException(
                $"Embedded firmware version mismatch for {targetSlot}. Firmware.bin={info.Version}, requested package version={expectedVersion}.");
        }

        return info;
    }

    private static bool MatchesAt(byte[] firmwareBytes, int index, byte[] expected)
    {
        for (int offset = 0; offset < expected.Length; offset++)
        {
            if (firmwareBytes[index + offset] != expected[offset])
            {
                return false;
            }
        }

        return true;
    }
}
