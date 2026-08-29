namespace Odyssey.Dtos.Finance;

public enum FileAnalysisMatchStatus
{
    NotRun = 0,
    Running = 1,
    Completed = 2,
    Skipped = 3,
    Failed = 4
}

public enum MatchMethod
{
    None = 0,
    Llm = 1,
    Manual = 2
}
