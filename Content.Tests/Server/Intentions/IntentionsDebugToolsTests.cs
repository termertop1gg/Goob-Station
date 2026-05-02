using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Content.Server.Intentions.Debug;
using Content.Server.Intentions.Runtime;
using Content.Server.Intentions.Waves;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.UI;
using Content.Shared.Intentions.Validation;
using Content.Shared.Intentions.Waves;
using NUnit.Framework;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Utility;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsDebugFormatters))]
/// <summary>
/// Covers debug formatter and debug-command helper behavior for Intentions tooling.
/// </summary>
public sealed class IntentionsDebugToolsTests
{
    [Test]
    public void ValidationReportPrintsCountsAndIssues()
    {
        var catalog = new ValidationCatalog();
        catalog.Issues.Add(new ValidationIssue(
            ValidationObjectType.ScenarioTemplate,
            "scenario",
            "entries.owner",
            "invalid-owner-count",
            "Owner is invalid."));

        var report = IntentionsDebugFormatters.FormatValidation(catalog);

        Assert.That(report, Does.Contain("Intentions validation report"));
        Assert.That(report, Does.Contain("Valid scenarios: 0"));
        Assert.That(report, Does.Contain("invalid-owner-count"));
    }

    [Test]
    public void WavePreviewPrintsQuotaPoolsBuildsAndRejects()
    {
        var context = new DistributionWaveContext(
            7,
            "snapshot-7",
            123,
            TimeSpan.FromMinutes(5),
            3,
            kind: IntentionsWaveKind.Refill,
            distributionCrewBaseline: 5)
        {
            Status = DistributionWaveStatus.Completed,
        };
        var state = new CategoryWaveState("social", 2, 1, desiredQuota: 4, existingActiveFrozenCount: 2, refillTarget: 2)
        {
            FilledQuota = 1,
            IsExhausted = true,
            ExhaustReason = CategoryExhaustReason.PoolExhausted,
        };
        state.CandidateScenarioIds.Add("scenario-a");
        state.RejectCounters["candidate-pool-exhausted"] = 1;
        context.AllowedCategoryIds.Add("social");
        context.CategoryStateById["social"] = state;
        context.SuccessfulBuilds.Add(ScenarioBuildResult.Success(
            "scenario-a",
            "social",
            [new ScenarioSlotBuildResult("owner", IntentionsPrototypeConstants.Primary, "primary", new EntityUid(1), new EntityUid(1001), true, ScenarioSlotBuildState.Assigned)],
            [],
            []));
        context.RejectReasons.Add(new ScenarioRejectReason("scenario-b", "social", "global-predicates-failed", "No match."));

        var report = IntentionsDebugFormatters.FormatWavePreview(new StartWaveResult(context));

        Assert.That(report, Does.Contain("kind=Refill"));
        Assert.That(report, Does.Contain("baseline=5"));
        Assert.That(report, Does.Contain("target=2"));
        Assert.That(report, Does.Contain("scenario=scenario-a"));
        Assert.That(report, Does.Contain("global-predicates-failed"));
    }

    [Test]
    public void SnapshotPreviewPrintsRoundFactsAndCandidates()
    {
        var snapshot = IntentionsSnapshotFactory.Build(
            IntentionsSnapshotRequest.Refill(4),
            "Extended",
            TimeSpan.FromMinutes(12),
            "NTVG Test",
            ["FestiveSeason"],
            [
                new CandidateFacts(
                    new EntityUid(7),
                    new NetUserId(Guid.Parse("00000000-0000-0000-0000-000000000007")),
                    new EntityUid(1007),
                    "Test Candidate",
                    "Passenger",
                    "Civilian",
                    28,
                    "Human",
                    "Male",
                    ["Blindness"],
                    false,
                    ["Traitor"],
                    ["EscapeAlive"],
                    false),
            ],
            TimeSpan.FromMinutes(12),
            "snapshot-refill")
            .Snapshot!;

        var report = IntentionsDebugFormatters.FormatSnapshotPreview(snapshot);

        Assert.That(report, Does.Contain("snapshot-refill"));
        Assert.That(report, Does.Contain("kind=Refill"));
        Assert.That(report, Does.Contain("Event tags: FestiveSeason"));
        Assert.That(report, Does.Contain("Antag roles:"));
        Assert.That(report, Does.Contain("mind=7"));
        Assert.That(report, Does.Contain("job=Passenger"));
    }

    [Test]
    public void PredicateDictionaryPrintsSourceNotesAndValues()
    {
        var dictionary = new IntentionsPredicateDictionary(
            "EventTags",
            "Loaded holiday prototypes.",
            ImmutableArray.Create("FestiveSeason", "NewYear"),
            "Snapshot preview includes only the holidays that are active right now.");

        var report = IntentionsDebugFormatters.FormatPredicateDictionary(dictionary);

        Assert.That(report, Does.Contain("Intentions predicate dictionary: EventTags"));
        Assert.That(report, Does.Contain("Loaded holiday prototypes."));
        Assert.That(report, Does.Contain("active right now"));
        Assert.That(report, Does.Contain("- FestiveSeason"));
        Assert.That(report, Does.Contain("- NewYear"));
    }

