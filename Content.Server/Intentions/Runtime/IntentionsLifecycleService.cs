using System.Linq;
using Content.Shared.Intentions.Runtime;
using Robust.Shared.GameObjects;

namespace Content.Server.Intentions.Runtime;

/// <summary>
/// Applies lifecycle state transitions such as missing, frozen, restored, and cancelled runtime scenarios.
/// </summary>
public sealed class IntentionsLifecycleService
{
    /// <summary>
    /// Marks the owner slot as missing without cancelling the scenario immediately.
    /// </summary>
    public LifecycleOperationResult MarkOwnerMissing(
        IntentionsRuntimeRegistry registry,
        ScenarioInstanceUid scenarioUid,
        TimeSpan now,
        string reason)
    {
        if (!registry.ScenarioByUid.TryGetValue(scenarioUid, out var scenario))
            return LifecycleOperationResult.Failure("scenario-not-found", scenarioUid);

        if (scenario.Status == ScenarioRuntimeStatus.Cancelled)
            return LifecycleOperationResult.Success(scenarioUid);

        if (!registry.SlotAssignmentByScenarioAndSlot.TryGetValue((scenarioUid, scenario.OwnerSlotId), out var ownerSlot))
            return LifecycleOperationResult.Failure("owner-slot-not-found", scenarioUid);

        var updatedSlot = ownerSlot.WithStatus(ScenarioSlotAssignmentStatus.Missing, now, reason);
        var updatedScenario = scenario.WithSlotAssignment(updatedSlot);

        registry.ReplaceSlotAssignment(updatedSlot);
        registry.ReplaceScenario(updatedScenario);
        registry.AddMissingOwnerScenarioId(scenarioUid);

        return LifecycleOperationResult.Success(scenarioUid);
    }

    /// <summary>
    /// Freezes a scenario whose owner slot was still missing when refill reconciliation began.
    /// </summary>
    public LifecycleOperationResult FreezeScenarioWithMissingOwner(
        IntentionsRuntimeRegistry registry,
        ScenarioInstanceUid scenarioUid,
        TimeSpan now,
        string reason)
    {
        if (!registry.ScenarioByUid.TryGetValue(scenarioUid, out var scenario))
            return LifecycleOperationResult.Failure("scenario-not-found", scenarioUid);

        if (scenario.Status == ScenarioRuntimeStatus.Cancelled)
            return LifecycleOperationResult.Success(scenarioUid);

        if (!registry.SlotAssignmentByScenarioAndSlot.TryGetValue((scenarioUid, scenario.OwnerSlotId), out var ownerSlot))
            return LifecycleOperationResult.Failure("owner-slot-not-found", scenarioUid);

        var updatedSlot = ownerSlot.Status == ScenarioSlotAssignmentStatus.Missing
            ? ownerSlot
            : ownerSlot.WithStatus(ScenarioSlotAssignmentStatus.Missing, now, reason);
        var updatedScenario = scenario
            .WithSlotAssignment(updatedSlot)
            .WithStatus(ScenarioRuntimeStatus.Frozen, now, reason);

        registry.ReplaceSlotAssignment(updatedSlot);
        registry.ReplaceScenario(updatedScenario);
        registry.AddMissingOwnerScenarioId(scenarioUid);

        return LifecycleOperationResult.Success(scenarioUid);
    }

    /// <summary>
    /// Restores an owner to an active scenario and updates the owner entity across runtime objects.
    /// </summary>
    public LifecycleOperationResult RestoreOwner(
        IntentionsRuntimeRegistry registry,
        ScenarioInstanceUid scenarioUid,
        EntityUid newOwnerEntityUid,
        TimeSpan now,
        string reason)
    {
        if (!registry.ScenarioByUid.TryGetValue(scenarioUid, out var scenario))
            return LifecycleOperationResult.Failure("scenario-not-found", scenarioUid);

        if (scenario.Status == ScenarioRuntimeStatus.Cancelled)
            return LifecycleOperationResult.Failure("scenario-cancelled", scenarioUid);

        if (!registry.SlotAssignmentByScenarioAndSlot.TryGetValue((scenarioUid, scenario.OwnerSlotId), out var ownerSlot))
            return LifecycleOperationResult.Failure("owner-slot-not-found", scenarioUid);

        var updatedSlot = ownerSlot.WithStatusAndOwnerEntity(
            ScenarioSlotAssignmentStatus.Assigned,
            newOwnerEntityUid,
            now,
            reason);
        var updatedScenario = scenario
            .WithSlotAssignment(updatedSlot)
            .WithOwnerEntity(newOwnerEntityUid, now, reason)
            .WithStatus(ScenarioRuntimeStatus.Active, now, reason);

        registry.ReplaceSlotAssignment(updatedSlot);
        registry.ReplaceScenario(updatedScenario);
        registry.RemoveMissingOwnerScenarioId(scenarioUid);

        if (registry.IntentionByUid.TryGetValue(updatedSlot.IntentionUid, out var ownerIntention))
        {
            registry.ReplaceIntention(ownerIntention.WithOwnerEntity(newOwnerEntityUid, now, reason));
        }

        return LifecycleOperationResult.Success(scenarioUid);
    }

