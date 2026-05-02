using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Content.Server.Intentions.Runtime;
using Content.Shared.Intentions.Predicates;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Validation;
using Content.Shared.Intentions.Waves;
using Robust.Shared.GameObjects;

namespace Content.Server.Intentions.Waves;

/// <summary>
/// Coordinates deterministic start and refill waves from validated content, snapshots, and optional runtime commit.
/// </summary>
public sealed class IntentionsWaveOrchestrator
{
    private readonly IntentionsPredicateEngine _predicateEngine;
    private readonly IntentionsScenarioBuilder _builder;

    /// <summary>
    /// Initializes the orchestrator with the predicate engine and pure scenario builder used during waves.
    /// </summary>
    public IntentionsWaveOrchestrator(IntentionsPredicateEngine? predicateEngine = null, IntentionsScenarioBuilder? builder = null)
    {
        _predicateEngine = predicateEngine ?? new IntentionsPredicateEngine();
        _builder = builder ?? new IntentionsScenarioBuilder(_predicateEngine);
    }

    /// <summary>
    /// Runs a dry start wave without committing runtime instances.
    /// </summary>
    public StartWaveResult RunStartWave(
        ValidationCatalog catalog,
        IntentionsSnapshot snapshot,
        IntentionsStartWaveRequest request)
    {
        return RunWaveInternal(
            catalog,
            snapshot,
            request.WaveId,
            request.Seed,
            IntentionsWaveKind.Start,
            registry: null,
            commitService: null,
            assignedScenarioIds: request.AssignedScenarioIds,
            assignedPrimaryByMind: request.AssignedPrimaryByMind,
            distributionCrewBaseline: snapshot.RoundFacts.CrewCount,
            writeWaveContext: false);
    }

    /// <summary>
    /// Runs a start wave and commits each successful build to the runtime registry.
    /// </summary>
    public StartWaveResult RunStartWaveAndCommit(
        ValidationCatalog catalog,
        IntentionsSnapshot snapshot,
        IntentionsStartWaveRequest request,
        IntentionsRuntimeRegistry registry,
        IntentionsCommitService commitService)
    {
        return RunWaveInternal(
            catalog,
            snapshot,
            request.WaveId,
            request.Seed,
            IntentionsWaveKind.Start,
            registry,
            commitService,
            MergeAssignedScenarioIds(request.AssignedScenarioIds, registry),
            MergeAssignedPrimaryByMind(request.AssignedPrimaryByMind, registry),
            snapshot.RoundFacts.CrewCount,
            writeWaveContext: true);
    }

    /// <summary>
    /// Runs a dry refill wave against the current runtime registry.
    /// </summary>
    public StartWaveResult RunRefillWave(
        ValidationCatalog catalog,
        IntentionsSnapshot snapshot,
        IntentionsRefillWaveRequest request,
        IntentionsRuntimeRegistry registry)
    {
        return RunWaveInternal(
            catalog,
            snapshot,
            request.WaveId,
            request.Seed,
            IntentionsWaveKind.Refill,
            registry,
            commitService: null,
            assignedScenarioIds: registry.AssignedScenarioIds,
            assignedPrimaryByMind: RegistryAssignedPrimary(registry),
            distributionCrewBaseline: CalculateRefillBaseline(registry, snapshot),
            writeWaveContext: false);
    }

    /// <summary>
    /// Runs a refill wave and commits each successful build to the runtime registry.
    /// </summary>
    public StartWaveResult RunRefillWaveAndCommit(
        ValidationCatalog catalog,
        IntentionsSnapshot snapshot,
        IntentionsRefillWaveRequest request,
        IntentionsRuntimeRegistry registry,
        IntentionsCommitService commitService)
    {
        return RunWaveInternal(
            catalog,
            snapshot,
            request.WaveId,
            request.Seed,
            IntentionsWaveKind.Refill,
            registry,
            commitService,
            assignedScenarioIds: registry.AssignedScenarioIds,
            assignedPrimaryByMind: RegistryAssignedPrimary(registry),
            distributionCrewBaseline: CalculateRefillBaseline(registry, snapshot),
            writeWaveContext: true);
    }

