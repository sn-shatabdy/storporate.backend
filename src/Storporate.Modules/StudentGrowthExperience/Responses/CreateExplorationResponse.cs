namespace Storporate.Modules.StudentGrowthExperience.Responses;

/// <summary>Response body for <c>POST /api/growth/explorations</c> — the
/// freshly-created exploration's id and the always-<see cref="Status"/>
/// <c>Working</c> (an opening-turn job has been enqueued).</summary>
public sealed record CreateExplorationResponse(
    Guid Id,
    string Status);
