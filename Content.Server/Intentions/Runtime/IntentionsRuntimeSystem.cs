using System.Collections.Immutable;
using System.Linq;
using Content.Shared.GameTicking;
using Robust.Shared.GameObjects;

namespace Content.Server.Intentions.Runtime;

/// <summary>
/// Owns the round-scoped authoritative Intentions runtime registry on the server.
/// </summary>
public sealed class IntentionsRuntimeSystem : EntitySystem
{
    private int _nextManualWaveId = -1;

    /// <summary>
    /// Gets the current round-scoped runtime registry.
    /// </summary>
    public IntentionsRuntimeRegistry Registry { get; private set; } = new();

    /// <summary>
    /// Replaces the runtime registry and returns the minds that were touched by the previous registry contents.
    /// </summary>
    public ImmutableArray<EntityUid> ResetRegistry()
    {
        var affectedMindIds = Registry.IntentionIdsByMind.Keys
            .Concat(Registry.IntentionByUid.Values.Select(intention => intention.OwnerMindId))
            .Concat(Registry.SlotAssignmentByScenarioAndSlot.Values.Select(assignment => assignment.MindId))
            .Concat(Registry.ScenarioByUid.Values.Select(scenario => scenario.OwnerMindId))
            .Distinct()
            .ToImmutableArray();

        Registry = new IntentionsRuntimeRegistry();
        return affectedMindIds;
    }

    /// <summary>
    /// Allocates the next debug/manual wave id without disturbing the scheduler-owned positive ids.
    /// </summary>
    public int NextManualWaveId()
    {
        return _nextManualWaveId--;
    }

    /// <summary>
    /// Subscribes to round cleanup so the registry can be reset between rounds.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
    }

    /// <summary>
    /// Replaces the runtime registry when the round is fully cleaned up.
    /// </summary>
    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        ResetRegistry();
        _nextManualWaveId = -1;
    }
}