    /// <summary>
    /// Cancels a scenario, invalidates its slot assignments, and releases its primary quota hold.
    /// </summary>
    public LifecycleOperationResult CancelScenario(
        IntentionsRuntimeRegistry registry,
        ScenarioInstanceUid scenarioUid,
        TimeSpan now,
        string reason)
    {
        if (!registry.ScenarioByUid.TryGetValue(scenarioUid, out var scenario))
            return LifecycleOperationResult.Failure("scenario-not-found", scenarioUid);

        if (scenario.Status == ScenarioRuntimeStatus.Cancelled)
            return LifecycleOperationResult.Success(scenarioUid);

        var updatedSlots = registry.SlotAssignmentByScenarioAndSlot.Values
            .Where(assignment => assignment.ScenarioUid == scenarioUid)
            .Select(assignment => assignment.WithStatus(ScenarioSlotAssignmentStatus.Invalidated, now, reason))
            .ToArray();

        var updatedScenario = scenario
            .WithSlotAssignments(updatedSlots)
            .WithStatus(ScenarioRuntimeStatus.Cancelled, now, reason);

        foreach (var updatedSlot in updatedSlots)
        {
            registry.ReplaceSlotAssignment(updatedSlot);
        }

        registry.ReplaceScenario(updatedScenario);
        registry.RemoveMissingOwnerScenarioId(scenarioUid);
        registry.DecrementAssignedPrimary(scenario.OwnerMindId, scenario.CategoryId);

        foreach (var intentionUid in registry.GetIntentionUidsForScenario(scenarioUid))
        {
            if (!registry.IntentionByUid.TryGetValue(intentionUid, out var intention))
                continue;

            registry.RemoveHiddenRevealForIntention(intentionUid);
            registry.ReplaceIntention(intention.WithStatus(IntentionRuntimeStatus.Cancelled, now, reason));
        }

        return LifecycleOperationResult.Success(scenarioUid);
    }

    /// <summary>
    /// Runs the refill-time lifecycle pass that upgrades older missing/frozen scenarios before new distribution.
    /// </summary>
    public IReadOnlyList<LifecycleOperationResult> ReconcileBeforeRefill(
        IntentionsRuntimeRegistry registry,
        Func<EntityUid, OwnerAvailabilityResult> availabilityResolver,
        TimeSpan now)
    {
        var results = new List<LifecycleOperationResult>();
        var frozenAtRefillStart = registry.ScenarioByUid.Values
            .Where(scenario => scenario.Status == ScenarioRuntimeStatus.Frozen)
            .Select(scenario => scenario.Uid)
            .ToArray();

        foreach (var scenarioUid in frozenAtRefillStart)
        {
            if (!registry.ScenarioByUid.TryGetValue(scenarioUid, out var scenario)
                || scenario.Status == ScenarioRuntimeStatus.Cancelled)
                continue;

            var availability = availabilityResolver(scenario.OwnerMindId);
            results.Add(ApplyFrozenAvailabilityAtRefill(registry, scenarioUid, availability, now));
        }

        foreach (var scenario in registry.ScenarioByUid.Values.ToArray())
        {
            if (scenario.Status is ScenarioRuntimeStatus.Cancelled or ScenarioRuntimeStatus.Frozen)
                continue;

            if (!registry.SlotAssignmentByScenarioAndSlot.TryGetValue((scenario.Uid, scenario.OwnerSlotId), out var ownerSlot)
                || ownerSlot.Status != ScenarioSlotAssignmentStatus.Missing)
                continue;

            var availability = availabilityResolver(scenario.OwnerMindId);
            results.Add(ApplyMissingOwnerSlotAvailabilityAtRefill(registry, scenario.Uid, availability, now));
        }

        return results;
    }

