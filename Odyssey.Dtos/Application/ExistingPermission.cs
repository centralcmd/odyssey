namespace Odyssey.Dtos.Application;

public sealed record ExistingPermission
{
    public string Value { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;
}
