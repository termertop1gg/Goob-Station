using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Intentions.Predicates;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.Validation;
using Content.Shared.Intentions.Waves;
using Robust.Shared.GameObjects;

namespace Content.Server.Intentions.Waves;

/// <summary>
/// Pure builder that assembles scenario slots from a snapshot without mutating runtime state.
/// </summary>
public sealed class IntentionsScenarioBuilder
{
    private readonly IntentionsPredicateEngine _predicateEngine;

    /// <summary>
    /// Initializes a scenario builder with the shared predicate engine used for candidate checks.
    /// </summary>
    public IntentionsScenarioBuilder(IntentionsPredicateEngine? predicateEngine = null)
    {
        _predicateEngine = predicateEngine ?? new IntentionsPredicateEngine();
    }

    /// <summary>
    /// Attempts to build every slot for one scenario template using its precomputed slot order.
    /// </summary>
    public ScenarioBuildResult TryBuildScenario(
        ValidatedScenarioTemplate scenario,
        IntentionsSnapshot snapshot,
        DistributionWaveContext waveContext,
        CategoryWaveState categoryState,
        IntentionsDeterministicRandom random)
    {
        var template = scenario.Template;
        var builtSlots = new List<ScenarioSlotBuildResult>();
        var skippedOptionalSlots = new List<string>();
        var slotRejectReasons = new List<SlotRejectReason>();
        var selectedBySlotId = new Dictionary<string, CandidateFacts>(StringComparer.Ordinal);
        var reservedMindIds = new HashSet<EntityUid>();
        var entriesBySlot = template.Entries.ToDictionary(entry => entry.SlotId, StringComparer.Ordinal);

        if (!TryValidateSlotBuildOrder(scenario, entriesBySlot, out var orderFailure))
            return Failure(orderFailure);

        foreach (var slotId in scenario.SlotBuildOrder)
        {
            var entry = entriesBySlot[slotId];

            if (entry.BindToSlot is { } bindToSlot)
            {
                if (!selectedBySlotId.TryGetValue(bindToSlot, out var boundCandidate))
                {
                    var reject = RejectSlot(template.ID, entry, "missing-bind-source", "Bound slot source has not been selected.");
                    slotRejectReasons.Add(reject);

                    if (entry.Required)
                        return Failure("missing-bind-source", slotId);

                    skippedOptionalSlots.Add(slotId);
                    continue;
                }

                selectedBySlotId[slotId] = boundCandidate;
                builtSlots.Add(BuildSlotResult(entry, boundCandidate, ScenarioSlotBuildState.Bound, wasBound: true, boundToSlotId: bindToSlot));
                continue;
            }

            if (!TryCheckCompareSources(template.ID, entry, selectedBySlotId, slotRejectReasons))
            {
                if (entry.Required)
                    return Failure("missing-compare-source", slotId);

                skippedOptionalSlots.Add(slotId);
                continue;
            }

            var allowSameActorMinds = entry.AllowSameActorAs
                .Where(selectedBySlotId.ContainsKey)
                .Select(slot => selectedBySlotId[slot].MindId)
                .ToHashSet();

            var localPool = snapshot.Candidates
                .Where(candidate => !reservedMindIds.Contains(candidate.MindId) || allowSameActorMinds.Contains(candidate.MindId))
                .ToList();

            if (entry.Kind == IntentionsPrototypeConstants.Primary)
            {
                localPool = localPool
                    .Where(candidate => GetPrimaryCount(waveContext, candidate.MindId, template.Category) < categoryState.EffectiveMaxPrimaryPerMind)
                    .ToList();
            }

            if (!TrySelectCandidate(template.ID, entry, snapshot, selectedBySlotId, localPool, random, slotRejectReasons, out var selected))
            {
                slotRejectReasons.Add(RejectSlot(template.ID, entry, "candidate-pool-exhausted", "No candidate matched this slot."));

                if (entry.Required)
                    return Failure("no-candidate-for-required-slot", slotId);

                skippedOptionalSlots.Add(slotId);
                continue;
            }

            selectedBySlotId[slotId] = selected;
            builtSlots.Add(BuildSlotResult(entry, selected, ScenarioSlotBuildState.Assigned));

            if (!reservedMindIds.Contains(selected.MindId))
                reservedMindIds.Add(selected.MindId);
        }

        foreach (var entry in template.Entries)
        {
            if (entry.Required && !selectedBySlotId.ContainsKey(entry.SlotId))
                return Failure("required-slot-not-built", entry.SlotId);
        }

        return ScenarioBuildResult.Success(template.ID, template.Category, builtSlots, skippedOptionalSlots, slotRejectReasons);

        ScenarioBuildResult Failure(string code, string? slotId = null)
        {
            return ScenarioBuildResult.Failure(template.ID, template.Category, code, builtSlots, skippedOptionalSlots, slotRejectReasons);
        }
    }