    /// <summary>
    /// Executes the shared start/refill wave pipeline with optional commit support.
    /// </summary>
    private StartWaveResult RunWaveInternal(
        ValidationCatalog catalog,
        IntentionsSnapshot snapshot,
        int waveId,
        int? requestedSeed,
        IntentionsWaveKind kind,
        IntentionsRuntimeRegistry? registry,
        IntentionsCommitService? commitService,
        IEnumerable<string> assignedScenarioIds,
        IReadOnlyDictionary<EntityUid, IReadOnlyDictionary<string, int>> assignedPrimaryByMind,
        int distributionCrewBaseline,
        bool writeWaveContext)
    {
        var seed = requestedSeed ?? IntentionsDeterministicRandom.BuildSeed(
            waveId,
            snapshot.SnapshotId,
            snapshot.RoundFacts.GameMode,
            snapshot.RoundFacts.StationTime.Ticks);
        var committedIntentions = ImmutableArray.CreateBuilder<IntentionInstance>();
        var context = new DistributionWaveContext(
            waveId,
            snapshot.SnapshotId,
            seed,
            snapshot.RoundFacts.StationTime,
            snapshot.RoundFacts.CrewCount,
            assignedScenarioIds,
            assignedPrimaryByMind,
            kind,
            distributionCrewBaseline);

        context.Status = DistributionWaveStatus.Running;

        var random = new IntentionsDeterministicRandom(seed);
        var orderedCategories = OrderedCategories(catalog).ToList();
        var orderedScenarios = OrderedScenarios(catalog).ToList();

        foreach (var category in orderedCategories)
        {
            context.AllowedCategoryIds.Add(category.ID);

            var desiredQuota = IntentionsQuotaCalculator.CalculateTargetQuota(
                category,
                snapshot.RoundFacts.GameMode,
                context.DistributionCrewBaseline);
            var existingActiveFrozen = kind == IntentionsWaveKind.Refill && registry is not null
                ? CountActiveFrozenScenarios(registry, category.ID)
                : 0;
            var targetQuota = kind == IntentionsWaveKind.Refill
                ? Math.Max(0, desiredQuota - existingActiveFrozen)
                : desiredQuota;
            var effectiveMaxPrimary = IntentionsQuotaCalculator.CalculateEffectiveMaxPrimaryPerMind(category, snapshot.RoundFacts.GameMode);
            var state = new CategoryWaveState(
                category.ID,
                targetQuota,
                effectiveMaxPrimary,
                desiredQuota,
                existingActiveFrozen,
                targetQuota);
            context.CategoryStateById[category.ID] = state;

            if (targetQuota <= 0)
            {
                Exhaust(state, CategoryExhaustReason.QuotaFilled);
                continue;
            }

            if (effectiveMaxPrimary <= 0)
            {
                Exhaust(state, CategoryExhaustReason.Blocked);
                continue;
            }

            // Pools are built once up front so category iteration stays deterministic for the whole wave.
            BuildCategoryScenarioPool(catalog, snapshot, context, category, orderedScenarios, state);

            if (state.CandidateScenarioIds.Count == 0)
                Exhaust(state, CategoryExhaustReason.PoolEmpty);
        }

        var maxIterations = Math.Max(1, orderedScenarios.Count + context.CategoryStateById.Values.Sum(state => state.TargetQuota) + 1);
        var iterations = 0;

        while (HasOpenCategory(context))
        {
            if (++iterations > maxIterations)
            {
                context.Status = DistributionWaveStatus.Failed;
                context.FailureReason = "wave-loop-stalled";
                return new StartWaveResult(context, committedIntentions);
            }

            var state = NextOpenCategory(context);
            if (state is null)
                break;

            var scenario = PickScenario(catalog, state, random);
            if (scenario is null)
            {
                Exhaust(state, CategoryExhaustReason.PoolExhausted);
                continue;
            }

            state.CandidateScenarioIds.Remove(scenario.Template.ID);
            var build = _builder.TryBuildScenario(scenario, snapshot, context, state, random);

            if (!build.IsSuccess)
            {
                AddBuildReject(context, state, scenario, build);

                if (state.CandidateScenarioIds.Count == 0)
                    Exhaust(state, CategoryExhaustReason.PoolExhausted);

                continue;
            }

            if (registry is not null && commitService is not null)
            {
                var commit = commitService.CommitScenarioBuild(build, scenario, catalog, snapshot, context, registry);
                if (!commit.IsSuccess)
                {
                    state.RejectedScenarioIds.Add(scenario.Template.ID);
                    AddRejectCounter(state, commit.FailureReason ?? "commit-failed");
                    context.RejectReasons.Add(new ScenarioRejectReason(
                        scenario.Template.ID,
                        scenario.Template.Category,
                        commit.FailureReason ?? "commit-failed",
                        "Scenario commit failed."));

                    if (!commit.RollbackCompleted)
                    {
                        context.Status = DistributionWaveStatus.Failed;
                        context.FailureReason = commit.FailureReason ?? "commit-failed";
                        return new StartWaveResult(context, committedIntentions);
                    }

                    if (state.CandidateScenarioIds.Count == 0)
                        Exhaust(state, CategoryExhaustReason.PoolExhausted);

                    continue;
                }

                committedIntentions.AddRange(commit.IntentionInstances);
            }

            context.SuccessfulBuilds.Add(build);
            context.AssignedScenarioIds.Add(scenario.Template.ID);
            state.FilledQuota += 1;

            if (build.BuiltSlotsBySlotId.TryGetValue("owner", out var ownerSlot))
                IncrementPrimaryCount(context, ownerSlot.MindId, scenario.Template.Category);

            if (state.FilledQuota >= state.TargetQuota)
                Exhaust(state, CategoryExhaustReason.QuotaFilled);
            else if (state.CandidateScenarioIds.Count == 0)
                Exhaust(state, CategoryExhaustReason.PoolExhausted);
        }

        context.Status = DistributionWaveStatus.Completed;
        if (writeWaveContext && registry is not null)
            registry.SetWaveContext(context, out _);

        return new StartWaveResult(context, committedIntentions);
    }

