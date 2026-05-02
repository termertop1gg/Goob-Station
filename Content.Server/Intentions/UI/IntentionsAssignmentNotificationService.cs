using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Content.Server.Chat.Managers;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Validation;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server.Intentions.UI;

/// <summary>
/// Builds and dispatches private chat summaries for newly assigned runtime intentions.
/// </summary>
public sealed class IntentionsAssignmentNotificationService
{
    private static readonly Dictionary<string, string> EmptyParameters = new(StringComparer.Ordinal);

    private readonly Func<string, IReadOnlyDictionary<string, string>, string?> _locResolver;

    /// <summary>
    /// Creates the notification service with an optional localization resolver override for tests.
    /// </summary>
    public IntentionsAssignmentNotificationService(Func<string, IReadOnlyDictionary<string, string>, string?>? locResolver = null)
    {
        _locResolver = locResolver ?? DefaultLocResolver;
    }

    /// <summary>
    /// Builds one private chat notification per affected mind from a batch of newly committed intentions.
    /// </summary>
    public ImmutableArray<IntentionsAssignmentChatNotification> BuildNotifications(
        ValidationCatalog catalog,
        IEnumerable<IntentionInstance> intentions)
    {
        var notifications = ImmutableArray.CreateBuilder<IntentionsAssignmentChatNotification>();

        foreach (var group in intentions
                     .OrderBy(intention => intention.AssignedAtRoundTime)
                     .ThenBy(intention => intention.Uid.Value)
                     .GroupBy(intention => intention.OwnerMindId)
                     .OrderBy(group => group.Key.Id))
        {
            var visibleTitles = ImmutableArray.CreateBuilder<string>();
            var hiddenCount = 0;

            foreach (var intention in group)
            {
                if (intention.IsHidden)
                {
                    hiddenCount++;
                    continue;
                }

                visibleTitles.Add(ResolveVisibleTitle(catalog, intention));
            }

            if (visibleTitles.Count == 0 && hiddenCount == 0)
                continue;

            var visible = visibleTitles.ToImmutable();
            notifications.Add(new IntentionsAssignmentChatNotification(
                group.Key,
                visible,
                hiddenCount,
                BuildMessage(visible, hiddenCount)));
        }

        return notifications.ToImmutable();
    }

    /// <summary>
    /// Dispatches the private notifications for a committed batch to connected in-round players.
    /// </summary>
    public void DispatchNotifications(
        ValidationCatalog catalog,
        IEnumerable<IntentionInstance> intentions,
        IEntityManager entities,
        IPlayerManager player,
        IChatManager chat)
    {
        foreach (var notification in BuildNotifications(catalog, intentions))
        {
            if (!TryGetSession(notification.MindId, entities, player, out var session))
                continue;

            chat.DispatchServerMessage(session, notification.Message);
        }
    }

    private string ResolveVisibleTitle(ValidationCatalog catalog, IntentionInstance intention)
    {
        if (!catalog.ValidIntentions.TryGetValue(intention.IntentionTemplateId, out var template))
        {
            return ResolveLoc("intentions-ui-missing-template-title", EmptyParameters)
                ?? "Missing intention template";
        }

        return ResolveLoc(template.NameLoc, intention.ResolvedTextParameters)
            ?? template.ID;
    }

    private string BuildMessage(ImmutableArray<string> visibleTitles, int hiddenCount)
    {
        var totalCount = visibleTitles.Length + hiddenCount;
        if (hiddenCount == 0)
        {
            return ResolveMessage(
                totalCount == 1
                    ? "intentions-chat-assigned-visible-one"
                    : "intentions-chat-assigned-visible-many",
                totalCount == 1
                    ? $"You received 1 intention: {JoinTitles(visibleTitles)}"
                    : $"You received {totalCount.ToString(CultureInfo.InvariantCulture)} intentions: {JoinTitles(visibleTitles)}",
                totalCount,
                JoinTitles(visibleTitles),
                hiddenCount);
        }

        if (visibleTitles.Length == 0)
        {
            return ResolveMessage(
                hiddenCount == 1
                    ? "intentions-chat-assigned-hidden-one"
                    : "intentions-chat-assigned-hidden-many",
                hiddenCount == 1
                    ? "You received 1 hidden intention. Its details are hidden for now."
                    : $"You received {hiddenCount.ToString(CultureInfo.InvariantCulture)} hidden intentions. Their details are hidden for now.",
                hiddenCount,
                string.Empty,
                hiddenCount);
        }

        return ResolveMessage(
            hiddenCount == 1
                ? "intentions-chat-assigned-mixed-hidden-one"
                : "intentions-chat-assigned-mixed-hidden-many",
            hiddenCount == 1
                ? $"You received {totalCount.ToString(CultureInfo.InvariantCulture)} intentions: {JoinTitles(visibleTitles)}. One more intention is hidden for now."
                : $"You received {totalCount.ToString(CultureInfo.InvariantCulture)} intentions: {JoinTitles(visibleTitles)}. {hiddenCount.ToString(CultureInfo.InvariantCulture)} more intentions are hidden for now.",
            totalCount,
            JoinTitles(visibleTitles),
            hiddenCount);
    }

    private string ResolveMessage(string locId, string fallback, int count, string names, int hiddenCount)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["count"] = count.ToString(CultureInfo.InvariantCulture),
            ["names"] = names,
            ["hiddenCount"] = hiddenCount.ToString(CultureInfo.InvariantCulture),
        };

        return ResolveLoc(locId, parameters) ?? fallback;
    }

    private static string JoinTitles(IEnumerable<string> titles)
    {
        return string.Join(", ", titles);
    }

    private string? ResolveLoc(string locId, IReadOnlyDictionary<string, string> parameters)
    {
        return _locResolver(locId, parameters);
    }

    private static bool TryGetSession(
        EntityUid mindId,
        IEntityManager entities,
        IPlayerManager player,
        [NotNullWhen(true)] out ICommonSession? session)
    {
        session = null;

        if (!entities.TryGetComponent<MindComponent>(mindId, out var mind) || mind.UserId is not { } userId)
            return false;

        if (!player.TryGetSessionById(userId, out var resolved) || resolved.Status != SessionStatus.InGame)
            return false;

        session = resolved;
        return true;
    }

    private static string? DefaultLocResolver(string locId, IReadOnlyDictionary<string, string> parameters)
    {
        var args = parameters
            .Select(pair => (pair.Key, (object) pair.Value))
            .ToArray();

        try
        {
            var value = Loc.GetString(locId, args);
            return value == locId ? null : value;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// One private chat summary for a single mind's newly assigned intentions.
/// </summary>
public sealed record IntentionsAssignmentChatNotification(
    EntityUid MindId,
    ImmutableArray<string> VisibleTitles,
    int HiddenCount,
    string Message)
{
    /// <summary>
    /// Total number of newly assigned intention cards described by the notification.
    /// </summary>
    public int TotalCount => VisibleTitles.Length + HiddenCount;
}
