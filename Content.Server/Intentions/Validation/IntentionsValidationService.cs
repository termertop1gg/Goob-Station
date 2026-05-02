using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Content.Server.GameTicking.Presets;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Intentions.Predicates;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Validation;
using Content.Shared.Roles;
using Content.Shared.Traits;
using Robust.Shared.Prototypes;

namespace Content.Server.Intentions.Validation;

/// <summary>
/// Validates Intentions content prototypes and produces the preloaded catalog consumed by runtime systems.
/// </summary>
public sealed class IntentionsValidationService
{
    private static readonly Regex ColorRegex = new("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    private static readonly HashSet<string> IntentionKinds = [IntentionsPrototypeConstants.Primary, IntentionsPrototypeConstants.Secondary];
    private static readonly HashSet<string> VisibilityTypes = [IntentionsPrototypeConstants.Visible, IntentionsPrototypeConstants.Hidden];
    private static readonly HashSet<string> RevealTypes = [IntentionsPrototypeConstants.RevealNone, IntentionsPrototypeConstants.RevealTimer];

    private readonly IPrototypeManager _prototypes;
    private readonly Func<string, string?> _locResolver;

    /// <summary>
    /// Initializes a validator that can resolve prototypes and Fluent localization keys.
    /// </summary>
    public IntentionsValidationService(IPrototypeManager prototypes, Func<string, string?>? locResolver = null)
    {
        _prototypes = prototypes;
        _locResolver = locResolver ?? DefaultLocResolver;
    }

    /// <summary>
    /// Validates every loaded Intentions prototype and excludes invalid objects from the returned catalog.
    /// </summary>
    public ValidationCatalog ValidateAll()
    {
        var catalog = new ValidationCatalog();

        foreach (var category in _prototypes.EnumeratePrototypes<ScenarioCategoryPrototype>())
        {
            var issues = ValidateCategory(category);
            catalog.Issues.AddRange(issues);

            if (!HasErrors(issues))
            {
                catalog.ValidCategories[category.ID] = category;
                catalog.ValidCategoryOrder.Add(category.ID);
            }
        }

        foreach (var intention in _prototypes.EnumeratePrototypes<IntentionTemplatePrototype>())
        {
            var issues = ValidateIntention(intention);
            catalog.Issues.AddRange(issues);

            if (!HasErrors(issues))
                catalog.ValidIntentions[intention.ID] = intention;
        }

        foreach (var scenario in _prototypes.EnumeratePrototypes<ScenarioTemplatePrototype>())
        {
            var issues = ValidateScenario(scenario, catalog.ValidCategories, catalog.ValidIntentions, out var slotBuildOrder);
            catalog.Issues.AddRange(issues);

            if (!HasErrors(issues))
            {
                catalog.ValidScenarios[scenario.ID] = new ValidatedScenarioTemplate(scenario, slotBuildOrder);
                catalog.ValidScenarioOrder.Add(scenario.ID);
            }
        }

        return catalog;
    }

    /// <summary>
    /// Validates one Intention template and all of its content-facing constraints.
    /// </summary>
    private List<ValidationIssue> ValidateIntention(IntentionTemplatePrototype intention)
    {
        var issues = new List<ValidationIssue>();

        if (!IntentionKinds.Contains(intention.Kind))
            Add(issues, ValidationObjectType.IntentionTemplate, intention.ID, "kind", "invalid-kind", "Intention kind must be primary or secondary.");

        ValidateLoc(issues, intention.ID, ValidationObjectType.IntentionTemplate, "nameLoc", intention.NameLoc, required: true, maxLength: 35);
        ValidateLoc(issues, intention.ID, ValidationObjectType.IntentionTemplate, "summaryLoc", intention.SummaryLoc, required: false, maxLength: 35);
        ValidateLoc(issues, intention.ID, ValidationObjectType.IntentionTemplate, "descriptionLoc", intention.DescriptionLoc, required: true, maxLength: 2000);
        ValidateLoc(issues, intention.ID, ValidationObjectType.IntentionTemplate, "oocInfoLoc", intention.OocInfoLoc, required: false, maxLength: 500);
        ValidateLoc(issues, intention.ID, ValidationObjectType.IntentionTemplate, "copyableTextLoc", intention.CopyableTextLoc, required: false, maxLength: 5000);
        ValidateLoc(issues, intention.ID, ValidationObjectType.IntentionTemplate, "hiddenLabelLoc", intention.HiddenLabelLoc, required: false, maxLength: 45);

        if (!VisibilityTypes.Contains(intention.DefaultVisibility))
            Add(issues, ValidationObjectType.IntentionTemplate, intention.ID, "defaultVisibility", "invalid-visibility", "Default visibility must be visible or hidden.");

        if (intention.Color is { } color && !ColorRegex.IsMatch(color))
            Add(issues, ValidationObjectType.IntentionTemplate, intention.ID, "color", "invalid-color", "Color must use #RRGGBB or #RRGGBBAA format.");

        if (intention.CreationDate is { } date
            && (!DateRegex.IsMatch(date) || !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
        {
            Add(issues, ValidationObjectType.IntentionTemplate, intention.ID, "creationDate", "invalid-date", "Creation date must use YYYY-MM-DD format.");
        }

        return issues;
    }

    /// <summary>
    /// Validates one scenario category and its quota configuration.
    /// </summary>
    private List<ValidationIssue> ValidateCategory(ScenarioCategoryPrototype category)
    {
        var issues = new List<ValidationIssue>();

        if (category.Priority < 0)
            Add(issues, ValidationObjectType.ScenarioCategory, category.ID, "priority", "invalid-priority", "Priority must be greater than or equal to zero.");

        if (category.Color is { } color && !ColorRegex.IsMatch(color))
            Add(issues, ValidationObjectType.ScenarioCategory, category.ID, "color", "invalid-color", "Color must use #RRGGBB or #RRGGBBAA format.");

        if (!category.QuotaByGameMode.ContainsKey("default"))
            Add(issues, ValidationObjectType.ScenarioCategory, category.ID, "quotaByGameMode.default", "missing-default-quota", "Category must define default quota rule.");

        if (!category.MaxPrimaryPerMindByGameMode.ContainsKey("default"))
            Add(issues, ValidationObjectType.ScenarioCategory, category.ID, "maxPrimaryPerMindByGameMode.default", "missing-default-max-primary", "Category must define default max primary per mind.");

        foreach (var (mode, rule) in category.QuotaByGameMode)
            ValidateQuotaRule(issues, category.ID, $"quotaByGameMode.{mode}", rule);

        foreach (var (mode, value) in category.MaxPrimaryPerMindByGameMode)
        {
            if (value < 0)
                Add(issues, ValidationObjectType.ScenarioCategory, category.ID, $"maxPrimaryPerMindByGameMode.{mode}", "invalid-max-primary", "Max primary per mind must be non-negative.");
        }

        return issues;
    }

    /// <summary>
    /// Validates one quota rule entry from a scenario category.
    /// </summary>
    private void ValidateQuotaRule(List<ValidationIssue> issues, string categoryId, string path, QuotaRule rule)
    {
        switch (rule.Mode)
        {
            case "fixed":
                if (rule.Value is null or < 0)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.value", "invalid-fixed-quota", "Fixed quota requires value greater than or equal to zero.");
                if (rule.Ratio is not null)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.ratio", "fixed-quota-has-ratio", "Fixed quota cannot define ratio.");
                if (rule.Min is not null)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.min", "fixed-quota-has-min", "Fixed quota cannot define min.");
                if (rule.Max is not null)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.max", "fixed-quota-has-max", "Fixed quota cannot define max.");
                break;
            case "ratio":
                if (rule.Ratio is null or < 0)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.ratio", "invalid-ratio-quota", "Ratio quota requires ratio greater than or equal to zero.");
                if (rule.Value is not null)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.value", "ratio-quota-has-value", "Ratio quota cannot define value.");
                if (rule.Min is not null)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.min", "ratio-quota-has-min", "Ratio quota cannot define min.");
                if (rule.Max is not null)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.max", "ratio-quota-has-max", "Ratio quota cannot define max.");
                break;
            case "clamp":
                if (rule.Ratio is null or < 0)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.ratio", "invalid-clamp-ratio", "Clamp quota requires ratio greater than or equal to zero.");
                if (rule.Value is not null)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.value", "clamp-quota-has-value", "Clamp quota cannot define value.");
                if (rule.Min is null or < 0)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.min", "invalid-min-quota", "Quota min must be greater than or equal to zero.");
                if (rule.Max is null or < 0)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.max", "invalid-max-quota", "Quota max must be greater than or equal to zero.");
                if (rule.Min is { } min && rule.Max is { } max && max < min)
                    Add(issues, ValidationObjectType.ScenarioCategory, categoryId, path, "invalid-quota-range", "Quota max must be greater than or equal to min.");
                break;
            default:
                Add(issues, ValidationObjectType.ScenarioCategory, categoryId, $"{path}.mode", "invalid-quota-mode", "Quota mode must be fixed, ratio, or clamp.");
                break;
        }
    }

    /// <summary>
    /// Validates a scenario template and computes the stable slot build order used later by the pure builder.
    /// </summary>
    private List<ValidationIssue> ValidateScenario(
        ScenarioTemplatePrototype scenario,
        IReadOnlyDictionary<string, ScenarioCategoryPrototype> validCategories,
        IReadOnlyDictionary<string, IntentionTemplatePrototype> validIntentions,
        out IReadOnlyList<string> slotBuildOrder)
    {
        var issues = new List<ValidationIssue>();
        slotBuildOrder = Array.Empty<string>();

        if (!validCategories.ContainsKey(scenario.Category))
            Add(issues, ValidationObjectType.ScenarioTemplate, scenario.ID, "category", "invalid-category", "Scenario category must exist and be valid.");

        if (scenario.Weight <= 0)
            Add(issues, ValidationObjectType.ScenarioTemplate, scenario.ID, "weight", "invalid-weight", "Scenario weight must be greater than zero.");

        ValidatePredicateList(issues, scenario.ID, ValidationObjectType.ScenarioTemplate, "globalPredicates", scenario.GlobalPredicates, expectedScope: "round", slotIds: null);

        var slotIds = scenario.Entries.Select(entry => entry.SlotId).ToList();
        var slotIdSet = slotIds.ToHashSet();
        var duplicateSlot = slotIds.GroupBy(id => id).FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        var hasInvalidSlotIds = duplicateSlot is not null;
        if (duplicateSlot is not null)
            Add(issues, ValidationObjectType.ScenarioTemplate, scenario.ID, "entries", "invalid-slot-id", "Slot ids must be non-empty and unique.");

        var ownerEntries = scenario.Entries.Where(entry => entry.SlotId == "owner").ToList();
        if (ownerEntries.Count != 1)
            Add(issues, ValidationObjectType.ScenarioTemplate, scenario.ID, "entries", "invalid-owner-count", "Scenario must define exactly one owner slot.");

        foreach (var entry in scenario.Entries)
            ValidateEntry(issues, scenario, entry, validIntentions, slotIdSet);

        if (!hasInvalidSlotIds && !TryBuildSlotOrder(scenario, slotIdSet, issues, out slotBuildOrder))
            slotBuildOrder = Array.Empty<string>();

        return issues;
    }

    /// <summary>
    /// Validates one scenario entry against both the template contract and cross-slot references.
    /// </summary>
    private void ValidateEntry(
        List<ValidationIssue> issues,
        ScenarioTemplatePrototype scenario,
        ScenarioEntry entry,
        IReadOnlyDictionary<string, IntentionTemplatePrototype> validIntentions,
        HashSet<string> slotIds)
    {
        var path = $"entries.{entry.SlotId}";

        if (entry.SlotId == "owner")
        {
            if (entry.Kind != IntentionsPrototypeConstants.Primary)
                Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, $"{path}.kind", "invalid-owner-kind", "Owner slot must be primary.");
            if (!entry.Required)
                Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, $"{path}.required", "invalid-owner-required", "Owner slot must be required.");
        }
        else if (entry.Kind != IntentionsPrototypeConstants.Secondary)
        {
            Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, $"{path}.kind", "invalid-secondary-kind", "Non-owner slots must be secondary.");
        }

        if (!validIntentions.TryGetValue(entry.IntentionId, out var intention))
        {
            Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, $"{path}.intentionId", "invalid-intention", "Slot intention must exist and be valid.");
        }
        else if (intention.Kind != entry.Kind)
        {
            Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, $"{path}.kind", "kind-mismatch", "Scenario entry kind must match intention kind.");
        }

        if (entry.BindToSlot is not null && entry.AllowSameActorAs.Count > 0)
            Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, path, "bind-and-allow-same-actor", "bindToSlot and allowSameActorAs cannot be used together.");

        if (entry.BindToSlot is not null && entry.CandidatePredicates.Count > 0)
            Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, $"{path}.candidatePredicates", "bound-slot-has-predicates", "A bound slot cannot define candidatePredicates.");

        if (entry.BindToSlot is { } bindToSlot && !slotIds.Contains(bindToSlot))
            Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, $"{path}.bindToSlot", "missing-bound-slot", "bindToSlot must reference an existing slot.");

        var seenAllowed = new HashSet<string>();
        foreach (var allowedSlot in entry.AllowSameActorAs)
        {
            if (allowedSlot == entry.SlotId)
                Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, $"{path}.allowSameActorAs", "self-allow-same-actor", "allowSameActorAs cannot reference the current slot.");
            if (!seenAllowed.Add(allowedSlot))
                Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, $"{path}.allowSameActorAs", "duplicate-allow-same-actor", "allowSameActorAs cannot contain duplicates.");
            if (!slotIds.Contains(allowedSlot))
                Add(issues, ValidationObjectType.ScenarioEntry, scenario.ID, $"{path}.allowSameActorAs", "missing-allow-same-actor-slot", "allowSameActorAs must reference existing slots.");
        }

        ValidatePredicateList(issues, scenario.ID, ValidationObjectType.ScenarioEntry, $"{path}.candidatePredicates", entry.CandidatePredicates, expectedScope: "candidate", slotIds);
        ValidateTextParameterBindings(issues, scenario.ID, path, entry, slotIds);
        ValidateVisibilityOverride(issues, scenario.ID, path, entry.VisibilityOverride);
    }

    /// <summary>
    /// Validates text parameter bindings that will later be resolved once during commit.
    /// </summary>
    private void ValidateTextParameterBindings(
        List<ValidationIssue> issues,
        string scenarioId,
        string path,
        ScenarioEntry entry,
        HashSet<string> slotIds)
    {
        foreach (var (name, binding) in entry.TextParameterBindings)
        {
            var bindingPath = $"{path}.textParameterBindings.{name}";
            switch (binding.Source)
            {
                case "self":
                case "literal":
                    break;
                case "slot":
                    if (binding.SlotId is null || !slotIds.Contains(binding.SlotId))
                        Add(issues, ValidationObjectType.TextParameterBinding, scenarioId, $"{bindingPath}.slotId", "invalid-text-binding-slot", "Slot text binding must reference an existing slot.");
                    break;
                case "round":
                    if (binding.Field is not ("stationName" or "stationTime"))
                        Add(issues, ValidationObjectType.TextParameterBinding, scenarioId, $"{bindingPath}.field", "invalid-round-binding", "Round text binding only supports stationName and stationTime in S1.");
                    break;
                default:
                    Add(issues, ValidationObjectType.TextParameterBinding, scenarioId, $"{bindingPath}.source", "invalid-text-binding-source", "Text binding source must be self, slot, round or literal.");
                    break;
            }
        }
    }

    /// <summary>
    /// Validates visibility override and reveal configuration for one scenario entry.
    /// </summary>
    private void ValidateVisibilityOverride(
        List<ValidationIssue> issues,
        string scenarioId,
        string path,
        VisibilityOverrideDefinition? visibility)
    {
        if (visibility is null)
            return;

        if (!VisibilityTypes.Contains(visibility.Type))
        {
            Add(issues, ValidationObjectType.Visibility, scenarioId, $"{path}.visibilityOverride.type", "invalid-visibility-type", "Visibility override type must be visible or hidden.");
            return;
        }

        if (visibility.Type == IntentionsPrototypeConstants.Visible && visibility.Reveal is not null)
        {
            Add(issues, ValidationObjectType.Visibility, scenarioId, $"{path}.visibilityOverride.reveal", "visible-with-reveal", "Visible override cannot define reveal.");
            return;
        }

        if (visibility.Reveal is null)
            return;

        if (!RevealTypes.Contains(visibility.Reveal.Type))
        {
            Add(issues, ValidationObjectType.Visibility, scenarioId, $"{path}.visibilityOverride.reveal.type", "invalid-reveal-type", "Reveal type must be none or timer in MVP.");
            return;
        }

        if (visibility.Reveal.Type == IntentionsPrototypeConstants.RevealNone && visibility.Reveal.Minutes is not null)
            Add(issues, ValidationObjectType.Visibility, scenarioId, $"{path}.visibilityOverride.reveal.minutes", "none-reveal-with-minutes", "Reveal none cannot define minutes.");

        if (visibility.Reveal.Type == IntentionsPrototypeConstants.RevealTimer && visibility.Reveal.Minutes is null or <= 0)
            Add(issues, ValidationObjectType.Visibility, scenarioId, $"{path}.visibilityOverride.reveal.minutes", "invalid-timer-reveal", "Reveal timer requires minutes greater than zero.");
    }

    /// <summary>
    /// Validates every predicate in a list that shares the same scope expectations.
    /// </summary>
    private void ValidatePredicateList(
        List<ValidationIssue> issues,
        string objectId,
        ValidationObjectType objectType,
        string path,
        List<PredicateDefinition> predicates,
        string expectedScope,
        HashSet<string>? slotIds)
    {
        for (var i = 0; i < predicates.Count; i++)
            ValidatePredicate(issues, objectId, objectType, $"{path}.{i}", predicates[i], expectedScope, slotIds);
    }

    /// <summary>
    /// Validates one predicate against the shared schema, value-shape rules, and identifier references.
    /// </summary>
    private void ValidatePredicate(
        List<ValidationIssue> issues,
        string objectId,
        ValidationObjectType objectType,
        string path,
        PredicateDefinition predicate,
        string expectedScope,
        HashSet<string>? slotIds)
    {
        if (!IntentionsPredicateSchema.IsValidScope(predicate.Scope))
            Add(issues, objectType, objectId, $"{path}.scope", "invalid-predicate-scope", "Predicate scope must be round or candidate.");
        else if (predicate.Scope != expectedScope)
            Add(issues, objectType, objectId, $"{path}.scope", "wrong-predicate-scope", $"Predicate scope must be {expectedScope} here.");

        if (!IntentionsPredicateSchema.IsValidOperator(predicate.Operator))
            Add(issues, objectType, objectId, $"{path}.operator", "invalid-predicate-operator", "Predicate operator is not supported.");

        if (!IntentionsPredicateSchema.TryGetField(expectedScope, predicate.Field, out var fieldType))
        {
            Add(issues, objectType, objectId, $"{path}.field", "invalid-predicate-field", $"Predicate field is not valid for {expectedScope} predicates.");
            return;
        }

        var isMapField = fieldType == PredicateFieldType.MapStringInt;
        if (isMapField && string.IsNullOrWhiteSpace(predicate.Key))
            Add(issues, objectType, objectId, $"{path}.key", "missing-predicate-key", "Map predicate field requires key.");
        if (!isMapField && predicate.Key is not null)
            Add(issues, objectType, objectId, $"{path}.key", "unexpected-predicate-key", "key is only allowed for map predicate fields.");

        ValidatePredicateShape(issues, objectId, objectType, path, predicate, fieldType);
        ValidatePredicateValueTypes(issues, objectId, objectType, path, predicate, fieldType);
        ValidatePredicateIdentifiers(issues, objectId, objectType, path, predicate);

        if (predicate.CompareTo is not null)
            ValidateCompareTo(issues, objectId, objectType, path, predicate, expectedScope, slotIds);
    }

    /// <summary>
    /// Validates which predicate value form is used for the selected operator.
    /// </summary>
    private void ValidatePredicateShape(
        List<ValidationIssue> issues,
        string objectId,
        ValidationObjectType objectType,
        string path,
        PredicateDefinition predicate,
        PredicateFieldType fieldType)
    {
        var hasValue = predicate.Value is not null;
        var hasValues = predicate.Values is not null;
        var hasRange = predicate.ValueFrom is not null || predicate.ValueTo is not null;
        var hasCompare = predicate.CompareTo is not null;
        var formCount = new[] { hasValue, hasValues, hasRange, hasCompare }.Count(value => value);

        if (formCount > 1)
            Add(issues, objectType, objectId, path, "conflicting-predicate-values", "Predicate value forms are mutually exclusive.");

        switch (predicate.Operator)
        {
            case "equals":
            case "notEquals":
            case ">":
            case ">=":
            case "<":
            case "<=":
            case "contains":
            case "notContains":
                if (!hasValue)
                    Add(issues, objectType, objectId, $"{path}.value", "missing-predicate-value", "Predicate operator requires value.");
                break;
            case "in":
            case "notIn":
                if (predicate.Values is not { Count: > 0 })
                    Add(issues, objectType, objectId, $"{path}.values", "missing-predicate-values", "Predicate operator requires non-empty values.");
                break;
            case "between":
                if (predicate.ValueFrom is null || predicate.ValueTo is null)
                    Add(issues, objectType, objectId, path, "missing-predicate-range", "between requires valueFrom and valueTo.");
                break;
            case "sameAs":
            case "notSameAs":
                if (predicate.CompareTo is null)
                    Add(issues, objectType, objectId, $"{path}.compareTo", "missing-compare-to", "sameAs and notSameAs require compareTo.");
                break;
        }

        if (predicate.Operator is ("contains" or "notContains") && fieldType != PredicateFieldType.ListString)
            Add(issues, objectType, objectId, $"{path}.operator", "operator-field-mismatch", "contains and notContains require list<string> field.");

        if (predicate.Operator is (">" or ">=" or "<" or "<=" or "between")
            && fieldType is not (PredicateFieldType.Int or PredicateFieldType.Float or PredicateFieldType.TimeSpan or PredicateFieldType.MapStringInt))
        {
            Add(issues, objectType, objectId, $"{path}.operator", "operator-field-mismatch", "Comparison operators require numeric, time or map field.");
        }
    }

    /// <summary>
    /// Validates literal value types against the declared predicate field type.
    /// </summary>
    private void ValidatePredicateValueTypes(
        List<ValidationIssue> issues,
        string objectId,
        ValidationObjectType objectType,
        string path,
        PredicateDefinition predicate,
        PredicateFieldType fieldType)
    {
        if (predicate.Value is { } value && !ValueMatchesFieldType(value, fieldType, allowListElement: true))
            Add(issues, objectType, objectId, $"{path}.value", "invalid-predicate-value-type", "Predicate value does not match field type.");

        if (predicate.Values is { } values)
        {
            foreach (var item in values)
            {
                if (!ValueMatchesFieldType(item, fieldType, allowListElement: true))
                    Add(issues, objectType, objectId, $"{path}.values", "invalid-predicate-value-type", "Predicate values do not match field type.");
            }
        }

        if (predicate.ValueFrom is { } from && !ValueMatchesFieldType(from, fieldType, allowListElement: false))
            Add(issues, objectType, objectId, $"{path}.valueFrom", "invalid-predicate-value-type", "Predicate valueFrom does not match field type.");
        if (predicate.ValueTo is { } to && !ValueMatchesFieldType(to, fieldType, allowListElement: false))
            Add(issues, objectType, objectId, $"{path}.valueTo", "invalid-predicate-value-type", "Predicate valueTo does not match field type.");
    }

    /// <summary>
    /// Checks whether one serialized predicate value is compatible with the target field type.
    /// </summary>
    private bool ValueMatchesFieldType(string value, PredicateFieldType fieldType, bool allowListElement)
    {
        return fieldType switch
        {
            PredicateFieldType.String => true,
            PredicateFieldType.Bool => bool.TryParse(value, out _),
            PredicateFieldType.Int => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            PredicateFieldType.Float => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            PredicateFieldType.TimeSpan => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _),
            PredicateFieldType.ListString => allowListElement,
            PredicateFieldType.MapStringInt => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            _ => false,
        };
    }

    /// <summary>
    /// Validates string values that must match existing content identifiers or enum literals.
    /// </summary>
    private void ValidatePredicateIdentifiers(
        List<ValidationIssue> issues,
        string objectId,
        ValidationObjectType objectType,
        string path,
        PredicateDefinition predicate)
    {
        foreach (var value in PredicateStringValues(predicate))
        {
            switch (predicate.Field)
            {
                case "gameMode":
                    ValidatePrototypeIdentifier<GamePresetPrototype>(issues, objectId, objectType, path, value, "invalid-game-mode", MatchesGamePreset);
                    break;
                case "job":
                    ValidatePrototypeIdentifier<JobPrototype>(issues, objectId, objectType, path, value, "invalid-job");
                    break;
                case "department":
                    ValidatePrototypeIdentifier<DepartmentPrototype>(issues, objectId, objectType, path, value, "invalid-department");
                    break;
                case "species":
                    ValidatePrototypeIdentifier<SpeciesPrototype>(issues, objectId, objectType, path, value, "invalid-species");
                    break;
                case "traits":
                    ValidatePrototypeIdentifier<TraitPrototype>(issues, objectId, objectType, path, value, "invalid-trait");
                    break;
                case "antagRole":
                case "antagSummary.byRole":
                    ValidatePrototypeIdentifier<AntagPrototype>(issues, objectId, objectType, path, value, "invalid-antag-role");
                    break;
                case "sex":
                    if (!Enum.TryParse<Sex>(value, ignoreCase: true, out _))
                        Add(issues, objectType, objectId, path, "invalid-sex", "Sex predicate value must match a valid Sex enum value.");
                    break;
            }
        }
    }

    /// <summary>
    /// Enumerates every string literal that should participate in identifier validation for a predicate.
    /// </summary>
    private IEnumerable<string> PredicateStringValues(PredicateDefinition predicate)
    {
        if (predicate.Key is not null)
            yield return predicate.Key;
        if (predicate.Value is not null)
            yield return predicate.Value;
        if (predicate.Values is not null)
        {
            foreach (var value in predicate.Values)
                yield return value;
        }
    }

    /// <summary>
    /// Validates that a string value references a known prototype identifier.
    /// </summary>
    private void ValidatePrototypeIdentifier<T>(
        List<ValidationIssue> issues,
        string objectId,
        ValidationObjectType objectType,
        string path,
        string value,
        string code,
        Func<string, bool>? customMatcher = null) where T : class, IPrototype
    {
        if (!_prototypes.EnumeratePrototypes<T>().Any())
            return;

        if (customMatcher?.Invoke(value) == true)
            return;

        if (!_prototypes.HasIndex<T>(value))
            Add(issues, objectType, objectId, path, code, $"Unknown prototype identifier '{value}'.");
    }

    /// <summary>
    /// Matches game presets against both their canonical ids and configured aliases.
    /// </summary>
    private bool MatchesGamePreset(string value)
    {
        return _prototypes.EnumeratePrototypes<GamePresetPrototype>()
            .Any(preset => preset.ID == value || preset.Alias.Contains(value));
    }

    /// <summary>
    /// Validates compare-to metadata for predicates that reference already built slots.
    /// </summary>
    private void ValidateCompareTo(
        List<ValidationIssue> issues,
        string objectId,
        ValidationObjectType objectType,
        string path,
        PredicateDefinition predicate,
        string expectedScope,
        HashSet<string>? slotIds)
    {
        if (predicate.Operator is not ("sameAs" or "notSameAs"))
            Add(issues, objectType, objectId, $"{path}.compareTo", "unexpected-compare-to", "compareTo is only allowed for sameAs and notSameAs.");

        if (expectedScope != "candidate")
            Add(issues, objectType, objectId, $"{path}.compareTo", "compare-to-outside-candidate", "compareTo is only allowed in candidate predicates.");

        var compareTo = predicate.CompareTo!;
        if (compareTo.Scope != "slot")
            Add(issues, objectType, objectId, $"{path}.compareTo.scope", "invalid-compare-scope", "compareTo scope must be slot.");
        if (slotIds is not null && !slotIds.Contains(compareTo.SlotId))
            Add(issues, objectType, objectId, $"{path}.compareTo.slotId", "missing-compare-slot", "compareTo slotId must reference an existing slot.");
        if (compareTo.Field != predicate.Field)
            Add(issues, objectType, objectId, $"{path}.compareTo.field", "compare-field-mismatch", "compareTo field must match predicate field.");
    }

    /// <summary>
    /// Builds a stable topological order for scenario slots so runtime selection can stay deterministic.
    /// </summary>
    private bool TryBuildSlotOrder(
        ScenarioTemplatePrototype scenario,
        HashSet<string> slotIds,
        List<ValidationIssue> issues,
        out IReadOnlyList<string> slotBuildOrder)
    {
        // Precompute slot dependencies once during validation so the runtime builder can stay pure and deterministic.
        var dependencies = scenario.Entries.ToDictionary(entry => entry.SlotId, _ => new HashSet<string>());

        foreach (var entry in scenario.Entries)
        {
            if (!dependencies.ContainsKey(entry.SlotId))
                continue;

            if (entry.BindToSlot is { } bindToSlot && slotIds.Contains(bindToSlot))
                dependencies[entry.SlotId].Add(bindToSlot);

            foreach (var allowedSlot in entry.AllowSameActorAs)
            {
                if (slotIds.Contains(allowedSlot))
                    dependencies[entry.SlotId].Add(allowedSlot);
            }

            foreach (var predicate in entry.CandidatePredicates)
            {
                if (predicate.CompareTo is { } compareTo && slotIds.Contains(compareTo.SlotId))
                    dependencies[entry.SlotId].Add(compareTo.SlotId);
            }
        }

        if (dependencies.TryGetValue("owner", out var ownerDeps) && ownerDeps.Count > 0)
            Add(issues, ValidationObjectType.ScenarioTemplate, scenario.ID, "entries.owner", "owner-has-dependencies", "Owner slot cannot depend on other slots.");

        var result = new List<string>();
        var processed = new HashSet<string>();

        while (result.Count < dependencies.Count)
        {
            var next = scenario.Entries.FirstOrDefault(entry =>
                dependencies.ContainsKey(entry.SlotId)
                && !processed.Contains(entry.SlotId)
                && dependencies[entry.SlotId].All(processed.Contains));

            if (next is null)
            {
                Add(issues, ValidationObjectType.ScenarioTemplate, scenario.ID, "entries", "slot-dependency-cycle", "Slot dependency graph must not contain cycles.");
                slotBuildOrder = Array.Empty<string>();
                return false;
            }

            processed.Add(next.SlotId);
            result.Add(next.SlotId);
        }

        if (result.FirstOrDefault() != "owner")
            Add(issues, ValidationObjectType.ScenarioTemplate, scenario.ID, "entries", "owner-not-first", "Owner slot must be first in slotBuildOrder.");

        slotBuildOrder = result;
        return true;
    }

    /// <summary>
    /// Resolves a localization key and enforces the maximum string length configured by the requirements.
    /// </summary>
    private void ValidateLoc(
        List<ValidationIssue> issues,
        string objectId,
        ValidationObjectType objectType,
        string path,
        string? locId,
        bool required,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(locId))
        {
            if (required)
                Add(issues, objectType, objectId, path, "missing-loc", "Required localization key is missing.");
            return;
        }

        if (!HasLocKey(locId, out var resolved))
        {
            Add(issues, objectType, objectId, path, "unknown-loc", $"Localization key '{locId}' does not exist.");
            return;
        }

        if (resolved.Length > maxLength)
            Add(issues, objectType, objectId, path, "loc-too-long", $"Localized string must be no longer than {maxLength} characters.");
    }

    /// <summary>
    /// Tries to resolve one localization key without throwing when the key is missing.
    /// </summary>
    private bool HasLocKey(string locId, out string resolved)
    {
        var value = _locResolver(locId);
        if (value is null)
        {
            resolved = string.Empty;
            return false;
        }

        resolved = value;
        return true;
    }

    /// <summary>
    /// Default localization resolver used outside of tests.
    /// </summary>
    private static string? DefaultLocResolver(string locId)
    {
        try
        {
            var value = Loc.GetString(locId);
            return value == locId ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns whether the provided issue list contains any errors that should exclude an object from runtime.
    /// </summary>
    private static bool HasErrors(IEnumerable<ValidationIssue> issues)
    {
        return issues.Any(issue => issue.Severity == ValidationIssueSeverity.Error);
    }

    /// <summary>
    /// Adds one validation issue to the current issue list.
    /// </summary>
    private static void Add(
        List<ValidationIssue> issues,
        ValidationObjectType objectType,
        string objectId,
        string path,
        string code,
        string message)
    {
        issues.Add(new ValidationIssue(objectType, objectId, path, code, message));
    }

}
