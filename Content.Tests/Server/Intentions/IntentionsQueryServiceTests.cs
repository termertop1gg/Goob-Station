using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.Intentions.Runtime;
using Content.Server.Intentions.UI;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.UI;
using Content.Shared.Intentions.Validation;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsQueryService))]
/// <summary>
/// Covers hidden-safe and visible read-model assembly for the Intentions UI query service.
/// </summary>
public sealed class IntentionsQueryServiceTests
{
    [Test]
    public void PlayerHiddenCardDoesNotLeakTemplateTextOrIds()
    {
        var fixture = Fixture(hidden: true);
        var state = Query().GetIntentionsForMind(
            fixture.Catalog,
            fixture.Registry,
            fixture.MindId,
            TimeSpan.FromMinutes(6),
            IntentionsEuiMode.Player,
            "Alex");
        var card = state.OwnIntentions.Single();

        Assert.That(card.Title, Is.EqualTo("Classified"));
        Assert.That(card.Description, Is.EqualTo("Hidden body"));
        Assert.That(card.CopyableText, Is.Null);
        Assert.That(card.IntentionTemplateId, Is.Empty);
        Assert.That(card.ScenarioTemplateId, Is.Empty);
        Assert.That(card.CategoryId, Is.Empty);
        Assert.That(card.OocInfo, Is.Null.Or.Empty);
        Assert.That(card.ResolvedTextParameters, Is.Empty);
        Assert.That(card.Description, Does.Not.Contain("Real"));
        Assert.That(card.Author, Is.EqualTo("Hidden"));
        Assert.That(card.Icon, Is.TypeOf<SpriteSpecifier.Texture>());
        Assert.That(((SpriteSpecifier.Texture) card.Icon!).TexturePath, Is.EqualTo(new ResPath("/Textures/Interface/VerbIcons/lock.svg.192dpi.png")));
    }

    [Test]
    public void PlayerVisibleCardUsesRuntimeResolvedTextAndCopyableText()
    {
        var fixture = Fixture(hidden: false);
        var state = Query().GetIntentionsForMind(
            fixture.Catalog,
            fixture.Registry,
            fixture.MindId,
            TimeSpan.FromMinutes(6),
            IntentionsEuiMode.Player,
            "Alex");
        var card = state.OwnIntentions.Single();

        Assert.That(card.Title, Is.EqualTo("Open Alex"));
        Assert.That(card.Summary, Is.EqualTo("Summary Alex"));
        Assert.That(card.Description, Is.EqualTo("Real description for Alex"));
        Assert.That(card.OocInfo, Is.EqualTo("OOC Alex"));
        Assert.That(card.CopyableText, Is.EqualTo("Runtime copy for Alex"));
        Assert.That(card.IntentionTemplateId, Is.EqualTo("primary"));
        Assert.That(card.ResolvedTextParameters, Does.ContainKey("target"));
        Assert.That(card.ResolvedTextParameters["target"], Is.EqualTo("Alex"));
        Assert.That(card.Author, Is.EqualTo("Test Author"));
        Assert.That(card.Icon, Is.TypeOf<SpriteSpecifier.Texture>());
        Assert.That(((SpriteSpecifier.Texture) card.Icon!).TexturePath, Is.EqualTo(new ResPath("/Textures/Interface/VerbIcons/sentient.svg.192dpi.png")));
    }

    [Test]
    public void AdminReadOnlyModelIncludesRuntimeMetadataForHiddenCard()
    {
        var fixture = Fixture(hidden: true);
        var state = Query().GetIntentionsForMind(
            fixture.Catalog,
            fixture.Registry,
            fixture.MindId,
            TimeSpan.FromMinutes(6),
            IntentionsEuiMode.Admin,
            "Alex");
        var card = state.OwnIntentions.Single();

        Assert.That(card.IsHidden, Is.True);
        Assert.That(card.Title, Is.EqualTo("Open Alex"));
        Assert.That(card.CopyableText, Is.EqualTo("Runtime copy for Alex"));
        Assert.That(card.IntentionTemplateId, Is.EqualTo("primary"));
        Assert.That(card.ScenarioTemplateId, Is.EqualTo("scenario"));
        Assert.That(state.AdminScenarios, Has.Length.EqualTo(1));
        Assert.That(state.AdminScenarios[0].ScenarioTemplateId, Is.EqualTo("scenario"));
    }

