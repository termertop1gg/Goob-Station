using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.Intentions.Runtime;
using Content.Server.Intentions.Waves;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.Validation;
using Content.Shared.Intentions.Waves;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsWaveOrchestrator))]
/// <summary>
/// Covers committed wave execution against the runtime registry across multiple waves.
/// </summary>
public sealed class IntentionsWaveCommitIntegrationTests
{
    [Test]
    public void CommittedStartWaveCreatesFullScenarioInRegistry()
    {
        var catalog = Catalog(
            [Category("social", quota: 1, maxPrimary: 5)],
            [Scenario("scenario", "social", withHelper: true)],
            [Intention("primary", IntentionsPrototypeConstants.Primary), Intention("secondary", IntentionsPrototypeConstants.Secondary)]);
        var registry = new IntentionsRuntimeRegistry();

        var result = RunCommitted(catalog, registry, Snapshot([Candidate(1, 1), Candidate(2, 2)]));

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(registry.ScenarioByUid, Has.Count.EqualTo(1));
        Assert.That(registry.IntentionByUid, Has.Count.EqualTo(2));
        Assert.That(registry.SlotAssignmentByScenarioAndSlot, Has.Count.EqualTo(2));
        Assert.That(registry.AssignedScenarioIds, Does.Contain("scenario"));
    }

