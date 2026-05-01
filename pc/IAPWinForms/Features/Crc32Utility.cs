namespace IAPWinForms.Features;

public static class Crc32Utility
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ value) & 0xFF];
        }

        return ~crc;
    }

    public static uint Compute(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Compute(data.AsSpan());
    }

    public static string ComputeHex(byte[] data)
    {
        return Compute(data).ToString("X8");
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int j = 0; j < 8; j++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
