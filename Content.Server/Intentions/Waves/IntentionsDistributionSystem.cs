using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.Intentions.Runtime;
using Content.Server.Intentions.Snapshot;
using Content.Server.Intentions.UI;
using Content.Server.Intentions.Validation;
using Content.Shared.GameTicking;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.Validation;
using Content.Shared.Intentions.Waves;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Server.Player;

namespace Content.Server.Intentions.Waves;

/// <summary>
/// Describes the scheduler state for automatic start and refill waves.
/// </summary>
public readonly record struct IntentionsDistributionScheduleStatus(
    bool StartWaveFinished,
    bool StartWavePending,
    int NextWaveId,
    TimeSpan CurrentTime,
    TimeSpan? NextStartWaveAttempt,
    TimeSpan? NextRefillWaveAttempt)
{
    /// <summary>
    /// Gets the remaining time until the next scheduled start-wave attempt, if any.
    /// </summary>
    public TimeSpan? RemainingToStartWave =>
        NextStartWaveAttempt is { } startTime
            ? startTime > CurrentTime ? startTime - CurrentTime : TimeSpan.Zero
            : null;

    /// <summary>
    /// Gets the remaining time until the next scheduled refill-wave attempt, if any.
    /// </summary>
    public TimeSpan? RemainingToRefillWave =>
        NextRefillWaveAttempt is { } refillTime
            ? refillTime > CurrentTime ? refillTime - CurrentTime : TimeSpan.Zero
            : null;
}

/// <summary>
/// Describes how a manual scheduler-aware wave run request completed.
/// </summary>
public enum IntentionsDistributionManualRunOutcome : byte
{
    Rejected,
    Executed,
    RetryScheduled,
}

/// <summary>
/// Contains the result of a scheduler-aware manual wave run request.
/// </summary>
public readonly record struct IntentionsDistributionManualRunResult(
    IntentionsDistributionManualRunOutcome Outcome,
    string Message,
    StartWaveResult? WaveResult,
    IntentionsDistributionScheduleStatus ScheduleStatus)
{
    /// <summary>
    /// Indicates whether the scheduler actually executed the requested wave.
    /// </summary>
    public bool WasExecuted => Outcome == IntentionsDistributionManualRunOutcome.Executed;
}

/// <summary>
/// Owns automatic Intentions start/refill scheduling and the official scheduler-aware manual wave entry points.
/// </summary>
public sealed class IntentionsDistributionSystem : EntitySystem
{
    private static readonly TimeSpan StartWaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefillRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RefillMinDelay = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan RefillMaxDelay = TimeSpan.FromMinutes(15);
    private static readonly SoundSpecifier AssignmentSound =
        new SoundPathSpecifier("/Audio/Intentions/vintage_alert_notification.ogg", AudioParams.Default.WithVolume(-2f));
    private const int MaxEmptySnapshotAttempts = 10;
    private const string LogSawmillName = "intentions.distribution";

    [Dependency] private readonly ILogManager _log = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IntentionsRuntimeSystem _runtime = default!;
    [Dependency] private readonly IntentionsLifecycleSystem _lifecycle = default!;
    [Dependency] private readonly IntentionsSnapshotService _snapshot = default!;
    [Dependency] private readonly IntentionsUiSystem _ui = default!;

    private readonly IntentionsWaveOrchestrator _orchestrator = new();
    private readonly IntentionsCommitService _commitService = new();
    private readonly IntentionsAssignmentNotificationService _notifications = new();
    private ISawmill _sawmill = default!;

    private bool _pendingStartWave;
    private bool _startWaveFinished;
    private int _emptySnapshotAttempts;
    private int _nextWaveId = 1;
    private TimeSpan _nextStartWaveAttempt;
    private TimeSpan? _nextRefillWaveAttempt;

    /// <summary>
    /// Subscribes to round lifecycle events that can start or reset automatic distribution.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _log.GetSawmill(LogSawmillName);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnGameRunLevelChanged);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    /// <summary>
    /// Drives scheduled start/refill attempts while the round is active.
    /// </summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_gameTicker.RunLevel != GameRunLevel.InRound)
        {
            return;
        }

        if (!_startWaveFinished && !_pendingStartWave)
        {
            ScheduleStartWave("in-round fallback");
        }

