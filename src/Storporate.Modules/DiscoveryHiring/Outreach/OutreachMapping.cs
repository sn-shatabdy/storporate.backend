using System.Text.Json;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.Outreach;

/// <summary>Shared projection helpers for the employer and student conversation surfaces (STOR-68).</summary>
internal static class OutreachMapping
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string Preview(string body) =>
        body.Length <= OutreachLimits.PreviewLength ? body : body[..OutreachLimits.PreviewLength];

    /// <summary>Summary row; <paramref name="viewerRole"/> decides which party's name is the counterpart.</summary>
    internal static ConversationSummaryResponse ToSummary(
        OutreachConversation conversation,
        string viewerRole,
        string lastBody,
        DateTimeOffset lastAt) =>
        new(
            conversation.Id,
            CounterpartName(conversation, viewerRole),
            conversation.Status,
            Preview(lastBody),
            lastAt,
            conversation.UpdatedAt);

    internal static ConversationDetailResponse ToDetail(
        OutreachConversation conversation,
        IReadOnlyList<OutreachMessage> messages,
        string viewerRole)
    {
        var ordered = messages.OrderBy(m => m.CreatedAt).ToList();
        var last = ordered.Count > 0 ? ordered[^1] : null;
        return new ConversationDetailResponse(
            conversation.Id,
            CounterpartName(conversation, viewerRole),
            conversation.Status,
            Preview(last?.Body ?? string.Empty),
            last?.CreatedAt ?? conversation.UpdatedAt,
            conversation.UpdatedAt,
            ordered
                .Select(m => new OutreachMessageResponse(
                    m.Id,
                    m.SenderRole,
                    m.Body,
                    m.CreatedAt,
                    string.Equals(m.SenderRole, viewerRole, StringComparison.Ordinal)))
                .ToList());
    }

    private static string CounterpartName(OutreachConversation conversation, string viewerRole) =>
        viewerRole == OutreachSenderRoles.Organization
            ? conversation.StudentDisplayName
            : conversation.OrganizationName;
}
