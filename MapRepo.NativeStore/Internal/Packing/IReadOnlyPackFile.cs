namespace MapRepo.NativeStore.Internal.Packing;

internal interface IReadOnlyPackFile : IDisposable
{
    long Length { get; }
    bool IsMemoryMapped { get; }
    long EstimatedManagedBytes { get; }
    ReadOnlySpan<byte> Slice(long offset, int length);
}