    /// <summary>
    /// Verifies that the validated slot build order is still structurally usable at runtime.
    /// </summary>
    private static bool TryValidateSlotBuildOrder(
        ValidatedScenarioTemplate scenario,
        IReadOnlyDictionary<string, ScenarioEntry> entriesBySlot,
        out string failureReason)
    {
        failureReason = string.Empty;

        if (scenario.SlotBuildOrder.Count == 0)
        {
            failureReason = "missing-or-invalid-precomputed-build-order";
            return false;
        }

        if (scenario.SlotBuildOrder[0] != "owner")
        {
            failureReason = "missing-or-invalid-precomputed-build-order";
            return false;
        }

        if (scenario.SlotBuildOrder.Any(slotId => !entriesBySlot.ContainsKey(slotId)))
        {
            failureReason = "missing-or-invalid-precomputed-build-order";
            return false;
        }

        if (scenario.SlotBuildOrder.Distinct(StringComparer.Ordinal).Count() != scenario.SlotBuildOrder.Count)
        {
            failureReason = "missing-or-invalid-precomputed-build-order";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Ensures every compare-to slot referenced by the entry has already been selected.
    /// </summary>
    private static bool TryCheckCompareSources(
        string scenarioId,
        ScenarioEntry entry,
        IReadOnlyDictionary<string, CandidateFacts> selectedBySlotId,
        List<SlotRejectReason> slotRejectReasons)
    {
        foreach (var predicate in entry.CandidatePredicates)
        {
            if (predicate.CompareTo is not { } compareTo)
                continue;

            if (selectedBySlotId.ContainsKey(compareTo.SlotId))
                continue;

            slotRejectReasons.Add(RejectSlot(scenarioId, entry, "missing-compare-source", "Compared slot has not been selected."));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Selects one candidate from the current pool using deterministic iteration and predicate checks.
    /// </summary>
    private bool TrySelectCandidate(
        string scenarioId,
        ScenarioEntry entry,
        IntentionsSnapshot snapshot,
        IReadOnlyDictionary<string, CandidateFacts> selectedBySlotId,
        List<CandidateFacts> localPool,
        IntentionsDeterministicRandom random,
        List<SlotRejectReason> slotRejectReasons,
        out CandidateFacts selected)
    {
        while (localPool.Count > 0)
        {
            var index = random.Next(localPool.Count);
            var candidate = localPool[index];
            localPool.RemoveAt(index);

            var result = _predicateEngine.EvaluateCandidate(entry.CandidatePredicates, candidate, snapshot, selectedBySlotId);
            if (result.IsMatch)
            {
                selected = candidate;
                return true;
            }

            slotRejectReasons.Add(new SlotRejectReason(
                scenarioId,
                entry.SlotId,
                result.HasError ? "candidate-predicates-error" : "candidate-predicates-failed",
                "Candidate predicates did not match.",
                candidate.MindId,
                result.RejectReasons));
        }

        selected = default!;
        return false;
    }

    /// <summary>
    /// Creates the runtime-agnostic slot build result for one selected candidate.
    /// </summary>
    private static ScenarioSlotBuildResult BuildSlotResult(
        ScenarioEntry entry,
        CandidateFacts candidate,
        ScenarioSlotBuildState state,
        bool wasBound = false,
        string? boundToSlotId = null)
    {
        return new ScenarioSlotBuildResult(
            entry.SlotId,
            entry.Kind,
            entry.IntentionId,
            candidate.MindId,
            candidate.OwnerEntityUid,
            entry.Required,
            state,
            wasBound,
            boundToSlotId);
    }

    /// <summary>
    /// Returns how many primary scenarios the mind already owns in the current category.
    /// </summary>
    private static int GetPrimaryCount(DistributionWaveContext waveContext, EntityUid mindId, string categoryId)
    {
        if (!waveContext.AssignedPrimaryByMind.TryGetValue(mindId, out var byCategory))
            return 0;

        return byCategory.GetValueOrDefault(categoryId);
    }

    /// <summary>
    /// Creates a standardized slot rejection record.
    /// </summary>
    private static SlotRejectReason RejectSlot(string scenarioId, ScenarioEntry entry, string code, string message)
    {
        return new SlotRejectReason(scenarioId, entry.SlotId, code, message);
    }
}
