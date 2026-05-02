using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.Validation;
using Content.Shared.Intentions.Waves;

namespace Content.Server.Intentions.Runtime;

/// <summary>
/// Converts successful dry-run builds into committed runtime instances and updates registry indexes atomically.
/// </summary>
public sealed class IntentionsCommitService
{
    private readonly Func<string, IReadOnlyDictionary<string, string>, string?> _locResolver;
    private readonly Func<CommitFailurePoint, bool>? _failureHook;

    /// <summary>
    /// Initializes a commit service that resolves localized copyable text through the default resolver.
    /// </summary>
    public IntentionsCommitService(Func<string, IReadOnlyDictionary<string, string>, string?>? locResolver = null)
        : this(locResolver, failureHook: null)
    {
    }

    /// <summary>
    /// Initializes a commit service with an injected failure hook used by rollback tests.
    /// </summary>
    internal IntentionsCommitService(
        Func<CommitFailurePoint, bool> failureHook,
        Func<string, IReadOnlyDictionary<string, string>, string?>? locResolver = null)
        : this(locResolver, failureHook)
    {
    }

    private IntentionsCommitService(
        Func<string, IReadOnlyDictionary<string, string>, string?>? locResolver,
        Func<CommitFailurePoint, bool>? failureHook)
    {
        _locResolver = locResolver ?? DefaultLocResolver;
        _failureHook = failureHook;
    }

    /// <summary>
    /// Commits one successful scenario build into the runtime registry or rolls back all partial writes on failure.
    /// </summary>
    public CommitScenarioBuildResult CommitScenarioBuild(
        ScenarioBuildResult build,
        ValidatedScenarioTemplate scenario,
        ValidationCatalog catalog,
        IntentionsSnapshot snapshot,
        DistributionWaveContext waveContext,
        IntentionsRuntimeRegistry registry)
    {
        if (!TryPrepareCommit(build, scenario, catalog, snapshot, waveContext, registry, out var prepared, out var failureReason))
            return CommitScenarioBuildResult.Failure(failureReason, rollbackCompleted: true);

        var rollbackActions = new List<Action>();

        try
        {
            registry.AddScenario(prepared.Scenario);
            rollbackActions.Add(() => registry.RemoveScenario(prepared.Scenario.Uid));
            MaybeFail(CommitFailurePoint.AfterScenarioSaved);

            foreach (var intention in prepared.Intentions)
            {
                registry.AddIntention(intention);
                rollbackActions.Add(() => registry.RemoveIntention(intention.Uid));
                MaybeFail(CommitFailurePoint.AfterIntentionSaved);

                // Index registrations happen after the runtime objects exist so rollback can unwind them in reverse order.
                registry.AttachIntentionToMind(intention.OwnerMindId, intention.Uid);
                rollbackActions.Add(() => registry.DetachIntentionFromMind(intention.OwnerMindId, intention.Uid));
                MaybeFail(CommitFailurePoint.AfterMindAttached);

                registry.AddScenarioBackReference(intention.Uid, prepared.Scenario.Uid);
                rollbackActions.Add(() => registry.RemoveScenarioBackReference(intention.Uid));
                MaybeFail(CommitFailurePoint.AfterBackReferenceIndexed);
            }

            foreach (var assignment in prepared.SlotAssignments)
            {
                registry.AddSlotAssignment(assignment);
                rollbackActions.Add(() => registry.RemoveSlotAssignment(assignment.ScenarioUid, assignment.SlotId));
                MaybeFail(CommitFailurePoint.AfterSlotAssignmentIndexed);
            }

            foreach (var intention in prepared.Intentions)
            {
                if (intention.IsHidden && intention.RevealMode == IntentionRevealMode.Timer && intention.RevealedAtRoundTime is { } revealTime)
                {
                    registry.AddHiddenReveal(revealTime, intention.Uid);
                    rollbackActions.Add(() => registry.RemoveHiddenReveal(revealTime, intention.Uid));
                    MaybeFail(CommitFailurePoint.AfterRevealIndexed);
                }
            }

            registry.AddAssignedScenarioId(prepared.Scenario.ScenarioTemplateId);
            rollbackActions.Add(() => registry.RemoveAssignedScenarioId(prepared.Scenario.ScenarioTemplateId));
            MaybeFail(CommitFailurePoint.AfterAssignedScenarioIndexed);

            registry.IncrementAssignedPrimary(prepared.Scenario.OwnerMindId, prepared.Scenario.CategoryId);
            rollbackActions.Add(() => registry.DecrementAssignedPrimary(prepared.Scenario.OwnerMindId, prepared.Scenario.CategoryId));
            MaybeFail(CommitFailurePoint.AfterAssignedPrimaryIndexed);

            registry.SetWaveContext(waveContext, out var previousWaveContext);
            rollbackActions.Add(() => registry.RestoreWaveContext(waveContext.WaveId, previousWaveContext));
            MaybeFail(CommitFailurePoint.AfterWaveContextIndexed);

            return CommitScenarioBuildResult.Success(prepared.Scenario, prepared.Intentions);
        }
        catch (CommitFailureException ex)
        {
            var rollbackCompleted = Rollback(rollbackActions);
            return CommitScenarioBuildResult.Failure(ex.Code, rollbackCompleted);
        }
        catch
        {
            var rollbackCompleted = Rollback(rollbackActions);
            return CommitScenarioBuildResult.Failure("commit-failed", rollbackCompleted);
        }
    }

