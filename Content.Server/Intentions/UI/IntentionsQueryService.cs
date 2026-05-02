using System.Linq;
using Content.Server.Intentions.Runtime;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.UI;
using Content.Shared.Intentions.Validation;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;

namespace Content.Server.Intentions.UI;

/// <summary>
/// Builds the hidden-safe read-model consumed by the Intentions player and admin UIs.
/// </summary>
public sealed class IntentionsQueryService
{
    private const string DefaultHiddenLabelLoc = "default-hidden-label";
    private const string DefaultOocInfoLoc = "default-ooc-info";
    private static readonly SpriteSpecifier HiddenIcon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/lock.svg.192dpi.png"));

    private static readonly Dictionary<string, string> EmptyParameters = new(StringComparer.Ordinal);

    private readonly Func<string, IReadOnlyDictionary<string, string>, string?> _locResolver;

    /// <summary>
    /// Initializes a query service that can resolve localized strings with runtime parameters.
    /// </summary>
    public IntentionsQueryService(Func<string, IReadOnlyDictionary<string, string>, string?>? locResolver = null)
    {
        _locResolver = locResolver ?? DefaultLocResolver;
    }

    /// <summary>
    /// Creates the current Intentions UI state for one target mind.
    /// </summary>
    public IntentionsEuiState GetIntentionsForMind(
        ValidationCatalog catalog,
        IntentionsRuntimeRegistry registry,
        EntityUid targetMindId,
        TimeSpan now,
        IntentionsEuiMode mode,
        string targetName = "")
    {
        var cardEntries = new List<CardSortEntry>();
        if (registry.IntentionIdsByMind.TryGetValue(targetMindId, out var intentionUids))
        {
            foreach (var intentionUid in intentionUids)
            {
                if (!registry.IntentionByUid.TryGetValue(intentionUid, out var intention))
                    continue;

                if (!registry.ScenarioUidByIntentionUid.TryGetValue(intentionUid, out var scenarioUid) ||
                    !registry.ScenarioByUid.TryGetValue(scenarioUid, out var scenario))
                    continue;

                registry.SlotAssignmentByScenarioAndSlot.TryGetValue((scenarioUid, intention.SlotId), out var assignment);
                catalog.ValidCategories.TryGetValue(scenario.CategoryId, out var category);
                var card = BuildCard(catalog, intention, scenario, assignment, now, mode);
                var hiddenRank = mode == IntentionsEuiMode.Player && card.IsHidden ? 1 : 0;
                cardEntries.Add(new CardSortEntry(
                    card,
                    GetScenarioStatusRank(scenario.Status),
                    hiddenRank,
                    hiddenRank == 0 ? category?.Priority ?? 0 : 0,
                    hiddenRank == 0 ? GetCategorySortName(category, scenario.CategoryId) : string.Empty));
            }
        }

        var ownIntentions = cardEntries
            .Where(entry => entry.Card.Kind == IntentionsPrototypeConstants.Primary)
            .OrderBy(entry => entry.ScenarioStatusRank)
            .ThenBy(entry => entry.HiddenRank)
            .ThenByDescending(entry => entry.CategoryPriority)
            .ThenBy(entry => entry.CategorySortName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Card.AssignedAtRoundTime)
            .ThenBy(entry => entry.Card.IntentionUid)
            .Select(entry => entry.Card)
            .ToArray();
        var linkedIntentions = cardEntries
            .Where(entry => entry.Card.Kind != IntentionsPrototypeConstants.Primary)
            .OrderBy(entry => entry.ScenarioStatusRank)
            .ThenBy(entry => entry.HiddenRank)
            .ThenByDescending(entry => entry.CategoryPriority)
            .ThenBy(entry => entry.CategorySortName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Card.AssignedAtRoundTime)
            .ThenBy(entry => entry.Card.IntentionUid)
            .Select(entry => entry.Card)
            .ToArray();
        var adminScenarios = mode == IntentionsEuiMode.Admin
            ? BuildAdminScenarios(registry, cardEntries.Select(entry => entry.Card).ToArray())
            : [];

        return new IntentionsEuiState(
            mode,
            string.IsNullOrWhiteSpace(targetName) ? ResolveUiLoc("intentions-ui-unknown-target", "Unknown") : targetName,
            now,
            ownIntentions,
            linkedIntentions,
            adminScenarios);
    }