    [Test]
    public void RevealedHiddenIntentionAppearsVisibleInPlayerModel()
    {
        var fixture = Fixture(hidden: true);
        new IntentionsRevealService().EvaluateTimerReveals(fixture.Registry, TimeSpan.FromMinutes(20));

        var state = Query().GetIntentionsForMind(
            fixture.Catalog,
            fixture.Registry,
            fixture.MindId,
            TimeSpan.FromMinutes(20),
            IntentionsEuiMode.Player,
            "Alex");
        var card = state.OwnIntentions.Single();

        Assert.That(card.IsHidden, Is.False);
        Assert.That(card.Title, Is.EqualTo("Open Alex"));
        Assert.That(card.CopyableText, Is.EqualTo("Runtime copy for Alex"));
    }

    [Test]
    public void HiddenCardUsesDefaultHiddenLabelWhenTemplateDoesNotOverrideIt()
    {
        var fixture = Fixture(hidden: true, hiddenLabelLoc: null);
        var state = Query().GetIntentionsForMind(
            fixture.Catalog,
            fixture.Registry,
            fixture.MindId,
            TimeSpan.FromMinutes(6),
            IntentionsEuiMode.Player,
            "Alex");
        var card = state.OwnIntentions.Single();

        Assert.That(card.Title, Is.EqualTo("Default hidden label"));
    }

    [Test]
    public void VisibleCardUsesDefaultOocInfoWhenTemplateDoesNotOverrideIt()
    {
        var fixture = Fixture(hidden: false, oocInfoLoc: null);
        var state = Query().GetIntentionsForMind(
            fixture.Catalog,
            fixture.Registry,
            fixture.MindId,
            TimeSpan.FromMinutes(6),
            IntentionsEuiMode.Player,
            "Alex");
        var card = state.OwnIntentions.Single();

        Assert.That(card.OocInfo, Is.EqualTo("Default OOC Alex"));
    }

    [Test]
    public void VisibleCardFallsBackToDefaultOocInfoWhenConfiguredLocDoesNotResolve()
    {
        var fixture = Fixture(hidden: false, oocInfoLoc: "missing-ooc");
        var state = Query().GetIntentionsForMind(
            fixture.Catalog,
            fixture.Registry,
            fixture.MindId,
            TimeSpan.FromMinutes(6),
            IntentionsEuiMode.Player,
            "Alex");
        var card = state.OwnIntentions.Single();

        Assert.That(card.OocInfo, Is.EqualTo("Default OOC Alex"));
    }

    [Test]
    public void VisibleCardFallsBackToCategoryIconWhenTemplateIconIsMissing()
    {
        var fixture = Fixture(hidden: false, includeDefaultIntentionIcon: false);
        var state = Query().GetIntentionsForMind(
            fixture.Catalog,
            fixture.Registry,
            fixture.MindId,
            TimeSpan.FromMinutes(6),
            IntentionsEuiMode.Player,
            "Alex");
        var card = state.OwnIntentions.Single();

        Assert.That(card.Icon, Is.TypeOf<SpriteSpecifier.Rsi>());
        var rsi = (SpriteSpecifier.Rsi) card.Icon!;
        Assert.That(rsi.RsiPath, Is.EqualTo(new ResPath("/Textures/Interface/Misc/job_icons.rsi")));
        Assert.That(rsi.RsiState, Is.EqualTo("Passenger"));
    }

    [Test]
    public void PlayerCardsKeepHiddenIntentionsAtTheEndOfEachStatusBlock()
    {
        var fixture = SortingFixture();
        var state = Query().GetIntentionsForMind(
            fixture.Catalog,
            fixture.Registry,
            fixture.MindId,
            TimeSpan.FromMinutes(30),
            IntentionsEuiMode.Player,
            "Alex");

        Assert.That(state.OwnIntentions.Select(card => card.AssignedAtRoundTime), Is.EqualTo(new[]
        {
            TimeSpan.FromMinutes(7),
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(6),
            TimeSpan.FromMinutes(9),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(11),
            TimeSpan.FromMinutes(12),
            TimeSpan.FromMinutes(13),
        }));
        Assert.That(state.LinkedIntentions.Select(card => card.AssignedAtRoundTime), Is.EqualTo(new[]
        {
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(14),
            TimeSpan.FromMinutes(16),
            TimeSpan.FromMinutes(17),
        }));

        Assert.That(state.OwnIntentions.Take(3).All(card => !card.IsHidden), Is.True);
        Assert.That(state.OwnIntentions.Skip(3).Take(2).All(card => card.IsHidden), Is.True);
        Assert.That(state.OwnIntentions.Skip(5).Take(1).All(card => !card.IsHidden), Is.True);
        Assert.That(state.OwnIntentions.Skip(6).All(card => card.IsHidden), Is.True);
    }

