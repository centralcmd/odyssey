using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Odyssey.Core.FileStorage;

namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// The calendar import's bounded grouper (issue #519 §3 component 6; v3 §E, v4 §7, v5 §1): every <c>VEVENT</c> of an
/// uploaded <c>.ics</c> regrouped by UID with an external merge sort, so a series' master, its <c>EXDATE</c>s and its
/// <c>RECURRENCE-ID</c> overrides are applied together in any file order, while memory never grows with the file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pass 1</b> scans the buffered upload byte by byte — the structural rules and caps of
/// <see cref="IcsComponentSplitter"/>, unchanged — and writes one 40-byte record per <c>VEVENT</c>:
/// <c>(uidHash 16 B, isOverride 1 B, recurrenceKey 8 B, fileOffset 8 B, length 4 B)</c>, padded. Records are sorted
/// in runs of at most <see cref="RunBufferBytes"/> and spilled. The UID hash is HMAC-SHA256 under a random per-import
/// key, truncated to 128 bits, so a crafted collision cannot be prepared in advance. More than
/// <c>max(1, uploadBytes / 64)</c> top-level <c>VEVENT</c>s is a <c>400 calendar_ics_import_too_many_components</c>
/// before anything is applied, which bounds the records at 0.625× the upload.
/// </para>
/// <para>
/// <b>Pass 2</b> k-way merges the runs, at most <see cref="MaxOpenRuns"/> at a time and in rounds above that, deleting
/// each round's inputs, so the spill peaks at 1.25× the upload. It yields one UID group at a time in hash order; the
/// caller re-reads each component's text from the upload by offset (there is no text spill) and confirms the UID, so a
/// residual collision splits rather than merges. Crossing the 1.25× spill bound, or <c>ENOSPC</c>, is a <c>507</c>
/// with nothing applied. The spill is deleted when the grouper is disposed, whatever happened.
/// </para>
/// </remarks>
internal sealed class IcsImportGrouper : IAsyncDisposable
{
    public const int RecordSize = 40;

    /// <summary>The in-memory run buffer: 32 MB of records.</summary>
    public const int RunBufferBytes = 32 * 1024 * 1024;

    public const int MaxOpenRuns = 64;

    /// <summary>The smallest average component the cap allows, in bytes (v5 §1).</summary>
    public const int MinBytesPerComponent = 64;

    public const string TooManyComponentsCode = "calendar_ics_import_too_many_components";

    /// <summary>The prefix of every file the calendar import spills (runs and upload copies), for the stale sweep.</summary>
    public const string SpillFilePrefix = "odyssey-ics-";

    public const string TooManyComponentsMessage =
        "The file contains more events than its size allows; it does not look like a calendar file.";

    /// <summary>The <c>VTIMEZONE</c> observance cap (issue #519 §6).</summary>
    public const int MaxObservancesPerTimeZone = 200;

    private readonly Stream source;
    private readonly string spillDirectory;
    private readonly long maxSpillBytes;
    private readonly int recordsPerRun;
    private readonly List<string> runs = [];
    private readonly List<string> allFiles = [];
    private readonly byte[] hashKey;
    private Record[]? memory;
    private int memoryCount;
    private long spilledBytes;

    private IcsImportGrouper(Stream source, string spillDirectory, long uploadBytes, int recordsPerRun, byte[]? hashKey)
    {
        this.hashKey = hashKey ?? RandomNumberGenerator.GetBytes(32);
        this.source = source;
        this.spillDirectory = spillDirectory;
        maxSpillBytes = uploadBytes + (uploadBytes / 4);
        this.recordsPerRun = recordsPerRun;
    }

    /// <summary>Top-level <c>VEVENT</c>s found by pass 1.</summary>
    public int ComponentCount { get; private set; }

    /// <summary>The <c>ATTACH</c> kinds the whole file carries (issue #521 §8.3): what the import's claim check reads.</summary>
    public IcsAttachmentKinds AttachmentKinds { get; private set; } = new();

    /// <summary>The most spill bytes on disk at any one time.</summary>
    public long PeakSpilledBytes { get; private set; }

    /// <summary>The component cap for an upload of <paramref name="uploadBytes"/> (implementation note 5).</summary>
    public static long ComponentCap(long uploadBytes) => Math.Max(1, uploadBytes / MinBytesPerComponent);

