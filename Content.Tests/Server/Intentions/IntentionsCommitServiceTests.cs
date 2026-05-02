using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.Intentions.Runtime;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.Validation;
using Content.Shared.Intentions.Waves;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsCommitService))]
/// <summary>
/// Covers atomic commit behavior, resolved text bindings, and rollback guarantees for Intentions commits.
/// </summary>
public sealed class IntentionsCommitServiceTests
{
    [Test]
    public void ResolvesTextParametersFromSelfSlotRoundAndLiteral()
    {
        var fixture = Fixture(entry =>
        {
            entry.TextParameterBindings = new Dictionary<string, TextParameterBindingDefinition>
            {
                ["selfName"] = Binding("self", field: "characterName"),
                ["helperName"] = Binding("slot", slotId: "helper", field: "characterName"),
                ["station"] = Binding("round", field: "stationName"),
                ["literal"] = Binding("literal", value: "fixed"),
            };
        });

        var result = Commit(fixture);
        var owner = result.IntentionInstances.Single(intention => intention.SlotId == "owner");

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(owner.ResolvedTextParameters["selfName"], Is.EqualTo("Owner Person"));
        Assert.That(owner.ResolvedTextParameters["helperName"], Is.EqualTo("Helper Person"));
        Assert.That(owner.ResolvedTextParameters["station"], Is.EqualTo("Test Station"));
        Assert.That(owner.ResolvedTextParameters["literal"], Is.EqualTo("fixed"));
    }

    [Test]
    public void CopyableTextResolvedAtCommitTime()
    {
        var fixture = Fixture(entry =>
        {
            entry.TextParameterBindings = new Dictionary<string, TextParameterBindingDefinition>
            {
                ["selfName"] = Binding("self", field: "characterName"),
            };
        });
        fixture.PrimaryIntention.CopyableTextLoc = "copy-loc";
        var service = new IntentionsCommitService((loc, parameters) => $"{loc}:{parameters["selfName"]}");

        var result = Commit(fixture, service);
        var owner = result.IntentionInstances.Single(intention => intention.SlotId == "owner");

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(owner.CopyableTextResolved, Is.EqualTo("copy-loc:Owner Person"));
        Assert.That(owner.ResolvedTextParameters["selfName"], Is.EqualTo("Owner Person"));
    }

    [Test]
    public void ResolvesInitialVisibilityAndOverrides()
    {
        var fixture = Fixture(owner =>
        {
            owner.VisibilityOverride = new VisibilityOverrideDefinition
            {
                Type = IntentionsPrototypeConstants.Hidden,
            };
        }, helper =>
        {
            helper.VisibilityOverride = new VisibilityOverrideDefinition
            {
                Type = IntentionsPrototypeConstants.Visible,
            };
        });
        fixture.SecondaryIntention.DefaultVisibility = IntentionsPrototypeConstants.Hidden;

        var result = Commit(fixture);
        var owner = result.IntentionInstances.Single(intention => intention.SlotId == "owner");
        var helper = result.IntentionInstances.Single(intention => intention.SlotId == "helper");

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(owner.IsHidden, Is.True);
        Assert.That(helper.IsHidden, Is.False);
    }

    [Test]
    public void HiddenTimerInsertsRevealIndex()
    {
        var fixture = Fixture(owner =>
        {
            owner.VisibilityOverride = new VisibilityOverrideDefinition
            {
                Type = IntentionsPrototypeConstants.Hidden,
                Reveal = new RevealDefinition
                {
                    Type = IntentionsPrototypeConstants.RevealTimer,
                    Minutes = 15,
                },
            };
        });
        var registry = new IntentionsRuntimeRegistry();

        var result = Commit(fixture, registry: registry);
        var owner = result.IntentionInstances.Single(intention => intention.SlotId == "owner");
        var revealTime = fixture.WaveContext.StartedAtRoundTime + TimeSpan.FromMinutes(15);

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(owner.IsHidden, Is.True);
        Assert.That(owner.RevealMode, Is.EqualTo(IntentionRevealMode.Timer));
        Assert.That(owner.RevealedAtRoundTime, Is.EqualTo(revealTime));
        Assert.That(registry.HiddenIntentionsByRevealTime[revealTime], Does.Contain(owner.Uid));
    }

