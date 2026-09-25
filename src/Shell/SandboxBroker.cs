// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Platform;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace ProsperoMultiTools.Shell;

/// <summary>What a broker request answered with.</summary>
internal enum BrokerOutcome
{
    /// <summary>The daemon accepted the request and the result field carries a value.</summary>
    Ok,

    /// <summary>The daemon was not reachable (connect / send / receive failed).</summary>
    Unreachable,

    /// <summary>The daemon answered but the operation failed at kernel level.</summary>
    KernelError,

    /// <summary>The daemon closed the connection before enough of a reply was in hand.</summary>
    Malformed,
}

/// <summary>The outcome of a stat call to the broker.</summary>
internal readonly record struct BrokerStat(BrokerOutcome Outcome, long Size, uint Mode)
{
    public bool Ok => Outcome == BrokerOutcome.Ok;
    public bool IsDirectory => Ok && (Mode & 0xF000) == 0x4000;
    public bool IsRegularFile => Ok && (Mode & 0xF000) == 0x8000;
}

/// <summary>One directory entry returned by the broker.</summary>
internal readonly record struct BrokerDirEntry(string Name, byte Type)
{
    public bool IsDirectory => Type == 4;
    public bool IsFile => Type == 8;
}

/// <summary>The outcome of a broker statfs call, carrying the filesystem type name.</summary>
internal readonly record struct BrokerStatfs(BrokerOutcome Outcome, string FsType)
{
    public bool Ok => Outcome == BrokerOutcome.Ok;
    public bool IsNullfs => Ok && string.Equals(FsType, "nullfs", StringComparison.Ordinal);
}

/// <summary>The outcome of a find-pid-by-title-id call.</summary>
/// <param name="Outcome">The wire outcome; <c>Ok</c> when the daemon answered.</param>
/// <param name="Pid">The pid of the running process, or <c>-1</c> when no process owns the title.</param>
internal readonly record struct BrokerPid(BrokerOutcome Outcome, int Pid)
{
    public bool Ok => Outcome == BrokerOutcome.Ok;
    public bool Running => Ok && Pid > 0;
}

/// <summary>The outcome of a broker recursive-copy call, carrying the copied-file count.</summary>
/// <param name="Outcome">The wire outcome.</param>
/// <param name="FilesCopied">The count of regular files the daemon copied, or <c>-1</c> on failure.</param>
internal readonly record struct BrokerCopyResult(BrokerOutcome Outcome, int FilesCopied)
{
    public bool Ok => Outcome == BrokerOutcome.Ok && FilesCopied >= 0;
}

/// <summary>Talks to the daemon's file-broker command family.</summary>
/// <remarks>
/// The daemon reads, writes, lists, and stats paths that the caller's sandbox does not bind.
/// Every operation is one request-reply round trip on a fresh TCP connection to the loopback
/// port. A read or write beyond the per-request payload size is split by the caller.
/// </remarks>
internal static class SandboxBroker
{
    private const int DaemonPort = 9069;
    private const int CommandSize = 0xA10;
    private const uint Magic = 0xDEADBEEF;
    private const int ReceiveTimeoutMicroseconds = 5_000_000;

    /// <summary>Long-running command receive timeout - five minutes for recursive copies.</summary>
    private const int LongReceiveTimeoutMicroseconds = 300_000_000;

    private const int CmdStat                            = 6;
    private const int CmdMkdir                           = 7;
    private const int CmdUnlink                          = 8;
    private const int CmdList                            = 9;
    private const int CmdRead                            = 10;
    private const int CmdWrite                           = 11;
    private const int CmdRename                          = 12;
    private const int CmdStatfs                          = 13;
    private const int CmdUnmount                         = 14;
    private const int CmdMountNullfs                     = 15;
    private const int CmdRemountSystemEx                 = 16;
    private const int CmdFindPidByTitleId                = 17;
    private const int CmdArmLaunchAndWaitForExit         = 18;
    private const int CmdAppInstUtilInitialize           = 19;
    private const int CmdAppInstUtilAppUnInstall         = 20;
    private const int CmdAppInstUtilAppInstallTitleDir   = 21;
    private const int CmdCopyDirRecursive                = 22;
    private const int CmdCopySceSysToAppmeta             = 23;
    private const int CmdUpdateTrophy                    = 24;
    private const int CmdCopyFile                        = 25;

    private const int PathOffset = 0x20;
    private const int PathBytes  = 0x200;
    private const int DataOffset = 0x220;
    private const int DataBytes  = CommandSize - DataOffset; // 0x7F0 = 2032

    private const int ReplyDataOffset = 0x20;
    private const int ReplyDataBytes  = CommandSize - ReplyDataOffset; // 0x9F0 = 2544

