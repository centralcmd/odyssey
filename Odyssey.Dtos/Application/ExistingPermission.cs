namespace Odyssey.Dtos.Application;

public sealed record ExistingPermission
{
    public string Value { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;
}