    /// <summary>
    /// Validates the build against runtime state and materializes all instances before mutating the registry.
    /// </summary>
    private bool TryPrepareCommit(
        ScenarioBuildResult build,
        ValidatedScenarioTemplate scenario,
        ValidationCatalog catalog,
        IntentionsSnapshot snapshot,
        DistributionWaveContext waveContext,
        IntentionsRuntimeRegistry registry,
        out PreparedCommit prepared,
        out string failureReason)
    {
        prepared = default!;
        failureReason = string.Empty;

        if (!build.IsSuccess)
            return Fail(out failureReason, "build-result-is-not-successful");

        var template = scenario.Template;
        if (build.ScenarioTemplateId != template.ID || build.CategoryId != template.Category)
            return Fail(out failureReason, "build-template-mismatch");

        if (registry.AssignedScenarioIds.Contains(template.ID))
            return Fail(out failureReason, "scenario-already-assigned");

        if (!build.BuiltSlotsBySlotId.TryGetValue("owner", out var ownerSlot))
            return Fail(out failureReason, "missing-owner-slot");

        var entriesBySlot = template.Entries.ToDictionary(entry => entry.SlotId, StringComparer.Ordinal);
        foreach (var slot in build.BuiltSlots)
        {
            if (!entriesBySlot.ContainsKey(slot.SlotId))
                return Fail(out failureReason, "built-slot-not-in-template");
        }

        var scenarioUid = registry.NextScenarioUid();
        var slotAssignments = new List<ScenarioSlotAssignment>();
        var intentions = new List<IntentionInstance>();

        foreach (var slot in build.BuiltSlots)
        {
            var entry = entriesBySlot[slot.SlotId];
            if (!catalog.ValidIntentions.TryGetValue(entry.IntentionId, out var intentionTemplate))
                return Fail(out failureReason, "missing-intention-template");

            if (!snapshot.CandidatesByMind.TryGetValue(slot.MindId, out var currentCandidate))
                return Fail(out failureReason, "missing-slot-candidate-facts");

            if (!TryResolveTextParameters(entry, build, snapshot, out var resolvedParameters, out failureReason))
                return false;

            if (!TryResolveCopyableText(intentionTemplate, resolvedParameters, out var copyableTextResolved, out failureReason))
                return false;

            var visibility = ResolveVisibility(intentionTemplate, entry, waveContext.StartedAtRoundTime);
            var intentionUid = registry.NextIntentionUid();
            var intention = new IntentionInstance(
                intentionUid,
                intentionTemplate.ID,
                scenarioUid,
                entry.SlotId,
                slot.MindId,
                slot.OwnerEntityUid,
                entry.Kind,
                IntentionRuntimeStatus.Active,
                waveContext.StartedAtRoundTime,
                waveContext.StartedAtRoundTime,
                visibility.IsHidden,
                visibility.RevealMode,
                visibility.RevealedAtRoundTime,
                resolvedParameters,
                copyableTextResolved);

            var assignment = new ScenarioSlotAssignment(
                scenarioUid,
                entry.SlotId,
                entry.Kind,
                slot.MindId,
                slot.OwnerEntityUid,
                ScenarioSlotAssignmentStatus.Assigned,
                intentionUid,
                entry.Required,
                slot.WasBound,
                slot.BoundToSlotId,
                waveContext.StartedAtRoundTime);

            intentions.Add(intention);
            slotAssignments.Add(assignment);
        }

        var scenarioInstance = new ScenarioInstance(
            scenarioUid,
            template.ID,
            template.Category,
            ScenarioRuntimeStatus.Active,
            "owner",
            ownerSlot.MindId,
            ownerSlot.OwnerEntityUid,
            waveContext.WaveId,
            waveContext.StartedAtRoundTime,
            slotAssignments);

        prepared = new PreparedCommit(scenarioInstance, intentions, slotAssignments);
        return true;
    }

