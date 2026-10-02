using Odyssey.Core.Finance;

namespace Odyssey.Api;

public static class FormFileExtensions
{
    /// <summary>Adapts a multipart part to the transport-neutral <see cref="FileUpload"/> the domain takes.</summary>
    public static FileUpload ToFileUpload(this IFormFile file) =>
        new(file.FileName, file.ContentType, file.Length, file.OpenReadStream);
}
