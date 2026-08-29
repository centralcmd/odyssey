namespace Odyssey.Dtos.Application;

public sealed record ExistingRole
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string> Permissions { get; init; } = [];
}