    /// <summary>
    /// Resolves text parameter bindings for one built scenario slot.
    /// </summary>
    private bool TryResolveTextParameters(
        ScenarioEntry entry,
        ScenarioBuildResult build,
        IntentionsSnapshot snapshot,
        out Dictionary<string, string> parameters,
        out string failureReason)
    {
        parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        failureReason = string.Empty;

        if (!build.BuiltSlotsBySlotId.TryGetValue(entry.SlotId, out var selfSlot))
            return Fail(out failureReason, "missing-self-slot");

        foreach (var (parameterName, binding) in entry.TextParameterBindings)
        {
            switch (binding.Source)
            {
                case "self":
                    if (!TryResolveSlotBindingValue(selfSlot, binding.Field, snapshot, out var selfValue, out failureReason))
                        return false;
                    parameters[parameterName] = selfValue;
                    break;
                case "slot":
                    if (binding.SlotId is null || !build.BuiltSlotsBySlotId.TryGetValue(binding.SlotId, out var otherSlot))
                        return Fail(out failureReason, "missing-bound-text-slot");
                    if (!TryResolveSlotBindingValue(otherSlot, binding.Field, snapshot, out var slotValue, out failureReason))
                        return false;
                    parameters[parameterName] = slotValue;
                    break;
                case "round":
                    if (!TryResolveRoundBindingValue(binding.Field, snapshot, out var roundValue))
                        return Fail(out failureReason, "invalid-round-text-binding");
                    parameters[parameterName] = roundValue;
                    break;
                case "literal":
                    if (binding.Value is null)
                        return Fail(out failureReason, "missing-literal-text-binding");
                    parameters[parameterName] = binding.Value;
                    break;
                default:
                    return Fail(out failureReason, "invalid-text-binding-source");
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves one text binding that targets a candidate fact on a built slot.
    /// </summary>
    private bool TryResolveSlotBindingValue(
        ScenarioSlotBuildResult slot,
        string? field,
        IntentionsSnapshot snapshot,
        out string value,
        out string failureReason)
    {
        value = string.Empty;
        failureReason = string.Empty;

        if (field is null)
        {
            failureReason = "missing-text-binding-field";
            return false;
        }

        if (!snapshot.CandidatesByMind.TryGetValue(slot.MindId, out var candidate))
        {
            failureReason = "missing-slot-candidate-facts";
            return false;
        }

        return TryResolveCandidateField(candidate, field, out value, out failureReason);
    }

    /// <summary>
    /// Converts one candidate fact field into its runtime text representation.
    /// </summary>
    private static bool TryResolveCandidateField(CandidateFacts candidate, string field, out string value, out string failureReason)
    {
        failureReason = string.Empty;
        value = field switch
        {
            "characterName" => candidate.CharacterName,
            "job" => candidate.Job ?? string.Empty,
            "department" => candidate.Department ?? string.Empty,
            "age" => candidate.Age?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "species" => candidate.Species ?? string.Empty,
            "sex" => candidate.Sex ?? string.Empty,
            "traits" => string.Join(", ", candidate.Traits),
            "hasMindshield" => candidate.HasMindshield.ToString().ToLowerInvariant(),
            "antagRole" => string.Join(", ", candidate.AntagRoles),
            "antagObjectiveType" => string.Join(", ", candidate.AntagObjectiveTypes),
            "mindId" => candidate.MindId.Id.ToString(CultureInfo.InvariantCulture),
            "ownerEntityUid" => candidate.OwnerEntityUid.Id.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

        if (field is "characterName" or "job" or "department" or "age" or "species" or "sex" or "traits" or "hasMindshield"
            or "antagRole" or "antagObjectiveType" or "mindId" or "ownerEntityUid")
        {
            return true;
        }

        failureReason = "invalid-candidate-text-binding-field";
        return false;
    }

    /// <summary>
    /// Resolves one round-scoped text binding.
    /// </summary>
    private static bool TryResolveRoundBindingValue(string? field, IntentionsSnapshot snapshot, out string value)
    {
        value = field switch
        {
            "stationName" => snapshot.RoundFacts.StationName,
            "stationTime" => snapshot.RoundFacts.StationTime.ToString("c", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

        return field is "stationName" or "stationTime";
    }

    /// <summary>
    /// Produces a standardized failure result while filling an out parameter.
    /// </summary>
    private static bool Fail(out string failureReason, string code)
    {
        failureReason = code;
        return false;
    }

    /// <summary>
    /// Resolves localized copyable text once at commit time so UI does not need to recompute it later.
    /// </summary>
    private bool TryResolveCopyableText(
        IntentionTemplatePrototype intentionTemplate,
        IReadOnlyDictionary<string, string> parameters,
        out string? copyableTextResolved,
        out string failureReason)
    {
        copyableTextResolved = null;
        failureReason = string.Empty;

        if (intentionTemplate.CopyableTextLoc is null)
            return true;

        copyableTextResolved = _locResolver(intentionTemplate.CopyableTextLoc, parameters);
        if (copyableTextResolved is not null)
            return true;

        failureReason = "copyable-text-loc-not-found";
        return false;
    }

    /// <summary>
    /// Resolves the final runtime visibility state for one intention instance.
    /// </summary>
    private static ResolvedVisibility ResolveVisibility(
        IntentionTemplatePrototype intentionTemplate,
        ScenarioEntry entry,
        TimeSpan startedAtRoundTime)
    {
        var visibilityType = entry.VisibilityOverride?.Type ?? intentionTemplate.DefaultVisibility;
        if (visibilityType == IntentionsPrototypeConstants.Visible)
            return new ResolvedVisibility(false, IntentionRevealMode.None, null);

        var reveal = entry.VisibilityOverride?.Reveal;
        if (reveal?.Type == IntentionsPrototypeConstants.RevealTimer && reveal.Minutes is { } minutes and > 0)
            return new ResolvedVisibility(true, IntentionRevealMode.Timer, startedAtRoundTime + TimeSpan.FromMinutes(minutes));

        return new ResolvedVisibility(true, IntentionRevealMode.None, null);
    }

    /// <summary>
    /// Invokes the optional failure hook used by rollback tests.
    /// </summary>
    private void MaybeFail(CommitFailurePoint point)
    {
        if (_failureHook?.Invoke(point) == true)
            throw new CommitFailureException(point.ToString());
    }

    /// <summary>
    /// Executes rollback actions in reverse registration order.
    /// </summary>
    private static bool Rollback(List<Action> rollbackActions)
    {
        try
        {
            for (var i = rollbackActions.Count - 1; i >= 0; i--)
                rollbackActions[i]();

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Default localization resolver used to render copyable text.
    /// </summary>
    private static string? DefaultLocResolver(string locId, IReadOnlyDictionary<string, string> parameters)
    {
        var args = parameters
            .Select(pair => (pair.Key, (object) pair.Value))
            .ToArray();

        try
        {
            var value = Loc.GetString(locId, args);
            return value == locId ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Holds fully materialized runtime objects that are ready to be written to the registry.
    /// </summary>
    private sealed record PreparedCommit(
        ScenarioInstance Scenario,
        IReadOnlyList<IntentionInstance> Intentions,
        IReadOnlyList<ScenarioSlotAssignment> SlotAssignments);

    /// <summary>
    /// Stores the final hidden/reveal state that will be copied into a runtime intention instance.
    /// </summary>
    private readonly record struct ResolvedVisibility(bool IsHidden, IntentionRevealMode RevealMode, TimeSpan? RevealedAtRoundTime);

    /// <summary>
    /// Internal exception used to stop commit flow at deterministic failure injection points.
    /// </summary>
    private sealed class CommitFailureException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
}

/// <summary>
/// Lists deterministic failure injection points for rollback tests.
/// </summary>
internal enum CommitFailurePoint : byte
{
    AfterScenarioSaved,
    AfterIntentionSaved,
    AfterMindAttached,
    AfterBackReferenceIndexed,
    AfterSlotAssignmentIndexed,
    AfterRevealIndexed,
    AfterAssignedScenarioIndexed,
    AfterAssignedPrimaryIndexed,
    AfterWaveContextIndexed,
}
