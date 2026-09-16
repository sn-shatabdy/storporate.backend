using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Portfolio.Analysis;

/// <summary>
/// A single skill finding parsed from the LLM's JSON response — the shape the
/// <see cref="PortfolioAnalysisPromptBuilder"/>'s system prompt asks the model to
/// emit, and the shape <see cref="PortfolioAnalysisJobProcessor"/> deserializes
/// back out before mapping into <see cref="PortfolioSkillFinding"/> rows.
/// </summary>
/// <remarks>
/// <para>
/// Held as a <c>public sealed record</c> rather than an entity POCO so it can
/// round-trip through <see cref="System.Text.Json.JsonSerializer"/> without picking
/// up EF Core mapping metadata or the global query filter. <see cref="ConfidenceBand"/>
/// is validated against <see cref="ConfidenceBands"/> in the processor before it's
/// written to the database.
/// </para>
/// </remarks>
public sealed record PortfolioSkillFindingDto(string Skill, string Band, string Explanation);

/// <summary>
/// Builds the system + user prompt pair <see cref="PortfolioAnalysisJobProcessor"/>
/// passes to <see cref="ILlmClient"/>. Pure function — no DI, no state — so it can
/// be unit-tested without any infrastructure and is cheap to call from every job
/// tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>System prompt contract.</b> The system prompt instructs the model to return
/// its answer as a strict JSON array of <see cref="PortfolioSkillFindingDto"/>
/// objects with the literal <c>Strong</c>/<c>Developing</c>/<c>Missing</c> band
/// values from <see cref="ConfidenceBands"/>. The model is told not to invent
/// facts beyond what the evidence text actually shows — this is the grounding
/// rule that the <c>Explanation</c> field is meant to expose to the student on
/// the detail page (see <see cref="PortfolioSkillFinding.Explanation"/>'s
/// doc-comment).
/// </para>
/// <para>
/// <b>User prompt shape.</b> The user prompt carries the extracted evidence text
/// (truncated to <see cref="MaxUserPromptEvidenceChars"/> to respect the local
/// model's context window) plus the item's label / category / description so the
/// model has the same framing a human reviewer would. The truncation is a hard
/// cut, not an ellipsis: a long PDF that exceeds the budget is still analyzed,
/// just from its opening portion.
/// </para>
/// </remarks>
public static class PortfolioAnalysisPromptBuilder
{
    /// <summary>Maximum number of output tokens to budget for a single completion.
    /// 2048 is generous enough for the local reasoning model (Gemma 3 12B's
    /// chain-of-thought is the documented drain — see <see cref="LlmCompletionRequest.MaxOutputTokens"/>)
    /// to fit the JSON array plus its reasoning trace, while still keeping
    /// runaway completions bounded.</summary>
    public const int MaxOutputTokens = 2048;

    /// <summary>Hard cap on the user-prompt evidence block (in characters) so a
    /// multi-megabyte PDF can't push the prompt past the model's context window.
    /// ~24 KB of UTF-8 text fits comfortably under the Gemma 3 12B 8K context
    /// once the system prompt + reasoning budget are accounted for.</summary>
    public const int MaxUserPromptEvidenceChars = 24_000;

    /// <summary>The system prompt sent on every analysis call. The verbatim wording
    /// is what <see cref="PortfolioAnalysisJobProcessor"/>'s JSON-parser test
    /// asserts against (alongside the user's evidence text) — keep this stable.</summary>
    public static string BuildSystemPrompt() =>
        """
        You are an evidence-grounded skill analyzer for a student portfolio platform.

        Given a single piece of portfolio evidence (text extracted from a file the
        student uploaded, or the student's own Label/Description for a link), return
        a JSON array of skill findings using ONLY information that is directly
        supported by the evidence. Do not invent skills the evidence does not back.

        Return STRICT JSON (no prose, no markdown code fences) in exactly this shape:

        [
          {
            "skill": string,
            "band": "Strong" | "Developing" | "Missing",
            "explanation": string
          }
        ]

        Band definitions:
        - "Strong": the evidence clearly backs the skill — direct, unambiguous
          evidence in the submitted text.
        - "Developing": the evidence partially backs the skill — relevant but
          incomplete.
        - "Missing": the student named the skill but the evidence doesn't
          substantiate it, OR the evidence explicitly shows the skill is missing.

        For each skill, the explanation must cite a concrete, observable detail
        from the evidence (or, for "Missing", name what is absent). Avoid generic
        platitudes. Plain text only inside the explanation field — no markdown,
        no bullet points, no lists.

        If the evidence contains no analyzable skill signal at all, return an
        empty array: [].
        """;

    /// <summary>Builds the user prompt from the portfolio item + already-extracted
    /// evidence text. The evidence block is truncated to
    /// <see cref="MaxUserPromptEvidenceChars"/> if needed.</summary>
    /// <param name="portfolioItem">The portfolio item whose evidence is being analyzed.</param>
    /// <param name="evidenceText">The text returned by
    /// <see cref="EvidenceContentExtractor.ExtractAsync"/> — already populated for
    /// text-extractable submissions; for link submissions it is the
    /// Label/Description/Category concatenation the extractor itself builds.</param>
    public static LlmCompletionRequest BuildCompletionRequest(
        PortfolioItem portfolioItem,
        string evidenceText)
    {
        ArgumentNullException.ThrowIfNull(portfolioItem);
        ArgumentNullException.ThrowIfNull(evidenceText);

        var truncatedEvidence = evidenceText.Length <= MaxUserPromptEvidenceChars
            ? evidenceText
            : evidenceText[..MaxUserPromptEvidenceChars];

        var userPrompt =
            $"""
             Portfolio item:
               Label: {portfolioItem.Label}
               Category: {portfolioItem.Category}{(string.IsNullOrWhiteSpace(portfolioItem.CustomCategoryText) ? string.Empty : $" ({portfolioItem.CustomCategoryText})")}
               Description: {portfolioItem.Description ?? string.Empty}

             Evidence text:
             {truncatedEvidence}
             """;

        return new LlmCompletionRequest(
            UserPrompt: userPrompt,
            SystemPrompt: BuildSystemPrompt(),
            MaxOutputTokens: MaxOutputTokens);
    }
}
