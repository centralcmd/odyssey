namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// Thrown once an <c>.ics</c> export carrying attached files inline has started streaming and the next record would
/// cross the byte cap. The response is already committed, so the controller aborts the connection: a browser then
/// reports the download as failed instead of saving a file that silently stops early. The attached files are judged
/// exactly before any byte is sent; only record text can push an export over during the stream.
/// </summary>
public sealed class IcsExportSizeExceededException(string message) : Exception(message);
