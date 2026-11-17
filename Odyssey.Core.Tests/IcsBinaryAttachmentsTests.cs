using System.Security.Cryptography;
using System.Text;
using Odyssey.Core;
using Odyssey.Core.Journal.Interop;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The streaming inline-attachment writer and the import's spooling pre-pass. The writer's size prediction must be
/// exact — the task export refuses an over-cap file from it before sending a byte — and the spooler must recover the
/// bytes whatever folding a producer used, without ever holding a value whole.
/// </summary>
public class IcsBinaryAttachmentsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(57)]
    [InlineData(48 * 1024 + 1)] // crosses the writer's read chunk with a remainder carried
    [InlineData(300_001)]
    public async Task EncodedLength_IsExact_AndEveryLineFitsIn75Octets(int size)
    {
        var content = RandomBytes(size, seed: size);
        var head = IcsBinaryAttachmentWriter.BuildHead(
            new IcsBinaryAttachmentHeader(Guid.NewGuid(), "Kvittering – Ålesund 📄.pdf", "application/pdf", new string('a', 64), size));

        var written = await WriteAsync(head, content);

        Assert.Equal(IcsBinaryAttachmentWriter.EncodedLength(head, size), written.Length);
        var lines = SplitLines(written);
        Assert.All(lines, line => Assert.True(line.Length <= IcsBinaryAttachmentWriter.MaxLineOctets, $"line of {line.Length} octets"));
        Assert.All(lines.Skip(1), line => Assert.Equal((byte)' ', line[0]));
        Assert.EndsWith("\r\n", Encoding.UTF8.GetString(written), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Head_DropsQuotesAndControlCharacters_FromTheFileName_AndAnUnsafeType()
    {
        var head = IcsBinaryAttachmentWriter.BuildHead(
            new IcsBinaryAttachmentHeader(null, "a\"b\r\nc.txt", "text/plain; charset=\"x\"", null, 1));

        Assert.Equal("ATTACH;ENCODING=BASE64;VALUE=BINARY;FILENAME=\"abc.txt\";SIZE=1:", head);
        Assert.NotNull(await WriteAsync(head, [42]));
    }

    [Fact]
    public async Task Spooler_RoundTripsTheWritersOutput_AndPassesOtherLinesThrough()
    {
        var content = RandomBytes(200_000, seed: 7);
        var fileId = Guid.NewGuid();
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var head = IcsBinaryAttachmentWriter.BuildHead(new IcsBinaryAttachmentHeader(fileId, "photo.jpg", "image/jpeg", sha, content.Length));
        var attach = Encoding.UTF8.GetString(await WriteAsync(head, content));
        var description = "DESCRIPTION:A long description that a producer folded over two\r\n  lines, with a colon: inside";
        var ics = $"BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nSUMMARY:x\r\n{description}\r\n{attach}END:VTODO\r\nEND:VCALENDAR\r\n";

        using var spool = await SpoolAsync(ics);

        var spooled = Assert.Single(spool.Attachments);
        Assert.Null(spooled.Problem);
        Assert.Equal((fileId, "photo.jpg", "image/jpeg", sha, (long)content.Length),
            (spooled.OriginalFileId, spooled.FileName, spooled.ContentType, spooled.Sha256Hash, spooled.Size));
        Assert.Equal(content, await File.ReadAllBytesAsync(spooled.Path!));
        Assert.Contains($"ATTACH:{IcsAttachmentSpool.Scheme}:{spooled.Reference}\r\n", spool.CalendarText);
        Assert.Contains(description + "\r\n", spool.CalendarText);
        Assert.DoesNotContain("BASE64", spool.CalendarText);
        Assert.Same(spooled, spool.Find(new Uri($"{IcsAttachmentSpool.Scheme}:{spooled.Reference}")));
    }

    [Fact]
    public async Task Spooler_ToleratesBareLineFeeds_TabFolds_AndMissingPadding()
    {
        var content = Encoding.UTF8.GetBytes("hello, world"); // 12 bytes → no padding needed; trim to force it
        var base64 = Convert.ToBase64String(content[..11]).TrimEnd('=');
        var ics = "BEGIN:VTODO\nATTACH;ENCODING=base64;VALUE=BINARY;FMTTYPE=TEXT/PLAIN:" + base64[..6] + "\n\t" + base64[6..] + "\nEND:VTODO\n";

        using var spool = await SpoolAsync(ics);

        var spooled = Assert.Single(spool.Attachments);
        Assert.Null(spooled.Problem);
        Assert.Equal("text/plain", spooled.ContentType);
        Assert.Equal(content[..11], await File.ReadAllBytesAsync(spooled.Path!));
    }

    [Theory]
    [InlineData("!!!!", IcsAttachmentSpooler.DamagedProblem)]
    [InlineData("QQ", null)] // "A"
    [InlineData("", IcsAttachmentSpooler.EmptyProblem)]
    public async Task Spooler_ReportsDamagedAndEmptyValues(string value, string? problem)
    {
        using var spool = await SpoolAsync($"BEGIN:VTODO\r\nATTACH;ENCODING=BASE64;VALUE=BINARY:{value}\r\nSUMMARY:after\r\nEND:VTODO\r\n");

        var spooled = Assert.Single(spool.Attachments);
        Assert.Equal(problem, spooled.Problem);
        Assert.Contains("SUMMARY:after\r\n", spool.CalendarText);
        if (problem is not null)
        {
            Assert.Null(spooled.Path); // a refused file's temp copy is gone at once
        }
    }

    [Fact]
    public async Task Spooler_StopsWritingAnOversizeFile_AndReportsIt()
    {
        var base64 = Convert.ToBase64String(new byte[4096]);

        using var spool = await SpoolAsync($"BEGIN:VTODO\r\nATTACH;ENCODING=BASE64:{base64}\r\nEND:VTODO\r\n", maxFileBytes: 1000);

        var spooled = Assert.Single(spool.Attachments);
        Assert.Equal(IcsAttachmentSpooler.TooLargeProblem, spooled.Problem);
        Assert.Null(spooled.Path);
    }

    [Fact]
    public async Task Spooler_ChecksTheDeclaredHash()
    {
        var base64 = Convert.ToBase64String([1, 2, 3]);

        using var spool = await SpoolAsync($"ATTACH;ENCODING=BASE64;X-ODYSSEY-SHA256={new string('f', 64)}:{base64}\r\n");

        Assert.Equal(IcsAttachmentSpooler.ChecksumProblem, Assert.Single(spool.Attachments).Problem);
    }

    [Fact]
    public async Task Spooler_LeavesUriAttachmentsAndQuotedColonsAlone()
    {
        const string ics = "ATTACH;FMTTYPE=text/plain:odyssey-file:3f2504e0-4f89-11d3-9a0c-0305e82c3301\r\n"
            + "ATTACH;X-LABEL=\"a:b;c\":https://example.com/x\r\n";

        using var spool = await SpoolAsync(ics);

        Assert.Equal(0, spool.Count);
        Assert.Equal(ics, spool.CalendarText);
    }

    [Fact]
    public async Task Spooler_RefusesCalendarTextOverItsCap()
    {
        var ics = "DESCRIPTION:" + new string('x', 5000) + "\r\n";

        await Assert.ThrowsAsync<DomainValidationException>(() => SpoolAsync(ics, maxTextChars: 1000));
    }

    [Fact]
    public async Task Dispose_DeletesTheTemporaryFiles()
    {
        var spool = await SpoolAsync($"ATTACH;ENCODING=BASE64:{Convert.ToBase64String([9, 9, 9])}\r\n");
        var path = Assert.Single(spool.Attachments).Path!;
        Assert.True(File.Exists(path));

        spool.Dispose();

        Assert.False(File.Exists(path));
    }

    private static async Task<byte[]> WriteAsync(string head, byte[] content)
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(content);
        var read = await IcsBinaryAttachmentWriter.WriteAsync(output, head, input, CancellationToken.None);
        Assert.Equal(content.Length, read);
        return output.ToArray();
    }

    private static Task<IcsAttachmentSpool> SpoolAsync(string ics, long maxFileBytes = long.MaxValue, long maxTextChars = long.MaxValue) =>
        IcsAttachmentSpooler.SpoolAsync(new StringReader(ics), maxFileBytes, maxTextChars, CancellationToken.None);

    private static byte[] RandomBytes(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static List<byte[]> SplitLines(byte[] text)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '\r' && text[i + 1] == '\n')
            {
                lines.Add(text[start..i]);
                start = i + 2;
                i++;
            }
        }

        return lines;
    }
}