    [Test]
    public void RejectsUnsuccessfulBuild()
    {
        var fixture = Fixture();
        var failedBuild = ScenarioBuildResult.Failure(fixture.Scenario.Template.ID, fixture.Scenario.Template.Category, "failed", [], [], []);

        var result = new IntentionsCommitService().CommitScenarioBuild(
            failedBuild,
            fixture.Scenario,
            fixture.Catalog,
            fixture.Snapshot,
            fixture.WaveContext,
            new IntentionsRuntimeRegistry());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureReason, Is.EqualTo("build-result-is-not-successful"));
    }

    [Test]
    public void RejectsDuplicateScenarioTemplateId()
    {
        var fixture = Fixture();
        var registry = new IntentionsRuntimeRegistry();
        registry.AddAssignedScenarioId(fixture.Scenario.Template.ID);

        var result = Commit(fixture, registry: registry);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureReason, Is.EqualTo("scenario-already-assigned"));
    }

    [Test]
    public void RollbackLeavesRegistryEmptyAfterFailureAtEachCommitStep()
    {
        foreach (var point in Enum.GetValues<CommitFailurePoint>())
        {
            var fixture = Fixture(owner =>
            {
                owner.VisibilityOverride = new VisibilityOverrideDefinition
                {
                    Type = IntentionsPrototypeConstants.Hidden,
                    Reveal = new RevealDefinition
                    {
                        Type = IntentionsPrototypeConstants.RevealTimer,
                        Minutes = 1,
                    },
                };
            });
            var registry = new IntentionsRuntimeRegistry();
            var service = new IntentionsCommitService(failureHook: current => current == point);

            var result = Commit(fixture, service, registry);

            Assert.That(result.IsSuccess, Is.False, point.ToString());
            Assert.That(result.RollbackCompleted, Is.True, point.ToString());
            AssertRegistryEmpty(registry, point.ToString());
        }
    }

    private static CommitScenarioBuildResult Commit(
        CommitFixture fixture,
        IntentionsCommitService? service = null,
        IntentionsRuntimeRegistry? registry = null)
    {
        return (service ?? new IntentionsCommitService()).CommitScenarioBuild(
            fixture.Build,
            fixture.Scenario,
            fixture.Catalog,
            fixture.Snapshot,
            fixture.WaveContext,
            registry ?? new IntentionsRuntimeRegistry());
    }

    private static CommitFixture Fixture(
        Action<ScenarioEntry>? configureOwner = null,
        Action<ScenarioEntry>? configureHelper = null)
    {
        var primary = Intention("primary", IntentionsPrototypeConstants.Primary);
        var secondary = Intention("secondary", IntentionsPrototypeConstants.Secondary);
        var owner = Entry("owner", IntentionsPrototypeConstants.Primary, "primary");
        configureOwner?.Invoke(owner);
        var helper = Entry("helper", IntentionsPrototypeConstants.Secondary, "secondary");
        configureHelper?.Invoke(helper);
        var scenarioTemplate = Scenario("scenario", [owner, helper]);
        var scenario = new ValidatedScenarioTemplate(scenarioTemplate, ["owner", "helper"]);
        var catalog = new ValidationCatalog();
        catalog.ValidIntentions[primary.ID] = primary;
        catalog.ValidIntentions[secondary.ID] = secondary;
        catalog.ValidScenarios[scenarioTemplate.ID] = scenario;
        catalog.ValidScenarioOrder.Add(scenarioTemplate.ID);

        var snapshot = Snapshot([
            Candidate(1, 1, "Owner Person"),
            Candidate(2, 2, "Helper Person"),
        ]);
        var build = ScenarioBuildResult.Success(
            scenarioTemplate.ID,
            scenarioTemplate.Category,
            [
                new ScenarioSlotBuildResult("owner", IntentionsPrototypeConstants.Primary, "primary", new EntityUid(1), new EntityUid(1001), true, ScenarioSlotBuildState.Assigned),
                new ScenarioSlotBuildResult("helper", IntentionsPrototypeConstants.Secondary, "secondary", new EntityUid(2), new EntityUid(1002), true, ScenarioSlotBuildState.Assigned),
            ],
            [],
            []);
        var waveContext = new DistributionWaveContext(7, snapshot.SnapshotId, 123, TimeSpan.FromMinutes(20), snapshot.RoundFacts.CrewCount);

        return new CommitFixture(primary, secondary, scenario, catalog, snapshot, build, waveContext);
    }

    private static TextParameterBindingDefinition Binding(string source, string? slotId = null, string? field = null, string? value = null)
    {
        return new TextParameterBindingDefinition
        {
            Source = source,
            SlotId = slotId,
            Field = field,
            Value = value,
        };
    }

    private static IntentionTemplatePrototype Intention(string id, string kind)
    {
        var intention = new IntentionTemplatePrototype
        {
            Kind = kind,
            NameLoc = $"{id}-name",
            DescriptionLoc = $"{id}-description",
            DefaultVisibility = IntentionsPrototypeConstants.Visible,
        };
        SetId(intention, id);
        return intention;
    }

    private static ScenarioEntry Entry(string slotId, string kind, string intentionId)
    {
        return new ScenarioEntry
        {
            SlotId = slotId,
            Kind = kind,
            IntentionId = intentionId,
            Required = true,
        };
    }

    private static ScenarioTemplatePrototype Scenario(string id, List<ScenarioEntry> entries)
    {
        var scenario = new ScenarioTemplatePrototype
        {
            Name = id,
            Category = "social",
            Enabled = true,
            Weight = 1,
            Entries = entries,
        };
        SetId(scenario, id);
        return scenario;
    }

    private static IntentionsSnapshot Snapshot(IReadOnlyList<CandidateFacts> candidates)
    {
        var result = IntentionsSnapshotFactory.Build(
            IntentionsSnapshotRequest.Start(7),
            "extended",
            TimeSpan.FromMinutes(20),
            "Test Station",
            [],
            candidates,
            TimeSpan.FromMinutes(20),
            "commit-test-snapshot");

        Assert.That(result.IsSuccess, Is.True);
        return result.Snapshot!;
    }

    private static CandidateFacts Candidate(int mindId, int userId, string name)
    {
        return new CandidateFacts(
            new EntityUid(mindId),
            new NetUserId(Guid.Parse($"00000000-0000-0000-0000-{userId:000000000000}")),
            new EntityUid(1000 + mindId),
            name,
            "Assistant",
            "Civilian",
            30,
            "Human",
            "Male");
    }

    private static void AssertRegistryEmpty(IntentionsRuntimeRegistry registry, string message)
    {
        Assert.That(registry.ScenarioByUid, Is.Empty, message);
        Assert.That(registry.IntentionByUid, Is.Empty, message);
        Assert.That(registry.IntentionIdsByMind, Is.Empty, message);
        Assert.That(registry.ScenarioUidByIntentionUid, Is.Empty, message);
        Assert.That(registry.SlotAssignmentByScenarioAndSlot, Is.Empty, message);
        Assert.That(registry.AssignedScenarioIds, Is.Empty, message);
        Assert.That(registry.AssignedPrimaryByMind, Is.Empty, message);
        Assert.That(registry.WaveContextByWaveId, Is.Empty, message);
        Assert.That(registry.HiddenIntentionsByRevealTime, Is.Empty, message);
    }

    private static void SetId<T>(T prototype, string id)
    {
        typeof(T).GetProperty("ID", BindingFlags.Instance | BindingFlags.Public)!.SetValue(prototype, id);
    }

    private sealed record CommitFixture(
        IntentionTemplatePrototype PrimaryIntention,
        IntentionTemplatePrototype SecondaryIntention,
        ValidatedScenarioTemplate Scenario,
        ValidationCatalog Catalog,
        IntentionsSnapshot Snapshot,
        ScenarioBuildResult Build,
        DistributionWaveContext WaveContext);
}