    /// <summary>
    /// Builds one UI card from runtime data while respecting player hidden-information rules.
    /// </summary>
    private IntentionsCardView BuildCard(
        ValidationCatalog catalog,
        IntentionInstance intention,
        ScenarioInstance scenario,
        ScenarioSlotAssignment? assignment,
        TimeSpan now,
        IntentionsEuiMode mode)
    {
        catalog.ValidIntentions.TryGetValue(intention.IntentionTemplateId, out var template);
        catalog.ValidCategories.TryGetValue(scenario.CategoryId, out var category);
        var redacted = mode == IntentionsEuiMode.Player && intention.IsHidden;
        var revealText = BuildRevealText(intention, now);
        var icon = redacted
            ? HiddenIcon
            : template?.Icon ?? category?.Icon;
        var color = redacted
            ? "#7A8199FF"
            : template?.Color ?? category?.Color ?? "#FFFFFFFF";

        var text = redacted
            ? BuildHiddenText(template, revealText)
            : BuildVisibleText(template, intention);
        var resolvedTextParameters = redacted
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(intention.ResolvedTextParameters, StringComparer.Ordinal);
        var author = redacted
            ? ResolveUiLoc("intentions-ui-author-hidden", "Hidden")
            : string.IsNullOrWhiteSpace(template?.Author)
                ? ResolveUiLoc("intentions-ui-author-unknown", "Unknown")
                : template!.Author!;

        return new IntentionsCardView(
            intention.Uid.Value,
            scenario.Uid.Value,
            redacted ? string.Empty : intention.IntentionTemplateId,
            redacted ? string.Empty : scenario.ScenarioTemplateId,
            redacted ? string.Empty : scenario.CategoryId,
            intention.SlotId,
            intention.Kind,
            text.Title,
            author,
            text.Summary,
            text.Description,
            text.OocInfo,
            redacted ? null : intention.CopyableTextResolved,
            resolvedTextParameters,
            intention.IsHidden,
            revealText,
            intention.RevealedAtRoundTime,
            intention.Status.ToString(),
            scenario.Status.ToString(),
            assignment?.Status.ToString() ?? ResolveUiLoc("intentions-ui-missing-slot-status", "Missing slot"),
            scenario.WaveId,
            intention.AssignedAtRoundTime,
            icon,
            color);
    }

    /// <summary>
    /// Returns the left-list scenario-status sort rank used by the Intentions UI.
    /// </summary>
    private static int GetScenarioStatusRank(ScenarioRuntimeStatus status)
    {
        return status switch
        {
            ScenarioRuntimeStatus.Active => 0,
            ScenarioRuntimeStatus.Cancelled => 1,
            _ => 2,
        };
    }

    /// <summary>
    /// Returns the localized category display name used as the alphabetical tie-breaker for left-list sorting.
    /// </summary>
    private string GetCategorySortName(ScenarioCategoryPrototype? category, string fallbackCategoryId)
    {
        if (category is null)
            return fallbackCategoryId;

        return ResolveLoc(category.NameLoc, EmptyParameters)
            ?? category.ID;
    }

    /// <summary>
    /// Builds the redacted text shown for hidden intentions in player mode.
    /// </summary>
    private CardText BuildHiddenText(IntentionTemplatePrototype? template, string revealText)
    {
        var titleLoc = template?.HiddenLabelLoc ?? DefaultHiddenLabelLoc;
        var title = ResolveLoc(titleLoc, EmptyParameters)
            ?? ResolveUiLoc("intentions-ui-hidden-title", "Hidden intention");

        return new CardText(
            title,
            revealText,
            ResolveUiLoc("intentions-ui-hidden-description", "This intention is hidden for now."),
            null);
    }

