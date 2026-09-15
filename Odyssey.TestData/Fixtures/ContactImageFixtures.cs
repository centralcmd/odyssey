namespace Odyssey.TestData.Fixtures;

/// <summary>
/// Real, decodable image containers for the contact-image tests (issue #86), plus the constructors for
/// the adversarial variants AC 10 and AC 13 name.
///
/// <para>
/// The base images are embedded as base64 rather than generated, because the project ships no raster
/// encoder and a hand-rolled JPEG would not be a JPEG a decoder agrees with — which is exactly the
/// property these fixtures exist to test. They are tiny (under 6 KB each) and byte-stable, so a strip
/// assertion is a real assertion rather than a re-statement of whatever the generator did today.
/// </para>
///
/// <para>
/// The DECORATIONS below are built in C#, because that is where the interesting variation lives: an
/// <c>APP2</c>/MPF segment carrying a second JPEG with its own GPS, a JFIF <c>APP0</c> thumbnail plus a
/// <c>JFXX</c> segment, an <c>APPn</c> interleaved between the scans of a progressive JPEG, a ZIP
/// trailer after <c>EOI</c>, and a PNG carrying <c>tEXt</c>/<c>eXIf</c>/<c>iCCP</c>.
/// </para>
/// </summary>
public static class ContactImageFixtures
{
    private const string BaselineJpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0d"
        + "Hx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4e"
        + "Hh4eHh4eHh7/wAARCAAwAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUF"
        + "BAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVW"
        + "V1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi"
        + "4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC"
        + "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVm"
        + "Z2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq"
        + "8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDEooor8/P2UKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigD"
        + "/9k=";

    /// <summary>677 bytes.</summary>
    public static byte[] BaselineJpeg() => Convert.FromBase64String(BaselineJpegBase64);

    private const string ProgressiveJpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0d"
        + "Hx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4e"
        + "Hh4eHh4eHh7/wgARCACWAMgDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAT/xAAVAQEBAAAAAAAAAAAAAAAAAAAABv/a"
        + "AAwDAQACEAMQAAABhE/ZAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
        + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAf/EABQQAQAAAAAAAAAAAAAAAAAAAID/2gAIAQEAAQUCbf8A/8QA"
        + "FBEBAAAAAAAAAAAAAAAAAAAAcP/aAAgBAwEBPwEC/8QAFBEBAAAAAAAAAAAAAAAAAAAAcP/aAAgBAgEBPwEC/8QAFBABAAAAAAAA"
        + "AAAAAAAAAAAAgP/aAAgBAQAGPwJt/wD/xAAUEAEAAAAAAAAAAAAAAAAAAACA/9oACAEBAAE/IW3/AP/aAAwDAQACAAMAAAAQ9999"
        + "9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999"
        + "99999999999999999999999999//xAAUEQEAAAAAAAAAAAAAAAAAAABw/9oACAEDAQE/EAL/xAAUEQEAAAAAAAAAAAAAAAAAAABw"
        + "/9oACAECAQE/EAL/xAAUEAEAAAAAAAAAAAAAAAAAAACA/9oACAEBAAE/EG3/AP/Z";

    /// <summary>723 bytes.</summary>
    public static byte[] ProgressiveJpeg() => Convert.FromBase64String(ProgressiveJpegBase64);

    private const string SmallJpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0d"
        + "Hx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4e"
        + "Hh4eHh4eHh7/wAARCAAQABADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUF"
        + "BAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVW"
        + "V1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi"
        + "4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC"
        + "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVm"
        + "Z2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq"
        + "8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD41oooqiT/2Q==";

    /// <summary>631 bytes.</summary>
    public static byte[] SmallJpeg() => Convert.FromBase64String(SmallJpegBase64);

    private const string BaselinePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAEYAAABGCAIAAAD+THXTAAAAbklEQVR4nO3PAQ3AIADAMEADOpGItpt48uxpFWxznzv+ZX0d8D5L"
        + "BZYKLBVYKrBUYKnAUoGlAksFlgosFVgqsFRgqcBSgaUCSwWWCiwVWCqwVGCpwFKBpQJLBZYKLBVYKrBUYKnAUoGlAksFlgoeINwB"
        + "zKnNYX8AAAAASUVORK5CYII=";

    /// <summary>167 bytes.</summary>
    public static byte[] BaselinePng() => Convert.FromBase64String(BaselinePngBase64);