    /// <summary>
    /// Runs pass 1 over <paramref name="source"/>, which must be seekable: validates the file, counts and caps the
    /// components, and spills the sorted runs to <paramref name="spillDirectory"/>.
    /// </summary>
    public static async Task<IcsImportGrouper> BuildAsync(
        Stream source, string spillDirectory, int? maxEvents, CancellationToken cancellationToken,
        int recordsPerRun = RunBufferBytes / RecordSize, byte[]? hashKey = null)
    {
        var uploadBytes = source.Length;
        var grouper = new IcsImportGrouper(source, spillDirectory, uploadBytes, recordsPerRun, hashKey);
        try
        {
            await grouper.ScanAsync(uploadBytes, maxEvents, cancellationToken);
            return grouper;
        }
        catch
        {
            await grouper.DisposeAsync();
            throw;
        }
    }

    /// <summary>Pass 2: each UID group's component references, in hash order, the master first.</summary>
    public async IAsyncEnumerable<IReadOnlyList<ComponentRef>> GroupsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IEnumerable<Record> ordered;
        if (runs.Count == 0)
        {
            var records = memory ?? [];
            Array.Sort(records, 0, memoryCount, RecordComparer.Instance);
            ordered = records.Take(memoryCount);
        }
        else
        {
            await SpillMemoryAsync(cancellationToken);
            while (runs.Count > MaxOpenRuns)
            {
                await MergeRoundAsync(cancellationToken);
            }

            ordered = MergeRuns(runs);
        }

        var group = new List<ComponentRef>();
        UInt128 current = default;
        foreach (var record in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Count > 0 && record.Hash != current)
            {
                yield return group;
                group = [];
            }