    [Test]
    public void RegistryDumpReportsConsistencyAndIndexes()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: true);
        var report = IntentionsDebugFormatters.FormatRegistryDump(fixture.Registry);

        Assert.That(report, Does.Contain("Consistency: ok"));
        Assert.That(report, Does.Contain("Scenarios: 1"));
        Assert.That(report, Does.Contain("Hidden reveal buckets: 1"));

        fixture.Registry.DetachIntentionFromMind(fixture.OwnerMindId, fixture.OwnerIntentionUid);
        var brokenReport = IntentionsDebugFormatters.FormatRegistryDump(fixture.Registry);

        Assert.That(brokenReport, Does.Contain("Consistency issues:"));
        Assert.That(brokenReport, Does.Contain("missing from mind index"));
    }

    [Test]
    public void MindShowPrintsUiReadModel()
    {
        var state = new IntentionsEuiState(
            IntentionsEuiMode.Admin,
            "Test Mind",
            TimeSpan.FromMinutes(9),
            [
                new IntentionsCardView(
                    1,
                    10,
                    "primary",
                    "scenario",
                    "social",
                    "owner",
                    IntentionsPrototypeConstants.Primary,
                    "Primary title",
                    "Author",
                    "Summary",
                    "Description",
                    null,
                    "Copy me",
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    false,
                    "Revealed",
                    null,
                    "Active",
                    "Active",
                    "Assigned",
                    1,
                    TimeSpan.FromMinutes(5),
                    new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/sentient.svg.192dpi.png")),
                    "#FFFFFFFF"),
            ],
            [],
            [
                new IntentionsScenarioAdminView(10, "scenario", "social", "Active", "owner", 1, 1001, 1, TimeSpan.FromMinutes(5)),
            ]);

        var report = IntentionsDebugFormatters.FormatMindShow(state);

        Assert.That(report, Does.Contain("Test Mind"));
        Assert.That(report, Does.Contain("Primary title"));
        Assert.That(report, Does.Contain("copyable=yes"));
        Assert.That(report, Does.Contain("Admin scenario metadata"));
    }

    [Test]
    public void RevealDumpPrintsDueTimers()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: true);
        var report = IntentionsDebugFormatters.FormatRevealDump(fixture.Registry, TimeSpan.FromMinutes(30));

        Assert.That(report, Does.Contain("state=due"));
        Assert.That(report, Does.Contain("hidden=True"));
        Assert.That(report, Does.Contain("mode=Timer"));
    }

    [Test]
    public void RuntimeRevealFormatterPrintsScopeAndEntries()
    {
        var result = RevealHiddenIntentionsRuntimeResult.Success(
            IntentionsRuntimeRevealScope.One,
            new EntityUid(7),
            new IntentionInstanceUid(11),
            "Revealed hidden intention 11 for mind 7.",
            [
                new RevealedRuntimeIntentionResult(
                    new IntentionInstanceUid(11),
                    new ScenarioInstanceUid(4),
                    new EntityUid(7),
                    new EntityUid(1007),
                    IntentionRevealMode.Timer,
                    TimeSpan.FromMinutes(20),
                    TimeSpan.FromMinutes(12)),
            ]);

        var report = IntentionsDebugFormatters.FormatRuntimeReveal(result);

        Assert.That(report, Does.Contain("Intentions runtime reveal"));
        Assert.That(report, Does.Contain("scope=One"));
        Assert.That(report, Does.Contain("intentionUid=11"));
        Assert.That(report, Does.Contain("mode=Timer"));
        Assert.That(report, Does.Contain("revealedAt=00:12:00"));
    }

    [Test]
    public void RuntimeAssignFormatterPrintsIgnoredPredicatesFlag()
    {
        var result = ForceAssignScenarioRuntimeResult.Failure(
            "scenario-a",
            -7,
            "mind-not-in-snapshot",
            "Mind 99 is not present in the current Intentions snapshot candidate set for slot 'owner'.",
            expectedArgumentLayout: "owner=<mindId>",
            ignoredPredicates: true);

        var report = IntentionsDebugFormatters.FormatRuntimeAssign(result);

        Assert.That(report, Does.Contain("Intentions runtime assign"));
        Assert.That(report, Does.Contain("ignoredPredicates=yes"));
        Assert.That(report, Does.Contain("mind-not-in-snapshot"));
    }

    [Test]
    public void WaveTimerPrintsRemainingRefillTime()
    {
        var report = IntentionsDebugFormatters.FormatWaveTimer(new IntentionsDistributionScheduleStatus(
            StartWaveFinished: true,
            StartWavePending: false,
            NextWaveId: 3,
            CurrentTime: TimeSpan.FromMinutes(10),
            NextStartWaveAttempt: null,
            NextRefillWaveAttempt: TimeSpan.FromMinutes(19)));

        Assert.That(report, Does.Contain("start wave finished: yes"));
        Assert.That(report, Does.Contain("next wave id: 3"));
        Assert.That(report, Does.Contain("next refill wave in: 00:09:00"));
    }

    [Test]
    public void SafeWaveRunArgsRejectLegacySyntax()
    {
        var shell = new TestConsoleShell();

        var parsed = IntentionsDebugCommandHelpers.TryParseSafeWaveRunArgs(shell, ["start", "1", "12345"], out _, out _);

        Assert.That(parsed, Is.False);
        Assert.That(shell.Errors, Has.Count.EqualTo(1));
        Assert.That(shell.Errors[0], Does.Contain("intentions.test.wave.run"));
    }

    [Test]
    public void SafeWaveRunArgsAcceptSingleSeed()
    {
        var shell = new TestConsoleShell();

        var parsed = IntentionsDebugCommandHelpers.TryParseSafeWaveRunArgs(shell, ["refill", "12345"], out var kind, out var seed);

        Assert.That(parsed, Is.True);
        Assert.That(kind, Is.EqualTo("refill"));
        Assert.That(seed, Is.EqualTo(12345));
        Assert.That(shell.Errors, Is.Empty);
    }

    [Test]
    public void ParseIntentionUidAcceptsLongRuntimeIds()
    {
        var parsed = IntentionsDebugCommandHelpers.TryParseIntentionUid("42", out var intentionUid);

        Assert.That(parsed, Is.True);
        Assert.That(intentionUid, Is.EqualTo(new IntentionInstanceUid(42)));
    }

    private sealed class TestConsoleShell : IConsoleShell
    {
        public IConsoleHost ConsoleHost { get; } = new TestConsoleHost();
        public bool IsLocal => true;
        public bool IsServer => true;
        public ICommonSession? Player => null;
        public List<string> Lines { get; } = [];
        public List<string> Errors { get; } = [];

        public void ExecuteCommand(string command)
        {
            throw new NotSupportedException();
        }

        public void RemoteExecuteCommand(string command)
        {
            throw new NotSupportedException();
        }

        public void WriteLine(string text)
        {
            Lines.Add(text);
        }

        public void WriteLine(FormattedMessage message)
        {
            Lines.Add(message.ToString());
        }

        public void WriteError(string text)
        {
            Errors.Add(text);
        }

        public void Clear()
        {
            Lines.Clear();
            Errors.Clear();
        }
    }

    private sealed class TestConsoleHost : IConsoleHost
    {
        public bool IsServer => true;
        public IConsoleShell LocalShell => throw new NotSupportedException();
        public IReadOnlyDictionary<string, IConsoleCommand> AvailableCommands => throw new NotSupportedException();
        public event ConAnyCommandCallback? AnyCommandExecuted;
        public event EventHandler? ClearText;

        public void LoadConsoleCommands() => throw new NotSupportedException();
        public void RegisterCommand(string command, string description, string help, ConCommandCallback callback, bool requireServerOrSingleplayer = false) => throw new NotSupportedException();
        public void RegisterCommand(string command, string description, string help, ConCommandCallback callback, ConCommandCompletionCallback completionCallback, bool requireServerOrSingleplayer = false) => throw new NotSupportedException();
        public void RegisterCommand(string command, string description, string help, ConCommandCallback callback, ConCommandCompletionAsyncCallback completionCallback, bool requireServerOrSingleplayer = false) => throw new NotSupportedException();
        public void RegisterCommand(string command, ConCommandCallback callback, bool requireServerOrSingleplayer = false) => throw new NotSupportedException();
        public void RegisterCommand(string command, ConCommandCallback callback, ConCommandCompletionCallback completionCallback, bool requireServerOrSingleplayer = false) => throw new NotSupportedException();
        public void RegisterCommand(string command, ConCommandCallback callback, ConCommandCompletionAsyncCallback completionCallback, bool requireServerOrSingleplayer = false) => throw new NotSupportedException();
        public void RegisterCommand(IConsoleCommand command) => throw new NotSupportedException();
        public void BeginRegistrationRegion() => throw new NotSupportedException();
        public void EndRegistrationRegion() => throw new NotSupportedException();
        public void UnregisterCommand(string command) => throw new NotSupportedException();
        public IConsoleShell GetSessionShell(ICommonSession session) => throw new NotSupportedException();
        public void ExecuteCommand(string command) => throw new NotSupportedException();
        public void AppendCommand(string command) => throw new NotSupportedException();
        public void InsertCommand(string command) => throw new NotSupportedException();
        public void CommandBufferExecute() => throw new NotSupportedException();
        public void ExecuteCommand(ICommonSession? session, string command) => throw new NotSupportedException();
        public void RemoteExecuteCommand(ICommonSession? session, string command) => throw new NotSupportedException();
        public void WriteLine(ICommonSession? session, string text) => throw new NotSupportedException();
        public void WriteLine(ICommonSession? session, FormattedMessage msg) => throw new NotSupportedException();
        public void WriteError(ICommonSession? session, string text) => throw new NotSupportedException();
        public void ClearLocalConsole() => throw new NotSupportedException();
    }
}