    /// <summary>
    /// Runs the periodic reconciliation pass that only marks owners as missing or restores them.
    /// </summary>
    public IReadOnlyList<LifecycleOperationResult> ReconcileRuntimeState(
        IntentionsRuntimeRegistry registry,
        Func<EntityUid, OwnerAvailabilityResult> availabilityResolver,
        TimeSpan now)
    {
        var results = new List<LifecycleOperationResult>();
        foreach (var scenario in registry.ScenarioByUid.Values.ToArray())
        {
            if (scenario.Status == ScenarioRuntimeStatus.Cancelled)
                continue;

            var availability = availabilityResolver(scenario.OwnerMindId);
            results.Add(ApplyAvailability(registry, scenario.Uid, availability, now));
        }

        return results;
    }

    /// <summary>
    /// Reconciles all runtime scenarios owned by one mind.
    /// </summary>
    public IReadOnlyList<LifecycleOperationResult> ReconcileMind(
        IntentionsRuntimeRegistry registry,
        EntityUid mindId,
        Func<EntityUid, OwnerAvailabilityResult> availabilityResolver,
        TimeSpan now)
    {
        var availability = availabilityResolver(mindId);
        var results = new List<LifecycleOperationResult>();

        foreach (var scenario in registry.ScenarioByUid.Values.ToArray())
        {
            if (scenario.OwnerMindId != mindId || scenario.Status == ScenarioRuntimeStatus.Cancelled)
                continue;

            results.Add(ApplyAvailability(registry, scenario.Uid, availability, now));
        }

        return results;
    }

    /// <summary>
    /// Applies the normal between-wave availability rules for one scenario owner.
    /// </summary>
    private LifecycleOperationResult ApplyAvailability(
        IntentionsRuntimeRegistry registry,
        ScenarioInstanceUid scenarioUid,
        OwnerAvailabilityResult availability,
        TimeSpan now)
    {
        return availability.Status switch
        {
            OwnerAvailabilityStatus.Available when availability.CurrentOwnerEntityUid is { } ownerEntityUid =>
                RestoreOwner(registry, scenarioUid, ownerEntityUid, now, availability.Reason),
            OwnerAvailabilityStatus.Available =>
                LifecycleOperationResult.Failure("available-owner-missing-entity", scenarioUid),
            OwnerAvailabilityStatus.TemporarilyMissing or OwnerAvailabilityStatus.PermanentlyUnavailable =>
                MarkOwnerMissing(registry, scenarioUid, now, availability.Reason),
            _ => LifecycleOperationResult.Failure("unknown-owner-availability", scenarioUid),
        };
    }

    /// <summary>
    /// Applies refill-time rules to scenarios that were already frozen when the refill wave started.
    /// </summary>
    private LifecycleOperationResult ApplyFrozenAvailabilityAtRefill(
        IntentionsRuntimeRegistry registry,
        ScenarioInstanceUid scenarioUid,
        OwnerAvailabilityResult availability,
        TimeSpan now)
    {
        return availability.Status switch
        {
            OwnerAvailabilityStatus.Available when availability.CurrentOwnerEntityUid is { } ownerEntityUid =>
                RestoreOwner(registry, scenarioUid, ownerEntityUid, now, availability.Reason),
            OwnerAvailabilityStatus.Available =>
                LifecycleOperationResult.Failure("available-owner-missing-entity", scenarioUid),
            OwnerAvailabilityStatus.TemporarilyMissing or OwnerAvailabilityStatus.PermanentlyUnavailable =>
                CancelScenario(registry, scenarioUid, now, availability.Reason),
            _ => LifecycleOperationResult.Failure("unknown-owner-availability", scenarioUid),
        };
    }

    /// <summary>
    /// Applies refill-time rules to active scenarios whose owner slot was already marked missing.
    /// </summary>
    private LifecycleOperationResult ApplyMissingOwnerSlotAvailabilityAtRefill(
        IntentionsRuntimeRegistry registry,
        ScenarioInstanceUid scenarioUid,
        OwnerAvailabilityResult availability,
        TimeSpan now)
    {
        return availability.Status switch
        {
            OwnerAvailabilityStatus.Available when availability.CurrentOwnerEntityUid is { } ownerEntityUid =>
                RestoreOwner(registry, scenarioUid, ownerEntityUid, now, availability.Reason),
            OwnerAvailabilityStatus.Available =>
                LifecycleOperationResult.Failure("available-owner-missing-entity", scenarioUid),
            OwnerAvailabilityStatus.TemporarilyMissing or OwnerAvailabilityStatus.PermanentlyUnavailable =>
                FreezeScenarioWithMissingOwner(registry, scenarioUid, now, availability.Reason),
            _ => LifecycleOperationResult.Failure("unknown-owner-availability", scenarioUid),
        };
    }
}
