namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A single item fetched from one of the platform's configured outside
/// sources (RSS / Atom feeds). Read by all students, written only by the
/// global feed-refresh background service — hence <i>not</i>
/// <see cref="IAccountScoped"/>. The advisor pipeline only selects among
/// supplied items by id; it never writes an outside fact from memory.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Url"/> is the natural unique key for upserts (a given article
/// URL appears at most once in the table regardless of how many times the
/// feed worker fetches it). The summary is HTML-stripped at write time and
/// capped at 500 characters so a malformed feed can't bloat the table or
/// leak markup into prompts.
/// </para>
/// </remarks>
public sealed class FeedItem
{
    public Guid Id { get; set; }

    /// <summary>Human-readable name of the source the item came from
    /// (e.g. <c>"The Daily Star — Campus"</c>). Shown in the feed UI and in
    /// the <c>sourceName</c> field of advisor prompts that reference this
    /// item.</summary>
    public required string SourceName { get; set; }

    /// <summary>The configured source URL the item came from. Recorded so a
    /// later phase can group / filter by source; not displayed to end users
    /// directly (the article <see cref="Url"/> is the link the UI shows).</summary>
    public required string SourceUrl { get; set; }

    /// <summary>The article's canonical URL. Unique — the upsert path in the
    /// feed-refresh service keys on this column.</summary>
    public required string Url { get; set; }

    /// <summary>The article's title, HTML-stripped at write time.</summary>
    public required string Title { get; set; }

    /// <summary>HTML-stripped, ≤500-character excerpt of the article body.
    /// Passed to the LLM as delimited untrusted content when the item is a
    /// candidate for an advisor prompt.</summary>
    public required string Summary { get; set; }

    /// <summary>The article's published timestamp as reported by the source.
    /// Null when the feed didn't provide one (some feeds omit it).</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>The UTC timestamp the feed-refresh worker first persisted this
    /// row. Stamped once at insert; never updated (a re-fetched item is the
    /// same row, updated in-place by the upsert).</summary>
    public DateTimeOffset FetchedAt { get; set; }
}