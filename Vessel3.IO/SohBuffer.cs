using System.Buffers;

namespace Vessel3.IO;

internal static class SohBuffer
{
    public const int BlockSize = 65536;

    public static RentedBuffer Rent(int size = BlockSize) => new(size);
}

internal readonly struct RentedBuffer(int size = SohBuffer.BlockSize) : IDisposable
{
    public byte[] Array { get; } = ArrayPool<byte>.Shared.Rent(size);
    public int Length { get; } = size;

    public Memory<byte> Memory => Array.AsMemory(0, Length);
    public Span<byte> Span => Array.AsSpan(0, Length);

    public void Dispose() => ArrayPool<byte>.Shared.Return(Array);
}
