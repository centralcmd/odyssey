using System.Globalization;
using System.Text;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Builds a tiny but standards-valid single-page PDF (catalog → pages → page → content + font)
/// with a correct cross-reference table, so seeded document blobs actually open and preview in a
/// browser. Deterministic: the same title/lines always produce byte-identical output.
/// </summary>
internal static class MinimalPdf
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public static byte[] Create(string title, params string[] bodyLines)
    {
        // The page content stream: render the title, then each body line below it.
        var content = new StringBuilder();
        content.Append("BT /F1 16 Tf 36 150 Td (").Append(Escape(title)).Append(") Tj\n");
        content.Append("/F1 11 Tf\n");
        foreach (var line in bodyLines)
            content.Append("0 -22 Td (").Append(Escape(line)).Append(") Tj\n");
        content.Append("ET");
        var contentBytes = Latin1.GetBytes(content.ToString());

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 360 200] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {contentBytes.Length} >>\nstream\n{Latin1.GetString(contentBytes)}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        using var stream = new MemoryStream();
        void Write(string s) => stream.Write(Latin1.GetBytes(s));

        Write("%PDF-1.4\n");

        var offsets = new long[objects.Length + 1];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = stream.Position;
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefOffset = stream.Position;
        Write($"xref\n0 {objects.Length + 1}\n");
        Write("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Length; i++)
            Write(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");

        return stream.ToArray();
    }

    // Escape the characters that are special inside a PDF literal string.
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