    private static IntentionsQueryService Query()
    {
        return new IntentionsQueryService(ResolveLoc);
    }

    private static QueryFixture Fixture(
        bool hidden,
        string? hiddenLabelLoc = "primary-hidden",
        string? oocInfoLoc = "primary-ooc",
        bool includeDefaultIntentionIcon = true,
        SpriteSpecifier? intentionIcon = null,
        SpriteSpecifier? categoryIcon = null)
    {
        var registry = new IntentionsRuntimeRegistry();
        var scenarioUid = registry.NextScenarioUid();
        var intentionUid = registry.NextIntentionUid();
        var mindId = new EntityUid(1);
        var entityUid = new EntityUid(1001);
        var now = TimeSpan.FromMinutes(5);
        var revealTime = hidden ? TimeSpan.FromMinutes(20) : (TimeSpan?) null;

        var assignment = new ScenarioSlotAssignment(
            scenarioUid,
            "owner",
            IntentionsPrototypeConstants.Primary,
            mindId,
            entityUid,
            ScenarioSlotAssignmentStatus.Assigned,
            intentionUid,
            required: true,
            wasBound: false,
            boundToSlotId: null,
            now);
        var scenario = new ScenarioInstance(
            scenarioUid,
            "scenario",
            "social",
            ScenarioRuntimeStatus.Active,
            "owner",
            mindId,
            entityUid,
            waveId: 7,
            now,
            [assignment]);
        var intention = new IntentionInstance(
            intentionUid,
            "primary",
            scenarioUid,
            "owner",
            mindId,
            entityUid,
            IntentionsPrototypeConstants.Primary,
            IntentionRuntimeStatus.Active,
            now,
            now,
            hidden,
            hidden ? IntentionRevealMode.Timer : IntentionRevealMode.None,
            revealTime,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target"] = "Alex",
            },
            "Runtime copy for Alex");

        registry.AddScenario(scenario);
        registry.AddIntention(intention);
        registry.AttachIntentionToMind(mindId, intentionUid);
        registry.AddScenarioBackReference(intentionUid, scenarioUid);
        registry.AddSlotAssignment(assignment);
        if (revealTime is { } actualRevealTime)
            registry.AddHiddenReveal(actualRevealTime, intentionUid);

