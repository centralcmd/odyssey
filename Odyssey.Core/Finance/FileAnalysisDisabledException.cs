namespace Odyssey.Core.Finance;

public sealed class FileAnalysisDisabledException : FeatureDisabledException
{
    public FileAnalysisDisabledException()
        : base("File analysis is disabled by configuration.") { }
}
