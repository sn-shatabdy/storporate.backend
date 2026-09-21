namespace Storporate.Infrastructure.Llm;

/// <summary>
/// Configuration for the talent-search embedding model, bound to the
/// <c>Llm:Embedding</c> section. Mirrors
/// <see cref="BionicOptions"/>'s shape — a single OpenAI-compatible endpoint
/// pointed at the local Bionic server — but with a few knobs the chat
/// provider doesn't need: an explicit dimension the provider checks against
/// every response, and a batch-size cap the refresh processor never sees
/// directly because the embedding client itself chunks internally.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no <c>[Required]</c> members.</b> The two test host factories
/// (<c>AuthEndpointsFactory</c>, <c>PermissionCoverageTests.PermissionCoverageFactory</c>)
/// supply placeholder values for every Options-bound section at startup so
/// the host's fail-fast <c>ValidateOnStart</c> pipeline doesn't reject startup.
/// Marking any field here <c>[Required]</c> would force those factories to
/// add a new placeholder — the established project rule (see the comments on
/// <see cref="BackgroundJobOptions"/> / AdvisorOptions in <c>Program.cs</c>) is
/// that any future <c>[Required]</c> on a new options class must also be
/// added to those hosts. Skipping the <c>[Required]</c> attributes here keeps
/// those factories stable when this section is added. The startup check on
/// the embedding dimension lives in the refresh processor's call site rather
/// than in <c>ValidateDataAnnotations</c> for the same reason.
/// </para>
/// <para>
/// <b>Why <see cref="BaseUrl"/> falls back to <see cref="BionicOptions.BaseUrl"/>.</b>
/// When the section is missing the explicit <c>BaseUrl</c>, the embedding
/// provider reuses the same local Bionic server the chat provider does. A
/// future hosted embedding endpoint only needs to populate the field.
/// </para>
/// </remarks>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Llm:Embedding";

    /// <summary>The OpenAI-compatible base URL for the embedding endpoint.
    /// When empty, the embedding client falls back to
    /// <see cref="BionicOptions.BaseUrl"/>. Empty by design (no
    /// <c>[Required]</c>) so test hosts don't need a placeholder.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>The model id sent in every <c>/embeddings</c> request body.
    /// Required by the OpenAI-compatible contract — the server doesn't
    /// auto-select even with just-in-time loading on. Empty by design
    /// (no <c>[Required]</c>) so test hosts can supply a placeholder only
    /// for the tests that actually exercise the embedding client.</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>Vector dimension the provider checks against every response.
    /// Defaults to <see cref="Storporate.SharedKernel.Entities.TalentIndexConstants.EmbeddingDimensions"/>
    /// (768) so a misconfigured override fails fast at the embedding call
    /// rather than at the pgvector insert.</summary>
    public int Dimensions { get; init; } = Storporate.SharedKernel.Entities.TalentIndexConstants.EmbeddingDimensions;

    /// <summary>Maximum number of inputs the client sends in one
    /// <c>/embeddings</c> request. The OpenAI-compatible servers vary in
    /// what they accept per call; 16 keeps the request body well under any
    /// practical limit. Larger batches are split client-side.</summary>
    public int MaxBatchSize { get; init; } = 16;
}