            current = record.Hash;
            group.Add(new ComponentRef(record.Offset, record.Length, record.IsOverride) { HasInline = record.HasInline });
        }

        if (group.Count > 0)
        {
            yield return group;
        }
    }

    /// <summary>
    /// One component's bytes, unread, for the attachment spooler to stream through (issue #521 v5 §1). The stream is the
    /// upload itself, positioned and bounded; it is not disposed by the caller's reader.
    /// </summary>
    public Stream OpenComponent(ComponentRef component)
    {
        source.Seek(component.Offset, SeekOrigin.Begin);
        return new BoundedReadStream(source, component.Length);
    }

    /// <summary>The envelope a component is parsed in.</summary>
    public static string ComponentEnvelope => Envelope;

    /// <summary>Re-reads one component's text from the upload, wrapped in a minimal <c>VCALENDAR</c> for parsing.</summary>
    public async Task<string> ReadComponentAsync(ComponentRef component, CancellationToken cancellationToken)
    {
        var bytes = new byte[component.Length];
        source.Seek(component.Offset, SeekOrigin.Begin);
        await source.ReadExactlyAsync(bytes, cancellationToken);
        return Envelope + Encoding.UTF8.GetString(bytes) + "\r\nEND:VCALENDAR\r\n";
    }

    public ValueTask DisposeAsync()
    {
        memory = null;
        foreach (var file in allFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Best effort: the temp directory is the operator's backstop.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        allFiles.Clear();
        runs.Clear();
        return ValueTask.CompletedTask;
    }

    private const string Envelope = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Odyssey//Calendar import//EN\r\n";

    // ------------------------------------------------------------------ Pass 1

    private async Task ScanAsync(long uploadBytes, int? maxEvents, CancellationToken cancellationToken)
    {
        var cap = ComponentCap(uploadBytes);
        source.Seek(0, SeekOrigin.Begin);
        var scanner = new IcsByteScanner(source);
        AttachmentKinds = scanner.AttachmentKinds;
        await foreach (var scanned in scanner.ReadEventsAsync(cancellationToken))
        {
            ComponentCount++;
            if (ComponentCount > cap)
            {
                throw new DomainValidationException(TooManyComponentsMessage, TooManyComponentsCode);
            }

            if (maxEvents is { } max && ComponentCount > max)
            {
                throw new DomainValidationException($"The file contains more than {max} events.");
            }

            await AddAsync(new Record(
                HashUid(scanned.Uid, scanned.Offset), scanned.HasRecurrenceId, RecurrenceSortKey(scanned.RecurrenceId),
                scanned.Offset, scanned.Length, scanned.HasInlineAttachment), cancellationToken);
        }
    }

    private async Task AddAsync(Record record, CancellationToken cancellationToken)
    {
        memory ??= new Record[Math.Min(recordsPerRun, 1024)];
        if (memoryCount == memory.Length)
        {
            if (memory.Length < recordsPerRun)
            {
                Array.Resize(ref memory, Math.Min(recordsPerRun, memory.Length * 2));
            }
            else
            {
                await SpillMemoryAsync(cancellationToken);
            }
        }

        memory[memoryCount++] = record;
    }

    private async Task SpillMemoryAsync(CancellationToken cancellationToken)
    {
        if (memory is null || memoryCount == 0)
        {
            return;
        }

        Array.Sort(memory, 0, memoryCount, RecordComparer.Instance);
        var path = NewSpillPath();
        await WriteRunAsync(path, memory.Take(memoryCount), cancellationToken);
        runs.Add(path);
        memoryCount = 0;
    }

    private async Task MergeRoundAsync(CancellationToken cancellationToken)
    {
        var next = new List<string>();
        foreach (var chunk in runs.Chunk(MaxOpenRuns))
        {
            if (chunk.Length == 1)
            {
                next.Add(chunk[0]);
                continue;
            }

            var path = NewSpillPath();
            await WriteRunAsync(path, MergeRuns(chunk), cancellationToken);
            next.Add(path);
            foreach (var input in chunk)
            {
                spilledBytes -= new FileInfo(input).Length;
                File.Delete(input);
                allFiles.Remove(input);
            }
        }

        runs.Clear();
        runs.AddRange(next);
    }

    private async Task WriteRunAsync(string path, IEnumerable<Record> records, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            var buffer = new byte[RecordSize * 1024];
            var used = 0;
            foreach (var record in records)
            {
                record.Write(buffer.AsSpan(used, RecordSize));
                used += RecordSize;
                if (used == buffer.Length)
                {
                    await FlushAsync(file, buffer, used, cancellationToken);
                    used = 0;
                }
            }

            if (used > 0)
            {
                await FlushAsync(file, buffer, used, cancellationToken);
            }
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw SpillFull();
        }
    }

    private async Task FlushAsync(FileStream file, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        spilledBytes += count;
        PeakSpilledBytes = Math.Max(PeakSpilledBytes, spilledBytes);
        if (spilledBytes > maxSpillBytes)
        {
            throw SpillFull();
        }

        await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
    }

    private string NewSpillPath()
    {
        var path = Path.Combine(spillDirectory, $"{SpillFilePrefix}{Guid.NewGuid():N}.run");
        allFiles.Add(path);
        return path;
    }

    // ------------------------------------------------------------------ Pass 2

    private static IEnumerable<Record> MergeRuns(IReadOnlyList<string> paths)
    {
        var readers = paths.Select(path => new RunReader(path)).ToList();
        try
        {
            var heap = new PriorityQueue<int, Record>(RecordComparer.Instance);
            for (var i = 0; i < readers.Count; i++)
            {
                if (readers[i].TryRead(out var first))
                {
                    heap.Enqueue(i, first);
                }
            }

            while (heap.TryDequeue(out var index, out var record))
            {
                yield return record;
                if (readers[index].TryRead(out var next))
                {
                    heap.Enqueue(index, next);
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private sealed class RunReader(string path) : IDisposable
    {
        private readonly FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        private readonly byte[] buffer = new byte[RecordSize];

        public bool TryRead(out Record record)
        {
            var read = 0;
            while (read < RecordSize)
            {
                var n = file.Read(buffer, read, RecordSize - read);
                if (n == 0)
                {
                    record = default;
                    return false;
                }

                read += n;
            }

            record = Record.Read(buffer);
            return true;
        }

        public void Dispose() => file.Dispose();
    }

    // ------------------------------------------------------------------ Records

    /// <summary>
    /// The UID's keyed hash. The UID is folded the way the database compares it (case- and accent-insensitive), so two
    /// spellings the UID columns treat as one land in one group; a component with no UID gets a group of its own.
    /// </summary>
    private UInt128 HashUid(string? uid, long offset) => HashUid(hashKey, uid, offset);

    /// <summary>The sort hash of a UID under <paramref name="key"/>; a test with a fixed key can predict group order.</summary>
    internal static UInt128 HashUid(byte[] key, string? uid, long offset)
    {
        var input = uid is null ? $"\u0000{offset.ToString(CultureInfo.InvariantCulture)}" : FoldUid(uid);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(input), mac);
        return BinaryPrimitives.ReadUInt128BigEndian(mac[..16]);
    }

    internal static string FoldUid(string uid)
    {
        var decomposed = uid.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    // Orders overrides by their occurrence; only the digits matter, the zone is resolved by the importer.
    private static long RecurrenceSortKey(string? value)
    {
        if (value is null)
        {
            return 0;
        }

        long key = 0;
        var digits = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c) && digits < 14)
            {
                key = (key * 10) + (c - '0');
                digits++;
            }
        }

        for (; digits < 14; digits++)
        {
            key *= 10;
        }

        return key;
    }

    private static bool IsDiskFull(IOException exception) =>
        exception.HResult is 28 or unchecked((int)0x80070070) or unchecked((int)0x80070027);

    private static FileStorageFullException SpillFull() =>
        new("There is not enough free temporary disk space to sort this import. Try a smaller file or try again later.");

    // Byte 37 carries whether the component has an inline attachment (issue #521); 38–39 stay padding.
    private readonly record struct Record(UInt128 Hash, bool IsOverride, long RecurrenceKey, long Offset, int Length, bool HasInline = false)
    {
        public void Write(Span<byte> target)
        {
            BinaryPrimitives.WriteUInt128BigEndian(target, Hash);
            target[16] = IsOverride ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt64BigEndian(target[17..], RecurrenceKey);
            BinaryPrimitives.WriteInt64BigEndian(target[25..], Offset);
            BinaryPrimitives.WriteInt32BigEndian(target[33..], Length);
            target[37] = HasInline ? (byte)1 : (byte)0;
            target[38..RecordSize].Clear();
        }

        public static Record Read(ReadOnlySpan<byte> source) => new(
            BinaryPrimitives.ReadUInt128BigEndian(source),
            source[16] != 0,
            BinaryPrimitives.ReadInt64BigEndian(source[17..]),
            BinaryPrimitives.ReadInt64BigEndian(source[25..]),
            BinaryPrimitives.ReadInt32BigEndian(source[33..]),
            source[37] != 0);
    }

    // (uidHash, isOverride, recurrenceKey, fileOffset): a master always sorts before its overrides (v3 §E).
    private sealed class RecordComparer : IComparer<Record>
    {
        public static readonly RecordComparer Instance = new();

        public int Compare(Record x, Record y)
        {
            var c = x.Hash.CompareTo(y.Hash);
            if (c != 0)
            {
                return c;
            }

            c = x.IsOverride.CompareTo(y.IsOverride);
            if (c != 0)
            {
                return c;
            }

            c = x.RecurrenceKey.CompareTo(y.RecurrenceKey);
            return c != 0 ? c : x.Offset.CompareTo(y.Offset);
        }
    }
}

