namespace Storporate.SharedKernel.Entities;

/// <summary>
/// Sender role of a <see cref="ExplorationMessage"/>. The model can only ever
/// be <see cref="Student"/> (the human) or <see cref="Advisor"/> (the AI);
/// other roles (tool/function) are intentionally absent because the advisor
/// pipeline never makes tool calls in this phase.
/// </summary>
public static class ExplorationRoles
{
    public const string Student = "Student";
    public const string Advisor = "Advisor";
}