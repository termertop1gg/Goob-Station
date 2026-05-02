using Content.Shared.Intentions.Prototypes;

namespace Content.Shared.Intentions.Validation;

/// <summary>
/// Holds the validated subset of Intentions content that may safely reach runtime systems.
/// </summary>
public sealed class ValidationCatalog
{
    /// <summary>
    /// Scenario categories that passed validation, keyed by prototype id.
    /// </summary>
    public Dictionary<string, ScenarioCategoryPrototype> ValidCategories { get; } = new();

    /// <summary>
    /// Intention templates that passed validation, keyed by prototype id.
    /// </summary>
    public Dictionary<string, IntentionTemplatePrototype> ValidIntentions { get; } = new();

    /// <summary>
    /// Scenario templates that passed validation together with their precomputed slot build order.
    /// </summary>
    public Dictionary<string, ValidatedScenarioTemplate> ValidScenarios { get; } = new();

    /// <summary>
    /// Declaration order of valid categories used as a deterministic tie-breaker.
    /// </summary>
    public List<string> ValidCategoryOrder { get; } = new();

    /// <summary>
    /// Declaration order of valid scenarios used as a deterministic tie-breaker.
    /// </summary>
    public List<string> ValidScenarioOrder { get; } = new();

    /// <summary>
    /// All issues encountered while validating the loaded Intentions content.
    /// </summary>
    public List<ValidationIssue> Issues { get; } = new();
}

/// <summary>
/// Wraps a validated scenario template together with the deterministic slot build order.
/// </summary>
public sealed class ValidatedScenarioTemplate
{
    /// <summary>
    /// Creates a validated scenario wrapper for runtime-safe wave building.
    /// </summary>
    public ValidatedScenarioTemplate(ScenarioTemplatePrototype template, IReadOnlyList<string> slotBuildOrder)
    {
        Template = template;
        SlotBuildOrder = slotBuildOrder;
    }

    /// <summary>
    /// Original scenario prototype that passed structural validation.
    /// </summary>
    public ScenarioTemplatePrototype Template { get; }

    /// <summary>
    /// Stable topological order used by the builder when assigning slots.
    /// </summary>
    public IReadOnlyList<string> SlotBuildOrder { get; }
}

/// <summary>
/// Describes one validation issue emitted while inspecting Intentions content.
/// </summary>
public sealed record ValidationIssue(
    ValidationObjectType ObjectType,
    string ObjectId,
    string Path,
    string Code,
    string Message,
    ValidationIssueSeverity Severity = ValidationIssueSeverity.Error);

/// <summary>
/// Severity level for validation output.
/// </summary>
public enum ValidationIssueSeverity : byte
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Object categories used to classify validation issues.
/// </summary>
public enum ValidationObjectType : byte
{
    ScenarioCategory,
    IntentionTemplate,
    ScenarioTemplate,
    ScenarioEntry,
    Predicate,
    Visibility,
    TextParameterBinding,
}
