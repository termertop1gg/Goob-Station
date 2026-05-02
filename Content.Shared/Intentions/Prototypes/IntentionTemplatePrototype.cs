using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared.Intentions.Prototypes;

/// <summary>
/// Content prototype that describes a single intention card template.
/// </summary>
[Prototype("intentionTemplate")]
public sealed partial class IntentionTemplatePrototype : IPrototype
{
    [ViewVariables]
    [IdDataField]
    /// <summary>
    /// Unique prototype identifier referenced by scenario entries and runtime instances.
    /// </summary>
    public string ID { get; private set; } = default!;

    [DataField]
    /// <summary>
    /// Declares whether the template is used for a primary or secondary slot.
    /// </summary>
    public string Kind = IntentionsPrototypeConstants.Primary;

    [DataField]
    /// <summary>
    /// Fluent localization key for the visible title.
    /// </summary>
    public string NameLoc = string.Empty;

    [DataField]
    /// <summary>
    /// Optional Fluent localization key for the short list summary.
    /// </summary>
    public string? SummaryLoc;

    [DataField]
    /// <summary>
    /// Fluent localization key for the full visible description.
    /// </summary>
    public string DescriptionLoc = string.Empty;

    [DataField]
    /// <summary>
    /// Optional Fluent localization key for the out-of-character guidance block.
    /// </summary>
    public string? OocInfoLoc;

    [DataField]
    /// <summary>
    /// Optional Fluent localization key for copyable supporting material text.
    /// </summary>
    public string? CopyableTextLoc;

    [DataField]
    /// <summary>
    /// Default visibility mode applied when no scenario-level override is present.
    /// </summary>
    public string DefaultVisibility = IntentionsPrototypeConstants.Visible;

    [DataField]
    /// <summary>
    /// Optional Fluent localization key shown while a hidden intention is still concealed.
    /// </summary>
    public string? HiddenLabelLoc;

    [DataField]
    /// <summary>
    /// Free-form content tags reserved for future filtering and tooling.
    /// </summary>
    public List<string> Tags = new();

    [DataField]
    /// <summary>
    /// Optional icon shown by the UI for visible cards that use this template.
    /// </summary>
    public SpriteSpecifier? Icon;

    [DataField]
    /// <summary>
    /// Accent color used by the UI card list.
    /// </summary>
    public string? Color;

    [DataField]
    /// <summary>
    /// Optional author label shown in the detailed card view.
    /// </summary>
    public string? Author;

    [DataField]
    /// <summary>
    /// Optional content metadata date string kept in the prototype for auditing.
    /// </summary>
    public string? CreationDate;
}

/// <summary>
/// Shared string constants used by prototype validation and runtime logic.
/// </summary>
public static class IntentionsPrototypeConstants
{
    /// <summary>
    /// Prototype value for a primary intention or scenario slot.
    /// </summary>
    public const string Primary = "primary";

    /// <summary>
    /// Prototype value for a secondary intention or scenario slot.
    /// </summary>
    public const string Secondary = "secondary";

    /// <summary>
    /// Visibility value for intentions shown immediately.
    /// </summary>
    public const string Visible = "visible";

    /// <summary>
    /// Visibility value for intentions hidden until reveal.
    /// </summary>
    public const string Hidden = "hidden";

    /// <summary>
    /// Reveal mode value for hidden intentions without automatic reveal.
    /// </summary>
    public const string RevealNone = "none";

    /// <summary>
    /// Reveal mode value for hidden intentions revealed by timer.
    /// </summary>
    public const string RevealTimer = "timer";
}
