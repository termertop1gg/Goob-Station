using Robust.Shared.Prototypes;

namespace Content.Shared.Intentions.Prototypes;

/// <summary>
/// Content prototype that describes a scenario distributed as a bundle of intention slots.
/// </summary>
[Prototype("scenarioTemplate")]
public sealed partial class ScenarioTemplatePrototype : IPrototype
{
    [ViewVariables]
    [IdDataField]
    /// <summary>
    /// Unique identifier used during validation, wave selection, and runtime assignment tracking.
    /// </summary>
    public string ID { get; private set; } = default!;

    [DataField]
    /// <summary>
    /// Human-readable content name used by tooling and debug output.
    /// </summary>
    public string Name = string.Empty;

    [DataField]
    /// <summary>
    /// Category that controls quotas, UI fallbacks, and primary ownership limits.
    /// </summary>
    public ProtoId<ScenarioCategoryPrototype> Category;

    [DataField]
    /// <summary>
    /// Whether the scenario is eligible for runtime distribution after validation succeeds.
    /// </summary>
    public bool Enabled = true;

    [DataField]
    /// <summary>
    /// Relative selection weight inside the category pool.
    /// </summary>
    public int Weight = 1;

    [DataField]
    /// <summary>
    /// Round-scoped predicates evaluated once before the scenario enters a wave pool.
    /// </summary>
    public List<PredicateDefinition> GlobalPredicates = new();

    [DataField]
    /// <summary>
    /// Ordered slot declarations that are validated into a deterministic build order.
    /// </summary>
    public List<ScenarioEntry> Entries = new();
}

/// <summary>
/// Declares how one intention slot should be built inside a scenario.
/// </summary>
[DataDefinition]
public sealed partial class ScenarioEntry
{
    [DataField]
    /// <summary>
    /// Unique slot identifier referenced by binds, comparisons, and runtime assignments.
    /// </summary>
    public string SlotId = string.Empty;

    [DataField]
    /// <summary>
    /// Slot kind, expected to align with the referenced intention template kind.
    /// </summary>
    public string Kind = IntentionsPrototypeConstants.Secondary;

    [DataField]
    /// <summary>
    /// Intention template instantiated when this slot is successfully assigned.
    /// </summary>
    public ProtoId<IntentionTemplatePrototype> IntentionId;

    [DataField]
    /// <summary>
    /// Whether the scenario fails when this slot cannot be assigned.
    /// </summary>
    public bool Required = true;

    [DataField]
    /// <summary>
    /// Candidate-scoped predicates evaluated while searching for a matching participant.
    /// </summary>
    public List<PredicateDefinition> CandidatePredicates = new();

    [DataField]
    /// <summary>
    /// Optional slot id whose selected candidate should be reused directly.
    /// </summary>
    public string? BindToSlot;

    [DataField]
    /// <summary>
    /// Slot ids whose selected actors may be re-used for this entry.
    /// </summary>
    public List<string> AllowSameActorAs = new();

    [DataField]
    /// <summary>
    /// Named values captured at commit time for localized text rendering.
    /// </summary>
    public Dictionary<string, TextParameterBindingDefinition> TextParameterBindings = new();

    [DataField]
    /// <summary>
    /// Optional runtime visibility override applied on top of the intention template default.
    /// </summary>
    public VisibilityOverrideDefinition? VisibilityOverride;
}

/// <summary>
/// Generic predicate declaration shared by global and candidate checks.
/// </summary>
[DataDefinition]
public sealed partial class PredicateDefinition
{
    [DataField]
    /// <summary>
    /// Predicate scope, such as round or candidate.
    /// </summary>
    public string Scope = string.Empty;

    [DataField]
    /// <summary>
    /// Logical field name resolved by the predicate schema for the chosen scope.
    /// </summary>
    public string Field = string.Empty;

    [DataField("operator")]
    /// <summary>
    /// Comparison operator applied to the resolved field value.
    /// </summary>
    public string Operator = string.Empty;

    [DataField]
    /// <summary>
    /// Single expected value used by unary and comparison operators.
    /// </summary>
    public string? Value;

    [DataField]
    /// <summary>
    /// Expected value set used by in and notIn operators.
    /// </summary>
    public List<string>? Values;

    [DataField]
    /// <summary>
    /// Inclusive lower bound for between comparisons.
    /// </summary>
    public string? ValueFrom;

    [DataField]
    /// <summary>
    /// Inclusive upper bound for between comparisons.
    /// </summary>
    public string? ValueTo;

    [DataField]
    /// <summary>
    /// Map key used when addressing summary dictionaries such as antag counts.
    /// </summary>
    public string? Key;

    [DataField]
    /// <summary>
    /// Slot comparison metadata used by sameAs and notSameAs operators.
    /// </summary>
    public CompareToDefinition? CompareTo;
}

/// <summary>
/// Describes which already-built slot should be compared against by a predicate.
/// </summary>
[DataDefinition]
public sealed partial class CompareToDefinition
{
    [DataField]
    /// <summary>
    /// Comparison scope, constrained to slot references for Intentions MVP.
    /// </summary>
    public string Scope = string.Empty;

    [DataField]
    /// <summary>
    /// Slot id whose selected candidate should provide the comparison value.
    /// </summary>
    public string SlotId = string.Empty;

    [DataField]
    /// <summary>
    /// Candidate field to read from the compared slot.
    /// </summary>
    public string Field = string.Empty;
}

/// <summary>
/// Scenario-level visibility override applied to a built intention instance.
/// </summary>
[DataDefinition]
public sealed partial class VisibilityOverrideDefinition
{
    [DataField]
    /// <summary>
    /// Target visibility mode to force for this slot.
    /// </summary>
    public string Type = string.Empty;

    [DataField]
    /// <summary>
    /// Optional reveal rule used when the override keeps the intention hidden.
    /// </summary>
    public RevealDefinition? Reveal;
}

/// <summary>
/// Declares when a hidden intention should be revealed automatically.
/// </summary>
[DataDefinition]
public sealed partial class RevealDefinition
{
    [DataField]
    /// <summary>
    /// Reveal mode, such as none or timer.
    /// </summary>
    public string Type = string.Empty;

    [DataField]
    /// <summary>
    /// Timer delay in round minutes for timer-based reveal mode.
    /// </summary>
    public int? Minutes;
}

/// <summary>
/// Captures a single runtime parameter used when rendering localized text once at commit time.
/// </summary>
[DataDefinition]
public sealed partial class TextParameterBindingDefinition
{
    [DataField]
    /// <summary>
    /// Binding source, such as self, slot, round, or literal.
    /// </summary>
    public string Source = string.Empty;

    [DataField]
    /// <summary>
    /// Referenced slot id when the binding source reads from another selected slot.
    /// </summary>
    public string? SlotId;

    [DataField]
    /// <summary>
    /// Field name to read from the selected source.
    /// </summary>
    public string? Field;

    [DataField]
    /// <summary>
    /// Literal value used when the binding source is a fixed string.
    /// </summary>
    public string? Value;
}
