using System.Globalization;

namespace Odyssey.Client.Services;

/// <summary>
/// Human-readable byte sizes (B / KB / MB / GB) for display. Shared so size formatting
/// stays consistent across the client; returns an empty string for non-positive sizes.
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB"];

    public static string Format(long bytes)
    {
        if (bytes <= 0)
            return string.Empty;

        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
