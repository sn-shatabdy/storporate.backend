namespace Storporate.Modules.Portfolio.Analysis;

/// <summary>
/// Discriminator values for <see cref="Storporate.SharedKernel.Entities.Job"/> rows the
/// Portfolio module owns. Kept alongside the analysis worker in
/// <c>Storporate.Modules/Portfolio/Analysis/</c> — both because there is no other
/// <see cref="Storporate.SharedKernel.Entities.Job"/>-issuing feature today and because
/// the job producer (<see cref="CreatePortfolioItemHandler"/>) and the job consumer
/// (<see cref="PortfolioAnalysisJobProcessor"/>) live in the same module and want a
/// shared constant for the discriminator string.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="Storporate.SharedKernel.Entities.JobStatus"/> /
/// <see cref="Storporate.SharedKernel.Entities.PortfolioCategories"/>'s string-constant
/// style rather than a C# enum: the <c>Jobs.Type</c> column is already a string, and
/// adding a new job type later doesn't require a migration to widen anything. The
/// payload shape itself is a <c>jsonb</c> document on <c>Jobs.PayloadJson</c> — its
/// concrete shape lives next to the producer that fills it (see the
/// <c>AnalyzePortfolioItemPayload</c> record below).
/// </para>
/// </remarks>
public static class PortfolioJobTypes
{
    /// <summary>Type discriminator for the STOR-38 Phase 2 background analysis job. The
    /// worker (<see cref="PortfolioAnalysisWorker"/>) queries on this literal value to
    /// pick its work off the shared <c>Jobs</c> queue, so it must match
    /// <see cref="CreatePortfolioItemHandler"/>'s insert exactly.</summary>
    public const string AnalyzePortfolioItem = "AnalyzePortfolioItem";
}

/// <summary>
/// Concrete payload shape for an <see cref="PortfolioJobTypes.AnalyzePortfolioItem"/>
/// job, serialized into <see cref="Storporate.SharedKernel.Entities.Job.PayloadJson"/>
/// (<c>jsonb</c>). Property names are <c>PascalCase</c> to match the JSON the worker
/// deserializes back out; <see cref="System.Text.Json.JsonSerializerOptions.PropertyNamingPolicy"/>
/// is left at its default so the wire and property names align.
/// </summary>
/// <remarks>
/// Held as a <c>public sealed record</c> (not a record inside <see cref="PortfolioJobTypes"/>)
/// only to keep the type discoverable in its own file — a future job type from a
/// different module would add its own <c>public sealed record FooJobPayload</c> next
/// to its own job-type constant rather than reusing this one.
/// </remarks>
public sealed record AnalyzePortfolioItemPayload(Guid PortfolioItemId);
