using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Odyssey.Core.Internet.News.Content;

/// <summary>
/// SHA-256 over the news reader's keys and composite values. A composite hash frames every field (a tag byte and a
/// length), so two different field tuples can never produce the same byte stream: <c>("ab", "c")</c> and
/// <c>("a", "bc")</c>, an absent field and an empty one, and a stored value and a refused raw fragment all differ.
/// </summary>
public static class NewsHashing
{
    /// <summary>The SHA-256 of <paramref name="text"/>'s UTF-8 bytes.</summary>
    public static byte[] Sha256(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>Lower-case hex, the spelling the <c>h:</c> and <c>g#</c> identity keys carry.</summary>
    public static string ToHex(byte[] hash) => Convert.ToHexStringLower(hash);

    /// <summary>A framed composite of fields, hashed incrementally.</summary>
    public sealed class Composite : IDisposable
    {
        private const byte AbsentTag = 0;
        private const byte TextTag = 1;
        private const byte RawTag = 2;

        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        /// <summary>A text field, or an absent one when <paramref name="value"/> is <c>null</c>.</summary>
        public Composite Text(string? value)
        {
            if (value is null)
            {
                Frame(AbsentTag, []);
            }
            else
            {
                Frame(TextTag, Encoding.UTF8.GetBytes(value));
            }

            return this;
        }

        /// <summary>A refused fragment's raw bytes, standing in for the field it was refused from (§8.8).</summary>
        public Composite Raw(string rawFragment)
        {
            Frame(RawTag, Encoding.UTF8.GetBytes(rawFragment));
            return this;
        }

        public byte[] Finish() => hash.GetHashAndReset();

        public void Dispose() => hash.Dispose();

        private void Frame(byte tag, byte[] payload)
        {
            Span<byte> header = stackalloc byte[5];
            header[0] = tag;
            BinaryPrimitives.WriteInt32BigEndian(header[1..], payload.Length);
            hash.AppendData(header);
            hash.AppendData(payload);
        }
    }
}
