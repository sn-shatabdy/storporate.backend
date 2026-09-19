namespace Storporate.Modules.StudentGrowthExperience;

/// <summary>Module-level constants the advisor pipeline reads on the hot
/// path. Kept in their own type so the value lives in one place — the
/// opening-turn handler writes it as the default title, the
/// <see cref="Advisor.AdvisorTurnJobProcessor"/> checks against it before
/// overwriting with the AI's title suggestion.</summary>
public static class AdvisorDefaults
{
    /// <summary>The default title written to a freshly-created
    /// <see cref="Storporate.SharedKernel.Entities.Exploration"/>. The
    /// opening-turn processor overwrites it (only when still equal to this
    /// default!) with the AI's <c>title</c> suggestion; after that the
    /// student's <c>PUT /title</c> writes win.</summary>
    public const string DefaultExplorationTitle = "New exploration";

    /// <summary>The friendly <see cref="Storporate.SharedKernel.Entities.Exploration.LastError"/>
    /// string written when a turn exhausts its retry budget or when the
    /// reaper abandons a stale <c>Running</c> job. Never expose the raw LLM
    /// error in user-facing error slots — this constant is the only thing
    /// the user sees.</summary>
    public const string FriendlyAdvisorError = "The advisor could not finish this. Try again.";
}
