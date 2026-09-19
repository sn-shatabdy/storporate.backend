using Storporate.Infrastructure.Jobs;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Jobs;

/// <summary>
/// STOR-40 Phase 1: pin the round-robin processor-order rotation in
/// <see cref="PortfolioAnalysisWorker.OrderRoundRobin"/>. The worker must not let
/// the first registered processor starve the others when the first one always has
/// work to claim — every processor in the registered set must get a chance to run
/// before the first one is tried again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why test the static helper, not the live worker.</b>
/// <see cref="PortfolioAnalysisWorker.OrderRoundRobin"/> is the only piece of the
/// fairness contract that's a pure function — the live worker's iteration order is
/// observable through the helper's return value, and standing up a full
/// <see cref="IHostedService"/> loop just to read off one rotation would couple the
/// test to the host's task-scheduling behavior. The helper's contract is the
/// stable surface a future test can build on.
/// </para>
/// <para>
/// <b>Why four processors and four ticks.</b> The plan's fairness acceptance
/// criterion is "across four consecutive ticks the first-tried processor must not
/// be the same one twice in a row." Four processors cycled through four ticks
/// prove both the no-immediate-repeat rule and the wrap-around behavior when
/// the tick index passes <c>processors.Count</c>.
/// </para>
/// </remarks>
public class BackgroundJobWorkerFairnessTests
{
    [Fact]
    public void OrderRoundRobin_AcrossFourTicks_NoFirstProcessorIsTriedTwiceInARow()
    {
        var processors = new IBackgroundJobProcessor[]
        {
            new NoopProcessor("A"),
            new NoopProcessor("B"),
            new NoopProcessor("C"),
            new NoopProcessor("D"),
        };

        // Tick 1 starts at 0 (the default worker state).
        // Tick 2 starts at 1, tick 3 at 2, tick 4 at 3.
        // After four ticks the first-tried processor in each rotated order must
        // be A, B, C, D respectively.
        var tick1First = LabelOf(OrderRoundRobinProcessors(processors, tickIndex: 0).First());
        var tick2First = LabelOf(OrderRoundRobinProcessors(processors, tickIndex: 1).First());
        var tick3First = LabelOf(OrderRoundRobinProcessors(processors, tickIndex: 2).First());
        var tick4First = LabelOf(OrderRoundRobinProcessors(processors, tickIndex: 3).First());

        Assert.Equal("A", tick1First);
        Assert.Equal("B", tick2First);
        Assert.Equal("C", tick3First);
        Assert.Equal("D", tick4First);
    }

    [Fact]
    public void OrderRoundRobin_WrapsPastEndOfProcessorList()
    {
        // Five ticks with four processors: the modulo must wrap so tick 5's
        // first-tried is "A" again, and the sequence is preserved modulo length.
        var processors = new IBackgroundJobProcessor[]
        {
            new NoopProcessor("A"),
            new NoopProcessor("B"),
            new NoopProcessor("C"),
            new NoopProcessor("D"),
        };

        var tick5First = LabelOf(OrderRoundRobinProcessors(processors, tickIndex: 4).First());
        Assert.Equal("A", tick5First);

        var tick5Order = OrderRoundRobinProcessors(processors, tickIndex: 4)
            .Select(LabelOf)
            .ToArray();
        Assert.Equal(new[] { "A", "B", "C", "D" }, tick5Order);
    }

    [Fact]
    public void OrderRoundRobin_EmptyProcessorList_ReturnsEmpty()
    {
        // The worker dereferences `_roundRobinStartIndex % processors.Count`; an
        // empty list would throw on the modulo if the helper didn't guard it.
        // The worker itself skips the loop when processors.Count == 0, but the
        // helper's contract must still be safe to call.
        var result = PortfolioAnalysisWorker.OrderRoundRobin(Array.Empty<IBackgroundJobProcessor>(), 0);
        Assert.Empty(result);
    }

    [Fact]
    public void OrderRoundRobin_NormalizesNegativeStartIndex()
    {
        // Defensive: a future caller that wraps the index into a negative number
        // (e.g. an underflow on decrement) must still rotate correctly rather than
        // throw on the modulo.
        var processors = new IBackgroundJobProcessor[]
        {
            new NoopProcessor("A"),
            new NoopProcessor("B"),
            new NoopProcessor("C"),
        };

        var order = OrderRoundRobinProcessors(processors, tickIndex: -1)
            .Select(LabelOf)
            .ToArray();
        Assert.Equal(new[] { "C", "A", "B" }, order);
    }

    /// <summary>
    /// Extract the test-only label from the processor — the interface only exposes
    /// <see cref="IBackgroundJobProcessor.JobType"/>, so we cast to the test fake
    /// to read the human-readable tag.
    /// </summary>
    private static string LabelOf(IBackgroundJobProcessor processor) =>
        ((NoopProcessor)processor).Label;

    /// <summary>
    /// Mirror the worker's own call: pass the starting index that the worker would
    /// have just stored as <c>_roundRobinStartIndex</c> on this tick, then read off
    /// the first processor the worker would try.
    /// </summary>
    private static IReadOnlyList<IBackgroundJobProcessor> OrderRoundRobinProcessors(
        IReadOnlyList<IBackgroundJobProcessor> processors,
        int tickIndex) =>
        PortfolioAnalysisWorker.OrderRoundRobin(processors, tickIndex);

    /// <summary>Minimal <see cref="IBackgroundJobProcessor"/> whose
    /// <see cref="Label"/> is what the assertions read; <see cref="TryProcessOneAsync"/>
    /// returns <see cref="BackgroundJobTickOutcome.NoWork"/> so the worker would
    /// iterate to the next processor on the next tick.</summary>
    private sealed class NoopProcessor : IBackgroundJobProcessor
    {
        public NoopProcessor(string label) => Label = label;
        public string Label { get; }
        public string JobType => $"noop-{Label}";
        public Task<BackgroundJobTickOutcome> TryProcessOneAsync(CancellationToken cancellationToken) =>
            Task.FromResult(BackgroundJobTickOutcome.NoWork);
        public Task OnJobAbandonedAsync(Storporate.SharedKernel.Entities.Job job, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}