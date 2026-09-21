namespace Storporate.SharedKernel.Entities;

/// <summary>
/// One student's searchable projection. STOR-43 Phase 1's non-tenant table that
/// an Organization's talent search eventually queries (Phase 2). Written by the
/// <c>RefreshTalentIndexEntryProcessor</c> after the student's portfolio
/// analysis changes, and removed in the same request as a false-toggle PUT.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not <see cref="IAccountScoped"/>.</b> <see cref="FeedItem"/> is the
/// established "global table readable by every student" pattern; the same
/// pattern applies here — any <see cref="SystemRoles.Organization"/> account
/// can read this table's rows via the Phase 2 search endpoint, and only the
/// <c>RefreshTalentIndexEntryProcessor</c> background job writes to it.
/// Because there is no <c>AccountId</c> column, the global query filter loop
/// in <c>WriteDbContext.OnModelCreating</c> skips this entity and the
/// <c>AddTalentSearchTables</c> migration installs no RLS policy for it.
/// </para>
/// <para>
/// <b>Embedding column is intentionally NOT in the EF model.</b> The
/// 768-dimension <c>vector</c> column is created by the
/// <c>AddTalentSearchTables</c> migration's raw SQL (no Pgvector NuGet
/// installed; the column is opaque to EF) so the in-memory test suite keeps
/// booting without a Postgres-only type. Writes go through
/// <c>ITalentIndexRepository.UpsertAsync</c> which uses provider-name branching
/// to switch between the raw-SQL pgvector path (production) and the in-memory
/// dictionary store (tests).
/// </para>
/// <para>
/// <b>Self-reported fields respect the Show flags.</b> The processor nulls out
/// <see cref="Headline"/>, <see cref="University"/>, <see cref="FieldOfStudy"/>,
/// and <see cref="StudyYear"/> when the corresponding Show flag is false, so a
/// future search never accidentally surfaces a value the student hid. See
/// <see cref="DisplayName"/>, which is always populated — there is no Show flag
/// for it because a blank display name blocks opt-in at the validator level.
/// </para>
/// <para>
/// <b>SearchText content policy.</b> The processor builds
/// <see cref="SearchText"/> from labels, categories, skill names, and (when
/// shown) headline / field of study. Item <c>Description</c>,
/// <c>ExternalUrl</c>, <c>OriginalFileName</c>, and <c>StorageKey</c> are
/// deliberately excluded — the only places they exist are the private student
/// tables (<see cref="PortfolioItem"/>) that RLS blocks an Organization from
/// reading anyway. The cap is 8 000 characters to keep the embedding cost
/// bounded.
/// </para>
/// </remarks>
public sealed class TalentIndexEntry
{
    /// <summary>Primary key. Surfaced to Organization callers as <c>candidateId</c>
    /// in Phase 2's response shape.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning student's <see cref="User.Id"/>. Plain
    /// <c>unique</c> column (no FK to <c>Users</c>) so deleting a user account
    /// is not blocked by dependent index rows; a future account-deletion story
    /// can clean these up explicitly. See class remarks on the non-tenant
    /// design.</summary>
    public Guid StudentAccountId { get; set; }

    /// <summary>Always populated — see class remarks.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Headline when the student chose to show it; null otherwise.</summary>
    public string? Headline { get; set; }

    /// <summary>University when the student chose to show it; null otherwise.</summary>
    public string? University { get; set; }

    /// <summary>Field of study when the student chose to show it; null otherwise.</summary>
    public string? FieldOfStudy { get; set; }

    /// <summary>Study year when the student chose to show it; null otherwise.</summary>
    public int? StudyYear { get; set; }

    /// <summary>JSON snapshot of the analyzed items and skills (see
    /// <see cref="TalentIndexItemSnapshot"/> for the per-item shape). Stored as
    /// <c>jsonb</c> so Phase 2 can re-validate citations against the snapshot
    /// without round-tripping the per-item / per-skill rows that RLS locks
    /// away from the Organization.</summary>
    public required string ItemsJson { get; set; }

    /// <summary>Plain-text summary the embedding model was run against. Built
    /// by the processor from non-sensitive fields and capped at 8 000
    /// characters.</summary>
    public required string SearchText { get; set; }

    /// <summary>Lowercase hex SHA-256 over the projection inputs. When the
    /// processor computes the same hash on a re-run it skips the embedding
    /// call entirely (the vector would be identical), keeping the LLM bill
    /// bounded on no-op refreshes.</summary>
    public required string ContentHash { get; set; }