        var catalog = Catalog(hiddenLabelLoc, oocInfoLoc, includeDefaultIntentionIcon, intentionIcon, categoryIcon);
        return new QueryFixture(registry, catalog, mindId);
    }

    private static QueryFixture SortingFixture()
    {
        var registry = new IntentionsRuntimeRegistry();
        var catalog = new ValidationCatalog();
        var mindId = new EntityUid(1);
        var entityUid = new EntityUid(1001);
        var now = TimeSpan.FromMinutes(5);

        AddCategory(catalog, "cat-zeta", "category-alpha", priority: 10);
        AddCategory(catalog, "cat-alpha", "category-bravo", priority: 10);
        AddCategory(catalog, "cat-social", "category-social", priority: 1);
        AddCategory(catalog, "cat-cancel-high", "category-cancel-zulu", priority: 50);
        AddCategory(catalog, "cat-cancel-low", "category-cancel-yankee", priority: 2);

        AddCard(catalog, registry, mindId, entityUid, "primary-active-social", IntentionsPrototypeConstants.Primary, "cat-social", ScenarioRuntimeStatus.Active, now + TimeSpan.FromMinutes(1));
        AddCard(catalog, registry, mindId, entityUid, "primary-active-zeta", IntentionsPrototypeConstants.Primary, "cat-zeta", ScenarioRuntimeStatus.Active, now + TimeSpan.FromMinutes(2));
        AddCard(catalog, registry, mindId, entityUid, "primary-active-alpha", IntentionsPrototypeConstants.Primary, "cat-alpha", ScenarioRuntimeStatus.Active, now + TimeSpan.FromMinutes(3));
        AddCard(catalog, registry, mindId, entityUid, "primary-hidden-social", IntentionsPrototypeConstants.Primary, "cat-social", ScenarioRuntimeStatus.Active, now + TimeSpan.FromMinutes(4), hidden: true);
        AddCard(catalog, registry, mindId, entityUid, "primary-hidden-zeta", IntentionsPrototypeConstants.Primary, "cat-zeta", ScenarioRuntimeStatus.Active, now + TimeSpan.FromMinutes(5), hidden: true);
        AddCard(catalog, registry, mindId, entityUid, "primary-cancel-visible", IntentionsPrototypeConstants.Primary, "cat-cancel-high", ScenarioRuntimeStatus.Cancelled, now + TimeSpan.FromMinutes(6));
        AddCard(catalog, registry, mindId, entityUid, "primary-cancel-hidden-low", IntentionsPrototypeConstants.Primary, "cat-cancel-low", ScenarioRuntimeStatus.Cancelled, now + TimeSpan.FromMinutes(7), hidden: true);
        AddCard(catalog, registry, mindId, entityUid, "primary-cancel-hidden-high", IntentionsPrototypeConstants.Primary, "cat-cancel-high", ScenarioRuntimeStatus.Cancelled, now + TimeSpan.FromMinutes(8), hidden: true);

        AddCard(catalog, registry, mindId, entityUid, "secondary-hidden-first", "secondary", "cat-zeta", ScenarioRuntimeStatus.Active, now + TimeSpan.FromMinutes(9), hidden: true);
        AddCard(catalog, registry, mindId, entityUid, "secondary-visible", "secondary", "cat-alpha", ScenarioRuntimeStatus.Active, now + TimeSpan.FromMinutes(10));
        AddCard(catalog, registry, mindId, entityUid, "secondary-cancel-visible", "secondary", "cat-cancel-high", ScenarioRuntimeStatus.Cancelled, now + TimeSpan.FromMinutes(11));
        AddCard(catalog, registry, mindId, entityUid, "secondary-cancel-hidden", "secondary", "cat-cancel-low", ScenarioRuntimeStatus.Cancelled, now + TimeSpan.FromMinutes(12), hidden: true);

        return new QueryFixture(registry, catalog, mindId);
    }

    private static ValidationCatalog Catalog(
        string? hiddenLabelLoc,
        string? oocInfoLoc,
        bool includeDefaultIntentionIcon,
        SpriteSpecifier? intentionIcon,
        SpriteSpecifier? categoryIcon)
    {
        var intention = new IntentionTemplatePrototype
        {
            Kind = IntentionsPrototypeConstants.Primary,
            NameLoc = "primary-name",
            SummaryLoc = "primary-summary",
            DescriptionLoc = "primary-description",
            OocInfoLoc = oocInfoLoc,
            HiddenLabelLoc = hiddenLabelLoc,
            DefaultVisibility = IntentionsPrototypeConstants.Visible,
            Author = "Test Author",
            Icon = intentionIcon ?? (includeDefaultIntentionIcon
                ? new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/sentient.svg.192dpi.png"))
                : null),
            Color = "#AABBCCDD",
        };
        SetId(intention, "primary");

        var category = new ScenarioCategoryPrototype
        {
            NameLoc = "category-name",
            Icon = categoryIcon ?? new SpriteSpecifier.Rsi(new ResPath("/Textures/Interface/Misc/job_icons.rsi"), "Passenger"),
            Color = "#11223344",
        };
        SetId(category, "social");

        var catalog = new ValidationCatalog();
        catalog.ValidIntentions[intention.ID] = intention;
        catalog.ValidCategories[category.ID] = category;
        return catalog;
    }

    private static void AddCard(
        ValidationCatalog catalog,
        IntentionsRuntimeRegistry registry,
        EntityUid mindId,
        EntityUid entityUid,
        string templateId,
        string kind,
        string categoryId,
        ScenarioRuntimeStatus scenarioStatus,
        TimeSpan assignedAt,
        bool hidden = false)
    {
        AddIntentionTemplate(catalog, templateId, kind);

        var scenarioUid = registry.NextScenarioUid();
        var intentionUid = registry.NextIntentionUid();
        var assignment = new ScenarioSlotAssignment(
            scenarioUid,
            "owner",
            kind,
            mindId,
            entityUid,
            ScenarioSlotAssignmentStatus.Assigned,
            intentionUid,
            required: true,
            wasBound: false,
            boundToSlotId: null,
            assignedAt);
        var scenario = new ScenarioInstance(
            scenarioUid,
            $"scenario-{templateId}",
            categoryId,
            scenarioStatus,
            "owner",
            mindId,
            entityUid,
            waveId: 7,
            assignedAt,
            [assignment]);
        var intention = new IntentionInstance(
            intentionUid,
            templateId,
            scenarioUid,
            "owner",
            mindId,
            entityUid,
            kind,
            IntentionRuntimeStatus.Active,
            assignedAt,
            assignedAt,
            hidden,
            hidden ? IntentionRevealMode.Timer : IntentionRevealMode.None,
            hidden ? assignedAt + TimeSpan.FromMinutes(20) : null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target"] = templateId,
            },
            $"Runtime copy for {templateId}");

        registry.AddScenario(scenario);
        registry.AddIntention(intention);
        registry.AttachIntentionToMind(mindId, intentionUid);
        registry.AddScenarioBackReference(intentionUid, scenarioUid);
        registry.AddSlotAssignment(assignment);
        if (hidden && intention.RevealedAtRoundTime is { } revealTime)
            registry.AddHiddenReveal(revealTime, intentionUid);
    }

    private static void AddIntentionTemplate(ValidationCatalog catalog, string templateId, string kind)
    {
        var intention = new IntentionTemplatePrototype
        {
            Kind = kind,
            NameLoc = $"name-{templateId}",
            SummaryLoc = $"summary-{templateId}",
            DescriptionLoc = $"description-{templateId}",
            OocInfoLoc = "primary-ooc",
            HiddenLabelLoc = "primary-hidden",
            DefaultVisibility = IntentionsPrototypeConstants.Visible,
            Author = "Sorter",
            Color = "#AABBCCDD",
        };
        SetId(intention, templateId);
        catalog.ValidIntentions[intention.ID] = intention;
    }

    private static void AddCategory(ValidationCatalog catalog, string id, string nameLoc, int priority)
    {
        var category = new ScenarioCategoryPrototype
        {
            NameLoc = nameLoc,
            Priority = priority,
            Icon = new SpriteSpecifier.Rsi(new ResPath("/Textures/Interface/Misc/job_icons.rsi"), "Passenger"),
            Color = "#11223344",
        };
        SetId(category, id);
        catalog.ValidCategories[category.ID] = category;
    }

    private static string? ResolveLoc(string locId, IReadOnlyDictionary<string, string> parameters)
    {
        var target = parameters.TryGetValue("target", out var value) ? value : string.Empty;

        if (locId.StartsWith("name-", StringComparison.Ordinal))
            return $"Name {target}";

        if (locId.StartsWith("summary-", StringComparison.Ordinal))
            return $"Summary {target}";

        if (locId.StartsWith("description-", StringComparison.Ordinal))
            return $"Description {target}";

        return locId switch
        {
            "primary-name" => $"Open {target}",
            "primary-summary" => $"Summary {target}",
            "primary-description" => $"Real description for {target}",
            "primary-ooc" => $"OOC {target}",
            "primary-hidden" => "Classified",
            "category-alpha" => "Alpha",
            "category-bravo" => "Bravo",
            "category-social" => "Social",
            "category-cancel-zulu" => "Zulu",
            "category-cancel-yankee" => "Yankee",
            "default-hidden-label" => "Default hidden label",
            "default-ooc-info" => $"Default OOC {target}",
            "intentions-ui-hidden-description" => "Hidden body",
            "intentions-ui-hidden-reveal-timer" => $"Timer {parameters["time"]}",
            "intentions-ui-revealed" => "Revealed",
            "intentions-ui-hidden-reveal-none" => "No reveal",
            "intentions-ui-unknown-target" => "Unknown",
            "intentions-ui-missing-slot-status" => "Missing slot",
            "intentions-ui-missing-template-title" => "Missing template",
            "intentions-ui-missing-template-description" => "Missing template body",
            _ => null,
        };
    }

    private static void SetId<T>(T prototype, string id)
    {
        typeof(T).GetProperty("ID", BindingFlags.Instance | BindingFlags.Public)!.SetValue(prototype, id);
    }

    private sealed record QueryFixture(
        IntentionsRuntimeRegistry Registry,
        ValidationCatalog Catalog,
        EntityUid MindId);
}
