namespace Odyssey.Core.Tests.Thumbnails;

// The thumbnail scenarios on EF InMemory. The same scenarios run again on real MariaDB in Odyssey.IntegrationTests
// (PhotoThumbnailRelationalScenarioTests), where the token conflicts, the one-save atomicity and the constraints are
// real; keep both concrete classes when adding a scenario.

/// <summary>The job scenarios on EF InMemory.</summary>
public sealed class PhotoThumbnailJobTests : PhotoThumbnailJobScenarios;

/// <summary>The rename and delete scenarios on EF InMemory.</summary>
public sealed class PhotoThumbnailRenameTests : PhotoThumbnailRenameScenarios;
