using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Odyssey.Core.Internet.News.Fetching;

/// <summary>Where an address may be reached from (issue #470 §7.9).</summary>
public enum NewsAddressClass
{
    /// <summary>Global unicast: allowed.</summary>
    Public = 0,

    /// <summary>Blocked by default (private, loopback, unique-local, documentation, reserved); an allow-list grant opens it.</summary>
    Private = 1,

    /// <summary>Never allowed, allow-listed or not (metadata endpoints, link-local, "this network", 240/4).</summary>
    HardDenied = 2,
}

/// <summary>
/// The address policy's classification (§7.9). Wrapped forms — IPv4-mapped, IPv4-compatible, NAT64 (well-known and
/// local-use), 6to4 and Teredo (server and client) — are unwrapped and checked too, and the strictest class wins, so
/// <c>::ffff:169.254.169.254</c> is as hard-denied as the address it carries.
/// </summary>
public static class NewsAddressClassifier
{
    private static readonly (uint Network, int Prefix)[] HardDeniedV4 =
    [
        (V4(0, 0, 0, 0), 8),
        (V4(169, 254, 0, 0), 16),
        (V4(192, 0, 0, 0), 24),
        (V4(100, 100, 100, 200), 32),
        (V4(168, 63, 129, 16), 32), // Azure WireServer: instance metadata and the guest agent channel.
        (V4(240, 0, 0, 0), 4),
    ];

    private static readonly (uint Network, int Prefix)[] PrivateV4 =
    [
        (V4(10, 0, 0, 0), 8),
        (V4(100, 64, 0, 0), 10),
        (V4(127, 0, 0, 0), 8),
        (V4(172, 16, 0, 0), 12),
        (V4(192, 0, 2, 0), 24),
        (V4(192, 88, 99, 0), 24),
        (V4(192, 168, 0, 0), 16),
        (V4(198, 18, 0, 0), 15),
        (V4(198, 51, 100, 0), 24),
        (V4(203, 0, 113, 0), 24),
        (V4(224, 0, 0, 0), 4),
    ];

    public static NewsAddressClass Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => ClassifyV4(BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes())),
            AddressFamily.InterNetworkV6 => ClassifyV6(address.GetAddressBytes()),
            _ => NewsAddressClass.HardDenied,
        };
    }

    private static NewsAddressClass ClassifyV4(uint value)
    {
        if (HardDeniedV4.Any(r => InPrefix(value, r.Network, r.Prefix)))
        {
            return NewsAddressClass.HardDenied;
        }

        return PrivateV4.Any(r => InPrefix(value, r.Network, r.Prefix)) ? NewsAddressClass.Private : NewsAddressClass.Public;
    }

    private static NewsAddressClass ClassifyV6(byte[] b)
    {
        var high = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(0, 8));
        var low = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(8, 8));
        var lastV4 = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12, 4));

        // ::/128 and fe80::/10 and fd00:ec2::/64 are hard-denied as written.
        if (high == 0 && low == 0)
        {
            return NewsAddressClass.HardDenied;
        }

        if ((b[0] == 0xFE && (b[1] & 0xC0) == 0x80) || (high == 0xFD00_0EC2_0000_0000UL))
        {
            return NewsAddressClass.HardDenied;
        }

        // IPv4-mapped (::ffff:0:0/96): the address IS the IPv4 one.
        if (high == 0 && (low >> 32) == 0xFFFF)
        {
            return ClassifyV4(lastV4);
        }

        // ::1.
        if (high == 0 && low == 1)
        {
            return NewsAddressClass.Private;
        }

        // Deprecated IPv4-compatible (::/96).
        if (high == 0 && (low >> 32) == 0)
        {
            return Strictest(NewsAddressClass.Private, ClassifyV4(lastV4));
        }

        // NAT64: the well-known 64:ff9b::/96 and the local-use 64:ff9b:1::/48 carry an IPv4 address in the last 32 bits.
        if (high == 0x0064_FF9B_0000_0000UL && (low >> 32) == 0)
        {
            return Strictest(NewsAddressClass.Private, ClassifyV4(lastV4));
        }

        if ((high >> 16) == 0x0064_FF9B_0001UL)
        {
            return Strictest(NewsAddressClass.Private, ClassifyV4(lastV4));
        }

        // 6to4 (2002::/16): the IPv4 address is bits 16-47.
        if ((high >> 48) == 0x2002)
        {
            return Strictest(NewsAddressClass.Public, ClassifyV4((uint)(high >> 16)));
        }

        // Teredo (2001::/32): the server is bits 32-63, the client the last 32 bits inverted. Inside 2001::/23 as well.
        if ((high >> 32) == 0x2001_0000UL)
        {
            return Strictest(NewsAddressClass.Private, Strictest(ClassifyV4((uint)high), ClassifyV4(~lastV4)));
        }

        // Global unicast only: 2000::/3, minus the IETF protocol assignments (2001::/23), documentation
        // (2001:db8::/32, 3fff::/20).
        if ((b[0] & 0xE0) != 0x20)
        {
            return NewsAddressClass.Private;
        }

        if ((high >> 41) == (0x2001_0000_0000_0000UL >> 41)
            || (high >> 32) == 0x2001_0DB8UL
            || (high >> 44) == 0x3FFF0UL)
        {
            return NewsAddressClass.Private;
        }

        return NewsAddressClass.Public;
    }

    private static NewsAddressClass Strictest(NewsAddressClass a, NewsAddressClass b) => (NewsAddressClass)Math.Max((int)a, (int)b);

    private static bool InPrefix(uint value, uint network, int prefix) =>
        prefix == 0 || (value >> (32 - prefix)) == (network >> (32 - prefix));

    private static uint V4(byte a, byte b, byte c, byte d) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
}