    private const string MaxDimensionPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAABAAAAAQACAIAAADwf7zUAAAUoElEQVR4nO3XMQHAIADAsDEN6EQi2nABRxMFfTvm2h8AANDwvw4A"
        + "AADuMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABC"
        + "DAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAA"
        + "ACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgB"
        + "AACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAg"
        + "xAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAA"
        + "ABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQY"
        + "AAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAA"
        + "QgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMA"
        + "AAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECI"
        + "AQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAA"
        + "IMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEA"
        + "AAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACE"
        + "GAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAA"
        + "AEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBAD"
        + "AAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABA"
        + "iAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAA"
        + "ACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgx"
        + "AAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAA"
        + "hBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYA"
        + "AABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQ"
        + "AwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAA"
        + "QIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIA"
        + "AAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAI"
        + "MQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAA"
        + "AIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEG"
        + "AAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACA"
        + "EAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAA"
        + "AECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBi"
        + "AAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAA"
        + "CDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwA"
        + "AACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAh"
        + "BgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAA"
        + "gBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQA"
        + "AABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQ"
        + "YgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAA"
        + "AAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIM"
        + "AAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAA"
        + "IQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEA"
        + "AIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDE"
        + "AAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAA"
        + "EGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgA"
        + "AAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABC"
        + "DAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAA"
        + "ACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgB"
        + "AACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAg"
        + "xAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAA"
        + "ABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQY"
        + "AAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAA"
        + "QgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMA"
        + "AAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECI"
        + "AQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAA"
        + "IMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEA"
        + "AAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACE"
        + "GAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAA"
        + "AEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBAD"
        + "AAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABA"
        + "iAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAA"
        + "ACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgx"
        + "AAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAA"
        + "hBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYA"
        + "AABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQ"
        + "AwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAA"
        + "QIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIA"
        + "AAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAI"
        + "MQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAA"
        + "AIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEG"
        + "AAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACA"
        + "EAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBiAAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAA"
        + "AECIAQAAgBADAAAAIQYAAABCDAAAAIQYAAAACDEAAAAQYgAAACDEAAAAQIgBAACAEAMAAAAhBgAAAEIMAAAAhBgAAAAIMQAAABBi"
        + "AAAAIMQAAABAiAEAAIAQAwAAACEGAAAAQgwAAACEGAAAAAgxAAAAEGIAAAAgxAAAAECIAQAAgBADAAAAIQYAAABCDsmzCUD1h1s6"
        + "AAAAAElFTkSuQmCC";

    /// <summary>5337 bytes.</summary>
    public static byte[] MaxDimensionPng() => Convert.FromBase64String(MaxDimensionPngBase64);

    private const string OverDimensionPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAABEwAAAOECAIAAAA0fzO6AAATB0lEQVR4nO3XQQ3AIADAwDEN6EQi2jBBQtLcKei3Y679AQAAVPyv"
        + "AwAAAG4yOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACk"
        + "mBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQ"
        + "YnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABA"
        + "iskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAA"
        + "KSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAA"
        + "pJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAA"
        + "kGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAA"
        + "QIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAA"
        + "ACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMA"
        + "AKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4A"
        + "AJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkA"
        + "AECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQA"
        + "AAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMD"
        + "AACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwO"
        + "AACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5"
        + "AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXk"
        + "AAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBST"
        + "AwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJM"
        + "DgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgx"
        + "OQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF"
        + "5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAU"
        + "kwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABS"
        + "TA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABI"
        + "MTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAg"
        + "xeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACA"
        + "FJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAA"
        + "UkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAA"
        + "SDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAA"
        + "IMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAA"
        + "gBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEA"
        + "AFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcA"
        + "AEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwA"
        + "ACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIA"
        + "AIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskB"
        + "AABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYH"
        + "AABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgc"
        + "AAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJy"
        + "AACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJ"
        + "AQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkm"
        + "BwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSY"
        + "HAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBi"
        + "cgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECK"
        + "yQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAAp"
        + "JgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACk"
        + "mBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQ"
        + "YnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABA"
        + "iskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAA"
        + "KSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAA"
        + "pJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAA"
        + "kGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAA"
        + "QIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAA"
        + "ACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMA"
        + "AKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4A"
        + "AJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkA"
        + "AECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQA"
        + "AAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMD"
        + "AACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwO"
        + "AACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5"
        + "AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXk"
        + "AAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBST"
        + "AwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJM"
        + "DgAAkGJyAACAFJMDAACkmBwAACDF5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgx"
        + "OQAAQIrJAQAAUkwOAACQYnIAAIAUkwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFJMDgAAkGJyAACAFJMDAACkmBwAACDF"
        + "5AAAACkmBwAASDE5AABAiskBAABSTA4AAJBicgAAgBSTAwAApJgcAAAgxeQAAAApJgcAAEgxOQAAQIrJAQAAUkwOAACQYnIAAIAU"
        + "kwMAAKSYHAAAIMXkAAAAKSYHAABIMTkAAECKyQEAAFIOcScISLzLodwAAAAASUVORK5CYII=";