    /// <summary>
    /// Builds the candidate scenario pool for one category after global predicate filtering.
    /// </summary>
    private void BuildCategoryScenarioPool(
        ValidationCatalog catalog,
        IntentionsSnapshot snapshot,
        DistributionWaveContext context,
        ScenarioCategoryPrototype category,
        IReadOnlyList<ValidatedScenarioTemplate> orderedScenarios,
        CategoryWaveState state)
    {
        foreach (var scenario in orderedScenarios)
        {
            var template = scenario.Template;
            if (template.Category != category.ID)
                continue;

            if (!template.Enabled)
                continue;

            if (context.AssignedScenarioIds.Contains(template.ID))
                continue;

            var predicates = _predicateEngine.EvaluateGlobal(template.GlobalPredicates, snapshot);
            if (!predicates.IsMatch)
            {
                var code = predicates.HasError ? "global-predicates-error" : "global-predicates-failed";
                AddRejectCounter(state, code);
                context.RejectReasons.Add(new ScenarioRejectReason(
                    template.ID,
                    template.Category,
                    code,
                    "Scenario global predicates did not match.",
                    predicateRejectReasons: predicates.RejectReasons));
                continue;
            }

            state.CandidateScenarioIds.Add(template.ID);
        }
    }

    /// <summary>
    /// Returns categories in stable wave order: highest priority first, then declaration order.
    /// </summary>
    private static IEnumerable<ScenarioCategoryPrototype> OrderedCategories(ValidationCatalog catalog)
    {
        var declarationOrder = OrderedIds(catalog.ValidCategoryOrder, catalog.ValidCategories.Keys);
        var orderIndex = declarationOrder
            .Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);

