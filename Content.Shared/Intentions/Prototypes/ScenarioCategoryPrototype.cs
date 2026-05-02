using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared.Intentions.Prototypes;

/// <summary>
/// Content prototype that groups scenario templates under a shared quota and presentation style.
/// </summary>
[Prototype("scenarioCategory")]
public sealed partial class ScenarioCategoryPrototype : IPrototype
{
    [ViewVariables]
    [IdDataField]
    /// <summary>
    /// Unique identifier used by scenario templates and runtime category state.
    /// </summary>
    public string ID { get; private set; } = default!;

    [DataField]
    /// <summary>
    /// Fluent localization key for the category name.
    /// </summary>
    public string NameLoc = string.Empty;

    [DataField]
    /// <summary>
    /// Optional Fluent localization key for category description text.
    /// </summary>
    public string? DescriptionLoc;

    [DataField]
    /// <summary>
    /// Optional fallback icon used when an intention template does not define its own icon.
    /// </summary>
    public SpriteSpecifier? Icon;

    [DataField]
    /// <summary>
    /// Accent color used by UI cards and category-specific presentation.
    /// </summary>
    public string? Color;

    [DataField]
    /// <summary>
    /// Higher values make the category eligible earlier during deterministic wave ordering.
    /// </summary>
    public int Priority;

    [DataField]
    /// <summary>
    /// Quota rules keyed by game mode id, including a required default entry.
    /// </summary>
    public Dictionary<string, QuotaRule> QuotaByGameMode = new();

    [DataField]
    /// <summary>
    /// Per-mode cap for how many primary scenarios one mind may own in this category.
    /// </summary>
    public Dictionary<string, int> MaxPrimaryPerMindByGameMode = new();
}

/// <summary>
/// Declares how many scenarios from a category should be targeted for a wave.
/// </summary>
[DataDefinition]
public sealed partial class QuotaRule
{
    [DataField]
    /// <summary>
    /// Quota mode, such as fixed, ratio, or clamp.
    /// </summary>
    public string Mode = "fixed";

    [DataField]
    /// <summary>
    /// Absolute quota used by the fixed mode.
    /// </summary>
    public int? Value;

    [DataField]
    /// <summary>
    /// Crew ratio used by the ratio and clamp modes.
    /// </summary>
    public float? Ratio;

    [DataField]
    /// <summary>
    /// Required lower bound applied by the clamp mode.
    /// </summary>
    public int? Min;

    [DataField]
    /// <summary>
    /// Required upper bound applied by the clamp mode.
    /// </summary>
    public int? Max;
}