    /// <summary>4928 bytes.</summary>
    public static byte[] OverDimensionPng() => Convert.FromBase64String(OverDimensionPngBase64);

    private const string StillWebpBase64 =
        "UklGRkYAAABXRUJQVlA4IDoAAADQAwCdASpQADwAPm02mUmkIyKhIagAgA2JaQAADHTiJOU4cOHDdAAA/vohl87HuD/oPhonzO9A"
        + "AAAA";

    /// <summary>78 bytes.</summary>
    public static byte[] StillWebp() => Convert.FromBase64String(StillWebpBase64);

    private const string AnimatedWebpBase64 =
        "UklGRiwBAABXRUJQVlA4WAoAAAACAAAAJwAAJwAAQU5JTQYAAAAAAAAAAABBTk1GVgAAAAAAAAAAACcAACcAAGQAAAJWUDggPgAA"
        + "AHADAJ0BKigAKAA+bTaYSSQjIqEjiACADYlnAAIDiGGv401UAAD++F9/+6T9T/Rf/xv9WvkyU3/RKZv9EmAAQU5NRkgAAAAAAAAA"
        + "AAAnAAAnAABkAAAAVlA4IDAAAAD0AgCdASooACgAPm0mk0mBHAAA2JaQACA4or8/ZyGFAAD+9SdNv4E3P/eowf5oaABBTk1GUgAA"
        + "AAAAAAAAACcAACcAAGQAAABWUDggOgAAAPQCAJ0BKigAKAA+bS6RSII4AADYlnAAIDiiv0+CeIYAAP7w+ivoaNPw//87fiD9QYJf"
        + "6Hnz+41gAAA=";

    /// <summary>308 bytes.</summary>
    public static byte[] AnimatedWebp() => Convert.FromBase64String(AnimatedWebpBase64);

    private const string AnimatedPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAACgAAAAoCAIAAAADnC86AAAACGFjVEwAAAADAAAAAM7tusAAAAAaZmNUTAAAAAAAAAAoAAAAKAAA"
        + "AAAAAAAAAAEACgAAXA/y6AAAADFJREFUeJztzUERAAAEADBE8tY/lxju3FZgGT1xoU5WsVgsFovFYrFYLBaLxWLx03gBJmUAvuZa"
        + "m4wAAAAaZmNUTAAAAAEAAAAoAAAAKAAAAAAAAAAAAAEACgAAx3wYPAAAADVmZEFUAAAAAnic7c1BEQAABAAwRPLWP5cY7txWYDk9"
        + "caFOVrFYLBaLxWKxWCwWi8Vi8dN4AZTFAPqFQyTNAAAAGmZjVEwAAAADAAAAKAAAACgAAAAAAAAAAAABAAoAACrqy9UAAAA2ZmRB"
        + "VAAAAAR4nO3NMQ0AMAgAsDFJ3AhDPjJISGug0Vlvw19ZxWKxWCwWi8VisVgsFovFR+MBAzQBNtEl+BoAAAAASUVORK5CYII=";

    /// <summary>371 bytes.</summary>
    public static byte[] AnimatedPng() => Convert.FromBase64String(AnimatedPngBase64);

    private const string GifBase64 =
        "R0lGODdhFAAUAIEAAAkJCQAAAAAAAAAAACwAAAAAFAAUAEAIIgABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatxoMSAAOw==";

    /// <summary>73 bytes.</summary>
    public static byte[] Gif() => Convert.FromBase64String(GifBase64);

