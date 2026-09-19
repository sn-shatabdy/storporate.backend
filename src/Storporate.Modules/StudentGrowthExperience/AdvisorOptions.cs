namespace Storporate.Modules.StudentGrowthExperience;

/// <summary>
/// Knobs for the STOR-40 advisor pipeline (and the matching / summary jobs that
/// share the same prompt budget). Defaults were sized against the local
/// reasoning model on Bionic — <c>google/gemma-4-12b-qat</c>, a
/// Quantization-Aware-Trained Gemma 4 with a documented 128 k-token context
/// window — measured live in Phase 1 (2026-09-19, Bionic on localhost:1234):
/// cold-start ~48 s, warm latency ~2 s for a 16-token reply, of which ~13 of
/// those tokens were <c>reasoning_tokens</c>. The model is the same one the
/// Phase 2 portfolio analyzer already calls; the advisor inherits its
/// quirks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why character budgets, not token budgets.</b> The local Bionic endpoint
/// has no tokenizer of its own — the model counts tokens internally and the
/// HTTP layer can't see them. We size every prompt slice in characters and
/// keep a safety margin (~75 % of the model's documented window) so the
/// model never sees a request it has to truncate.
/// </para>
/// <para>
/// <b>Total prompt budget (Phase 2).</b> The user prompt is bounded by
/// <see cref="MaxUserPromptCharacters"/> = 24 000. Inside that envelope the
/// budget splits roughly as:
/// <list type="bullet">
/// <item>Up to <see cref="MaxFeedCandidates"/> × <see cref="MaxFeedCandidateCharacters"/>
/// ≈ 12 × 600 = 7 200 chars for feed candidates (title + summary; URL stays
/// server-side and is not embedded).</item>
/// <item>Up to <see cref="HistoryWindowTurns"/> × 2 history entries capped at
/// <see cref="MaxHistoryEntryCharacters"/> = 6 × 2 × 3 000 = 36 000 if the
/// window were ever fully populated, but only the most recent few turns
/// are ever included and each one is cut to <see cref="MaxHistoryEntryCharacters"/>.</item>
/// <item>Portfolio items are appended newest-first until the
/// <see cref="MaxUserPromptCharacters"/> budget is exhausted.</item>
/// </list>
/// The feed candidate slice is intentionally conservative because a feed
/// summary can be longer than the headline suggests; truncating the feed
/// summary to 600 chars keeps the candidate section from crowding out the
/// portfolio section.
/// </para>
/// <para>
/// <b>Why no <c>[Required]</c>.</b> Follows the rule established for the
/// other <c>Options</c> classes (see the <c>BackgroundJobOptions</c>
/// commentary in <c>Program.cs</c>): every value has a sensible default, and
/// tests that bind this section with no value pass through untouched.
/// </para>
/// </remarks>
public sealed class AdvisorOptions
{
    public const string SectionName = "Advisor";

    /// <summary>
    /// Maximum characters of <c>UserPrompt</c> sent on any single advisor
    /// turn. Sized to leave generous headroom for the system prompt,
    /// rolling history, feed candidates, and the 4096-token
    /// <see cref="MaxOutputTokens"/> budget on a 128 k-token model.
    /// </summary>
    public int MaxUserPromptCharacters { get; init; } = 24_000;

    /// <summary>
    /// Maximum characters of any single portfolio item embedded into the
    /// user prompt. Items longer than this are truncated (with an
    /// explicit "…[truncated]" marker) so a single large submission cannot
    /// crowd out the rest of the prompt.
    /// </summary>
    public int MaxPortfolioItemCharacters { get; init; } = 4_000;

    /// <summary>
    /// Maximum characters of any single feed candidate embedded into the
    /// user prompt. Feed items above this size are skipped entirely
    /// (rather than truncated) — a partial feed item is more misleading
    /// than no item at all. Phase 2: tightened from 1 500 → 600 to keep
    /// 12 candidates × 600 ≈ 7 200 chars inside the 24 000-char budget.
    /// </summary>
    public int MaxFeedCandidateCharacters { get; init; } = 600;

    /// <summary>
    /// Maximum number of feed candidates included in the user prompt. The
    /// top-N by recency are kept; the rest are dropped. Phase 2: widened
    /// from 6 → 12 so the advisor sees a broader slice of the platform's
    /// recent outside facts. The per-candidate character cap keeps the
    /// total slice at roughly 7 200 chars.
    /// </summary>
    public int MaxFeedCandidates { get; init; } = 12;

    /// <summary>
    /// Rolling-window size for prior assistant turns included as
    /// <see cref="Storporate.SharedKernel.Abstractions.LlmChatMessage"/>
    /// <c>history</c> in the request. Older turns are summarized (or
    /// dropped entirely on a fresh exploration) — see the matching
    /// pipeline's <c>ExplorationSummaryVersion</c> table. The default
    /// holds the most recent six turns verbatim.
    /// </summary>
    public int HistoryWindowTurns { get; init; } = 6;

    /// <summary>
    /// Maximum characters per prior-turn history entry. Sized to keep
    /// six turns within <see cref="MaxUserPromptCharacters"/> even when
    /// every turn is at the cap.
    /// </summary>
    public int MaxHistoryEntryCharacters { get; init; } = 3_000;

    /// <summary>
    /// Output budget per advisor turn. Sized generously because the
    /// configured model spends a non-trivial fraction of its budget on
    /// internal reasoning before producing the JSON payload — the same
    /// quirk the Phase 2 portfolio analyzer documents. The advisor's
    /// JSON contract (reply + questions[] + suggestions[] + gaps[] +
    /// contextNotes[]) is larger than the portfolio analyzer's, so this
    /// budget is several times higher than the portfolio's 512-token
    /// default.
    /// </summary>
    public int MaxOutputTokens { get; init; } = 4096;

    /// <summary>
    /// Timeout for a single advisor turn's HTTP call to Bionic. Sized
    /// against the measured cold-start (~48 s) plus the warm latency
    /// (~2 s per token). With <see cref="MaxOutputTokens"/> = 4096 and a
    /// reasonable expected reasoning share, the worst-case wall-clock
    /// is bounded well within 120 s; the timeout is left generous so
    /// the worker doesn't race a busy host.
    /// </summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Maximum number of <c>Exploration</c> rows a single student may own
    /// at any one time. Hitting this cap on <c>POST /api/growth/explorations</c>
    /// maps to <c>409 exploration_limit_reached</c> via
    /// <see cref="Advisor.Exceptions.ExplorationLimitReachedException"/>.
    /// Sized generously (20) because the per-student exploration is a
    /// long-running artifact — a student is expected to keep a handful of
    /// live directions in flight and refresh / prune the rest.
    /// </summary>
    public int MaxExplorationsPerStudent { get; init; } = 20;
}
