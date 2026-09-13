using System.ComponentModel.DataAnnotations;

namespace Storporate.Infrastructure.Llm;

/// <summary>
/// Configuration for the local Bionic-hosted LLM server, bound to the "Llm:Bionic" section.
/// CONFIRMED endpoint/model (Phase 1, 2026-09-12): BaseUrl "http://localhost:1234/v1",
/// ModelId "google/gemma-4-12b-qat" — the model id must be sent explicitly in every request body.
/// </summary>
public sealed class BionicOptions
{
    public const string SectionName = "Llm:Bionic";

    [Required]
    public string BaseUrl { get; init; } = string.Empty;

    [Required]
    public string ModelId { get; init; } = string.Empty;
}
