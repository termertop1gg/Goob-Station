using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Content.Shared.Intentions.Predicates;
using Content.Shared.Intentions.Runtime;
using Robust.Shared.GameObjects;

namespace Content.Shared.Intentions.Waves;

/// <summary>
/// Input contract for a start wave dry-run or committed execution.
/// </summary>
public sealed class IntentionsStartWaveRequest
{
    /// <summary>
    /// Creates a start-wave request.
    /// </summary>
    public IntentionsStartWaveRequest(
        int waveId,
        int? seed = null,
        IEnumerable<string>? assignedScenarioIds = null,
        IReadOnlyDictionary<EntityUid, IReadOnlyDictionary<string, int>>? assignedPrimaryByMind = null)
    {
        WaveId = waveId;
        Seed = seed;
        AssignedScenarioIds = assignedScenarioIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToImmutableHashSet(StringComparer.Ordinal)
            ?? ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        AssignedPrimaryByMind = assignedPrimaryByMind?
            .ToImmutableDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, int>) pair.Value.ToImmutableDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal))
            ?? ImmutableDictionary<EntityUid, IReadOnlyDictionary<string, int>>.Empty;
    }

    /// <summary>
    /// Wave id assigned by the distribution scheduler.
    /// </summary>
    public int WaveId { get; }

    /// <summary>
    /// Optional deterministic random seed override.
    /// </summary>
    public int? Seed { get; }

    /// <summary>
    /// Scenario template ids that are already reserved for the round.
    /// </summary>
    public ImmutableHashSet<string> AssignedScenarioIds { get; }

    /// <summary>
    /// Existing primary ownership counters carried into the wave.
    /// </summary>
    public IReadOnlyDictionary<EntityUid, IReadOnlyDictionary<string, int>> AssignedPrimaryByMind { get; }
}

/// <summary>
/// Input contract for a refill wave.
/// </summary>
public sealed class IntentionsRefillWaveRequest
{
    /// <summary>
    /// Creates a refill-wave request.
    /// </summary>
    public IntentionsRefillWaveRequest(int waveId, int? seed = null)
    {
        WaveId = waveId;
        Seed = seed;
    }

    /// <summary>
    /// Wave id assigned by the distribution scheduler.
    /// </summary>
    public int WaveId { get; }

    /// <summary>
    /// Optional deterministic random seed override.
    /// </summary>
    public int? Seed { get; }
}

/// <summary>
/// Top-level result returned by start and refill wave orchestration.
/// </summary>
public sealed class StartWaveResult
{
    /// <summary>
    /// Creates a wave result wrapper.
    /// </summary>
    public StartWaveResult(DistributionWaveContext context, IEnumerable<IntentionInstance>? committedIntentions = null)
    {
        Context = context;
        CommittedIntentions = committedIntentions?.ToImmutableArray() ?? [];
    }

    /// <summary>
    /// Final wave context containing all diagnostics and successful builds.
    /// </summary>
    public DistributionWaveContext Context { get; }

    /// <summary>
    /// Runtime intention instances committed by this wave, if the run used the commit path.
    /// </summary>
    public ImmutableArray<IntentionInstance> CommittedIntentions { get; }

    /// <summary>
    /// Whether the wave completed successfully.
    /// </summary>
    public bool IsSuccess => Context.Status == DistributionWaveStatus.Completed;

    /// <summary>
    /// Failure reason when the wave ended in a failed state.
    /// </summary>
    public string? FailureReason => Context.FailureReason;
}

