namespace MapRepo.NativeStore.Internal.Kernel;

internal static class Crc32C
{
    private const uint Polynomial = 0x82F63B78u;
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var state = Begin();
        state.Append(data);
        return state.Finish();
    }

    public static Crc32CState Begin() => new(uint.MaxValue);

    internal struct Crc32CState
    {
        private uint _crc;

        internal Crc32CState(uint crc) => _crc = crc;

        public void Append(ReadOnlySpan<byte> data)
        {
            var crc = _crc;
            foreach (var value in data)
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            _crc = crc;
        }

        public readonly uint Finish() => ~_crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ Polynomial;
            table[i] = value;
        }
        return table;
    }
}