    [Test]
    public void CommitFailureBecomesRejectAndWaveCanContinue()
    {
        var catalog = Catalog(
            [Category("social", quota: 1, maxPrimary: 5)],
            [Scenario("first", "social"), Scenario("second", "social")],
            [Intention("primary", IntentionsPrototypeConstants.Primary)]);
        var registry = new IntentionsRuntimeRegistry();
        var failedOnce = false;
        var commitService = new IntentionsCommitService(failureHook: point =>
        {
            if (failedOnce || point != CommitFailurePoint.AfterScenarioSaved)
                return false;

            failedOnce = true;
            return true;
        });

        var result = RunCommitted(catalog, registry, Snapshot([Candidate(1, 1), Candidate(2, 2)]), commitService);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Context.RejectReasons.Any(reason => reason.Code == CommitFailurePoint.AfterScenarioSaved.ToString()), Is.True);
        Assert.That(registry.ScenarioByUid, Has.Count.EqualTo(1));
        Assert.That(result.Context.SuccessfulBuilds, Has.Count.EqualTo(1));
    }

    [Test]
    public void SameScenarioTemplateIsNotAssignedTwiceAcrossCommittedWaves()
    {
        var catalog = Catalog(
            [Category("social", quota: 1, maxPrimary: 5)],
            [Scenario("scenario", "social")],
            [Intention("primary", IntentionsPrototypeConstants.Primary)]);
        var registry = new IntentionsRuntimeRegistry();
        var snapshot = Snapshot([Candidate(1, 1)]);

        RunCommitted(catalog, registry, snapshot, waveId: 1);
        var second = RunCommitted(catalog, registry, snapshot, waveId: 2);

        Assert.That(registry.ScenarioByUid, Has.Count.EqualTo(1));
        Assert.That(second.Context.SuccessfulBuilds, Is.Empty);
    }

    [Test]
    public void AssignedPrimaryByMindFromRegistryEnforcesCategoryLimitInLaterWave()
    {
        var catalog = Catalog(
            [Category("social", quota: 1, maxPrimary: 1)],
            [Scenario("first", "social"), Scenario("second", "social")],
            [Intention("primary", IntentionsPrototypeConstants.Primary)]);
        var registry = new IntentionsRuntimeRegistry();
        var snapshot = Snapshot([Candidate(1, 1)]);

        RunCommitted(catalog, registry, snapshot, waveId: 1);
        var second = RunCommitted(catalog, registry, snapshot, waveId: 2);

        Assert.That(registry.ScenarioByUid, Has.Count.EqualTo(1));
        Assert.That(second.Context.SuccessfulBuilds, Is.Empty);
        Assert.That(second.Context.RejectReasons.Any(reason => reason.Code == "no-candidate-for-required-slot"), Is.True);
    }

    private static StartWaveResult RunCommitted(
        ValidationCatalog catalog,
        IntentionsRuntimeRegistry registry,
        IntentionsSnapshot snapshot,
        IntentionsCommitService? commitService = null,
        int waveId = 1)
    {
        return new IntentionsWaveOrchestrator().RunStartWaveAndCommit(
            catalog,
            snapshot,
            new IntentionsStartWaveRequest(waveId, seed: 1),
            registry,
            commitService ?? new IntentionsCommitService());
    }

    private static ValidationCatalog Catalog(
        IReadOnlyList<ScenarioCategoryPrototype> categories,
        IReadOnlyList<ScenarioTemplatePrototype> scenarios,
        IReadOnlyList<IntentionTemplatePrototype> intentions)
    {
        var catalog = new ValidationCatalog();

        foreach (var category in categories)
        {
            catalog.ValidCategories[category.ID] = category;
            catalog.ValidCategoryOrder.Add(category.ID);
        }

        foreach (var intention in intentions)
            catalog.ValidIntentions[intention.ID] = intention;

        foreach (var scenario in scenarios)
        {
            catalog.ValidScenarios[scenario.ID] = new ValidatedScenarioTemplate(scenario, scenario.Entries.Select(entry => entry.SlotId).ToArray());
            catalog.ValidScenarioOrder.Add(scenario.ID);
        }

        return catalog;
    }

    private static ScenarioCategoryPrototype Category(string id, int quota, int maxPrimary)
    {
        var category = new ScenarioCategoryPrototype
        {
            Priority = 1,
            QuotaByGameMode = new Dictionary<string, QuotaRule>
            {
                ["default"] = new()
                {
                    Mode = "fixed",
                    Value = quota,
                },
            },
            MaxPrimaryPerMindByGameMode = new Dictionary<string, int>
            {
                ["default"] = maxPrimary,
            },
        };
        SetId(category, id);
        return category;
    }

    private static ScenarioTemplatePrototype Scenario(string id, string categoryId, bool withHelper = false)
    {
        var entries = new List<ScenarioEntry>
        {
            new()
            {
                SlotId = "owner",
                Kind = IntentionsPrototypeConstants.Primary,
                IntentionId = "primary",
                Required = true,
            },
        };

        if (withHelper)
        {
            entries.Add(new ScenarioEntry
            {
                SlotId = "helper",
                Kind = IntentionsPrototypeConstants.Secondary,
                IntentionId = "secondary",
                Required = true,
            });
        }

        var scenario = new ScenarioTemplatePrototype
        {
            Name = id,
            Category = categoryId,
            Enabled = true,
            Weight = 1,
            Entries = entries,
        };
        SetId(scenario, id);
        return scenario;
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

    private static IntentionsSnapshot Snapshot(IReadOnlyList<CandidateFacts> candidates)
    {
        var result = IntentionsSnapshotFactory.Build(
            IntentionsSnapshotRequest.Start(1),
            "extended",
            TimeSpan.FromMinutes(15),
            "Test Station",
            [],
            candidates,
            TimeSpan.FromMinutes(15),
            "wave-commit-test-snapshot");

        Assert.That(result.IsSuccess, Is.True);
        return result.Snapshot!;
    }

    private static CandidateFacts Candidate(int mindId, int userId)
    {
        return new CandidateFacts(
            new EntityUid(mindId),
            new NetUserId(Guid.Parse($"00000000-0000-0000-0000-{userId:000000000000}")),
            new EntityUid(1000 + mindId),
            $"Candidate {mindId}",
            "Assistant",
            "Civilian",
            30,
            "Human",
            "Male");
    }

    private static void SetId<T>(T prototype, string id)
    {
        typeof(T).GetProperty("ID", BindingFlags.Instance | BindingFlags.Public)!.SetValue(prototype, id);
    }
}