/// <summary>
/// Mutable orchestration context that accumulates the full result of one wave.
/// </summary>
public sealed class DistributionWaveContext
{
    /// <summary>
    /// Creates a new wave context.
    /// </summary>
    public DistributionWaveContext(
        int waveId,
        string snapshotId,
        int seed,
        TimeSpan startedAtRoundTime,
        int waveActiveCrew,
        IEnumerable<string>? assignedScenarioIds = null,
        IReadOnlyDictionary<EntityUid, IReadOnlyDictionary<string, int>>? assignedPrimaryByMind = null,
        IntentionsWaveKind kind = IntentionsWaveKind.Start,
        int? distributionCrewBaseline = null)
    {
        WaveId = waveId;
        SnapshotId = snapshotId;
        Seed = seed;
        StartedAtRoundTime = startedAtRoundTime;
        WaveActiveCrew = waveActiveCrew;
        Kind = kind;
        DistributionCrewBaseline = distributionCrewBaseline ?? waveActiveCrew;

        if (assignedScenarioIds is not null)
        {
            foreach (var scenarioId in assignedScenarioIds)
            {
                if (!string.IsNullOrWhiteSpace(scenarioId))
                    AssignedScenarioIds.Add(scenarioId);
            }
        }

        if (assignedPrimaryByMind is not null)
        {
            foreach (var (mindId, counts) in assignedPrimaryByMind)
            {
                AssignedPrimaryByMind[mindId] = new Dictionary<string, int>(counts, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Wave id assigned by the scheduler.
    /// </summary>
    public int WaveId { get; }

    /// <summary>
    /// Snapshot id used for all predicate checks in this wave.
    /// </summary>
    public string SnapshotId { get; }

    /// <summary>
    /// Deterministic seed resolved for this wave.
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// Whether the context belongs to a start or refill wave.
    /// </summary>
    public IntentionsWaveKind Kind { get; }

    /// <summary>
    /// Round time when wave processing started.
    /// </summary>
    public TimeSpan StartedAtRoundTime { get; }

    /// <summary>
    /// Current active crew count observed in the snapshot.
    /// </summary>
    public int WaveActiveCrew { get; }

    /// <summary>
    /// Crew baseline used when calculating quotas for this wave.
    /// </summary>
    public int DistributionCrewBaseline { get; }

    /// <summary>
    /// Current execution status of the wave.
    /// </summary>
    public DistributionWaveStatus Status { get; set; } = DistributionWaveStatus.Pending;

    /// <summary>
    /// Failure reason recorded when the wave stalls or aborts.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Category ids eligible to be processed in this wave.
    /// </summary>
    public List<string> AllowedCategoryIds { get; } = new();

    /// <summary>
    /// Per-category mutable state tracked while the wave runs.
    /// </summary>
    public Dictionary<string, CategoryWaveState> CategoryStateById { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Scenario template ids reserved before or during the wave.
    /// </summary>
    public HashSet<string> AssignedScenarioIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Primary ownership counters carried through the wave.
    /// </summary>
    public Dictionary<EntityUid, Dictionary<string, int>> AssignedPrimaryByMind { get; } = new();

    /// <summary>
    /// Scenario-level reject reasons accumulated during selection and building.
    /// </summary>
    public List<ScenarioRejectReason> RejectReasons { get; } = new();

    /// <summary>
    /// Successfully built scenarios ready for commit or inspection.
    /// </summary>
    public List<ScenarioBuildResult> SuccessfulBuilds { get; } = new();
}

/// <summary>
/// Execution states for one Intentions wave.
/// </summary>
public enum DistributionWaveStatus : byte
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Distinguishes the initial round-start wave from later refill waves.
/// </summary>
public enum IntentionsWaveKind : byte
{
    Start,
    Refill,
}

/// <summary>
/// Mutable per-category state tracked while a wave is running.
/// </summary>
public sealed class CategoryWaveState
{
    /// <summary>
    /// Creates per-category wave state.
    /// </summary>
    public CategoryWaveState(
        string categoryId,
        int targetQuota,
        int effectiveMaxPrimaryPerMind,
        int desiredQuota = 0,
        int existingActiveFrozenCount = 0,
        int refillTarget = 0)
    {
        CategoryId = categoryId;
        TargetQuota = targetQuota;
        EffectiveMaxPrimaryPerMind = effectiveMaxPrimaryPerMind;
        DesiredQuota = desiredQuota == 0 ? targetQuota : desiredQuota;
        ExistingActiveFrozenCount = existingActiveFrozenCount;
        RefillTarget = refillTarget == 0 ? targetQuota : refillTarget;
    }

    /// <summary>
    /// Category id this state belongs to.
    /// </summary>
    public string CategoryId { get; }

    /// <summary>
    /// Quota the wave is currently trying to fill for this category.
    /// </summary>
    public int TargetQuota { get; }

    /// <summary>
    /// Full desired quota before refill deficit is applied.
    /// </summary>
    public int DesiredQuota { get; }

    /// <summary>
    /// Number of active or frozen runtime scenarios already counted before refill.
    /// </summary>
    public int ExistingActiveFrozenCount { get; }

    /// <summary>
    /// Number of new scenarios the refill wave is trying to add.
    /// </summary>
    public int RefillTarget { get; }

    /// <summary>
    /// Number of successful builds already produced in this category.
    /// </summary>
    public int FilledQuota { get; set; }

    /// <summary>
    /// Effective cap for primary ownership in this category.
    /// </summary>
    public int EffectiveMaxPrimaryPerMind { get; }

    /// <summary>
    /// Whether the category can no longer produce additional scenarios this wave.
    /// </summary>
    public bool IsExhausted { get; set; }

    /// <summary>
    /// Reason why the category became exhausted.
    /// </summary>
    public CategoryExhaustReason ExhaustReason { get; set; } = CategoryExhaustReason.None;

    /// <summary>
    /// Scenario template ids that remain eligible in the category pool.
    /// </summary>
    public List<string> CandidateScenarioIds { get; } = new();

    /// <summary>
    /// Scenario template ids rejected for this wave.
    /// </summary>
    public HashSet<string> RejectedScenarioIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Diagnostic reject counters grouped by reason code.
    /// </summary>
    public Dictionary<string, int> RejectCounters { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Reasons why a category stopped producing scenarios in the current wave.
/// </summary>
public enum CategoryExhaustReason : byte
{
    None,
    Blocked,
    PoolEmpty,
    PoolExhausted,
    QuotaFilled,
}

/// <summary>
/// Result of attempting to build one scenario inside a wave.
/// </summary>
public sealed class ScenarioBuildResult
{
    /// <summary>
    /// Creates a scenario build result.
    /// </summary>
    private ScenarioBuildResult(
        string scenarioTemplateId,
        string categoryId,
        bool isSuccess,
        IEnumerable<ScenarioSlotBuildResult> builtSlots,
        IEnumerable<string> skippedOptionalSlots,
        string? failureReason,
        IEnumerable<SlotRejectReason> slotRejectReasons)
    {
        ScenarioTemplateId = scenarioTemplateId;
        CategoryId = categoryId;
        IsSuccess = isSuccess;
        BuiltSlots = builtSlots.ToImmutableArray();
        BuiltSlotsBySlotId = BuiltSlots.ToImmutableDictionary(slot => slot.SlotId, StringComparer.Ordinal);
        SkippedOptionalSlots = skippedOptionalSlots.ToImmutableArray();
        FailureReason = failureReason;
        SlotRejectReasons = slotRejectReasons.ToImmutableArray();
    }

    /// <summary>
    /// Source scenario template id.
    /// </summary>
    public string ScenarioTemplateId { get; }

    /// <summary>
    /// Source category id.
    /// </summary>
    public string CategoryId { get; }

    /// <summary>
    /// Whether the scenario built successfully.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Slots that were assigned or bound before the build completed.
    /// </summary>
    public ImmutableArray<ScenarioSlotBuildResult> BuiltSlots { get; }

    /// <summary>
    /// Slot lookup for later commit and diagnostics.
    /// </summary>
    public ImmutableDictionary<string, ScenarioSlotBuildResult> BuiltSlotsBySlotId { get; }

    /// <summary>
    /// Optional slots that were skipped without failing the scenario.
    /// </summary>
    public ImmutableArray<string> SkippedOptionalSlots { get; }

    /// <summary>
    /// Failure reason for unsuccessful builds.
    /// </summary>
    public string? FailureReason { get; }

    /// <summary>
    /// Slot-level reject reasons collected while building.
    /// </summary>
    public ImmutableArray<SlotRejectReason> SlotRejectReasons { get; }

    /// <summary>
    /// Creates a successful scenario build result.
    /// </summary>
    public static ScenarioBuildResult Success(
        string scenarioTemplateId,
        string categoryId,
        IEnumerable<ScenarioSlotBuildResult> builtSlots,
        IEnumerable<string> skippedOptionalSlots,
        IEnumerable<SlotRejectReason> slotRejectReasons)
    {
        return new ScenarioBuildResult(scenarioTemplateId, categoryId, true, builtSlots, skippedOptionalSlots, null, slotRejectReasons);
    }

    /// <summary>
    /// Creates a failed scenario build result.
    /// </summary>
    public static ScenarioBuildResult Failure(
        string scenarioTemplateId,
        string categoryId,
        string failureReason,
        IEnumerable<ScenarioSlotBuildResult> builtSlots,
        IEnumerable<string> skippedOptionalSlots,
        IEnumerable<SlotRejectReason> slotRejectReasons)
    {
        return new ScenarioBuildResult(scenarioTemplateId, categoryId, false, builtSlots, skippedOptionalSlots, failureReason, slotRejectReasons);
    }
}

/// <summary>
/// One built scenario slot captured by the pure builder.
/// </summary>
public sealed record ScenarioSlotBuildResult(
    string SlotId,
    string Kind,
    string IntentionId,
    EntityUid MindId,
    EntityUid OwnerEntityUid,
    bool Required,
    ScenarioSlotBuildState State,
    bool WasBound = false,
    string? BoundToSlotId = null);

/// <summary>
/// States a slot may pass through while a scenario is being built.
/// </summary>
public enum ScenarioSlotBuildState : byte
{
    Pending,
    Assigned,
    Bound,
    Skipped,
    Failed,
}

/// <summary>
/// Scenario-level explanation for why a template was rejected during a wave.
/// </summary>
public sealed class ScenarioRejectReason
{
    /// <summary>
    /// Creates a scenario reject reason.
    /// </summary>
    public ScenarioRejectReason(
        string scenarioTemplateId,
        string categoryId,
        string code,
        string message,
        string? slotId = null,
        IEnumerable<SlotRejectReason>? slotRejectReasons = null,
        IEnumerable<PredicateRejectReason>? predicateRejectReasons = null)
    {
        ScenarioTemplateId = scenarioTemplateId;
        CategoryId = categoryId;
        Code = code;
        Message = message;
        SlotId = slotId;
        SlotRejectReasons = slotRejectReasons?.ToImmutableArray() ?? ImmutableArray<SlotRejectReason>.Empty;
        PredicateRejectReasons = predicateRejectReasons?.ToImmutableArray() ?? ImmutableArray<PredicateRejectReason>.Empty;
    }

    /// <summary>
    /// Rejected scenario template id.
    /// </summary>
    public string ScenarioTemplateId { get; }

    /// <summary>
    /// Category that contained the rejected scenario.
    /// </summary>
    public string CategoryId { get; }

    /// <summary>
    /// Stable machine-readable reject code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Human-readable reject message for diagnostics.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Optional slot id responsible for the rejection.
    /// </summary>
    public string? SlotId { get; }

    /// <summary>
    /// Nested slot-level reject reasons.
    /// </summary>
    public ImmutableArray<SlotRejectReason> SlotRejectReasons { get; }

    /// <summary>
    /// Nested predicate reject reasons.
    /// </summary>
    public ImmutableArray<PredicateRejectReason> PredicateRejectReasons { get; }
}

/// <summary>
/// Slot-level explanation for why a candidate or slot assignment failed.
/// </summary>
public sealed class SlotRejectReason
{
    /// <summary>
    /// Creates a slot reject reason.
    /// </summary>
    public SlotRejectReason(
        string scenarioTemplateId,
        string slotId,
        string code,
        string message,
        EntityUid? candidateMindId = null,
        IEnumerable<PredicateRejectReason>? predicateRejectReasons = null)
    {
        ScenarioTemplateId = scenarioTemplateId;
        SlotId = slotId;
        Code = code;
        Message = message;
        CandidateMindId = candidateMindId;
        PredicateRejectReasons = predicateRejectReasons?.ToImmutableArray() ?? ImmutableArray<PredicateRejectReason>.Empty;
    }

    /// <summary>
    /// Source scenario template id.
    /// </summary>
    public string ScenarioTemplateId { get; }

    /// <summary>
    /// Rejected slot id.
    /// </summary>
    public string SlotId { get; }

    /// <summary>
    /// Stable machine-readable reject code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Human-readable reject message for diagnostics.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Candidate mind that failed the slot, when applicable.
    /// </summary>
    public EntityUid? CandidateMindId { get; }

    /// <summary>
    /// Predicate-level reasons that explain this slot rejection in more detail.
    /// </summary>
    public ImmutableArray<PredicateRejectReason> PredicateRejectReasons { get; }
}
