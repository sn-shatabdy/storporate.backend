namespace Storporate.SharedKernel.Entities;

/// <summary>
/// Constants that pin the shape of the talent-search index so a future
/// embedding-model swap (or a future column-dimension change) is a single
/// constant edit, not a migration-driven re-key of every reference site.
/// </summary>
public static class TalentIndexConstants
{
    /// <summary>Dimension of the <c>vector</c> column the embedding model
    /// writes. Matched against <c>Llm:Embedding:Dimensions</c> at startup
    /// (see <c>RefreshTalentIndexEntryProcessor</c>) and against the
    /// <c>vector(N)</c> declaration in the
    /// <c>AddTalentSearchTables</c> migration's raw SQL. Changing this
    /// constant without a paired migration would break
    /// <c>INSERT ... vector</c> casts at runtime.</summary>
    public const int EmbeddingDimensions = 768;

    /// <summary>Upper bound the processor applies to the
    /// <see cref="TalentIndexEntry.SearchText"/> column. Chosen to keep a
    /// single embedding call's input well within the Bionic embedding model's
    /// 8K-token envelope (a single string in this code path is roughly 1
    /// token per 4 characters of English text, so 8 000 characters is
    /// ~2 000 tokens).</summary>
    public const int SearchTextMaxCharacters = 8_000;
}
