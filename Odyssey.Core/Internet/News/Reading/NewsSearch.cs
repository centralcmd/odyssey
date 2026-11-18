using System.Text;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>
/// The search terms of issue #470 §8.4: words of Unicode letters and digits only, every other character a separator;
/// a term shorter than <see cref="NewsLimits.MinSearchTermLength"/> or in <see cref="NewsSearchStopwords"/> dropped; at
/// most <see cref="MaxTerms"/> kept, de-duplicated. No quote and no operator survives, so FTS syntax in the input can
/// never reach the server.
/// </summary>
public static class NewsSearchTerms
{
    public const int MaxTerms = 8;

    /// <summary>The kept terms, lower-cased; empty when none is usable.</summary>
    public static IReadOnlyList<string> Parse(string? search)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(search))
        {
            return terms;
        }

        var word = new StringBuilder();
        void Flush()
        {
            if (word.Length == 0)
            {
                return;
            }

            var term = word.ToString().ToLowerInvariant();
            word.Clear();
            if (terms.Count < MaxTerms && new System.Globalization.StringInfo(term).LengthInTextElements >= NewsLimits.MinSearchTermLength
                && !NewsSearchStopwords.Contains(term) && !terms.Contains(term, StringComparer.Ordinal))
            {
                terms.Add(term);
            }
        }

        foreach (var rune in search.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                word.Append(rune.ToString());
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return terms;
    }

    /// <summary>The boolean-mode expression: every term required and matched as a prefix (<c>+word*</c>).</summary>
    public static string BooleanExpression(IReadOnlyList<string> terms) => string.Join(' ', terms.Select(t => $"+{t}*"));
}

/// <summary>InnoDB's default full-text stopword list (<c>INFORMATION_SCHEMA.INNODB_FT_DEFAULT_STOPWORD</c>).</summary>
public static class NewsSearchStopwords
{
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "a", "about", "an", "are", "as", "at", "be", "by", "com", "de", "en", "for", "from", "how", "i", "in", "is",
        "it", "la", "of", "on", "or", "that", "the", "this", "to", "was", "what", "when", "where", "who", "will",
        "with", "und", "www",
    };

    public static bool Contains(string term) => Words.Contains(term);
}

/// <summary>
/// The search seam (§8.4): it returns an <see cref="IQueryable{T}"/>, so the caller composes the rest of the list query
/// over it. Relational: <c>MATCH … AGAINST</c> in boolean mode over the <c>FULLTEXT</c> index, with the expression a
/// bound parameter. EF InMemory: a <c>LIKE</c> per term, the shape the other lookups use under that provider.
/// </summary>
public interface INewsArticleSearch
{
    IQueryable<NewsArticle> Apply(IQueryable<NewsArticle> articles, IReadOnlyList<string> terms);

    /// <summary>
    /// Relevance order: the match score, then <c>SortAtUtc</c> descending, then the id descending. EF InMemory has no
    /// score, so it falls back to newest first.
    /// </summary>
    IOrderedQueryable<NewsArticle> OrderByRelevance(IQueryable<NewsArticle> articles, IReadOnlyList<string> terms);
}

/// <inheritdoc cref="INewsArticleSearch" />
public sealed class NewsArticleSearch(OdysseyContext context) : INewsArticleSearch
{
    public IQueryable<NewsArticle> Apply(IQueryable<NewsArticle> articles, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(terms);
        if (context.Database.IsRelational())
        {
            var expression = NewsSearchTerms.BooleanExpression(terms);
            return articles.Where(a => EF.Functions.Match(new[] { a.Title, a.Author!, a.Excerpt }, expression, MySqlMatchSearchMode.Boolean) > 0);
        }

        foreach (var term in terms)
        {
            var pattern = ListQuery.ContainsPattern(term);
            articles = articles.Where(a => EF.Functions.Like(a.Title, pattern)
                || (a.Author != null && EF.Functions.Like(a.Author, pattern))
                || EF.Functions.Like(a.Excerpt, pattern));
        }

        return articles;
    }

    public IOrderedQueryable<NewsArticle> OrderByRelevance(IQueryable<NewsArticle> articles, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(articles);
        if (context.Database.IsRelational())
        {
            var expression = NewsSearchTerms.BooleanExpression(terms);
            return articles
                .OrderByDescending(a => EF.Functions.Match(new[] { a.Title, a.Author!, a.Excerpt }, expression, MySqlMatchSearchMode.Boolean))
                .ThenByDescending(a => a.SortAtUtc)
                .ThenByDescending(a => a.NewsArticleId);
        }

        return articles.OrderByDescending(a => a.SortAtUtc).ThenByDescending(a => a.NewsArticleId);
    }
}