        return declarationOrder
            .Where(catalog.ValidCategories.ContainsKey)
            .Select(id => catalog.ValidCategories[id])
            .OrderByDescending(category => category.Priority)
            .ThenBy(category => orderIndex[category.ID]);
    }

    /// <summary>
    /// Returns scenarios in declaration order with fallback to dictionary order when tests construct catalogs manually.
    /// </summary>
    private static IEnumerable<ValidatedScenarioTemplate> OrderedScenarios(ValidationCatalog catalog)
    {
        return OrderedIds(catalog.ValidScenarioOrder, catalog.ValidScenarios.Keys)
            .Where(catalog.ValidScenarios.ContainsKey)
            .Select(id => catalog.ValidScenarios[id]);
    }

    /// <summary>
    /// Produces a stable id order that honors explicit declaration order when it is available.
    /// </summary>
    private static List<string> OrderedIds(IReadOnlyList<string> explicitOrder, IEnumerable<string> fallbackIds)
    {
        if (explicitOrder.Count > 0)
            return explicitOrder.Concat(fallbackIds.Where(id => !explicitOrder.Contains(id, StringComparer.Ordinal))).ToList();

        return fallbackIds.ToList();
    }

    /// <summary>
    /// Returns whether at least one category can still make progress in the current wave.
    /// </summary>
    private static bool HasOpenCategory(DistributionWaveContext context)
    {
        return context.CategoryStateById.Values.Any(state =>
            !state.IsExhausted
            && state.FilledQuota < state.TargetQuota
            && state.CandidateScenarioIds.Count > 0);
    }

    /// <summary>
    /// Returns the next category that should be processed according to the deterministic category order.
    /// </summary>
    private static CategoryWaveState? NextOpenCategory(DistributionWaveContext context)
    {
        foreach (var categoryId in context.AllowedCategoryIds)
        {
            if (!context.CategoryStateById.TryGetValue(categoryId, out var state))
                continue;

            if (!state.IsExhausted && state.FilledQuota < state.TargetQuota && state.CandidateScenarioIds.Count > 0)
                return state;
        }

        return null;
    }

    /// <summary>
    /// Picks one candidate scenario from the category pool using deterministic weighted selection.
    /// </summary>
    private static ValidatedScenarioTemplate? PickScenario(
        ValidationCatalog catalog,
        CategoryWaveState state,
        IntentionsDeterministicRandom random)
    {
        var scenarios = state.CandidateScenarioIds
            .Where(catalog.ValidScenarios.ContainsKey)
            .Select(id => catalog.ValidScenarios[id])
            .ToList();

        if (scenarios.Count == 0)
            return null;

        return random.PickWeighted(scenarios, scenario => scenario.Template.Weight);
    }

    /// <summary>
    /// Increments the in-wave primary ownership counter for one mind and category.
    /// </summary>
    private static void IncrementPrimaryCount(DistributionWaveContext context, EntityUid mindId, string categoryId)
    {
        if (!context.AssignedPrimaryByMind.TryGetValue(mindId, out var byCategory))
        {
            byCategory = new Dictionary<string, int>(StringComparer.Ordinal);
            context.AssignedPrimaryByMind[mindId] = byCategory;
        }

        byCategory[categoryId] = byCategory.GetValueOrDefault(categoryId) + 1;
    }

    /// <summary>
    /// Marks a category as exhausted for the rest of the current wave.
    /// </summary>
    private static void Exhaust(CategoryWaveState state, CategoryExhaustReason reason)
    {
        state.IsExhausted = true;
        state.ExhaustReason = reason;
    }

    /// <summary>
    /// Increments a category-level rejection counter.
    /// </summary>
    private static void AddRejectCounter(CategoryWaveState state, string code)
    {
        state.RejectCounters[code] = state.RejectCounters.GetValueOrDefault(code) + 1;
    }

    /// <summary>
    /// Records a rejected scenario build in the wave context and category diagnostics.
    /// </summary>
    private static void AddBuildReject(
        DistributionWaveContext context,
        CategoryWaveState state,
        ValidatedScenarioTemplate scenario,
        ScenarioBuildResult build)
    {
        state.RejectedScenarioIds.Add(scenario.Template.ID);
        AddRejectCounter(state, build.FailureReason ?? "scenario-build-failed");
        context.RejectReasons.Add(new ScenarioRejectReason(
            scenario.Template.ID,
            scenario.Template.Category,
            build.FailureReason ?? "scenario-build-failed",
            "Scenario builder failed.",
            slotRejectReasons: build.SlotRejectReasons));
    }

    /// <summary>
    /// Merges already assigned scenario ids from the request and runtime registry.
    /// </summary>
    private static ImmutableHashSet<string> MergeAssignedScenarioIds(
        IEnumerable<string> assignedScenarioIds,
        IntentionsRuntimeRegistry? registry)
    {
        if (registry is null)
            return assignedScenarioIds.ToImmutableHashSet(StringComparer.Ordinal);

        return assignedScenarioIds
            .Concat(registry.AssignedScenarioIds)
            .ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Merges per-mind primary ownership counters from the request and runtime registry.
    /// </summary>
    private static Dictionary<EntityUid, IReadOnlyDictionary<string, int>> MergeAssignedPrimaryByMind(
        IReadOnlyDictionary<EntityUid, IReadOnlyDictionary<string, int>> assignedPrimaryByMind,
        IntentionsRuntimeRegistry? registry)
    {
        var result = new Dictionary<EntityUid, Dictionary<string, int>>();

        foreach (var (mindId, counts) in assignedPrimaryByMind)
            result[mindId] = new Dictionary<string, int>(counts, StringComparer.Ordinal);

        if (registry is not null)
        {
            foreach (var (mindId, counts) in registry.AssignedPrimaryByMind)
            {
                if (!result.TryGetValue(mindId, out var merged))
                {
                    merged = new Dictionary<string, int>(StringComparer.Ordinal);
                    result[mindId] = merged;
                }

                foreach (var (categoryId, count) in counts)
                    merged[categoryId] = merged.GetValueOrDefault(categoryId) + count;
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, int>) pair.Value);
    }

    /// <summary>
    /// Copies assigned-primary counters from the runtime registry into an immutable wave input shape.
    /// </summary>
    private static IReadOnlyDictionary<EntityUid, IReadOnlyDictionary<string, int>> RegistryAssignedPrimary(IntentionsRuntimeRegistry registry)
    {
        return registry.AssignedPrimaryByMind.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, int>) new Dictionary<string, int>(pair.Value, StringComparer.Ordinal));
    }

    /// <summary>
    /// Calculates the refill crew baseline using the maximum of the current and previously committed wave baselines.
    /// </summary>
    private static int CalculateRefillBaseline(IntentionsRuntimeRegistry registry, IntentionsSnapshot snapshot)
    {
        var previousBaseline = registry.WaveContextByWaveId.Values
            .Select(context => context.DistributionCrewBaseline)
            .DefaultIfEmpty(snapshot.RoundFacts.CrewCount)
            .Max();

        return Math.Max(previousBaseline, snapshot.RoundFacts.CrewCount);
    }

    /// <summary>
    /// Counts active and frozen scenarios in the requested category.
    /// </summary>
    private static int CountActiveFrozenScenarios(IntentionsRuntimeRegistry registry, string categoryId)
    {
        return registry.ScenarioByUid.Values.Count(scenario =>
            scenario.CategoryId == categoryId
            && scenario.Status is ScenarioRuntimeStatus.Active or ScenarioRuntimeStatus.Frozen);
    }
}
