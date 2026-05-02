using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.Intentions.UI;
using Content.Server.Intentions.Waves;
using Content.Shared.Intentions.Predicates;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.Validation;
using Content.Shared.Intentions.Waves;
using Robust.Server.Player;
using Robust.Shared.GameObjects;

namespace Content.Server.Intentions.Runtime;

/// <summary>
/// Applies debug/admin runtime mutations such as full resets, targeted scenario removal, manual reveal, and forced assignment.
/// </summary>
public sealed class IntentionsRuntimeAdminService
{
    private readonly IntentionsPredicateEngine _predicateEngine;
    private readonly IntentionsCommitService _commitService;
    private readonly IntentionsAssignmentNotificationService _notifications;
    private readonly IEntityManager? _entities;
    private readonly IPlayerManager? _player;
    private readonly IChatManager? _chat;

    /// <summary>
    /// Creates the runtime admin service with the shared predicate engine and commit service.
    /// </summary>
    public IntentionsRuntimeAdminService(
        IntentionsPredicateEngine? predicateEngine = null,
        IntentionsCommitService? commitService = null,
        IntentionsAssignmentNotificationService? notifications = null,
        IEntityManager? entities = null,
        IPlayerManager? player = null,
        IChatManager? chat = null)
    {
        _predicateEngine = predicateEngine ?? new IntentionsPredicateEngine();
        _commitService = commitService ?? new IntentionsCommitService();
        _notifications = notifications ?? new IntentionsAssignmentNotificationService();
        _entities = entities;
        _player = player;
        _chat = chat;
    }

    /// <summary>
    /// Removes every runtime scenario and intention by resetting the registry.
    /// </summary>
    public ClearAllScenariosResult ClearAllScenarios(IntentionsRuntimeSystem runtime)
    {
        var removedScenarioCount = runtime.Registry.ScenarioByUid.Count;
        var removedIntentionCount = runtime.Registry.IntentionByUid.Count;
        var affectedMindIds = runtime.ResetRegistry();

        return new ClearAllScenariosResult(
            removedScenarioCount,
            removedIntentionCount,
            affectedMindIds);
    }

    /// <summary>
    /// Physically purges one runtime scenario and all related indexes from the registry.
    /// </summary>
    public RemoveScenarioRuntimeResult RemoveScenario(
        IntentionsRuntimeRegistry registry,
        ScenarioInstanceUid scenarioUid,
        EntityUid ownerMindId)
    {
        if (!registry.ScenarioByUid.TryGetValue(scenarioUid, out var scenario))
        {
            return RemoveScenarioRuntimeResult.Failure(
                "scenario-not-found",
                $"Scenario {scenarioUid.Value.ToString(CultureInfo.InvariantCulture)} was not found.");
        }

        if (scenario.OwnerMindId != ownerMindId)
        {
            return RemoveScenarioRuntimeResult.Failure(
                "owner-mind-mismatch",
                $"Scenario {scenarioUid.Value.ToString(CultureInfo.InvariantCulture)} is owned by mind {scenario.OwnerMindId.Id.ToString(CultureInfo.InvariantCulture)}, not {ownerMindId.Id.ToString(CultureInfo.InvariantCulture)}.");
        }

        var affectedMindIds = new HashSet<EntityUid> { scenario.OwnerMindId };
        var removedIntentionUids = registry.GetIntentionUidsForScenario(scenarioUid)
            .Distinct()
            .ToArray();

        foreach (var assignment in scenario.SlotAssignments)
        {
            affectedMindIds.Add(assignment.MindId);
            registry.RemoveSlotAssignment(scenarioUid, assignment.SlotId);
        }

        registry.RemoveMissingOwnerScenarioId(scenarioUid);

        if (scenario.Status is ScenarioRuntimeStatus.Active or ScenarioRuntimeStatus.Frozen)
            registry.DecrementAssignedPrimary(scenario.OwnerMindId, scenario.CategoryId);

        foreach (var intentionUid in removedIntentionUids)
        {
            registry.RemoveScenarioBackReference(intentionUid);

            if (!registry.IntentionByUid.TryGetValue(intentionUid, out var intention))
                continue;

            affectedMindIds.Add(intention.OwnerMindId);
            registry.RemoveHiddenRevealForIntention(intentionUid);
            registry.DetachIntentionFromMind(intention.OwnerMindId, intentionUid);
            registry.RemoveIntention(intentionUid);
        }

        registry.RemoveScenario(scenarioUid);

        if (!registry.ScenarioByUid.Values.Any(existing => existing.ScenarioTemplateId == scenario.ScenarioTemplateId))
            registry.RemoveAssignedScenarioId(scenario.ScenarioTemplateId);

        return RemoveScenarioRuntimeResult.Success(
            scenario,
            removedIntentionUids,
            affectedMindIds.ToImmutableArray());
    }

