using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace Vessel3.Storage;

internal struct Crc32C
{
    private static readonly uint[] Table = BuildTable();
    private uint state = 0xFFFFFFFFu;

    public Crc32C()
    {
    }

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0x82F63B78u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(ReadOnlySpan<byte> data)
    {
        state = Update(state, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetCurrentHashAndReset()
    {
        var v = state ^ 0xFFFFFFFFu;
        state = 0xFFFFFFFFu;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HashToUInt32(ReadOnlySpan<byte> data)
    {
        return Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return crc;

        if (Sse42.X64.IsSupported)
        {
            while (data.Length >= 32)
            {
                crc = (uint)Sse42.X64.Crc32(crc, MemoryMarshal.Read<ulong>(data));
                crc = (uint)Sse42.X64.Crc32(crc, MemoryMarshal.Read<ulong>(data[8..]));
                crc = (uint)Sse42.X64.Crc32(crc, MemoryMarshal.Read<ulong>(data[16..]));
                crc = (uint)Sse42.X64.Crc32(crc, MemoryMarshal.Read<ulong>(data[24..]));
                data = data[32..];
            }
            while (data.Length >= 8)
            {
                crc = (uint)Sse42.X64.Crc32(crc, MemoryMarshal.Read<ulong>(data));
                data = data[8..];
            }
            while (data.Length > 0)
            {
                crc = Sse42.Crc32(crc, data[0]);
                data = data[1..];
            }
            return crc;
        }

        if (ArmCrc32.Arm64.IsSupported)
        {
            while (data.Length >= 32)
            {
                crc = ArmCrc32.Arm64.ComputeCrc32C(crc, MemoryMarshal.Read<ulong>(data));
                crc = ArmCrc32.Arm64.ComputeCrc32C(crc, MemoryMarshal.Read<ulong>(data[8..]));
                crc = ArmCrc32.Arm64.ComputeCrc32C(crc, MemoryMarshal.Read<ulong>(data[16..]));
                crc = ArmCrc32.Arm64.ComputeCrc32C(crc, MemoryMarshal.Read<ulong>(data[24..]));
                data = data[32..];
            }
            while (data.Length >= 8)
            {
                crc = ArmCrc32.Arm64.ComputeCrc32C(crc, MemoryMarshal.Read<ulong>(data));
                data = data[8..];
            }
            while (data.Length > 0)
            {
                crc = ArmCrc32.ComputeCrc32C(crc, data[0]);
                data = data[1..];
            }
            return crc;
        }

        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }
}