    // ── JPEG decorations ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A JPEG carrying an <c>APP2</c>/<b>MPF</b> segment whose payload is an entire second JPEG. This is
    /// the fixture behind the reason every <c>APPn</c> is dropped rather than filtered: a segment a
    /// keep-list does not understand can hold a complete second image with its own location data.
    /// </summary>
    public static byte[] JpegWithMpfSecondImage()
    {
        var inner = SmallJpeg();
        var payload = new byte[4 + 8 + inner.Length];
        "MPF\0"u8.CopyTo(payload);
        inner.CopyTo(payload, 12);
        return InsertAfterSoi(BaselineJpeg(), Segment(0xE2, payload));
    }

    /// <summary>
    /// A JPEG carrying a JFIF <c>APP0</c> with a 2 × 2 embedded thumbnail AND a <c>JFXX</c> segment with
    /// its own. <c>APP0</c> is the one marker a keep-list is tempted to retain — and it is the one that
    /// can still smuggle a second image.
    /// </summary>
    public static byte[] JpegWithJfifThumbnail()
    {
        // JFIF\0 · version 1.2 · units · Xdensity · Ydensity · Xthumbnail=2 · Ythumbnail=2 · RGB triples
        var app0 = new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x00, 0x00, 0x01, 0x00, 0x01, 0x02, 0x02 }
            .Concat(new byte[2 * 2 * 3])
            .ToArray();

        var inner = SmallJpeg();
        var jfxx = new byte[5 + 1 + inner.Length];
        "JFXX\0"u8.CopyTo(jfxx);
        jfxx[5] = 0x10; // thumbnail coded as a JPEG
        inner.CopyTo(jfxx, 6);