    /// <summary>
    /// Reveals one hidden runtime intention for the requested owner mind.
    /// </summary>
    public RevealHiddenIntentionsRuntimeResult RevealHiddenIntention(
        IntentionsRuntimeRegistry registry,
        IntentionInstanceUid intentionUid,
        EntityUid ownerMindId,
        TimeSpan now)
    {
        if (!registry.IntentionByUid.TryGetValue(intentionUid, out var intention))
        {
            return RevealHiddenIntentionsRuntimeResult.Failure(
                IntentionsRuntimeRevealScope.One,
                ownerMindId,
                intentionUid,
                "intention-not-found",
                $"Intention {intentionUid.Value.ToString(CultureInfo.InvariantCulture)} was not found.");
        }

        if (intention.OwnerMindId != ownerMindId)
        {
            return RevealHiddenIntentionsRuntimeResult.Failure(
                IntentionsRuntimeRevealScope.One,
                ownerMindId,
                intentionUid,
                "owner-mind-mismatch",
                $"Intention {intentionUid.Value.ToString(CultureInfo.InvariantCulture)} belongs to mind {intention.OwnerMindId.Id.ToString(CultureInfo.InvariantCulture)}, not {ownerMindId.Id.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (intention.Status != IntentionRuntimeStatus.Active)
        {
            return RevealHiddenIntentionsRuntimeResult.Failure(
                IntentionsRuntimeRevealScope.One,
                ownerMindId,
                intentionUid,
                "intention-not-active",
                $"Intention {intentionUid.Value.ToString(CultureInfo.InvariantCulture)} is {intention.Status} and cannot be revealed.");
        }

        if (!intention.IsHidden)
        {
            return RevealHiddenIntentionsRuntimeResult.Failure(
                IntentionsRuntimeRevealScope.One,
                ownerMindId,
                intentionUid,
                "intention-not-hidden",
                $"Intention {intentionUid.Value.ToString(CultureInfo.InvariantCulture)} is already visible.");
        }

        var revealed = RevealIntentionInternal(registry, intention, now);
        return RevealHiddenIntentionsRuntimeResult.Success(
            IntentionsRuntimeRevealScope.One,
            ownerMindId,
            intentionUid,
            $"Revealed hidden intention {intentionUid.Value.ToString(CultureInfo.InvariantCulture)} for mind {ownerMindId.Id.ToString(CultureInfo.InvariantCulture)}.",
            [revealed]);
    }

    /// <summary>
    /// Reveals every active hidden runtime intention that belongs to one owner mind.
    /// </summary>
    public RevealHiddenIntentionsRuntimeResult RevealAllHiddenIntentionsForMind(
        IntentionsRuntimeRegistry registry,
        EntityUid ownerMindId,
        TimeSpan now)
    {
        var hiddenIntentions = registry.IntentionByUid.Values
            .Where(intention =>
                intention.OwnerMindId == ownerMindId
                && intention.Status == IntentionRuntimeStatus.Active
                && intention.IsHidden)
            .OrderBy(intention => intention.Uid.Value)
            .ToArray();

        if (hiddenIntentions.Length == 0)
        {
            return RevealHiddenIntentionsRuntimeResult.Failure(
                IntentionsRuntimeRevealScope.AllForMind,
                ownerMindId,
                null,
                "no-hidden-intentions",
                $"Mind {ownerMindId.Id.ToString(CultureInfo.InvariantCulture)} has no active hidden intentions.");
        }

        var revealed = hiddenIntentions
            .Select(intention => RevealIntentionInternal(registry, intention, now))
            .ToImmutableArray();

        return RevealHiddenIntentionsRuntimeResult.Success(
            IntentionsRuntimeRevealScope.AllForMind,
            ownerMindId,
            null,
            $"Revealed {revealed.Length.ToString(CultureInfo.InvariantCulture)} hidden intentions for mind {ownerMindId.Id.ToString(CultureInfo.InvariantCulture)}.",
            revealed);
    }

    /// <summary>
    /// Builds and commits one explicit scenario assignment while bypassing quota limits and optionally bypassing predicate checks.
    /// </summary>
    public ForceAssignScenarioRuntimeResult TryForceAssignScenario(
        ValidationCatalog catalog,
        IntentionsSnapshot snapshot,
        IntentionsRuntimeRegistry registry,
        string scenarioTemplateId,
        IReadOnlyList<string> slotArguments,
        int waveId,
        int? requestedSeed = null,
        bool ignorePredicates = false)
    {
        if (!catalog.ValidScenarios.TryGetValue(scenarioTemplateId, out var scenario))
        {
            return ForceAssignScenarioRuntimeResult.Failure(
                scenarioTemplateId,
                waveId,
                "scenario-template-not-found",
                $"Scenario template '{scenarioTemplateId}' is missing from the validated Intentions catalog.",
                ignoredPredicates: ignorePredicates);
        }

        var expectedArgumentLayout = BuildExpectedArgumentLayout(scenario);

        if (!scenario.Template.Enabled)
        {
            return ForceAssignScenarioRuntimeResult.Failure(
                scenarioTemplateId,
                waveId,
                "scenario-disabled",
                $"Scenario template '{scenarioTemplateId}' is disabled and cannot be force-assigned.",
                expectedArgumentLayout: expectedArgumentLayout,
                ignoredPredicates: ignorePredicates);
        }

        if (registry.AssignedScenarioIds.Contains(scenario.Template.ID))
        {
            return ForceAssignScenarioRuntimeResult.Failure(
                scenarioTemplateId,
                waveId,
                "scenario-already-assigned",
                $"Scenario template '{scenarioTemplateId}' is already reserved for this round.",
                expectedArgumentLayout: expectedArgumentLayout,
                ignoredPredicates: ignorePredicates);
        }

        var explicitSlots = DescribeExplicitSlots(scenario);
        if (slotArguments.Count != explicitSlots.Length)
        {
            return ForceAssignScenarioRuntimeResult.Failure(
                scenarioTemplateId,
                waveId,
                "wrong-slot-argument-count",
                $"Scenario template '{scenarioTemplateId}' expects {explicitSlots.Length.ToString(CultureInfo.InvariantCulture)} non-bound slot arguments in slotBuildOrder order.",
                expectedArgumentLayout: expectedArgumentLayout,
                ignoredPredicates: ignorePredicates);
        }

        if (!ignorePredicates)
        {
            var globalPredicates = _predicateEngine.EvaluateGlobal(scenario.Template.GlobalPredicates, snapshot);
            if (!globalPredicates.IsMatch)
            {
                return ForceAssignScenarioRuntimeResult.Failure(
                    scenarioTemplateId,
                    waveId,
                    globalPredicates.HasError ? "global-predicates-error" : "global-predicates-failed",
                    "Scenario global predicates did not match the current snapshot.",
                    expectedArgumentLayout: expectedArgumentLayout,
                    globalPredicateResult: globalPredicates,
                    ignoredPredicates: false);
            }
        }

        var build = TryBuildForcedScenario(
            scenario,
            snapshot,
            explicitSlots,
            slotArguments,
            ignorePredicates,
            out var buildFailureCode,
            out var buildFailureMessage);

        if (!build.IsSuccess)
        {
            return ForceAssignScenarioRuntimeResult.Failure(
                scenarioTemplateId,
                waveId,
                buildFailureCode ?? build.FailureReason ?? "scenario-build-failed",
                buildFailureMessage ?? "Scenario build failed.",
                expectedArgumentLayout: expectedArgumentLayout,
                buildResult: build,
                ignoredPredicates: ignorePredicates);
        }

        var resolvedSeed = requestedSeed ?? IntentionsDeterministicRandom.BuildSeed(
            waveId,
            snapshot.SnapshotId,
            snapshot.RoundFacts.GameMode,
            snapshot.RoundFacts.StationTime.Ticks);
        var context = new DistributionWaveContext(
            waveId,
            snapshot.SnapshotId,
            resolvedSeed,
            snapshot.RoundFacts.StationTime,
            snapshot.RoundFacts.CrewCount,
            registry.AssignedScenarioIds,
            RegistryAssignedPrimary(registry),
            IntentionsWaveKind.Refill,
            snapshot.RoundFacts.CrewCount)
        {
            Status = DistributionWaveStatus.Running,
        };

        context.SuccessfulBuilds.Add(build);
        context.AssignedScenarioIds.Add(scenario.Template.ID);
        if (build.BuiltSlotsBySlotId.TryGetValue("owner", out var ownerSlot))
            IncrementPrimaryCount(context, ownerSlot.MindId, scenario.Template.Category);

        var commit = _commitService.CommitScenarioBuild(build, scenario, catalog, snapshot, context, registry);
        if (!commit.IsSuccess)
        {
            context.Status = DistributionWaveStatus.Failed;
            context.FailureReason = commit.FailureReason ?? "commit-failed";

            return ForceAssignScenarioRuntimeResult.Failure(
                scenarioTemplateId,
                waveId,
                commit.FailureReason ?? "commit-failed",
                $"Scenario commit failed: {commit.FailureReason ?? "commit-failed"}.",
                expectedArgumentLayout: expectedArgumentLayout,
                buildResult: build,
                commitResult: commit,
                waveContext: context,
                ignoredPredicates: ignorePredicates);
        }

        if (_entities is not null
            && _player is not null
            && _chat is not null)
        {
            _notifications.DispatchNotifications(catalog, commit.IntentionInstances, _entities, _player, _chat);
        }

        context.Status = DistributionWaveStatus.Completed;
        var affectedMindIds = commit.IntentionInstances
            .Select(intention => intention.OwnerMindId)
            .Distinct()
            .ToImmutableArray();

        return ForceAssignScenarioRuntimeResult.Success(
            scenarioTemplateId,
            waveId,
            ignorePredicates
                ? $"Assigned scenario template '{scenarioTemplateId}' as manual wave {waveId.ToString(CultureInfo.InvariantCulture)} while ignoring global and candidate predicates."
                : $"Assigned scenario template '{scenarioTemplateId}' as manual wave {waveId.ToString(CultureInfo.InvariantCulture)}.",
            expectedArgumentLayout,
            build,
            commit,
            context,
            affectedMindIds,
            ignorePredicates);
    }

    /// <summary>
    /// Returns the non-bound slots that must be supplied explicitly for a forced assignment.
    /// </summary>
    public static ImmutableArray<ForcedAssignmentSlotDescriptor> DescribeExplicitSlots(ValidatedScenarioTemplate scenario)
    {
        var entriesBySlot = scenario.Template.Entries.ToDictionary(entry => entry.SlotId, StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<ForcedAssignmentSlotDescriptor>();

        foreach (var slotId in scenario.SlotBuildOrder)
        {
            if (!entriesBySlot.TryGetValue(slotId, out var entry) || entry.BindToSlot is not null)
                continue;

            builder.Add(new ForcedAssignmentSlotDescriptor(slotId, entry.Required, entry.Kind));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Produces the expected positional syntax for a validated scenario template.
    /// </summary>
    public static string BuildExpectedArgumentLayout(ValidatedScenarioTemplate scenario)
    {
        return string.Join(" ", DescribeExplicitSlots(scenario)
            .Select(slot => slot.Required
                ? $"{slot.SlotId}=<mindId>"
                : $"{slot.SlotId}=<mindId|->"));
    }

    /// <summary>
    /// Builds a scenario from explicit slot arguments without using quota logic or candidate selection.
    /// </summary>
    private ScenarioBuildResult TryBuildForcedScenario(
        ValidatedScenarioTemplate scenario,
        IntentionsSnapshot snapshot,
        ImmutableArray<ForcedAssignmentSlotDescriptor> explicitSlots,
        IReadOnlyList<string> slotArguments,
        bool ignorePredicates,
        out string? failureCode,
        out string? failureMessage)
    {
        failureCode = null;
        failureMessage = null;

        var template = scenario.Template;
        var builtSlots = new List<ScenarioSlotBuildResult>();
        var skippedOptionalSlots = new List<string>();
        var slotRejectReasons = new List<SlotRejectReason>();
        var selectedBySlotId = new Dictionary<string, CandidateFacts>(StringComparer.Ordinal);
        var reservedMindIds = new HashSet<EntityUid>();
        var entriesBySlot = template.Entries.ToDictionary(entry => entry.SlotId, StringComparer.Ordinal);

        if (!TryValidateSlotBuildOrder(scenario, entriesBySlot, out var orderFailure))
        {
            failureCode = orderFailure;
            failureMessage = "Scenario slotBuildOrder is missing or invalid in runtime.";
            return ScenarioBuildResult.Failure(template.ID, template.Category, orderFailure, builtSlots, skippedOptionalSlots, slotRejectReasons);
        }

        var argumentIndex = 0;
        foreach (var slotId in scenario.SlotBuildOrder)
        {
            var entry = entriesBySlot[slotId];

            if (entry.BindToSlot is { } bindToSlot)
            {
                if (!selectedBySlotId.TryGetValue(bindToSlot, out var boundCandidate))
                {
                    var message = $"Bound slot '{entry.SlotId}' could not resolve source slot '{bindToSlot}'.";
                    slotRejectReasons.Add(RejectSlot(template.ID, entry.SlotId, "missing-bind-source", message));

                    if (entry.Required)
                    {
                        failureCode = "missing-bind-source";
                        failureMessage = message;
                        return ScenarioBuildResult.Failure(template.ID, template.Category, failureCode, builtSlots, skippedOptionalSlots, slotRejectReasons);
                    }

                    skippedOptionalSlots.Add(entry.SlotId);
                    continue;
                }

                selectedBySlotId[entry.SlotId] = boundCandidate;
                builtSlots.Add(BuildSlotResult(entry, boundCandidate, ScenarioSlotBuildState.Bound, wasBound: true, boundToSlotId: bindToSlot));
                continue;
            }

            if (argumentIndex >= slotArguments.Count)
            {
                failureCode = "wrong-slot-argument-count";
                failureMessage = $"Missing explicit argument for slot '{entry.SlotId}'.";
                return ScenarioBuildResult.Failure(template.ID, template.Category, failureCode, builtSlots, skippedOptionalSlots, slotRejectReasons);
            }

            var rawArgument = slotArguments[argumentIndex++];
            if (rawArgument == "-")
            {
                if (entry.Required)
                {
                    var message = $"Required slot '{entry.SlotId}' cannot be skipped.";
                    slotRejectReasons.Add(RejectSlot(template.ID, entry.SlotId, "required-slot-cannot-be-skipped", message));
                    failureCode = "required-slot-cannot-be-skipped";
                    failureMessage = message;
                    return ScenarioBuildResult.Failure(template.ID, template.Category, failureCode, builtSlots, skippedOptionalSlots, slotRejectReasons);
                }

                skippedOptionalSlots.Add(entry.SlotId);
                continue;
            }

            if (!TryCheckCompareSources(template.ID, entry, selectedBySlotId, slotRejectReasons))
            {
                failureCode = "missing-compare-source";
                failureMessage = $"Slot '{entry.SlotId}' compares against a slot that has not been selected.";
                return ScenarioBuildResult.Failure(template.ID, template.Category, failureCode, builtSlots, skippedOptionalSlots, slotRejectReasons);
            }

            if (!TryParseEntityUid(rawArgument, out var mindId))
            {
                var message = $"Slot '{entry.SlotId}' expected a MindId or '-', but got '{rawArgument}'.";
                slotRejectReasons.Add(RejectSlot(template.ID, entry.SlotId, "invalid-mind-id", message));
                failureCode = "invalid-mind-id";
                failureMessage = message;
                return ScenarioBuildResult.Failure(template.ID, template.Category, failureCode, builtSlots, skippedOptionalSlots, slotRejectReasons);
            }

            if (!snapshot.CandidatesByMind.TryGetValue(mindId, out var candidate))
            {
                var message = $"Mind {mindId.Id.ToString(CultureInfo.InvariantCulture)} is not present in the current Intentions snapshot candidate set for slot '{entry.SlotId}'.";
                slotRejectReasons.Add(new SlotRejectReason(template.ID, entry.SlotId, "candidate-not-in-snapshot", message, mindId));
                failureCode = "mind-not-in-snapshot";
                failureMessage = message;
                return ScenarioBuildResult.Failure(template.ID, template.Category, failureCode, builtSlots, skippedOptionalSlots, slotRejectReasons);
            }

            var allowSameActorMinds = entry.AllowSameActorAs
                .Where(selectedBySlotId.ContainsKey)
                .Select(slot => selectedBySlotId[slot].MindId)
                .ToHashSet();
            if (reservedMindIds.Contains(candidate.MindId) && !allowSameActorMinds.Contains(candidate.MindId))
            {
                var message = $"Mind {candidate.MindId.Id.ToString(CultureInfo.InvariantCulture)} cannot be reused for slot '{entry.SlotId}' without allowSameActorAs.";
                slotRejectReasons.Add(new SlotRejectReason(template.ID, entry.SlotId, "same-actor-not-allowed", message, candidate.MindId));
                failureCode = "same-actor-not-allowed";
                failureMessage = message;
                return ScenarioBuildResult.Failure(template.ID, template.Category, failureCode, builtSlots, skippedOptionalSlots, slotRejectReasons);
            }

            if (!ignorePredicates)
            {
                var predicateResult = _predicateEngine.EvaluateCandidate(entry.CandidatePredicates, candidate, snapshot, selectedBySlotId);
                if (!predicateResult.IsMatch)
                {
                    var rejectCode = predicateResult.HasError ? "candidate-predicates-error" : "candidate-predicates-failed";
                    var message = $"Mind {candidate.MindId.Id.ToString(CultureInfo.InvariantCulture)} did not satisfy candidate predicates for slot '{entry.SlotId}'.";
                    slotRejectReasons.Add(new SlotRejectReason(template.ID, entry.SlotId, rejectCode, message, candidate.MindId, predicateResult.RejectReasons));
                    failureCode = rejectCode;
                    failureMessage = message;
                    return ScenarioBuildResult.Failure(template.ID, template.Category, failureCode, builtSlots, skippedOptionalSlots, slotRejectReasons);
                }
            }

            selectedBySlotId[entry.SlotId] = candidate;
            builtSlots.Add(BuildSlotResult(entry, candidate, ScenarioSlotBuildState.Assigned));
            reservedMindIds.Add(candidate.MindId);
        }

        foreach (var entry in template.Entries)
        {
            if (!entry.Required || selectedBySlotId.ContainsKey(entry.SlotId))
                continue;

            failureCode = "required-slot-not-built";
            failureMessage = $"Required slot '{entry.SlotId}' was not built.";
            slotRejectReasons.Add(RejectSlot(template.ID, entry.SlotId, failureCode, failureMessage));
            return ScenarioBuildResult.Failure(template.ID, template.Category, failureCode, builtSlots, skippedOptionalSlots, slotRejectReasons);
        }

        return ScenarioBuildResult.Success(template.ID, template.Category, builtSlots, skippedOptionalSlots, slotRejectReasons);
    }

    /// <summary>
    /// Verifies that the validated slot build order still matches the template structure at runtime.
    /// </summary>
    private static bool TryValidateSlotBuildOrder(
        ValidatedScenarioTemplate scenario,
        IReadOnlyDictionary<string, Content.Shared.Intentions.Prototypes.ScenarioEntry> entriesBySlot,
        out string failureReason)
    {
        failureReason = string.Empty;

        if (scenario.SlotBuildOrder.Count == 0
            || scenario.SlotBuildOrder[0] != "owner"
            || scenario.SlotBuildOrder.Any(slotId => !entriesBySlot.ContainsKey(slotId))
            || scenario.SlotBuildOrder.Distinct(StringComparer.Ordinal).Count() != scenario.SlotBuildOrder.Count)
        {
            failureReason = "missing-or-invalid-precomputed-build-order";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Ensures that compare-to predicates only reference already selected slots.
    /// </summary>
    private static bool TryCheckCompareSources(
        string scenarioId,
        Content.Shared.Intentions.Prototypes.ScenarioEntry entry,
        IReadOnlyDictionary<string, CandidateFacts> selectedBySlotId,
        List<SlotRejectReason> slotRejectReasons)
    {
        foreach (var predicate in entry.CandidatePredicates)
        {
            if (predicate.CompareTo is not { } compareTo)
                continue;

            if (selectedBySlotId.ContainsKey(compareTo.SlotId))
                continue;

            slotRejectReasons.Add(RejectSlot(
                scenarioId,
                entry.SlotId,
                "missing-compare-source",
                "Compared slot has not been selected."));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates one slot build result for an explicit or bound assignment.
    /// </summary>
    private static ScenarioSlotBuildResult BuildSlotResult(
        Content.Shared.Intentions.Prototypes.ScenarioEntry entry,
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
    /// Marks one active hidden intention as revealed while keeping the rest of its runtime state intact.
    /// </summary>
    private static RevealedRuntimeIntentionResult RevealIntentionInternal(
        IntentionsRuntimeRegistry registry,
        IntentionInstance intention,
        TimeSpan now)
    {
        registry.RemoveHiddenRevealForIntention(intention.Uid);
        var revealed = intention.WithRevealed();
        registry.ReplaceIntention(revealed);

        return new RevealedRuntimeIntentionResult(
            intention.Uid,
            intention.ScenarioUid,
            intention.OwnerMindId,
            intention.OwnerEntityUid,
            intention.RevealMode,
            intention.RevealedAtRoundTime,
            now);
    }

    /// <summary>
    /// Creates a standardized slot rejection record.
    /// </summary>
    private static SlotRejectReason RejectSlot(string scenarioId, string slotId, string code, string message)
    {
        return new SlotRejectReason(scenarioId, slotId, code, message);
    }

    /// <summary>
    /// Parses the plain integer mind id syntax used by the debug commands.
    /// </summary>
    private static bool TryParseEntityUid(string value, out EntityUid entityUid)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            entityUid = default;
            return false;
        }

        entityUid = new EntityUid(id);
        return true;
    }

    /// <summary>
    /// Copies runtime primary counters into the immutable wave-input shape used by manual contexts.
    /// </summary>
    private static IReadOnlyDictionary<EntityUid, IReadOnlyDictionary<string, int>> RegistryAssignedPrimary(IntentionsRuntimeRegistry registry)
    {
        return registry.AssignedPrimaryByMind.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, int>) pair.Value.ToImmutableDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal));
    }

    /// <summary>
    /// Mirrors the wave-context primary counter increment used by normal orchestration.
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
}

/// <summary>
/// One non-bound slot that must be supplied explicitly for forced assignment.
/// </summary>
public readonly record struct ForcedAssignmentSlotDescriptor(string SlotId, bool Required, string Kind);

/// <summary>
/// Target scope for an admin runtime reveal operation.
/// </summary>
public enum IntentionsRuntimeRevealScope : byte
{
    One,
    AllForMind,
}

/// <summary>
/// One runtime intention that was forcibly revealed by an admin/debug command.
/// </summary>
public readonly record struct RevealedRuntimeIntentionResult(
    IntentionInstanceUid IntentionUid,
    ScenarioInstanceUid ScenarioUid,
    EntityUid MindId,
    EntityUid OwnerEntityUid,
    IntentionRevealMode PreviousRevealMode,
    TimeSpan? ScheduledRevealAtRoundTime,
    TimeSpan RevealedByAdminAtRoundTime);

/// <summary>
/// Result of clearing all runtime scenarios and intentions.
/// </summary>
public sealed record ClearAllScenariosResult(
    int RemovedScenarioCount,
    int RemovedIntentionCount,
    ImmutableArray<EntityUid> AffectedMindIds);

/// <summary>
/// Result of a debug/admin runtime hidden-intention reveal request.
/// </summary>
public sealed class RevealHiddenIntentionsRuntimeResult
{
    private RevealHiddenIntentionsRuntimeResult(
        bool isSuccess,
        IntentionsRuntimeRevealScope scope,
        EntityUid requestedMindId,
        IntentionInstanceUid? requestedIntentionUid,
        string? failureCode,
        string message,
        IEnumerable<RevealedRuntimeIntentionResult>? revealedIntentions)
    {
        IsSuccess = isSuccess;
        Scope = scope;
        RequestedMindId = requestedMindId;
        RequestedIntentionUid = requestedIntentionUid;
        FailureCode = failureCode;
        Message = message;
        RevealedIntentions = revealedIntentions?.ToImmutableArray() ?? ImmutableArray<RevealedRuntimeIntentionResult>.Empty;
        AffectedMindIds = RevealedIntentions
            .Select(intention => intention.MindId)
            .Distinct()
            .ToImmutableArray();
    }

    public bool IsSuccess { get; }
    public IntentionsRuntimeRevealScope Scope { get; }
    public EntityUid RequestedMindId { get; }
    public IntentionInstanceUid? RequestedIntentionUid { get; }
    public string? FailureCode { get; }
    public string Message { get; }
    public ImmutableArray<RevealedRuntimeIntentionResult> RevealedIntentions { get; }
    public ImmutableArray<EntityUid> AffectedMindIds { get; }

    public static RevealHiddenIntentionsRuntimeResult Success(
        IntentionsRuntimeRevealScope scope,
        EntityUid requestedMindId,
        IntentionInstanceUid? requestedIntentionUid,
        string message,
        IEnumerable<RevealedRuntimeIntentionResult> revealedIntentions)
    {
        return new RevealHiddenIntentionsRuntimeResult(
            true,
            scope,
            requestedMindId,
            requestedIntentionUid,
            null,
            message,
            revealedIntentions);
    }

    public static RevealHiddenIntentionsRuntimeResult Failure(
        IntentionsRuntimeRevealScope scope,
        EntityUid requestedMindId,
        IntentionInstanceUid? requestedIntentionUid,
        string failureCode,
        string message)
    {
        return new RevealHiddenIntentionsRuntimeResult(
            false,
            scope,
            requestedMindId,
            requestedIntentionUid,
            failureCode,
            message,
            null);
    }
}

/// <summary>
/// Result of removing one runtime scenario from the registry.
/// </summary>
public sealed class RemoveScenarioRuntimeResult
{
    private RemoveScenarioRuntimeResult(
        bool isSuccess,
        string? failureCode,
        string message,
        ScenarioInstance? removedScenario,
        IEnumerable<IntentionInstanceUid>? removedIntentionUids,
        IEnumerable<EntityUid>? affectedMindIds)
    {
        IsSuccess = isSuccess;
        FailureCode = failureCode;
        Message = message;
        RemovedScenario = removedScenario;
        RemovedIntentionUids = removedIntentionUids?.ToImmutableArray() ?? ImmutableArray<IntentionInstanceUid>.Empty;
        AffectedMindIds = affectedMindIds?.Distinct().ToImmutableArray() ?? ImmutableArray<EntityUid>.Empty;
    }

    public bool IsSuccess { get; }
    public string? FailureCode { get; }
    public string Message { get; }
    public ScenarioInstance? RemovedScenario { get; }
    public ImmutableArray<IntentionInstanceUid> RemovedIntentionUids { get; }
    public ImmutableArray<EntityUid> AffectedMindIds { get; }

    public static RemoveScenarioRuntimeResult Success(
        ScenarioInstance removedScenario,
        IEnumerable<IntentionInstanceUid> removedIntentionUids,
        IEnumerable<EntityUid> affectedMindIds)
    {
        return new RemoveScenarioRuntimeResult(
            true,
            null,
            $"Removed scenario {removedScenario.Uid.Value.ToString(CultureInfo.InvariantCulture)} (template={removedScenario.ScenarioTemplateId}) and {removedIntentionUids.Count().ToString(CultureInfo.InvariantCulture)} intentions from runtime.",
            removedScenario,
            removedIntentionUids,
            affectedMindIds);
    }

    public static RemoveScenarioRuntimeResult Failure(string failureCode, string message)
    {
        return new RemoveScenarioRuntimeResult(false, failureCode, message, null, null, null);
    }
}

/// <summary>
/// Result of attempting one explicit manual scenario assignment.
/// </summary>
public sealed class ForceAssignScenarioRuntimeResult
{
    private ForceAssignScenarioRuntimeResult(
        bool isSuccess,
        string scenarioTemplateId,
        int waveId,
        bool ignoredPredicates,
        string failureCode,
        string message,
        string? expectedArgumentLayout,
        ScenarioBuildResult? buildResult,
        CommitScenarioBuildResult? commitResult,
        DistributionWaveContext? waveContext,
        PredicateEvaluationResult? globalPredicateResult,
        IEnumerable<EntityUid>? affectedMindIds)
    {
        IsSuccess = isSuccess;
        ScenarioTemplateId = scenarioTemplateId;
        WaveId = waveId;
        IgnoredPredicates = ignoredPredicates;
        FailureCode = failureCode;
        Message = message;
        ExpectedArgumentLayout = expectedArgumentLayout;
        BuildResult = buildResult;
        CommitResult = commitResult;
        WaveContext = waveContext;
        GlobalPredicateResult = globalPredicateResult;
        AffectedMindIds = affectedMindIds?.Distinct().ToImmutableArray() ?? ImmutableArray<EntityUid>.Empty;
    }

    public bool IsSuccess { get; }
    public string ScenarioTemplateId { get; }
    public int WaveId { get; }
    public bool IgnoredPredicates { get; }
    public string FailureCode { get; }
    public string Message { get; }
    public string? ExpectedArgumentLayout { get; }
    public ScenarioBuildResult? BuildResult { get; }
    public CommitScenarioBuildResult? CommitResult { get; }
    public DistributionWaveContext? WaveContext { get; }
    public PredicateEvaluationResult? GlobalPredicateResult { get; }
    public ImmutableArray<EntityUid> AffectedMindIds { get; }

    public static ForceAssignScenarioRuntimeResult Success(
        string scenarioTemplateId,
        int waveId,
        string message,
        string expectedArgumentLayout,
        ScenarioBuildResult buildResult,
        CommitScenarioBuildResult commitResult,
        DistributionWaveContext waveContext,
        IEnumerable<EntityUid> affectedMindIds,
        bool ignoredPredicates = false)
    {
        return new ForceAssignScenarioRuntimeResult(
            true,
            scenarioTemplateId,
            waveId,
            ignoredPredicates,
            string.Empty,
            message,
            expectedArgumentLayout,
            buildResult,
            commitResult,
            waveContext,
            null,
            affectedMindIds);
    }

    public static ForceAssignScenarioRuntimeResult Failure(
        string scenarioTemplateId,
        int waveId,
        string failureCode,
        string message,
        string? expectedArgumentLayout = null,
        ScenarioBuildResult? buildResult = null,
        CommitScenarioBuildResult? commitResult = null,
        DistributionWaveContext? waveContext = null,
        PredicateEvaluationResult? globalPredicateResult = null,
        bool ignoredPredicates = false)
    {
        return new ForceAssignScenarioRuntimeResult(
            false,
            scenarioTemplateId,
            waveId,
            ignoredPredicates,
            failureCode,
            message,
            expectedArgumentLayout,
            buildResult,
            commitResult,
            waveContext,
            globalPredicateResult,
            null);
    }
}
