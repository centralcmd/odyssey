namespace Odyssey.Dtos.Application;

public sealed record ExistingRole
{
    public string Name { get; set; } = string.Empty;

    public IReadOnlyList<string> Permissions { get; set; } = [];
}