        return InsertAfterSoi(BaselineJpeg(), Segment(0xE0, app0), Segment(0xE0, jfxx));
    }

    /// <summary>A JPEG carrying an EXIF <c>APP1</c> with a GPS-shaped payload, and a <c>COM</c> comment.</summary>
    public static byte[] JpegWithExifAndComment()
    {
        var exif = new byte[6 + 64];
        "Exif\0\0"u8.CopyTo(exif);
        for (var i = 6; i < exif.Length; i++)
        {
            exif[i] = (byte)(i * 7);
        }

        return InsertAfterSoi(BaselineJpeg(), Segment(0xE1, exif), Segment(0xFE, "a private note"u8.ToArray()));
    }

    /// <summary>
    /// <b>AC 13's fixture.</b> A progressive JPEG — several <c>DHT</c> segments and several scans — with
    /// an <c>APPn</c> INTERLEAVED between two of them.
    ///
    /// <para>
    /// This is what exercises the enumerated frame-header list. Byte-stuffing means <c>0xC4</c>,
    /// <c>0xC8</c> and <c>0xCC</c> cannot appear in a marker-like position inside entropy-coded data, so
    /// the enumeration guards the <b>marker chain</b>, not the scan — and a progressive image is the only
    /// ordinary input whose marker chain resumes after a scan at all.
    /// </para>
    /// </summary>
    public static byte[] ProgressiveJpegWithInterleavedAppn()
    {
        var source = ProgressiveJpeg();
        var comment = Segment(0xE1, "Exif\0\0interleaved"u8.ToArray());

        // Insert before the SECOND scan, so the walk has to resume the marker chain past a scan and
        // drop what it finds there — not merely drop a segment before the first one.
        var scans = 0;
        for (var i = 2; i + 1 < source.Length; i++)
        {
            if (source[i] != 0xFF || source[i + 1] != 0xDA)
            {
                continue;
            }

            scans++;
            if (scans < 2)
            {
                continue;
            }

            return [.. source[..i], .. comment, .. source[i..]];
        }

        throw new InvalidOperationException(
            "The progressive fixture no longer has two scans, so it cannot exercise the interleaved case.");
    }

    /// <summary>A JPEG with a ZIP local-file header appended after <c>EOI</c> — a polyglot trailer.</summary>
    public static byte[] JpegWithZipTrailer() =>
        [.. BaselineJpeg(), 0x50, 0x4B, 0x03, 0x04, .. new byte[256]];

    /// <summary>Wraps <paramref name="payload"/> in a marker segment with its two-byte big-endian length.</summary>
    private static byte[] Segment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    private static byte[] InsertAfterSoi(byte[] jpeg, params byte[][] segments) =>
        [.. jpeg[..2], .. segments.SelectMany(s => s), .. jpeg[2..]];

    // ── PNG decorations ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A PNG carrying <c>tEXt</c>, <c>eXIf</c> and <c>iCCP</c>. <c>iCCP</c> is in the set because an ICC
    /// profile carries <c>desc</c> and <c>cprt</c> TEXT — it is a text channel, not only a colour one.
    /// </summary>
    public static byte[] PngWithTextAndProfile()
    {
        var text = "Comment\0a private location note"u8.ToArray();
        var exif = new byte[48];
        for (var i = 0; i < exif.Length; i++)
        {
            exif[i] = (byte)(i * 5);
        }

        var iccp = "profile\0\0"u8.ToArray().Concat(new byte[32]).ToArray();

        return InsertPngChunksBeforeIend(
            BaselinePng(), ("tEXt", text), ("eXIf", exif), ("iCCP", iccp));
    }

    /// <summary>A PNG with a ZIP local-file header appended after <c>IEND</c>.</summary>
    public static byte[] PngWithTrailer() =>
        [.. BaselinePng(), 0x50, 0x4B, 0x03, 0x04, .. new byte[128]];

    private static byte[] InsertPngChunksBeforeIend(byte[] png, params (string Type, byte[] Data)[] chunks)
    {
        // The last 12 bytes of a well-formed PNG are the IEND chunk.
        var iendStart = png.Length - 12;
        var inserted = chunks.SelectMany(c => PngChunk(c.Type, c.Data)).ToArray();
        return [.. png[..iendStart], .. inserted, .. png[iendStart..]];
    }

    private static byte[] PngChunk(string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var chunk = new byte[12 + data.Length];
        chunk[0] = (byte)(data.Length >> 24);
        chunk[1] = (byte)(data.Length >> 16);
        chunk[2] = (byte)(data.Length >> 8);
        chunk[3] = (byte)data.Length;
        typeBytes.CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);

        var crc = Crc32(typeBytes, data);
        chunk[^4] = (byte)(crc >> 24);
        chunk[^3] = (byte)(crc >> 16);
        chunk[^2] = (byte)(crc >> 8);
        chunk[^1] = (byte)crc;
        return chunk;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data))
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    // ── WebP decorations ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A still WebP rebuilt with a <c>VP8X</c> header whose ICC, EXIF and XMP flag bits are SET, plus the
    /// <c>ICCP</c>, <c>EXIF</c> and <c>XMP </c> chunks those bits advertise. The stored output must carry
    /// none of the three chunks and must have the three bits CLEARED — a container that still advertises
    /// payloads it no longer holds is a container a reader will go looking in.
    /// </summary>
    public static byte[] WebpWithMetadataChunks()
    {
        var still = StillWebp();
        var vp8 = still[12..];

        // flags(1) reserved(3) canvasWidth-1(3 LE) canvasHeight-1(3 LE)
        const int width = 80;
        const int height = 60;
        var vp8x = new byte[10];
        vp8x[0] = 0x20 | 0x08 | 0x04; // ICC | EXIF | XMP
        WriteUInt24(vp8x.AsSpan(4), width - 1);
        WriteUInt24(vp8x.AsSpan(7), height - 1);

        var body = new List<byte>();
        body.AddRange("WEBP"u8.ToArray());
        body.AddRange(WebpChunk("VP8X", vp8x));
        body.AddRange(WebpChunk("ICCP", new byte[40]));
        body.AddRange(vp8);
        body.AddRange(WebpChunk("EXIF", "Exif\0\0"u8.ToArray().Concat(new byte[30]).ToArray()));
        body.AddRange(WebpChunk("XMP ", "<x:xmpmeta>a private note</x:xmpmeta>"u8.ToArray()));

        var payload = body.ToArray();
        byte[] output = [.. "RIFF"u8, 0, 0, 0, 0, .. payload];
        output[4] = (byte)payload.Length;
        output[5] = (byte)(payload.Length >> 8);
        output[6] = (byte)(payload.Length >> 16);
        output[7] = (byte)(payload.Length >> 24);
        return output;
    }

    private static byte[] WebpChunk(string fourCc, byte[] data)
    {
        var header = new byte[8];
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(header, 0);
        header[4] = (byte)data.Length;
        header[5] = (byte)(data.Length >> 8);
        header[6] = (byte)(data.Length >> 16);
        header[7] = (byte)(data.Length >> 24);

        return (data.Length & 1) == 1
            ? [.. header, .. data, 0x00]
            : [.. header, .. data];
    }

    private static void WriteUInt24(Span<byte> target, int value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
    }
}
