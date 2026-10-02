namespace Odyssey.Core;

/// <summary>
/// Converts between an <c>Odyssey.Context</c> enum and its same-named <c>Odyssey.Dtos</c> mirror by
/// ordinal (issue #287 M6). The pairs are separate declarations so the wire contract and the stored
/// column can each be read without the other project, and <c>EnumMirrorParityTests</c> pins every
/// same-named pair to identical names and ordinals — which is what makes the ordinal the mapping.
///
/// <para>
/// A value the target does not define (a retired ordinal, a hand-edited row) never crosses as itself:
/// it becomes the caller's fallback, or a throw where no member is a safe stand-in.
/// </para>
/// </summary>
public static class EnumMirror
{
    public static TTo Convert<TFrom, TTo>(TFrom value, TTo fallback)
        where TFrom : struct, Enum
        where TTo : struct, Enum =>
        TryConvert(value, out TTo converted) ? converted : fallback;

    /// <summary>For a pair with no safe stand-in member: an undefined value throws rather than mapping.</summary>
    public static TTo ConvertOrThrow<TFrom, TTo>(TFrom value)
        where TFrom : struct, Enum
        where TTo : struct, Enum =>
        TryConvert(value, out TTo converted)
            ? converted
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{typeof(TFrom).Name} value {System.Convert.ToInt64(value)} has no {typeof(TTo).FullName} member.");

    private static bool TryConvert<TFrom, TTo>(TFrom value, out TTo converted)
        where TFrom : struct, Enum
        where TTo : struct, Enum
    {
        var raw = System.Convert.ToInt64(value);
        converted = (TTo)Enum.ToObject(typeof(TTo), raw);
        return IsDefined(converted, raw);
    }

    // A [Flags] value is defined when every set bit belongs to some member; Enum.IsDefined alone would
    // refuse every combination. Whether the type is [Flags], and its member mask, are read once per type.
    private static bool IsDefined<TTo>(TTo converted, long raw) where TTo : struct, Enum =>
        FlagsShape<TTo>.Mask is { } mask ? (raw & ~mask) == 0 : Enum.IsDefined(converted);

    private static class FlagsShape<TEnum> where TEnum : struct, Enum
    {
        public static readonly long? Mask = typeof(TEnum).IsDefined(typeof(FlagsAttribute), inherit: false)
            ? Enum.GetValues<TEnum>().Aggregate(0L, (all, member) => all | System.Convert.ToInt64(member))
            : null;
    }
}