        if (_pendingStartWave && !_startWaveFinished && _timing.CurTime >= _nextStartWaveAttempt)
        {
            ExecuteStartWave(null, "scheduled");
            return;
        }

        if (_startWaveFinished
            && _nextRefillWaveAttempt is { } refillTime
            && _timing.CurTime >= refillTime)
        {
            ExecuteRefillWave(null, "scheduled");
        }
    }

    /// <summary>
    /// Returns the current scheduler status for debug tooling.
    /// </summary>
    public IntentionsDistributionScheduleStatus GetScheduleStatus()
    {
        return new IntentionsDistributionScheduleStatus(
            _startWaveFinished,
            _pendingStartWave,
            _nextWaveId,
            _timing.CurTime,
            _pendingStartWave ? _nextStartWaveAttempt : null,
            _nextRefillWaveAttempt);
    }

    /// <summary>
    /// Executes the official start wave immediately through the scheduler-aware path.
    /// </summary>
    public IntentionsDistributionManualRunResult RunStartWaveNow(int? seed = null)
    {
        var scheduleStatus = GetScheduleStatus();
        if (!CanRunStartManually(scheduleStatus, _gameTicker.RunLevel, out var rejectionMessage))
            return new IntentionsDistributionManualRunResult(IntentionsDistributionManualRunOutcome.Rejected, rejectionMessage, null, scheduleStatus);

        _pendingStartWave = false;
        _nextStartWaveAttempt = TimeSpan.Zero;
        return ExecuteStartWave(seed, "manual");
    }

    /// <summary>
    /// Executes the official refill wave immediately through the scheduler-aware path.
    /// </summary>
    public IntentionsDistributionManualRunResult RunRefillWaveNow(int? seed = null)
    {
        var scheduleStatus = GetScheduleStatus();
        if (!CanRunRefillManually(scheduleStatus, _gameTicker.RunLevel, out var rejectionMessage))
            return new IntentionsDistributionManualRunResult(IntentionsDistributionManualRunOutcome.Rejected, rejectionMessage, null, scheduleStatus);

        _nextRefillWaveAttempt = null;
        return ExecuteRefillWave(seed, "manual");
    }

    /// <summary>
    /// Returns whether the scheduler currently allows a manual start-wave run.
    /// </summary>
    internal static bool CanRunStartManually(
        IntentionsDistributionScheduleStatus scheduleStatus,
        GameRunLevel runLevel,
        out string message)
    {
        if (runLevel != GameRunLevel.InRound)
        {
            message = "Start wave can only be run while the round is InRound.";
            return false;
        }

        if (scheduleStatus.StartWaveFinished)
        {
            message = "Start wave already finished for this round.";
            return false;
        }

        message = scheduleStatus.StartWavePending
            ? "Start wave is pending and will be executed immediately."
            : "Start wave will be executed immediately.";
        return true;
    }

    /// <summary>
    /// Returns whether the scheduler currently allows a manual refill-wave run.
    /// </summary>
    internal static bool CanRunRefillManually(
        IntentionsDistributionScheduleStatus scheduleStatus,
        GameRunLevel runLevel,
        out string message)
    {
        if (runLevel != GameRunLevel.InRound)
        {
            message = "Refill wave can only be run while the round is InRound.";
            return false;
        }

        if (!scheduleStatus.StartWaveFinished)
        {
            message = "Refill wave is unavailable until the start wave finishes.";
            return false;
        }

        message = scheduleStatus.RemainingToRefillWave is not null
            ? "Refill wave will be executed immediately and the previous refill timer will be replaced."
            : "Refill wave will be executed immediately.";
        return true;
    }

    /// <summary>
    /// Resets automatic scheduling when the round is fully cleaned up.
    /// </summary>
    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _pendingStartWave = false;
        _startWaveFinished = false;
        _emptySnapshotAttempts = 0;
        _nextWaveId = 1;
        _nextStartWaveAttempt = TimeSpan.Zero;
        _nextRefillWaveAttempt = null;
    }

    /// <summary>
    /// Schedules the start wave once the round reaches the active in-round state.
    /// </summary>
    private void OnGameRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New == GameRunLevel.InRound)
            ScheduleStartWave("round entered InRound");
    }

    /// <summary>
    /// Schedules the start wave after player spawn completes.
    /// </summary>
    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        ScheduleStartWave($"player spawn mob={ev.Mob.Id} lateJoin={ev.LateJoin}");
    }

    /// <summary>
    /// Executes the official start-wave pipeline, including snapshot retries and first refill scheduling.
    /// </summary>
    private IntentionsDistributionManualRunResult ExecuteStartWave(int? seed, string trigger)
    {
        _pendingStartWave = false;

        var waveId = _nextWaveId;
        _sawmill.Info($"Intentions start wave starting. waveId={waveId} trigger={trigger} seed={(seed?.ToString() ?? "auto")}");

        var snapshotResult = _snapshot.BuildSnapshot(IntentionsSnapshotRequest.Start(waveId));
        if (!snapshotResult.IsSuccess || snapshotResult.Snapshot is not { } snapshot)
        {
            _sawmill.Warning($"Intentions start wave snapshot failed. waveId={waveId} issues={snapshotResult.Issues.Length}");
            ScheduleRetry();
            return new IntentionsDistributionManualRunResult(
                IntentionsDistributionManualRunOutcome.RetryScheduled,
                $"Intentions start wave did not execute. waveId={waveId} snapshot failed; retry scheduled.",
                null,
                GetScheduleStatus());
        }

        if (snapshot.Candidates.Length == 0 && ++_emptySnapshotAttempts < MaxEmptySnapshotAttempts)
        {
            _sawmill.Warning($"Intentions start wave snapshot has no candidates. waveId={waveId} attempt={_emptySnapshotAttempts}/{MaxEmptySnapshotAttempts}");
            ScheduleRetry();
            return new IntentionsDistributionManualRunResult(
                IntentionsDistributionManualRunOutcome.RetryScheduled,
                $"Intentions start wave did not execute. waveId={waveId} snapshot had no candidates; retry scheduled.",
                null,
                GetScheduleStatus());
        }

        var catalog = new IntentionsValidationService(_prototypes).ValidateAll();
        var result = _orchestrator.RunStartWaveAndCommit(
            catalog,
            snapshot,
            new IntentionsStartWaveRequest(waveId, seed),
            _runtime.Registry,
            _commitService);

        NotifyAssignedCharacters(result);
        NotifyAssignedIntentions(catalog, result);
        RefreshAssignedMindUis(result);

        _sawmill.Info($"Intentions start wave finished. waveId={waveId} status={result.Context.Status} successfulBuilds={result.Context.SuccessfulBuilds.Count} rejectReasons={result.Context.RejectReasons.Count}");

        _nextWaveId++;
        _startWaveFinished = true;
        ScheduleNextRefillWave();

        return new IntentionsDistributionManualRunResult(
            IntentionsDistributionManualRunOutcome.Executed,
            $"Intentions start wave finished. waveId={waveId} status={result.Context.Status} successfulBuilds={result.Context.SuccessfulBuilds.Count} rejectReasons={result.Context.RejectReasons.Count}",
            result,
            GetScheduleStatus());
    }

    /// <summary>
    /// Executes the official refill-wave pipeline, including lifecycle pre-pass and retry scheduling.
    /// </summary>
    private IntentionsDistributionManualRunResult ExecuteRefillWave(int? seed, string trigger)
    {
        _nextRefillWaveAttempt = null;

        var waveId = _nextWaveId;
        _sawmill.Info($"Intentions refill wave starting. waveId={waveId} trigger={trigger} seed={(seed?.ToString() ?? "auto")}");

        var lifecycleResults = _lifecycle.ReconcileBeforeRefillNow();
        var lifecycleFailures = lifecycleResults.Count(result => !result.IsSuccess);
        if (lifecycleFailures > 0)
            _sawmill.Warning($"Intentions refill lifecycle reconciliation had failures. waveId={waveId} failures={lifecycleFailures}");

        var snapshotResult = _snapshot.BuildSnapshot(IntentionsSnapshotRequest.Refill(waveId));
        if (!snapshotResult.IsSuccess || snapshotResult.Snapshot is not { } snapshot)
        {
            _sawmill.Warning($"Intentions refill wave snapshot failed. waveId={waveId} issues={snapshotResult.Issues.Length}");
            ScheduleRefillRetry();
            return new IntentionsDistributionManualRunResult(
                IntentionsDistributionManualRunOutcome.RetryScheduled,
                $"Intentions refill wave did not execute. waveId={waveId} snapshot failed; retry scheduled.",
                null,
                GetScheduleStatus());
        }

        var catalog = new IntentionsValidationService(_prototypes).ValidateAll();
        var result = _orchestrator.RunRefillWaveAndCommit(
            catalog,
            snapshot,
            new IntentionsRefillWaveRequest(waveId, seed),
            _runtime.Registry,
            _commitService);

        NotifyAssignedCharacters(result);
        NotifyAssignedIntentions(catalog, result);
        RefreshAssignedMindUis(result);

        _sawmill.Info($"Intentions refill wave finished. waveId={waveId} status={result.Context.Status} successfulBuilds={result.Context.SuccessfulBuilds.Count} rejectReasons={result.Context.RejectReasons.Count}");

        _nextWaveId++;
        ScheduleNextRefillWave();

        return new IntentionsDistributionManualRunResult(
            IntentionsDistributionManualRunOutcome.Executed,
            $"Intentions refill wave finished. waveId={waveId} status={result.Context.Status} successfulBuilds={result.Context.SuccessfulBuilds.Count} rejectReasons={result.Context.RejectReasons.Count}",
            result,
            GetScheduleStatus());
    }

    /// <summary>
    /// Schedules another delayed attempt for the start wave.
    /// </summary>
    private void ScheduleRetry()
    {
        ScheduleStartWave("start wave retry");
    }

    /// <summary>
    /// Schedules another delayed attempt for the refill wave.
    /// </summary>
    private void ScheduleRefillRetry()
    {
        _nextRefillWaveAttempt = _timing.CurTime + RefillRetryDelay;
        _sawmill.Info($"Intentions refill wave retry scheduled. waveId={_nextWaveId} delay={RefillRetryDelay:g}");
    }

    /// <summary>
    /// Schedules the next automatic refill wave using the configured random delay window.
    /// </summary>
    private void ScheduleNextRefillWave()
    {
        var delay = TimeSpan.FromSeconds(_random.NextFloat(
            (float) RefillMinDelay.TotalSeconds,
            (float) RefillMaxDelay.TotalSeconds));

        _nextRefillWaveAttempt = _timing.CurTime + delay;
        _sawmill.Info($"Intentions next refill wave scheduled. waveId={_nextWaveId} delay={delay:g}");
    }

    /// <summary>
    /// Marks the start wave as pending and records the next attempt time.
    /// </summary>
    private void ScheduleStartWave(string reason)
    {
        if (_startWaveFinished)
            return;

        var wasPending = _pendingStartWave;
        var attemptTime = _timing.CurTime + StartWaveDelay;
        _pendingStartWave = true;

        if (!wasPending || attemptTime < _nextStartWaveAttempt)
            _nextStartWaveAttempt = attemptTime;

        _sawmill.Info($"Intentions start wave scheduled. waveId={_nextWaveId} delay={StartWaveDelay:g} reason=\"{reason}\"");
    }

    /// <summary>
    /// Plays the assignment sound once for every unique mind that received a new runtime intention.
    /// </summary>
    private void NotifyAssignedCharacters(StartWaveResult result)
    {
        var notifiedMinds = new HashSet<EntityUid>();
        foreach (var slot in result.Context.SuccessfulBuilds.SelectMany(build => build.BuiltSlots))
        {
            if (!notifiedMinds.Add(slot.MindId) || !Exists(slot.OwnerEntityUid))
                continue;

            _audio.PlayEntity(AssignmentSound, slot.OwnerEntityUid, slot.OwnerEntityUid);
        }
    }

    /// <summary>
    /// Sends one private server-chat summary to each player that received new intentions in this commit batch.
    /// </summary>
    private void NotifyAssignedIntentions(ValidationCatalog catalog, StartWaveResult result)
    {
        if (result.CommittedIntentions.IsEmpty)
            return;

        _notifications.DispatchNotifications(catalog, result.CommittedIntentions, EntityManager, _player, _chat);
    }

    /// <summary>
    /// Refreshes the UI for every unique mind that received a newly committed slot.
    /// </summary>
    private void RefreshAssignedMindUis(StartWaveResult result)
    {
        foreach (var mindId in result.Context.SuccessfulBuilds
                     .SelectMany(build => build.BuiltSlots)
                     .Select(slot => slot.MindId)
                     .Distinct())
        {
            _ui.RefreshMind(mindId);
        }
    }
}
