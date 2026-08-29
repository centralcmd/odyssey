namespace Odyssey.Core.Finance;

public class FileAnalysisProviderException : Exception
{
    public FileAnalysisProviderException(string message) : base(message) { }
    public FileAnalysisProviderException(string message, Exception inner) : base(message, inner) { }
}
