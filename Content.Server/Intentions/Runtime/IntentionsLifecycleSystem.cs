using System.Collections.Generic;
using System.Linq;
using Content.Server.GameTicking;
using Content.Server.Intentions.UI;
using Content.Server.Station.Systems;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Roles.Jobs;
using Content.Shared.Silicons.Borgs.Components;
using Content.Shared.Silicons.StationAi;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.Intentions.Runtime;

/// <summary>
/// Owns lifecycle reconciliation timing and propagates runtime changes to open Intentions UIs.
/// </summary>
public sealed class IntentionsLifecycleSystem : EntitySystem
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedJobSystem _jobs = default!;
    [Dependency] private readonly StationJobsSystem _stationJobs = default!;
    [Dependency] private readonly IntentionsRuntimeSystem _runtime = default!;
    [Dependency] private readonly IntentionsUiSystem _ui = default!;

    private readonly IntentionsLifecycleService _lifecycle = new();
    private TimeSpan _nextReconcile;

    /// <summary>
    /// Subscribes to mind and player-session events that can change owner availability.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MindGotRemovedEvent>(OnMindGotRemoved);
        SubscribeLocalEvent<MindGotAddedEvent>(OnMindGotAdded);
        _player.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    /// <summary>
    /// Removes event subscriptions owned by this system.
    /// </summary>
    public override void Shutdown()
    {
        base.Shutdown();
        _player.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    /// <summary>
    /// Runs the periodic availability pass while the round is active.
    /// </summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_gameTicker.RunLevel != GameRunLevel.InRound)
            return;

        if (_timing.CurTime < _nextReconcile)
            return;

        _nextReconcile = _timing.CurTime + ReconcileInterval;
        var results = _lifecycle.ReconcileRuntimeState(_runtime.Registry, ResolveOwnerAvailability, _gameTicker.RoundDuration());
        RefreshAffectedMindUis(results);
    }

    /// <summary>
    /// Performs the refill-time lifecycle pre-pass immediately and refreshes any affected UIs.
    /// </summary>
    public IReadOnlyList<LifecycleOperationResult> ReconcileBeforeRefillNow()
    {
        var results = _lifecycle.ReconcileBeforeRefill(_runtime.Registry, ResolveOwnerAvailability, _gameTicker.RoundDuration());
        RefreshAffectedMindUis(results);
        return results;
    }

    /// <summary>
    /// Reconciles every scenario owned by a removed mind.
    /// </summary>
    private void OnMindGotRemoved(MindGotRemovedEvent ev)
    {
        ReconcileMind(ev.Mind.Owner);
    }

    /// <summary>
    /// Reconciles every scenario owned by a newly reattached mind.
    /// </summary>
    private void OnMindGotAdded(MindGotAddedEvent ev)
    {
        ReconcileMind(ev.Mind.Owner);
    }

    /// <summary>
    /// Reconciles the owner mind when its session availability changes.
    /// </summary>
    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (_mind.TryGetMind(args.Session.UserId, out var mindId, out _))
            ReconcileMind(mindId.Value);
    }

    /// <summary>
    /// Reconciles all runtime scenarios owned by one mind id.
    /// </summary>
    private void ReconcileMind(EntityUid mindId)
    {
        var results = _lifecycle.ReconcileMind(_runtime.Registry, mindId, ResolveOwnerAvailability, _gameTicker.RoundDuration());
        RefreshAffectedMindUis(results);
    }

    /// <summary>
    /// Resolves the current availability state for one owner mind using live SS14 session and body data.
    /// </summary>
    private OwnerAvailabilityResult ResolveOwnerAvailability(EntityUid mindId)
    {
        if (!TryComp<MindComponent>(mindId, out var mind))
            return OwnerAvailabilityResult.PermanentlyUnavailable(mindId, "mind-not-found");

        if (mind.TimeOfDeath is not null)
            return OwnerAvailabilityResult.PermanentlyUnavailable(mindId, "mind-dead");

        if (mind.UserId is not { } userId)
            return OwnerAvailabilityResult.PermanentlyUnavailable(mindId, "owner-has-no-user");

        if (!_player.TryGetSessionById(userId, out var session) || session.Status != SessionStatus.InGame)
            return OwnerAvailabilityResult.TemporarilyMissing(mindId, "owner-session-not-in-game");

        if (mind.OwnedEntity is not { } owned || TerminatingOrDeleted(owned))
            return OwnerAvailabilityResult.TemporarilyMissing(mindId, "owner-entity-missing");

        if (!IsEligibleActiveCrewOwner(mindId, userId, owned, out var reason))
            return OwnerAvailabilityResult.TemporarilyMissing(mindId, reason);

        return OwnerAvailabilityResult.Available(mindId, owned);
    }

    /// <summary>
    /// Checks whether the owner's current body still counts as an active crew participant for Intentions.
    /// </summary>
    private bool IsEligibleActiveCrewOwner(EntityUid mindId, NetUserId userId, EntityUid ownedEntity, out string reason)
    {
        if (HasComp<BorgChassisComponent>(ownedEntity) || HasComp<StationAiHeldComponent>(ownedEntity))
        {
            reason = "owner-not-active-crew-body";
            return false;
        }

        if (!_jobs.MindTryGetJobId(mindId, out var currentJob) || currentJob is not { } actualJob)
        {
            reason = "owner-has-no-crew-job";
            return false;
        }

        if (!_stationJobs.PlayerHoldsJobOnAnyStation(userId, actualJob))
        {
            reason = "owner-not-holding-crew-job-slot";
            return false;
        }

        reason = "owner-available";
        return true;
    }

    /// <summary>
    /// Collects every mind whose open Intention window should refresh after lifecycle-only changes.
    /// </summary>
    internal static HashSet<EntityUid> CollectAffectedMindIds(
        IReadOnlyList<LifecycleOperationResult> results,
        IntentionsRuntimeRegistry registry)
    {
        var affectedMindIds = new HashSet<EntityUid>();

        foreach (var result in results)
        {
            if (!result.IsSuccess || result.ScenarioUid is not { } scenarioUid)
                continue;

            foreach (var assignment in registry.SlotAssignmentByScenarioAndSlot.Values.Where(assignment => assignment.ScenarioUid == scenarioUid))
            {
                affectedMindIds.Add(assignment.MindId);
            }

            foreach (var intentionUid in registry.GetIntentionUidsForScenario(scenarioUid))
            {
                if (registry.IntentionByUid.TryGetValue(intentionUid, out var intention))
                    affectedMindIds.Add(intention.OwnerMindId);
            }
        }

        return affectedMindIds;
    }

    /// <summary>
    /// Refreshes all open Intentions windows linked to the scenarios changed by lifecycle operations.
    /// </summary>
    private void RefreshAffectedMindUis(IReadOnlyList<LifecycleOperationResult> results)
    {
        foreach (var mindId in CollectAffectedMindIds(results, _runtime.Registry))
        {
            _ui.RefreshMind(mindId);
        }
    }
}