    /// <summary>
    /// Builds the fully visible text shown when the intention is no longer hidden from the viewer.
    /// </summary>
    private CardText BuildVisibleText(IntentionTemplatePrototype? template, IntentionInstance intention)
    {
        if (template is null)
        {
            return new CardText(
                ResolveUiLoc("intentions-ui-missing-template-title", "Missing intention template"),
                string.Empty,
                ResolveUiLoc("intentions-ui-missing-template-description", "This runtime intention references missing content."),
                null);
        }

        var title = ResolveLoc(template.NameLoc, intention.ResolvedTextParameters) ?? template.ID;
        var summary = template.SummaryLoc is { } summaryLoc
            ? ResolveLoc(summaryLoc, intention.ResolvedTextParameters) ?? string.Empty
            : string.Empty;
        var description = ResolveLoc(template.DescriptionLoc, intention.ResolvedTextParameters)
            ?? ResolveUiLoc("intentions-ui-missing-template-description", "This runtime intention references missing content.");
        var oocLoc = template.OocInfoLoc ?? DefaultOocInfoLoc;
        var oocInfo = ResolveLoc(oocLoc, intention.ResolvedTextParameters)
            ?? ResolveUiLoc(
                DefaultOocInfoLoc,
                "Don't break the server rules. Intentions are optional roleplay prompts. Other players are not required to follow them.",
                intention.ResolvedTextParameters);

        return new CardText(title, summary, description, oocInfo);
    }

    /// <summary>
    /// Builds the reveal-status text shown on the card.
    /// </summary>
    private string BuildRevealText(IntentionInstance intention, TimeSpan now)
    {
        if (!intention.IsHidden)
            return ResolveUiLoc("intentions-ui-revealed", "Revealed");

        if (intention.RevealMode != IntentionRevealMode.Timer || intention.RevealedAtRoundTime is not { } revealTime)
            return ResolveUiLoc("intentions-ui-hidden-reveal-none", "Hidden with no automatic reveal.");

        var remaining = revealTime > now ? revealTime - now : TimeSpan.Zero;
        return ResolveUiLoc(
            "intentions-ui-hidden-reveal-timer",
            $"Reveals in {remaining.ToString(@"hh\:mm\:ss")}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["time"] = remaining.ToString(@"hh\:mm\:ss"),
            });
    }

    /// <summary>
    /// Builds admin-only scenario metadata for the scenarios referenced by the current cards.
    /// </summary>
    private IntentionsScenarioAdminView[] BuildAdminScenarios(IntentionsRuntimeRegistry registry, IReadOnlyList<IntentionsCardView> cards)
    {
        var scenarioIds = cards
            .Select(card => new ScenarioInstanceUid(card.ScenarioUid))
            .Distinct()
            .ToHashSet();

        return registry.ScenarioByUid.Values
            .Where(scenario => scenarioIds.Contains(scenario.Uid))
            .OrderBy(scenario => scenario.CreatedAtRoundTime)
            .ThenBy(scenario => scenario.Uid.Value)
            .Select(scenario => new IntentionsScenarioAdminView(
                scenario.Uid.Value,
                scenario.ScenarioTemplateId,
                scenario.CategoryId,
                scenario.Status.ToString(),
                scenario.OwnerSlotId,
                scenario.OwnerMindId.Id,
                scenario.OwnerEntityUid.Id,
                scenario.WaveId,
                scenario.CreatedAtRoundTime))
            .ToArray();
    }

    /// <summary>
    /// Resolves a localized string with runtime parameters and returns null when the key is unavailable.
    /// </summary>
    private string? ResolveLoc(string locId, IReadOnlyDictionary<string, string> parameters)
    {
        return _locResolver(locId, parameters);
    }

    /// <summary>
    /// Resolves one UI-owned localization key with a plain-text fallback.
    /// </summary>
    private string ResolveUiLoc(string locId, string fallback, IReadOnlyDictionary<string, string>? parameters = null)
    {
        return _locResolver(locId, parameters ?? EmptyParameters) ?? fallback;
    }

    /// <summary>
    /// Default localization resolver used outside of tests.
    /// </summary>
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

    /// <summary>
    /// Groups the text fields rendered by one UI card.
    /// </summary>
    private readonly record struct CardText(string Title, string Summary, string Description, string? OocInfo);

    /// <summary>
    /// Holds one UI card plus the server-side sort keys needed for stable left-list ordering.
    /// </summary>
    private readonly record struct CardSortEntry(
        IntentionsCardView Card,
        int ScenarioStatusRank,
        int HiddenRank,
        int CategoryPriority,
        string CategorySortName);
}
