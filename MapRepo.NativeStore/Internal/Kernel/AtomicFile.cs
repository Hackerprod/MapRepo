namespace MapRepo.NativeStore.Internal.Kernel;

internal static class AtomicFile
{
    public static async Task WriteNewAsync(
        string temporaryPath,
        string finalPath,
        byte[] bytes,
        NativeStoreOptions options,
        Action afterBytesWritten,
        Action afterDurable,
        Action afterPublished,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var fileOptions = FileOptions.Asynchronous;
        if (options.WriteThrough) fileOptions |= FileOptions.WriteThrough;
        await using (var stream = new FileStream(
            temporaryPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = fileOptions,
                BufferSize = 64 * 1024
            }))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            afterBytesWritten();
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (options.FlushToDisk) stream.Flush(flushToDisk: true);
            afterDurable();
        }
        File.Move(temporaryPath, finalPath, overwrite: false);
        afterPublished();
    }

    public static async Task ReplaceAsync(
        string temporaryPath,
        string finalPath,
        byte[] bytes,
        NativeStoreOptions options,
        Action afterBytesWritten,
        Action afterDurable,
        Action afterPublished,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var fileOptions = FileOptions.Asynchronous;
        if (options.WriteThrough) fileOptions |= FileOptions.WriteThrough;
        await using (var stream = new FileStream(
            temporaryPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = fileOptions,
                BufferSize = 4096
            }))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            afterBytesWritten();
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (options.FlushToDisk) stream.Flush(flushToDisk: true);
            afterDurable();
        }
        PublishReplacement(temporaryPath, finalPath);
        afterPublished();
    }

    private static void PublishReplacement(string temporaryPath, string finalPath)
    {
        if (!File.Exists(finalPath))
        {
            File.Move(temporaryPath, finalPath, overwrite: false);
            return;
        }

        try
        {
            File.Replace(temporaryPath, finalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(temporaryPath, finalPath, overwrite: true);
        }
    }
}
