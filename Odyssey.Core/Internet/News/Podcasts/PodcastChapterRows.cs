using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>Writes one source's chapters of an episode (issue #556 §4.5).</summary>
public static class PodcastChapterRows
{
    /// <summary>
    /// Replaces the episode's chapters of <paramref name="source"/> with <paramref name="chapters"/>, staged on the
    /// caller's context and unsaved. Rows are updated IN PLACE by position — never a delete and a re-insert of one
    /// position in one save, which EF could order insert-first and so raise <c>1062</c> on the unique
    /// <c>(episode, source, position)</c> index (#556 arch B1). Extra old rows are removed, extra new ones added.
    /// </summary>
    public static async Task ReplaceInPlaceAsync(
        OdysseyContext context, Guid episodeId, PodcastChapterSource source, IReadOnlyList<PodcastNormalizedChapter> chapters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(chapters);
        var existing = await context.PodcastChapters
            .Where(c => c.NewsArticleId == episodeId && c.Source == source)
            .OrderBy(c => c.Position)
            .ToListAsync(cancellationToken);
        var byPosition = existing.ToDictionary(c => c.Position);
        for (var position = 0; position < chapters.Count; position++)
        {
            var chapter = chapters[position];
            if (!byPosition.Remove(position, out var row))
            {
                row = new PodcastChapter { PodcastChapterId = Guid.NewGuid(), NewsArticleId = episodeId, Source = source, Position = position };
                context.PodcastChapters.Add(row);
            }

            row.StartMs = chapter.StartMs;
            row.EndMs = chapter.EndMs;
            row.Title = chapter.Title;
            row.Url = chapter.Url;
        }

        context.PodcastChapters.RemoveRange(byPosition.Values);
    }
}