    /// <summary>UTC timestamp of the most recent successful upsert by the
    /// refresh processor.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Per-item snapshot embedded in <see cref="TalentIndexEntry.ItemsJson"/>.
/// The shape is stable across Phase 2 — Phase 2 re-validates its citations
/// against this snapshot and rejects any <c>portfolioItemId</c> that isn't
/// present here.</summary>
/// <param name="PortfolioItemId">The <see cref="PortfolioItem.Id"/> this
/// snapshot describes. Stable across re-analysis (item ids are preserved;
/// finding ids are not).</param>
/// <param name="Label">The item's <see cref="PortfolioItem.Label"/>.</param>
/// <param name="Category">The display value the processor stores for the item's
/// category — either the fixed category from <see cref="PortfolioCategories"/>
/// or the student's <see cref="PortfolioItem.CustomCategoryText"/> when the
/// category is <see cref="PortfolioCategories.Other"/>.</param>
/// <param name="Skills">The Strong / Developing skill findings for the item.
/// Missing-band findings are deliberately excluded — the index only carries
/// skills the student can claim.</param>
/// <param name="Original">STOR-44 Phase 1: the per-item "where is the original?"
/// descriptor that an Organization uses to drill down behind the index in
/// Phase 2. Populated ONLY when the student explicitly opted this item in via
/// <see cref="PortfolioItem.ShareOriginalWithEmployers"/>; null otherwise —
/// which keeps the JSON shape lean (no <c>original</c> property at all for
/// items the student kept private) and keeps existing stored snapshots
/// (Phase 1 wrote no <c>original</c> property) deserialisable without
/// migration.</param>
public sealed record TalentIndexItemSnapshot(
    Guid PortfolioItemId,
    string Label,
    string Category,
    IReadOnlyList<TalentIndexSkillSnapshot> Skills,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] TalentIndexOriginalSnapshot? Original = null);

/// <summary>STOR-44 Phase 1: the per-item "where is the original?" descriptor
/// the refresh processor copies into <see cref="TalentIndexEntry.ItemsJson"/>
/// when the student explicitly opted this item in via
/// <see cref="PortfolioItem.ShareOriginalWithEmployers"/>.</summary>
/// <remarks>
/// <para>
/// <b>Why a server-side copy of the storage key.</b> An Organization account
/// cannot read <see cref="PortfolioItem"/> rows because the row-level-security
/// policy blocks them. The whole point of the descriptor is to give a
/// drill-down target — the storage key, or the external URL — without
/// reading the private table. The processor reads the private row under
/// the student's account scope and copies the relevant fields into this
/// snapshot for Phase 2 to read under the Organization's account scope.
/// </para>
/// <para>
/// <b>Why <see cref="FileName"/>, <see cref="ContentType"/>, and
/// <see cref="SizeBytes"/> are nullable.</b> Only <see cref="Kind"/> and one
/// of (<see cref="StorageKey"/> / <see cref="Url"/>) are required for the
/// drill-down target. The other three are display hints an Organization
/// endpoint can use to render a meaningful link/file row — but a Link
/// submission has no file, so the File-only fields are null for those.
/// </para>
/// </remarks>
/// <param name="Kind">One of <see cref="TalentIndexOriginalKinds.File"/> or
/// <see cref="TalentIndexOriginalKinds.Link"/>.</param>
/// <param name="FileName">The original uploaded file's filename, populated
/// for File submissions only.</param>
/// <param name="ContentType">The MIME content type of the uploaded file,
/// populated for File submissions only.</param>
/// <param name="SizeBytes">The size of the uploaded file in bytes, populated
/// for File submissions only.</param>
/// <param name="StorageKey">The <see cref="IArtifactStore"/> key for the upload,
/// populated for File submissions only. Phase 2 will hand this to
/// <see cref="IArtifactStore"/> through the Organization's account scope to
/// stream the bytes.</param>
/// <param name="Url">The external URL, populated for Link submissions only.</param>
public sealed record TalentIndexOriginalSnapshot(
    string Kind,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    string? StorageKey,
    string? Url);

/// <summary>Allowed values for <see cref="TalentIndexOriginalSnapshot.Kind"/>:
/// <c>File</c> for portfolio items uploaded to <c>IArtifactStore</c>,
/// <c>Link</c> for portfolio items whose original lives at an external URL.
/// Mirrors <see cref="PortfolioSubmissionTypes"/>'s split so the processor's
/// <c>PortfolioItem.SubmissionType == PortfolioSubmissionTypes.X</c> branch
/// maps one-to-one onto the descriptor's <c>Kind</c>.</summary>
public static class TalentIndexOriginalKinds
{
    public const string File = "File";
    public const string Link = "Link";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        File,
        Link,
    };
}

/// <summary>Per-skill entry inside <see cref="TalentIndexItemSnapshot"/>.
/// Carries the skill name, band, and AI-written reason; never carries item
/// descriptions, URLs, file names, or storage keys.</summary>
/// <param name="Name">The skill name.</param>
/// <param name="Band">One of <see cref="ConfidenceBands.Strong"/> or
/// <see cref="ConfidenceBands.Developing"/> — Missing-band findings are not
/// stored in the snapshot.</param>
/// <param name="Reason">The AI-written short explanation for the assigned
/// band, copied verbatim from <see cref="PortfolioSkillFinding.Explanation"/>
/// so the citation is grounded in what the model actually saw.</param>
public sealed record TalentIndexSkillSnapshot(
    string Name,
    string Band,
    string Reason);
