using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.Intentions.UI;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Validation;
using NUnit.Framework;
using Robust.Shared.GameObjects;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsAssignmentNotificationService))]
/// <summary>
/// Covers private chat summaries for newly assigned runtime intentions.
/// </summary>
public sealed class IntentionsAssignmentNotificationServiceTests
{
    [Test]
    public void VisibleIntentionsAggregateIntoOneMessagePerMind()
    {
        var service = NotificationService();
        var notifications = service.BuildNotifications(
            Catalog(Template("primary-a", "name-a"), Template("primary-b", "name-b")),
            [
                Intention(11, "primary-a", 1, assignedAt: TimeSpan.FromMinutes(2)),
                Intention(12, "primary-b", 1, assignedAt: TimeSpan.FromMinutes(3)),
            ]);

        Assert.That(notifications, Has.Length.EqualTo(1));
        Assert.That(notifications[0].MindId, Is.EqualTo(new EntityUid(1)));
        Assert.That(notifications[0].VisibleTitles, Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(notifications[0].HiddenCount, Is.Zero);
        Assert.That(notifications[0].TotalCount, Is.EqualTo(2));
        Assert.That(notifications[0].Message, Is.EqualTo("VISIBLE MANY 2: Alpha, Beta"));
    }

    [Test]
    public void MixedIntentionsDoNotRevealHiddenNames()
    {
        var service = NotificationService();
        var notifications = service.BuildNotifications(
            Catalog(Template("primary-a", "name-a"), Template("hidden-b", "name-b")),
            [
                Intention(11, "primary-a", 1, assignedAt: TimeSpan.FromMinutes(2)),
                Intention(12, "hidden-b", 1, hidden: true, assignedAt: TimeSpan.FromMinutes(3)),
            ]);

        Assert.That(notifications, Has.Length.EqualTo(1));
        Assert.That(notifications[0].VisibleTitles, Is.EqualTo(new[] { "Alpha" }));
        Assert.That(notifications[0].HiddenCount, Is.EqualTo(1));
        Assert.That(notifications[0].Message, Is.EqualTo("MIXED 2: Alpha + 1 hidden"));
        Assert.That(notifications[0].Message, Does.Not.Contain("Beta"));
        Assert.That(notifications[0].Message, Does.Not.Contain("hidden-b"));
    }

    [Test]
    public void HiddenOnlyIntentionsOnlyReportCount()
    {
        var service = NotificationService();
        var notifications = service.BuildNotifications(
            Catalog(Template("hidden-a", "name-a"), Template("hidden-b", "name-b")),
            [
                Intention(11, "hidden-a", 1, hidden: true, assignedAt: TimeSpan.FromMinutes(2)),
                Intention(12, "hidden-b", 1, hidden: true, assignedAt: TimeSpan.FromMinutes(3)),
            ]);

        Assert.That(notifications, Has.Length.EqualTo(1));
        Assert.That(notifications[0].VisibleTitles, Is.Empty);
        Assert.That(notifications[0].HiddenCount, Is.EqualTo(2));
        Assert.That(notifications[0].Message, Is.EqualTo("HIDDEN 2"));
        Assert.That(notifications[0].Message, Does.Not.Contain("Alpha"));
        Assert.That(notifications[0].Message, Does.Not.Contain("Beta"));
    }

    [Test]
    public void DifferentMindsReceiveSeparateNotifications()
    {
        var service = NotificationService();
        var notifications = service.BuildNotifications(
            Catalog(Template("primary-a", "name-a"), Template("primary-b", "name-b")),
            [
                Intention(11, "primary-a", 1, assignedAt: TimeSpan.FromMinutes(2)),
                Intention(12, "primary-b", 2, assignedAt: TimeSpan.FromMinutes(3)),
            ]);

        Assert.That(notifications, Has.Length.EqualTo(2));
        Assert.That(notifications.Select(notification => notification.MindId), Is.EqualTo(new[] { new EntityUid(1), new EntityUid(2) }));
        Assert.That(notifications.Select(notification => notification.Message), Is.EqualTo(new[]
        {
            "VISIBLE ONE 1: Alpha",
            "VISIBLE ONE 1: Beta",
        }));
    }

    private static IntentionsAssignmentNotificationService NotificationService()
    {
        return new IntentionsAssignmentNotificationService(ResolveLoc);
    }

    private static ValidationCatalog Catalog(params IntentionTemplatePrototype[] templates)
    {
        var catalog = new ValidationCatalog();
        foreach (var template in templates)
        {
            catalog.ValidIntentions[template.ID] = template;
        }

        return catalog;
    }

    private static IntentionTemplatePrototype Template(string id, string nameLoc)
    {
        var template = new IntentionTemplatePrototype
        {
            Kind = IntentionsPrototypeConstants.Primary,
            NameLoc = nameLoc,
            DescriptionLoc = "unused-description",
            DefaultVisibility = IntentionsPrototypeConstants.Visible,
        };

        SetId(template, id);
        return template;
    }

    private static IntentionInstance Intention(
        long uid,
        string templateId,
        long mindId,
        bool hidden = false,
        TimeSpan? assignedAt = null)
    {
        var assigned = assignedAt ?? TimeSpan.FromMinutes(1);
        return new IntentionInstance(
            new IntentionInstanceUid(uid),
            templateId,
            new ScenarioInstanceUid(uid),
            "owner",
            new EntityUid((int) mindId),
            new EntityUid((int) (1000 + uid)),
            IntentionsPrototypeConstants.Primary,
            IntentionRuntimeStatus.Active,
            assigned,
            assigned,
            hidden,
            hidden ? IntentionRevealMode.Timer : IntentionRevealMode.None,
            hidden ? assigned + TimeSpan.FromMinutes(5) : null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            copyableTextResolved: null);
    }

    private static string? ResolveLoc(string locId, IReadOnlyDictionary<string, string> parameters)
    {
        return locId switch
        {
            "name-a" => "Alpha",
            "name-b" => "Beta",
            "intentions-chat-assigned-visible-one" => $"VISIBLE ONE {parameters["count"]}: {parameters["names"]}",
            "intentions-chat-assigned-visible-many" => $"VISIBLE MANY {parameters["count"]}: {parameters["names"]}",
            "intentions-chat-assigned-hidden-one" => $"HIDDEN {parameters["count"]}",
            "intentions-chat-assigned-hidden-many" => $"HIDDEN {parameters["count"]}",
            "intentions-chat-assigned-mixed-hidden-one" => $"MIXED {parameters["count"]}: {parameters["names"]} + {parameters["hiddenCount"]} hidden",
            "intentions-chat-assigned-mixed-hidden-many" => $"MIXED {parameters["count"]}: {parameters["names"]} + {parameters["hiddenCount"]} hidden",
            "intentions-ui-missing-template-title" => "Missing template",
            _ => null,
        };
    }

    private static void SetId<T>(T prototype, string id)
    {
        typeof(T).GetProperty("ID", BindingFlags.Instance | BindingFlags.Public)!.SetValue(prototype, id);
    }
}
