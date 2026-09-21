using Storporate.SharedKernel.Abstractions;

namespace Storporate.Tests.Unit.Fakes;

/// <summary>
/// Test double for <see cref="ITalentIndexRepository"/> used to verify
/// opt-out atomicity in <c>UpdateSearchableProfileHandler</c>:
/// <see cref="DeleteAsync"/> throws <see cref="InvalidOperationException"/>
/// so the test can prove the profile row is NOT flipped to
/// <c>IsSearchable=false</c> when the entry delete fails. Upsert and search
/// are no-op so the rest of the pipeline (and other tests sharing the same
/// <see cref="Fakes.FakeAuditLogWriter"/>) keep working.
/// </summary>
public sealed class FailingTalentIndexRepository : ITalentIndexRepository
{
    public int DeleteCallCount { get; private set; }

    public Task DeleteAsync(Guid studentAccountId, CancellationToken cancellationToken = default)
    {
        DeleteCallCount++;
        throw new InvalidOperationException(
            $"Simulated delete failure for student {studentAccountId}.");
    }

    public Task UpsertAsync(
        Guid studentAccountId,
        string displayName,
        string? headline,
        string? university,
        string? fieldOfStudy,
        int? studyYear,
        string itemsJson,
        string searchText,
        string contentHash,
        IReadOnlyList<float> embedding,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "FailingTalentIndexRepository only simulates a Delete failure; "
            + "Upsert should not be reached by tests that swap it in.");

    public Task<IReadOnlyList<TalentIndexSearchHit>> SearchNearestAsync(
        IReadOnlyList<float> queryVector,
        int k,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TalentIndexSearchHit>>(Array.Empty<TalentIndexSearchHit>());

    /// <summary>STOR-44 Phase 1: when the test wires this fake in for the
    /// switch-off path, ClearOriginalAsync must throw too so the test
    /// proves the flag stays true when the descriptor clear fails. The
    /// counter lets the test assert "ClearOriginalAsync was attempted" so
    /// the assertion is not vacuous.</summary>
    public int ClearOriginalCallCount { get; private set; }

    public Task ClearOriginalAsync(
        Guid studentAccountId,
        Guid portfolioItemId,
        CancellationToken cancellationToken = default)
    {
        ClearOriginalCallCount++;
        throw new InvalidOperationException(
            $"Simulated ClearOriginal failure for student {studentAccountId}, item {portfolioItemId}.");
    }
}
