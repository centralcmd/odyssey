namespace Odyssey.Client.Pages.Storage;

/// <summary>The Storage health panel's own load state, independent of the settings load.</summary>
public enum StorageHealthPhase
{
    Loading,
    Ready,
    Error,
}
