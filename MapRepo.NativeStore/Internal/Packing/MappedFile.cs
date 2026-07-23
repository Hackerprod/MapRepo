using System.IO.MemoryMappedFiles;

namespace MapRepo.NativeStore.Internal.Packing;

/// <summary>
/// Isolated low-level kernel for immutable pack access. Unsafe code is confined to pointer
/// acquisition/release. CompactManaged uses one compact byte array; MemoryMapped exposes
/// read-only file-system pages without materializing the complete snapshot as managed objects.
/// </summary>
internal unsafe sealed class MappedFile : IReadOnlyPackFile
{
    private readonly MemoryMappedFile? _mapping;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly byte[]? _managed;
    private byte* _pointer;
    private int _disposed;

    private MappedFile(MemoryMappedFile mapping, MemoryMappedViewAccessor view, byte* pointer, long length)
    {
        _mapping = mapping;
        _view = view;
        _pointer = pointer;
        Length = length;
        IsMemoryMapped = true;
    }

    private MappedFile(byte[] managed)
    {
        _managed = managed;
        Length = managed.LongLength;
        IsMemoryMapped = false;
    }

    public long Length { get; }
    public bool IsMemoryMapped { get; }
    public long EstimatedManagedBytes => _managed?.LongLength ?? 0;

    public static MappedFile OpenRead(string path, long maximumLength, NativeMemoryMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Snapshot pack was not found.", path);
        if (info.Length < PackLayout.HeaderSize || info.Length > maximumLength)
            throw new InvalidDataException($"Snapshot pack length {info.Length} is outside the accepted range.");

        if (mode == NativeMemoryMode.CompactManaged)
        {
            if (info.Length > int.MaxValue)
                throw new InvalidDataException("CompactManaged mode cannot load a pack larger than Int32.MaxValue bytes.");
            return new MappedFile(File.ReadAllBytes(path));
        }

        var mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor? view = null;
        byte* acquired = null;
        try
        {
            view = mapping.CreateViewAccessor(0, info.Length, MemoryMappedFileAccess.Read);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref acquired);
            var pointer = (byte*)((nint)acquired + checked((nint)view.PointerOffset));
            return new MappedFile(mapping, view, pointer, info.Length);
        }
        catch
        {
            if (acquired != null && view is not null) view.SafeMemoryMappedViewHandle.ReleasePointer();
            view?.Dispose();
            mapping.Dispose();
            throw;
        }
    }

    public ReadOnlySpan<byte> Slice(long offset, int length)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MappedFile));
        if (offset < 0 || length < 0 || offset > Length - length)
            throw new InvalidDataException($"Pack slice [{offset}, {offset + length}) is outside a {Length}-byte file.");
        if (_managed is not null) return _managed.AsSpan(checked((int)offset), length);
        var pointer = (byte*)((nint)_pointer + checked((nint)offset));
        return new ReadOnlySpan<byte>(pointer, length);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
            _view.Dispose();
        }
        _mapping?.Dispose();
    }
}
