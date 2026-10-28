namespace Odyssey.Core.SystemSettings;

/// <summary>
/// What the API and the worker must agree on to read one Data Protection key ring (issue #348). Both hosts
/// name this one constant: two copies kept equal by a test could still drift in a way nothing reports, and a
/// mismatch derives different keys from the same files, so every stored secret would read as unreadable.
/// </summary>
public static class DataProtectionKeyRing
{
    public const string ApplicationName = "Odyssey";
}