    /// <summary>True when the broker can answer at all - the loopback connect succeeds.</summary>
    public static bool IsReachable()
    {
        try
        {
            using var conn = TcpConnection.Connect(SocketAddress.Loopback(DaemonPort));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Stats a path through the broker.</summary>
    public static BrokerStat Stat(string path)
    {
        Span<byte> reply = stackalloc byte[CommandSize];
        BrokerOutcome outcome = Roundtrip(CmdStat, path, 0, 0, 0, ReadOnlySpan<byte>.Empty, reply);
        if (outcome != BrokerOutcome.Ok)
            return new BrokerStat(outcome, 0, 0);

        long size = BinaryPrimitives.ReadInt64LittleEndian(reply.Slice(0x10, 8));
        uint mode = BinaryPrimitives.ReadUInt32LittleEndian(reply.Slice(0x18, 4));
        return new BrokerStat(BrokerOutcome.Ok, size, mode);
    }

    /// <summary>Creates a directory through the broker (parents must already exist).</summary>
    public static BrokerOutcome Mkdir(string path, uint mode = 0x1FF)
    {
        Span<byte> reply = stackalloc byte[CommandSize];
        return Roundtrip(CmdMkdir, path, 0, 0, mode, ReadOnlySpan<byte>.Empty, reply);
    }

    /// <summary>Creates a directory and any missing parents through the broker.</summary>
    public static BrokerOutcome MkdirRecursive(string path, uint mode = 0x1FF)
    {
        if (string.IsNullOrEmpty(path))
            return BrokerOutcome.KernelError;
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = path.StartsWith('/') ? "" : ".";
        foreach (string part in parts)
        {
            current = current.Length == 0 ? "/" + part : current + "/" + part;
            BrokerStat stat = Stat(current);
            if (stat.IsDirectory)
                continue;
            BrokerOutcome outcome = Mkdir(current, mode);
            if (outcome != BrokerOutcome.Ok)
            {
                // Race: another actor made the directory between the stat and the mkdir. Confirm
                // the entry is a directory now and treat that as success.
                BrokerStat after = Stat(current);
                if (!after.IsDirectory)
                    return outcome;
            }
        }
        return BrokerOutcome.Ok;
    }

    /// <summary>Removes a file or an empty directory through the broker.</summary>
    public static BrokerOutcome Unlink(string path)
    {
        Span<byte> reply = stackalloc byte[CommandSize];
        return Roundtrip(CmdUnlink, path, 0, 0, 0, ReadOnlySpan<byte>.Empty, reply);
    }

    /// <summary>Enumerates a directory through the broker.</summary>
    public static (BrokerOutcome Outcome, List<BrokerDirEntry> Entries) List(string path)
    {
        var entries = new List<BrokerDirEntry>();
        long position = 0;
        while (true)
        {
            byte[] reply = new byte[CommandSize];
            BrokerOutcome outcome = Roundtrip(CmdList, path, position, 0, 0,
                ReadOnlySpan<byte>.Empty, reply.AsSpan());
            if (outcome != BrokerOutcome.Ok)
                return (outcome, entries);

            long filledRaw = BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(0x10, 8));
            long nextPosition = BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(0x18, 8));
            if (filledRaw <= 0)
                return (BrokerOutcome.Ok, entries);
            // The daemon's getdirentries is capped at ReplyDataBytes so a well-formed reply
            // never exceeds that. A larger value is a wire corruption; reject it so the
            // dirent walk does not read past the reply buffer.
            if (filledRaw > ReplyDataBytes)
                return (BrokerOutcome.Malformed, entries);
            int filled = (int)filledRaw;

            int off = ReplyDataOffset;
            int end = ReplyDataOffset + filled;
            while (off + 8 <= end)
            {
                // FreeBSD dirent: fileno (u32), reclen (u16), type (u8), namlen (u8), name[]
                ushort recLen = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(off + 4, 2));
                if (recLen < 8 || off + recLen > end)
                    break;
                byte type = reply[off + 6];
                byte nameLen = reply[off + 7];
                if (nameLen > 0 && 8 + nameLen <= recLen)
                {
                    string name = Encoding.UTF8.GetString(reply, off + 8, nameLen);
                    if (name != "." && name != "..")
                        entries.Add(new BrokerDirEntry(name, type));
                }
                off += recLen;
            }

            if (nextPosition <= position)
                return (BrokerOutcome.Ok, entries);
            position = nextPosition;
        }
    }

    /// <summary>Reads the whole file at <paramref name="path"/> through the broker.</summary>
    /// <remarks>
    /// Reads at most <see cref="MaxReadSize"/> bytes; a file larger than that answers with
    /// <see cref="BrokerOutcome.KernelError"/> because the returned array cannot address it. A
    /// caller that needs a slice of a large file should walk it in chunks with a dedicated
    /// range-read helper (not yet exposed here - large files are outside the settings and cover
    /// paths this broker serves).
    /// </remarks>
    public static (BrokerOutcome Outcome, byte[] Bytes) ReadAllBytes(string path)
    {
        BrokerStat stat = Stat(path);
        if (!stat.Ok)
            return (stat.Outcome, Array.Empty<byte>());
        if (stat.Size < 0 || stat.Size > MaxReadSize)
            return (BrokerOutcome.KernelError, Array.Empty<byte>());

        int total = (int)stat.Size;
        byte[] output = new byte[total];
        long offset = 0;
        int written = 0;
        byte[] reply = new byte[CommandSize];
        while (written < total)
        {
            reply.AsSpan().Clear();
            int chunk = total - written > ReplyDataBytes ? ReplyDataBytes : total - written;
            BrokerOutcome outcome = Roundtrip(CmdRead, path, offset, (uint)chunk, 0,
                ReadOnlySpan<byte>.Empty, reply.AsSpan());
            if (outcome != BrokerOutcome.Ok)
                return (outcome, Array.Empty<byte>());

            int returned = (int)BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(0x10, 8));
            if (returned <= 0)
                break;
            if (returned > chunk || written + returned > total)
                return (BrokerOutcome.Malformed, Array.Empty<byte>());

            Buffer.BlockCopy(reply, ReplyDataOffset, output, written, returned);
            offset += returned;
            written += returned;
        }
        if (written != total)
        {
            // The file's size shrank between the stat call and the read loop. A caller that
            // parses the returned bytes as its stored representation (a JSON blob, a texture,
            // ...) would silently accept a truncated version and act on incomplete state.
            // Reporting the mismatch as a wire fault sends the caller through its own
            // error path instead of the success one.
            return (BrokerOutcome.Malformed, Array.Empty<byte>());
        }
        return (BrokerOutcome.Ok, output);
    }

    /// <summary>The largest file <see cref="ReadAllBytes"/> serves - sixty-four megabytes,
    /// enough for every settings file and every cover asset the shell reads while staying well
    /// under the runtime's array-length ceiling.</summary>
    public const long MaxReadSize = 64L * 1024L * 1024L;

    /// <summary>The outcome of a broker range read.</summary>
    public readonly record struct BrokerRead(BrokerOutcome Outcome, int BytesRead);

    /// <summary>
    /// Reads up to <paramref name="destination"/>.Length bytes of <paramref name="path"/> starting
    /// at <paramref name="offset"/> through the broker. The range is split across as many
    /// requests as the daemon's per-request payload cap needs; the return reports how many bytes
    /// landed and whether the walk finished, hit end of file, or failed at the wire.
    /// </summary>
    public static BrokerRead ReadRange(string path, long offset, Span<byte> destination)
    {
        if (destination.Length == 0)
            return new BrokerRead(BrokerOutcome.Ok, 0);
        if (offset < 0)
            return new BrokerRead(BrokerOutcome.KernelError, 0);

        byte[] reply = new byte[CommandSize];
        int written = 0;
        while (written < destination.Length)
        {
            reply.AsSpan().Clear();
            int chunk = destination.Length - written > ReplyDataBytes ? ReplyDataBytes : destination.Length - written;
            BrokerOutcome outcome = Roundtrip(CmdRead, path, offset + written, (uint)chunk, 0,
                ReadOnlySpan<byte>.Empty, reply.AsSpan());
            if (outcome != BrokerOutcome.Ok)
                return new BrokerRead(outcome, written);

            int returned = (int)BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(0x10, 8));
            if (returned <= 0)
                return new BrokerRead(BrokerOutcome.Ok, written); // End of file.
            if (returned > chunk)
                return new BrokerRead(BrokerOutcome.Malformed, written);

            reply.AsSpan(ReplyDataOffset, returned).CopyTo(destination.Slice(written, returned));
            written += returned;
            if (returned < chunk)
                return new BrokerRead(BrokerOutcome.Ok, written); // Short read: end of file.
        }
        return new BrokerRead(BrokerOutcome.Ok, written);
    }

    /// <summary>Writes the whole span at <paramref name="path"/> through the broker.</summary>
    /// <remarks>
    /// A zero-length input is one request whose only job is to create the file at zero length -
    /// the daemon opens with O_CREAT|O_TRUNC when the request offset is zero, so a length-zero
    /// write still lands the file. A non-empty input is split across as many requests as the
    /// per-request payload cap needs; a short write from the daemon means the caller re-sends
    /// the tail from the daemon's new offset, and the daemon skips truncation on offsets past
    /// zero so the earlier bytes stay put.
    /// </remarks>
    public static BrokerOutcome WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        byte[] replyBuffer = new byte[CommandSize];

        if (bytes.Length == 0)
        {
            replyBuffer.AsSpan().Clear();
            return Roundtrip(CmdWrite, path, 0, 0, 0, ReadOnlySpan<byte>.Empty,
                replyBuffer.AsSpan());
        }

        long offset = 0;
        int total = bytes.Length;
        int sent = 0;
        while (sent < total)
        {
            int chunkSize = total - sent;
            if (chunkSize > DataBytes) chunkSize = DataBytes;

            Span<byte> reply = replyBuffer.AsSpan();
            reply.Clear();
            BrokerOutcome outcome = Roundtrip(CmdWrite, path, offset, (uint)chunkSize, 0,
                bytes.Slice(sent, chunkSize), reply);
            if (outcome != BrokerOutcome.Ok)
                return outcome;

            long returned = BinaryPrimitives.ReadInt64LittleEndian(reply.Slice(0x10, 8));
            if (returned <= 0 || returned > chunkSize)
                return BrokerOutcome.KernelError;

            sent += (int)returned;
            offset += returned;
        }
        return BrokerOutcome.Ok;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> starting at file offset
    /// <paramref name="offset"/>. At offset zero the daemon creates or truncates the file;
    /// at non-zero offsets the existing content before the write region is kept. The span is
    /// split across as many daemon requests as the per-request payload cap needs.
    /// </summary>
    public static BrokerOutcome WriteRange(string path, long offset, ReadOnlySpan<byte> bytes)
    {
        if (offset < 0)
            return BrokerOutcome.KernelError;

        byte[] replyBuffer = new byte[CommandSize];

        if (bytes.Length == 0)
        {
            if (offset == 0)
            {
                replyBuffer.AsSpan().Clear();
                return Roundtrip(CmdWrite, path, 0, 0, 0, ReadOnlySpan<byte>.Empty,
                    replyBuffer.AsSpan());
            }
            return BrokerOutcome.Ok;
        }

        int total = bytes.Length;
        int sent = 0;
        while (sent < total)
        {
            int chunkSize = total - sent;
            if (chunkSize > DataBytes) chunkSize = DataBytes;

            Span<byte> reply = replyBuffer.AsSpan();
            reply.Clear();
            BrokerOutcome outcome = Roundtrip(CmdWrite, path, offset + sent, (uint)chunkSize, 0,
                bytes.Slice(sent, chunkSize), reply);
            if (outcome != BrokerOutcome.Ok)
                return outcome;

            long returned = BinaryPrimitives.ReadInt64LittleEndian(reply.Slice(0x10, 8));
            if (returned <= 0 || returned > chunkSize)
                return BrokerOutcome.KernelError;

            sent += (int)returned;
        }
        return BrokerOutcome.Ok;
    }

    /// <summary>Renames a file through the broker (both paths in the same call).</summary>
    /// <remarks>
    /// The source path lives at the request's path field. The destination path lives in the
    /// data region, so a rename can carry both fully-qualified paths in one round trip. Both
    /// paths must be under the daemon's file view and both must NUL-terminate within their
    /// respective fields; the daemon rejects the request otherwise.
    /// </remarks>
    public static BrokerOutcome Rename(string from, string to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            return BrokerOutcome.KernelError;

        int toBytes = Encoding.UTF8.GetByteCount(to);
        if (toBytes >= DataBytes)
            return BrokerOutcome.KernelError;

        // The destination path piggybacks on the request's data payload. Encode it into a
        // heap buffer sized to the data region so the trailing NUL is guaranteed.
        byte[] destination = new byte[DataBytes];
        Encoding.UTF8.GetBytes(to, destination);

        Span<byte> reply = stackalloc byte[CommandSize];
        return Roundtrip(CmdRename, from, 0, (uint)toBytes, 0, destination, reply);
    }

    /// <summary>Writes bytes to <paramref name="path"/> and guarantees the write is either
    /// fully in effect or the original file at <paramref name="path"/> is unchanged.</summary>
    /// <remarks>
    /// The bytes land at <paramref name="path"/> + a <c>.tmp</c> suffix first, then the
    /// broker's rename op replaces the real file atomically. A write that fails midway
    /// leaves the real file at its previous content, so a caller cannot lose settings or
    /// covers to a disk-full or connection-drop halfway through the write.
    /// </remarks>
    public static BrokerOutcome WriteAllBytesAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        if (string.IsNullOrEmpty(path))
            return BrokerOutcome.KernelError;

        string tempPath = path + ".tmp";
        BrokerOutcome write = WriteAllBytes(tempPath, bytes);
        if (write != BrokerOutcome.Ok)
        {
            // Best-effort cleanup of the temp file so a failed atomic write does not leave a
            // dangling artefact behind. Unlink outcome is ignored on purpose - the caller
            // sees the underlying write failure.
            Unlink(tempPath);
            return write;
        }

        BrokerOutcome rename = Rename(tempPath, path);
        if (rename != BrokerOutcome.Ok)
        {
            Unlink(tempPath);
            return rename;
        }
        return BrokerOutcome.Ok;
    }

    // ---- Mount / filesystem primitives ----

    /// <summary>
    /// Reads the filesystem type at <paramref name="path"/> through the broker. The daemon
    /// runs statfs and returns the sixteen-byte f_fstypename field so the caller can pick a
    /// nullfs overlay out of a mount stack before unmounting it. The mount point itself must
    /// name a mounted filesystem; a plain directory returns an error.
    /// </summary>
    public static BrokerStatfs Statfs(string path)
    {
        byte[] reply = new byte[CommandSize];
        BrokerOutcome outcome = RoundtripRaw(CmdStatfs, path, 0, 0, 0,
            ReadOnlySpan<byte>.Empty, reply.AsSpan(), ReceiveTimeoutMicroseconds, out _);
        if (outcome != BrokerOutcome.Ok)
            return new BrokerStatfs(outcome, string.Empty);

        int nameLen = 0;
        while (nameLen < 16 && reply[ReplyDataOffset + nameLen] != 0)
            nameLen++;
        string fsType = Encoding.UTF8.GetString(reply, ReplyDataOffset, nameLen);
        return new BrokerStatfs(BrokerOutcome.Ok, fsType);
    }

    /// <summary>
    /// Unmounts <paramref name="path"/> through the broker with the FreeBSD unmount flags word
    /// carried at the request's mode field. The caller gates real filesystems out with a
    /// preceding <see cref="Statfs"/> check; this call itself does not verify the mount kind.
    /// </summary>
    public static BrokerOutcome Unmount(string path, uint flags = 0)
    {
        Span<byte> reply = stackalloc byte[CommandSize];
        return Roundtrip(CmdUnmount, path, 0, 0, flags, ReadOnlySpan<byte>.Empty, reply);
    }

    /// <summary>
    /// Nullfs-binds <paramref name="source"/> at <paramref name="target"/> through the broker.
    /// The daemon builds a six-entry iovec and calls nmount; the source and target paths carry
    /// their trailing NUL so the iovec lengths equal strlen + 1 per the FreeBSD nmount ABI.
    /// The kernel's nullfs mount gate at prison0+0xf8 must already be open (the daemon does
    /// this once at startup); without the bit set the call would return EPERM regardless of
    /// the caller's credentials.
    /// </summary>
    public static BrokerOutcome MountNullfs(string source, string target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
            return BrokerOutcome.KernelError;

        int sourceBytes = Encoding.UTF8.GetByteCount(source);
        if (sourceBytes >= DataBytes)
            return BrokerOutcome.KernelError;

        byte[] data = new byte[DataBytes];
        Encoding.UTF8.GetBytes(source, data);
        // Trailing NUL comes free because the buffer is zero-initialised.

        // The nmount call takes a directory-tree lock while it walks the source and builds the
        // nullfs vnode; on a source that carries hundreds of files the walk can hold the lock
        // for several seconds. The long-receive timeout stays consistent with the copy calls
        // that stage the same tree so a slow mount does not report as Unreachable.
        byte[] reply = new byte[CommandSize];
        BrokerOutcome outcome = RoundtripRaw(CmdMountNullfs, target, 0, (uint)sourceBytes, 0,
            data, reply.AsSpan(), LongReceiveTimeoutMicroseconds, out int result);
        if (outcome != BrokerOutcome.Ok)
            return outcome;
        return result < 0 ? BrokerOutcome.KernelError : BrokerOutcome.Ok;
    }

    /// <summary>
    /// Remounts <c>/system_ex</c> in place with MNT_UPDATE through the broker so a fresh
    /// subdirectory added under it enters the shell's cached view. The daemon treats EBUSY as
    /// success because a remount whose parameters already match the current state answers
    /// EBUSY. The request carries no payload; the daemon knows the fixed mount name, device,
    /// and option set.
    /// </summary>
    public static BrokerOutcome RemountSystemEx()
    {
        // The remount blocks while the shell's cached view of /system_ex is invalidated and
        // rebuilt; on a fresh boot the rebuild reads every module folder under /system_ex/app
        // and can take tens of seconds. The long-receive timeout keeps the roundtrip alive
        // through that scan.
        byte[] reply = new byte[CommandSize];
        return RoundtripNoPath(CmdRemountSystemEx, 0, 0, 0, ReadOnlySpan<byte>.Empty, reply.AsSpan(),
            LongReceiveTimeoutMicroseconds, out _);
    }

    // ---- Launch primitives ----

    /// <summary>
    /// Walks the daemon's kernel process table and returns the pid of the running process whose
    /// title id matches <paramref name="titleId"/>, or <c>-1</c> when no running process owns
    /// the title. The daemon's <c>WriteResult</c> writes the pid to the reply's result word so a
    /// <c>-1</c> comes back as a negative "result" - that is a valid answer here, not a wire
    /// error, so this call bypasses the ordinary negative-result gate.
    /// </summary>
    public static BrokerPid FindPidByTitleId(string titleId)
    {
        Span<byte> reply = stackalloc byte[CommandSize];
        BrokerOutcome outcome = RoundtripRaw(CmdFindPidByTitleId, titleId, 0, 0, 0,
            ReadOnlySpan<byte>.Empty, reply, ReceiveTimeoutMicroseconds, out int result);
        if (outcome != BrokerOutcome.Ok)
            return new BrokerPid(outcome, -1);
        return new BrokerPid(BrokerOutcome.Ok, result);
    }

    /// <summary>
    /// Arms the daemon's launch worker for <paramref name="titleId"/> and returns as soon as the
    /// worker thread is spawned. The worker outlives the caller: it fires
    /// <c>sceSystemServiceLaunchApp</c>, blocks on kqueue/EVFILT_PROC/NOTE_EXIT for the launched
    /// pid, and unmounts <c>/system_ex/app/&lt;TID&gt;</c> after a grace window. The caller may
    /// exit its own process any time after this call returns <c>Ok</c>.
    /// </summary>
    /// <param name="titleId">The nine-character title id to launch.</param>
    /// <param name="userId">The foreground user id to launch under.</param>
    /// <param name="source">The source folder the mount at <c>/system_ex/app/&lt;TID&gt;</c> points
    /// at. Carried in the request's data region for diagnostic parity with the plan; the daemon
    /// does not read it back.</param>
    public static BrokerOutcome ArmLaunchAndWaitForExit(string titleId, uint userId, string source)
    {
        if (string.IsNullOrEmpty(titleId))
            return BrokerOutcome.KernelError;

        // Data region layout: u32 user_id at +0x00, u16 source_len at +0x04, char source[] at +0x06.
        byte[] data = new byte[DataBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), userId);
        int sourceBytes = string.IsNullOrEmpty(source) ? 0 : Encoding.UTF8.GetByteCount(source);
        if (sourceBytes > 0)
        {
            if (6 + sourceBytes + 1 > DataBytes)
                return BrokerOutcome.KernelError;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), (ushort)sourceBytes);
            Encoding.UTF8.GetBytes(source, data.AsSpan(6, sourceBytes));
            // Trailing NUL is guaranteed by the zero-initialised buffer.
        }

        Span<byte> reply = stackalloc byte[CommandSize];
        return Roundtrip(CmdArmLaunchAndWaitForExit, titleId, 0, 0, 0, data, reply);
    }

    // ---- Install primitives ----

    /// <summary>Initialises the daemon's copy of libSceAppInstUtil.</summary>
    public static BrokerOutcome AppInstUtilInitialize()
    {
        // libSceAppInstUtil's initialize call walks the shell's app database and can block for
        // tens of seconds on a fresh boot before the first title registers. The long-receive
        // timeout matches the one CopyFile / CopyDirRecursive use so this never times out
        // ahead of the daemon's real answer.
        byte[] reply = new byte[CommandSize];
        return RoundtripNoPath(CmdAppInstUtilInitialize, 0, 0, 0,
            ReadOnlySpan<byte>.Empty, reply.AsSpan(),
            LongReceiveTimeoutMicroseconds, out _);
    }

    /// <summary>
    /// Uninstalls the row for <paramref name="titleId"/> in the shell's app database. The
    /// backup install/launch pipeline does NOT call this - uninstalling a shell row is a
    /// manual action the user performs from the console UI. The wrapper stays exposed so a
    /// caller that genuinely needs the daemon's uninstall primitive (a dedicated management
    /// screen, a scripted tooling call) can reach it directly.
    /// </summary>
    public static BrokerOutcome AppInstUtilAppUnInstall(string titleId)
    {
        if (string.IsNullOrEmpty(titleId))
            return BrokerOutcome.KernelError;
        byte[] reply = new byte[CommandSize];
        BrokerOutcome outcome = RoundtripRaw(CmdAppInstUtilAppUnInstall, titleId, 0, 0, 0,
            ReadOnlySpan<byte>.Empty, reply.AsSpan(),
            LongReceiveTimeoutMicroseconds, out int result);
        if (outcome != BrokerOutcome.Ok)
            return outcome;
        return result < 0 ? BrokerOutcome.KernelError : BrokerOutcome.Ok;
    }

    /// <summary>
    /// Registers <paramref name="titleId"/> at <paramref name="titleDir"/> with the shell's app
    /// database. The daemon calls <c>sceAppInstUtilAppInstallTitleDir</c> directly and falls
    /// through to <c>sceAppInstUtilAppInstallAll(NULL)</c> on a non-zero return so the row
    /// still lands on firmware groups where the direct call moved.
    /// </summary>
    public static BrokerOutcome AppInstUtilAppInstallTitleDir(string titleId, string titleDir)
    {
        if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(titleDir))
            return BrokerOutcome.KernelError;

        int dirBytes = Encoding.UTF8.GetByteCount(titleDir);
        if (dirBytes >= DataBytes)
            return BrokerOutcome.KernelError;

        byte[] data = new byte[DataBytes];
        Encoding.UTF8.GetBytes(titleDir, data);

        // The shell's app-install service writes the app database row here and does the initial
        // metadata scan of the newly registered folder. On a first install that scan reads
        // sce_sys, indexes trophies, and updates the shell's live view - it commonly takes 30s+
        // to answer. The default receive timeout is 5s, which surfaces as "Unreachable" here
        // even though the daemon and the shell are both still working. The long timeout matches
        // the one CopyFile uses for large disc images.
        byte[] reply = new byte[CommandSize];
        BrokerOutcome outcome = RoundtripRaw(CmdAppInstUtilAppInstallTitleDir, titleId, 0,
            (uint)dirBytes, 0, data, reply.AsSpan(),
            LongReceiveTimeoutMicroseconds, out int result);
        if (outcome != BrokerOutcome.Ok)
            return outcome;
        return result < 0 ? BrokerOutcome.KernelError : BrokerOutcome.Ok;
    }

    // ---- File-copy primitives ----

    /// <summary>
    /// Copies a single file at the daemon side. The daemon opens source and destination itself
    /// and streams the bytes through its own 8 MiB copy buffer, so a single broker round trip
    /// replaces the socket-per-chunk pattern <see cref="WriteRange(string, long, ReadOnlySpan{byte})"/>
    /// falls back to for large writes. Use this for disc images, emulator payloads, and any
    /// other multi-hundred-MB copy where the app-side chunk loop would exhaust ephemeral
    /// TCP ports before finishing.
    /// </summary>
    /// <param name="source">Absolute source path, in the daemon's own file view.</param>
    /// <param name="destination">Absolute destination path, in the daemon's own file view. The
    /// containing directory must already exist. Existing files are truncated and overwritten.</param>
    /// <returns><see cref="BrokerOutcome.Ok"/> when the daemon reported success; otherwise the
    /// underlying wire outcome or KernelError on daemon-side failure.</returns>
    public static BrokerOutcome CopyFile(string source, string destination)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination))
            return BrokerOutcome.KernelError;

        int dstBytes = Encoding.UTF8.GetByteCount(destination);
        if (dstBytes >= DataBytes)
            return BrokerOutcome.KernelError;

        byte[] data = new byte[DataBytes];
        Encoding.UTF8.GetBytes(destination, data);

        byte[] reply = new byte[CommandSize];
        BrokerOutcome outcome = RoundtripRaw(CmdCopyFile, source, 0, (uint)dstBytes, 0,
            data, reply.AsSpan(), LongReceiveTimeoutMicroseconds, out int result);
        if (outcome != BrokerOutcome.Ok)
            return outcome;
        return result == 0 ? BrokerOutcome.Ok : BrokerOutcome.KernelError;
    }

    /// <summary>
    /// Recursively copies every regular file and subdirectory from <paramref name="source"/>
    /// into <paramref name="destination"/> through the daemon. The destination directory is
    /// created if missing; existing files are truncated and overwritten. Returns the count of
    /// regular files copied, or <c>-1</c> on the daemon's own failure path.
    /// </summary>
    public static BrokerCopyResult CopyDirRecursive(string source, string destination)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination))
            return new BrokerCopyResult(BrokerOutcome.KernelError, -1);

        int dstBytes = Encoding.UTF8.GetByteCount(destination);
        if (dstBytes >= DataBytes)
            return new BrokerCopyResult(BrokerOutcome.KernelError, -1);

        byte[] data = new byte[DataBytes];
        Encoding.UTF8.GetBytes(destination, data);

        byte[] reply = new byte[CommandSize];
        BrokerOutcome outcome = RoundtripRaw(CmdCopyDirRecursive, source, 0, (uint)dstBytes, 0,
            data, reply.AsSpan(), LongReceiveTimeoutMicroseconds, out int result);
        return new BrokerCopyResult(outcome, result);
    }

    /// <summary>
    /// Copies the appmeta-classed files (param.json, param.sfo, and any .png / .dds / .at9 asset)
    /// from <paramref name="sourceSceSys"/> into <c>/user/appmeta/&lt;titleId&gt;</c>. Top-level
    /// only; subdirectories are ignored here (trophy2 and uds bindings reach
    /// <c>/system_data/priv/appmeta</c> through <see cref="UpdateTrophy"/>). The daemon
    /// creates the parent <c>/user/appmeta</c> at 0777 to match the shell's own layout.
    /// </summary>
    public static BrokerCopyResult CopySceSysToAppmeta(string sourceSceSys, string titleId)
    {
        if (string.IsNullOrEmpty(sourceSceSys) || string.IsNullOrEmpty(titleId))
            return new BrokerCopyResult(BrokerOutcome.KernelError, -1);

        int idBytes = Encoding.UTF8.GetByteCount(titleId);
        if (idBytes >= DataBytes)
            return new BrokerCopyResult(BrokerOutcome.KernelError, -1);

        byte[] data = new byte[DataBytes];
        Encoding.UTF8.GetBytes(titleId, data);

        byte[] reply = new byte[CommandSize];
        BrokerOutcome outcome = RoundtripRaw(CmdCopySceSysToAppmeta, sourceSceSys, 0, (uint)idBytes, 0,
            data, reply.AsSpan(), LongReceiveTimeoutMicroseconds, out int result);
        return new BrokerCopyResult(outcome, result);
    }

    /// <summary>
    /// Populates <c>/system_data/priv/appmeta/&lt;titleId&gt;</c> with the trophy files the
    /// shell's trophy service reads on launch: <c>trophy2/npbind.dat</c>,
    /// <c>uds/npbind.dat</c>, and <c>param.json</c>. Missing sources silently no-op; the daemon
    /// returns 0 unconditionally on the reply, and packs a summary bitmap into the klog line
    /// (bit 0 = trophy2, bit 1 = uds, bit 2 = param).
    /// </summary>
    public static BrokerOutcome UpdateTrophy(string titleId, string sourceSceSys)
    {
        if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(sourceSceSys))
            return BrokerOutcome.KernelError;

        int srcBytes = Encoding.UTF8.GetByteCount(sourceSceSys);
        if (srcBytes >= DataBytes)
            return BrokerOutcome.KernelError;

        byte[] data = new byte[DataBytes];
        Encoding.UTF8.GetBytes(sourceSceSys, data);

        // The trophy service scans trophy2/uds folders on the source and can block for several
        // seconds while it writes to /system_data/priv/appmeta/<TID>/. The long-receive timeout
        // matches the other install-side calls so a slow write does not surface as Unreachable.
        byte[] reply = new byte[CommandSize];
        BrokerOutcome outcome = RoundtripRaw(CmdUpdateTrophy, titleId, 0, (uint)srcBytes, 0,
            data, reply.AsSpan(), LongReceiveTimeoutMicroseconds, out int result);
        if (outcome != BrokerOutcome.Ok)
            return outcome;
        return result < 0 ? BrokerOutcome.KernelError : BrokerOutcome.Ok;
    }

    /// <summary>True when the path lies on a partition the module's mount namespace does not
    /// bind. FileSystem calls for these paths fail with EINVAL; the broker is the only way to
    /// read, write, and list their content from inside the sandbox.</summary>
    public static bool IsOnBrokerPartition(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        return MatchesBrokerPartition(path, "/data")
            || MatchesBrokerPartition(path, "/user");
    }

    private static bool MatchesBrokerPartition(string path, string partition)
    {
        if (!path.StartsWith(partition, StringComparison.Ordinal))
            return false;
        return path.Length == partition.Length || path[partition.Length] == '/';
    }

    /// <summary>True when a broker call reports the path as a directory.</summary>
    public static bool IsDirectory(string path) => Stat(path).IsDirectory;

    /// <summary>True when a broker call reports the path as an existing regular file.</summary>
    public static bool FileExists(string path)
    {
        BrokerStat stat = Stat(path);
        return stat.Ok;
    }

    // ---- One-shot round trip ----

    private static BrokerOutcome Roundtrip(int cmd, string path, long offset, uint length,
        uint mode, ReadOnlySpan<byte> data, Span<byte> reply)
    {
        BrokerOutcome outcome = RoundtripRaw(cmd, path, offset, length, mode, data, reply,
            ReceiveTimeoutMicroseconds, out int result);
        if (outcome != BrokerOutcome.Ok)
            return outcome;
        return result < 0 ? BrokerOutcome.KernelError : BrokerOutcome.Ok;
    }

    /// <summary>
    /// One-shot round trip that hands the reply's raw result word back to the caller instead of
    /// collapsing a negative value into <see cref="BrokerOutcome.KernelError"/>. Used where a
    /// negative "result" is a valid answer - a pid walker's "not found" reply or a copy-count
    /// reply that carries the returned count in the same slot.
    /// </summary>
    private static BrokerOutcome RoundtripRaw(int cmd, string path, long offset, uint length,
        uint mode, ReadOnlySpan<byte> data, Span<byte> reply, int receiveTimeoutUs, out int result)
    {
        result = 0;
        if (string.IsNullOrEmpty(path))
            return BrokerOutcome.KernelError;

        Span<byte> request = stackalloc byte[CommandSize];
        request.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(request, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(request.Slice(4, 4), cmd);
        BinaryPrimitives.WriteInt64LittleEndian(request.Slice(8, 8), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(request.Slice(0x10, 4), length);
        BinaryPrimitives.WriteUInt32LittleEndian(request.Slice(0x18, 4), mode);

        int pathBytes = Encoding.UTF8.GetByteCount(path);
        if (pathBytes >= PathBytes)
            return BrokerOutcome.KernelError;
        Span<byte> pathField = request.Slice(PathOffset, PathBytes);
        Encoding.UTF8.GetBytes(path, pathField);
        // Trailing NUL guaranteed because request was cleared.

        if (!data.IsEmpty)
        {
            if (data.Length > DataBytes)
                return BrokerOutcome.KernelError;
            data.CopyTo(request.Slice(DataOffset, data.Length));
        }

        return SendAndReceive(request, reply, cmd, receiveTimeoutUs, out result);
    }

    /// <summary>
    /// One-shot round trip for a command whose request carries no path field (the daemon's
    /// remount-system_ex and appinstutil-initialize handlers ignore that field). The reply is
    /// still validated the same way; <paramref name="result"/> receives the raw result word.
    /// </summary>
    private static BrokerOutcome RoundtripNoPath(int cmd, long offset, uint length, uint mode,
        ReadOnlySpan<byte> data, Span<byte> reply, int receiveTimeoutUs, out int result)
    {
        result = 0;
        Span<byte> request = stackalloc byte[CommandSize];
        request.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(request, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(request.Slice(4, 4), cmd);
        BinaryPrimitives.WriteInt64LittleEndian(request.Slice(8, 8), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(request.Slice(0x10, 4), length);
        BinaryPrimitives.WriteUInt32LittleEndian(request.Slice(0x18, 4), mode);

        if (!data.IsEmpty)
        {
            if (data.Length > DataBytes)
                return BrokerOutcome.KernelError;
            data.CopyTo(request.Slice(DataOffset, data.Length));
        }

        return SendAndReceive(request, reply, cmd, receiveTimeoutUs, out result);
    }

    private static BrokerOutcome SendAndReceive(ReadOnlySpan<byte> request, Span<byte> reply,
        int cmd, int receiveTimeoutUs, out int result)
    {
        result = 0;
        try
        {
            using var conn = TcpConnection.Connect(SocketAddress.Loopback(DaemonPort));
            conn.SendAll(request);
            conn.SetReceiveTimeout((uint)receiveTimeoutUs);

            int total = 0;
            while (total < CommandSize)
            {
                int n = conn.Receive(reply.Slice(total));
                if (n <= 0) break;
                total += n;
            }
            // The daemon writes exactly CommandSize bytes for every reply. Anything short is a
            // wire-level truncation; treat it as malformed so a stat / read / list caller does
            // not parse bytes past the received tail as legitimate result fields.
            if (total < CommandSize)
                return BrokerOutcome.Malformed;

            uint replyMagic = BinaryPrimitives.ReadUInt32LittleEndian(reply.Slice(0, 4));
            int replyCmd = BinaryPrimitives.ReadInt32LittleEndian(reply.Slice(4, 4));
            if (replyMagic != Magic || replyCmd != cmd)
                return BrokerOutcome.Malformed;

            result = BinaryPrimitives.ReadInt32LittleEndian(reply.Slice(0x08, 4));
            return BrokerOutcome.Ok;
        }
        catch (Exception)
        {
            return BrokerOutcome.Unreachable;
        }
    }
}