/// <summary>One <c>VEVENT</c>'s place in the upload, as pass 2 yields it.</summary>
internal readonly record struct ComponentRef(long Offset, int Length, bool IsOverride)
{
    /// <summary>It carries an inline attachment, so pass 2 streams it through the spooler rather than reading it whole.</summary>
    public bool HasInline { get; init; }
}

/// <summary>A read-only window of <paramref name="window"/> bytes over <paramref name="inner"/> from its position.</summary>
internal sealed class BoundedReadStream(Stream inner, long window) : Stream
{
    private readonly long length = window;
    private long remaining = window;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => length - remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
        remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (remaining <= 0)
        {
            return 0;
        }

        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken);
        remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A process-wide reservation of temporary disk for the import spill (issue #519 implementation note 6): an import
/// reserves 1.5× its upload before pass 1, against the free space less what other imports hold, so two concurrent
/// imports cannot each pass the check and together fill the volume. Released when disposed.
/// </summary>
internal sealed class IcsSpillReservation : IDisposable
{
    private static readonly object Gate = new();
    private static long reserved;

    private long bytes;

    private IcsSpillReservation(long bytes)
    {
        this.bytes = bytes;
    }

    /// <summary>Every reservation currently held.</summary>
    public static long Reserved
    {
        get
        {
            lock (Gate)
            {
                return reserved;
            }
        }
    }

    /// <summary>The bytes an upload needs free: 1.5× it (the 1.25× peak plus 0.25× headroom).</summary>
    public static long Required(long uploadBytes) => uploadBytes + (uploadBytes / 2);

    /// <summary>Reserves <see cref="Required"/> bytes, or refuses with a <c>507</c>.</summary>
    public static IcsSpillReservation Reserve(string directory, long uploadBytes, Func<string, long>? freeSpace = null)
    {
        var need = Required(uploadBytes);
        long available;
        try
        {
            available = (freeSpace ?? FreeSpace)(directory);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Fails closed: a volume whose free space cannot be read is refused with the 507, never assumed unlimited.
            available = 0;
        }

        lock (Gate)
        {
            if (available - reserved < need)
            {
                throw new FileStorageFullException(
                    "There is not enough free temporary disk space to import this file. Try a smaller file or try again later.");
            }

            reserved += need;
        }

        return new IcsSpillReservation(need);
    }

    public void Dispose()
    {
        lock (Gate)
        {
            reserved -= bytes;
            bytes = 0;
        }
    }

    private static long FreeSpace(string directory) => new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace;
}
