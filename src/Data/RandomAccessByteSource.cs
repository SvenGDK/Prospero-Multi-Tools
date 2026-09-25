// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Storage;
using System;

namespace ProsperoMultiTools.Data;

/// <summary>
/// A random-access byte source the metadata readers walk to fetch a header, a metadata table,
/// an ISO 9660 directory record, or a CUE-referenced raw sector out of a backup file. The two
/// implementations differ only in how the bytes reach them: <see cref="DeviceSource"/> is the
/// platform's own file stream, used for paths on partitions the module's mount namespace binds;
/// <see cref="BrokerSource"/> pages the bytes through the escalation daemon, used for paths on
/// partitions the module cannot open directly (a game backup landing under <c>/data</c>, a
/// metadata file under <c>/user</c>).
/// </summary>
internal abstract class RandomAccessByteSource : IDisposable
{
    public abstract long Size { get; }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>.</summary>
    /// <returns>The number of bytes read (0 at end of file, negative on failure).</returns>
    public abstract int ReadAt(long offset, Span<byte> buffer);

    public abstract void Dispose();

    /// <summary>
    /// Opens <paramref name="path"/> for random-access read. Returns a broker-backed source when
    /// the path lives on a partition the module cannot open directly and the escalation daemon
    /// is reachable; returns a direct source otherwise. Returns null when the file cannot be
    /// reached over either route.
    /// </summary>
    public static RandomAccessByteSource? Open(string path)
    {
        // The direct route is the fast path: a call that succeeds returns without ever touching
        // the broker. A path on a bound partition (/mnt/usb0, /app0, ...) is served from the
        // module's own file view without a round trip.
        try
        {
            if (FileSystem.Exists(path))
            {
                long size = FileSystem.GetFileSize(path);
                DeviceFileStream stream = FileSystem.OpenRead(path);
                return new DeviceSource(stream, size);
            }
        }
        catch
        {
            // The direct check throws on partitions the module does not bind (an EINVAL from
            // sceKernelOpen surfaces as an exception through FileSystem.Exists). Fall through
            // to the broker route.
        }

        if (SandboxBroker.IsOnBrokerPartition(path) && SandboxBroker.IsReachable())
        {
            BrokerStat stat = SandboxBroker.Stat(path);
            if (stat.Ok && stat.IsRegularFile)
                return new BrokerSource(path, stat.Size);
        }
        return null;
    }

    private sealed class DeviceSource : RandomAccessByteSource
    {
        private readonly DeviceFileStream _stream;
        private readonly long _size;

        public DeviceSource(DeviceFileStream stream, long size)
        {
            _stream = stream;
            _size = size;
        }

        public override long Size => _size;

        public override int ReadAt(long offset, Span<byte> buffer) => _stream.ReadAt(offset, buffer);

        public override void Dispose() => _stream.Dispose();
    }

    private sealed class BrokerSource : RandomAccessByteSource
    {
        private readonly string _path;
        private readonly long _size;

        public BrokerSource(string path, long size)
        {
            _path = path;
            _size = size;
        }

        public override long Size => _size;

        public override int ReadAt(long offset, Span<byte> buffer)
        {
            // The broker's per-request payload is a fraction of one CommandSize buffer; a caller
            // asking for a larger range is paged. This mirrors what SandboxBroker.ReadAllBytes
            // already does for full-file reads, only bounded by the caller's buffer.
            var result = SandboxBroker.ReadRange(_path, offset, buffer);
            return result.Outcome == BrokerOutcome.Ok ? result.BytesRead : -1;
        }

        public override void Dispose() { }
    }
}
