using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Waves;
using Robust.Shared.GameObjects;

namespace Content.Server.Intentions.Runtime;

public sealed class IntentionsRuntimeRegistry
{
    private long _nextScenarioUid = 1;
    private long _nextIntentionUid = 1;

    public Dictionary<ScenarioInstanceUid, ScenarioInstance> ScenarioByUid { get; } = new();
    public Dictionary<IntentionInstanceUid, IntentionInstance> IntentionByUid { get; } = new();
    public Dictionary<EntityUid, HashSet<IntentionInstanceUid>> IntentionIdsByMind { get; } = new();
    public Dictionary<IntentionInstanceUid, ScenarioInstanceUid> ScenarioUidByIntentionUid { get; } = new();
    public Dictionary<(ScenarioInstanceUid ScenarioUid, string SlotId), ScenarioSlotAssignment> SlotAssignmentByScenarioAndSlot { get; } = new();
    public HashSet<string> AssignedScenarioIds { get; } = new(StringComparer.Ordinal);
    public Dictionary<EntityUid, Dictionary<string, int>> AssignedPrimaryByMind { get; } = new();
    public Dictionary<int, DistributionWaveContext> WaveContextByWaveId { get; } = new();
    public SortedDictionary<TimeSpan, HashSet<IntentionInstanceUid>> HiddenIntentionsByRevealTime { get; } = new();
    public HashSet<ScenarioInstanceUid> MissingOwnerScenarioIds { get; } = new();

    public ScenarioInstanceUid NextScenarioUid()
    {
        return new ScenarioInstanceUid(_nextScenarioUid++);
    }

    public IntentionInstanceUid NextIntentionUid()
    {
        return new IntentionInstanceUid(_nextIntentionUid++);
    }

    public void AddScenario(ScenarioInstance scenario)
    {
        ScenarioByUid.Add(scenario.Uid, scenario);
    }

    public void RemoveScenario(ScenarioInstanceUid uid)
    {
        ScenarioByUid.Remove(uid);
    }

    public void ReplaceScenario(ScenarioInstance scenario)
    {
        ScenarioByUid[scenario.Uid] = scenario;
    }

    public void AddIntention(IntentionInstance intention)
    {
        IntentionByUid.Add(intention.Uid, intention);
    }

    public void RemoveIntention(IntentionInstanceUid uid)
    {
        IntentionByUid.Remove(uid);
    }

    public void ReplaceIntention(IntentionInstance intention)
    {
        IntentionByUid[intention.Uid] = intention;
    }

    public void AttachIntentionToMind(EntityUid mindId, IntentionInstanceUid intentionUid)
    {
        if (!IntentionIdsByMind.TryGetValue(mindId, out var intentions))
        {
            intentions = [];
            IntentionIdsByMind[mindId] = intentions;
        }

        intentions.Add(intentionUid);
    }

    public void DetachIntentionFromMind(EntityUid mindId, IntentionInstanceUid intentionUid)
    {
        if (!IntentionIdsByMind.TryGetValue(mindId, out var intentions))
            return;

        intentions.Remove(intentionUid);
        if (intentions.Count == 0)
            IntentionIdsByMind.Remove(mindId);
    }

    public void AddScenarioBackReference(IntentionInstanceUid intentionUid, ScenarioInstanceUid scenarioUid)
    {
        ScenarioUidByIntentionUid.Add(intentionUid, scenarioUid);
    }

    public void RemoveScenarioBackReference(IntentionInstanceUid intentionUid)
    {
        ScenarioUidByIntentionUid.Remove(intentionUid);
    }

    public void AddSlotAssignment(ScenarioSlotAssignment assignment)
    {
        SlotAssignmentByScenarioAndSlot.Add((assignment.ScenarioUid, assignment.SlotId), assignment);
    }

    public void RemoveSlotAssignment(ScenarioInstanceUid scenarioUid, string slotId)
    {
        SlotAssignmentByScenarioAndSlot.Remove((scenarioUid, slotId));
    }

    public void ReplaceSlotAssignment(ScenarioSlotAssignment assignment)
    {
        SlotAssignmentByScenarioAndSlot[(assignment.ScenarioUid, assignment.SlotId)] = assignment;
    }

    public void AddAssignedScenarioId(string scenarioTemplateId)
    {
        AssignedScenarioIds.Add(scenarioTemplateId);
    }

    public void RemoveAssignedScenarioId(string scenarioTemplateId)
    {
        AssignedScenarioIds.Remove(scenarioTemplateId);
    }

    public void IncrementAssignedPrimary(EntityUid mindId, string categoryId)
    {
        if (!AssignedPrimaryByMind.TryGetValue(mindId, out var byCategory))
        {
            byCategory = new Dictionary<string, int>(StringComparer.Ordinal);
            AssignedPrimaryByMind[mindId] = byCategory;
        }

        byCategory[categoryId] = byCategory.GetValueOrDefault(categoryId) + 1;
    }

    public void DecrementAssignedPrimary(EntityUid mindId, string categoryId)
    {
        if (!AssignedPrimaryByMind.TryGetValue(mindId, out var byCategory))
            return;

        var next = byCategory.GetValueOrDefault(categoryId) - 1;
        if (next > 0)
        {
            byCategory[categoryId] = next;
            return;
        }

        byCategory.Remove(categoryId);
        if (byCategory.Count == 0)
            AssignedPrimaryByMind.Remove(mindId);
    }

    public void SetWaveContext(DistributionWaveContext context, out DistributionWaveContext? previous)
    {
        WaveContextByWaveId.TryGetValue(context.WaveId, out previous);
        WaveContextByWaveId[context.WaveId] = context;
    }

    public void RestoreWaveContext(int waveId, DistributionWaveContext? previous)
    {
        if (previous is null)
        {
            WaveContextByWaveId.Remove(waveId);
            return;
        }

        WaveContextByWaveId[waveId] = previous;
    }

    public void AddHiddenReveal(TimeSpan revealTime, IntentionInstanceUid intentionUid)
    {
        if (!HiddenIntentionsByRevealTime.TryGetValue(revealTime, out var intentions))
        {
            intentions = [];
            HiddenIntentionsByRevealTime[revealTime] = intentions;
        }

        intentions.Add(intentionUid);
    }

    public void RemoveHiddenReveal(TimeSpan revealTime, IntentionInstanceUid intentionUid)
    {
        if (!HiddenIntentionsByRevealTime.TryGetValue(revealTime, out var intentions))
            return;

        intentions.Remove(intentionUid);
        if (intentions.Count == 0)
            HiddenIntentionsByRevealTime.Remove(revealTime);
    }

    public void RemoveHiddenRevealForIntention(IntentionInstanceUid intentionUid)
    {
        var revealTimes = HiddenIntentionsByRevealTime.Keys.ToArray();
        foreach (var revealTime in revealTimes)
        {
            RemoveHiddenReveal(revealTime, intentionUid);
        }
    }

    public void AddMissingOwnerScenarioId(ScenarioInstanceUid scenarioUid)
    {
        MissingOwnerScenarioIds.Add(scenarioUid);
    }

    public void RemoveMissingOwnerScenarioId(ScenarioInstanceUid scenarioUid)
    {
        MissingOwnerScenarioIds.Remove(scenarioUid);
    }

    public IEnumerable<IntentionInstanceUid> GetIntentionUidsForScenario(ScenarioInstanceUid scenarioUid)
    {
        return ScenarioUidByIntentionUid
            .Where(pair => pair.Value == scenarioUid)
            .Select(pair => pair.Key)
            .ToArray();
    }
}
