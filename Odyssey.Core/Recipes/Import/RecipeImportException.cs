using System.Net;

namespace Odyssey.Core.Recipes.Import;

/// <summary>
/// A recipe-import refusal with its §9 status and problem-details <c>code</c>. One type, because every one of them
/// carries the same three things — status, code and an optional retry hint or extension — and no more.
/// </summary>
public sealed class RecipeImportException : DomainException
{
    private readonly int statusCode;
    private readonly int? retryAfter;
    private readonly IReadOnlyDictionary<string, object?>? extensions;

    public RecipeImportException(
        HttpStatusCode status, string code, string message,
        int? retryAfterSeconds = null, IReadOnlyDictionary<string, object?>? extensions = null)
        : base(message)
    {
        statusCode = (int)status;
        Code = code;
        retryAfter = retryAfterSeconds;
        this.extensions = extensions;
    }

    public override int StatusCode => statusCode;

    public override string? Code { get; }

    public override int? RetryAfterSeconds => retryAfter;

    public override IReadOnlyDictionary<string, object?>? Extensions => extensions;
}
