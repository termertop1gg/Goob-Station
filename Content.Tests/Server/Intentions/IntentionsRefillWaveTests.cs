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
/// Covers refill quota math, baseline calculation, and duplicate-scenario prevention.
/// </summary>
public sealed class IntentionsRefillWaveTests
{
    [Test]
    public void DeficitCountsActiveAndFrozen()
    {
        var registry = new IntentionsRuntimeRegistry();
        IntentionsLifecycleServiceTests.Fixture("active", registry: registry, ownerMindId: new EntityUid(1));
        var frozen = IntentionsLifecycleServiceTests.Fixture("frozen", registry: registry, ownerMindId: new EntityUid(2));
        new IntentionsLifecycleService().FreezeScenarioWithMissingOwner(registry, frozen.ScenarioUid, TimeSpan.FromMinutes(10), "owner-left-at-refill");
        var catalog = Catalog(
            [Category("social", quota: 3, maxPrimary: 5)],
            [Scenario("new", "social")],
            [Intention("primary", IntentionsPrototypeConstants.Primary)]);

        var result = new IntentionsWaveOrchestrator().RunRefillWave(
            catalog,
            Snapshot(IntentionsSnapshotRequest.Refill(2), [Candidate(3, 3)]),
            new IntentionsRefillWaveRequest(2, seed: 1),
            registry);

        var state = result.Context.CategoryStateById["social"];
        Assert.That(state.DesiredQuota, Is.EqualTo(3));
        Assert.That(state.ExistingActiveFrozenCount, Is.EqualTo(2));
        Assert.That(state.RefillTarget, Is.EqualTo(1));
        Assert.That(result.Context.SuccessfulBuilds, Has.Count.EqualTo(1));
    }

    [Test]
    public void CancelledScenarioIsNotCountedAsFilledQuota()
    {
        var registry = new IntentionsRuntimeRegistry();
        var cancelled = IntentionsLifecycleServiceTests.Fixture("cancelled", registry: registry, ownerMindId: new EntityUid(1));
        new IntentionsLifecycleService().CancelScenario(registry, cancelled.ScenarioUid, TimeSpan.FromMinutes(10), "owner-dead");
        var catalog = Catalog(
            [Category("social", quota: 1, maxPrimary: 5)],
            [Scenario("replacement", "social")],
            [Intention("primary", IntentionsPrototypeConstants.Primary)]);

        var result = new IntentionsWaveOrchestrator().RunRefillWave(
            catalog,
            Snapshot(IntentionsSnapshotRequest.Refill(2), [Candidate(2, 2)]),
            new IntentionsRefillWaveRequest(2, seed: 1),
            registry);

        var state = result.Context.CategoryStateById["social"];
        Assert.That(state.ExistingActiveFrozenCount, Is.Zero);
        Assert.That(state.RefillTarget, Is.EqualTo(1));
        Assert.That(result.Context.SuccessfulBuilds.Single().ScenarioTemplateId, Is.EqualTo("replacement"));
    }

    [Test]
    public void RefillTargetIsZeroWhenQuotaIsAlreadyFilled()
    {
        var registry = new IntentionsRuntimeRegistry();
        IntentionsLifecycleServiceTests.Fixture("active", registry: registry, ownerMindId: new EntityUid(1));
        var catalog = Catalog(
            [Category("social", quota: 1, maxPrimary: 5)],
            [Scenario("new", "social")],
            [Intention("primary", IntentionsPrototypeConstants.Primary)]);

        var result = new IntentionsWaveOrchestrator().RunRefillWave(
            catalog,
            Snapshot(IntentionsSnapshotRequest.Refill(2), [Candidate(2, 2)]),
            new IntentionsRefillWaveRequest(2, seed: 1),
            registry);

        var state = result.Context.CategoryStateById["social"];
        Assert.That(state.RefillTarget, Is.Zero);
        Assert.That(state.ExhaustReason, Is.EqualTo(CategoryExhaustReason.QuotaFilled));
        Assert.That(result.Context.SuccessfulBuilds, Is.Empty);
    }

    [Test]
    public void RefillDoesNotSelectAlreadyAssignedScenarioIds()
    {
        var registry = new IntentionsRuntimeRegistry();
        registry.AddAssignedScenarioId("used");
        var catalog = Catalog(
            [Category("social", quota: 1, maxPrimary: 5)],
            [Scenario("used", "social"), Scenario("new", "social")],
            [Intention("primary", IntentionsPrototypeConstants.Primary)]);

        var result = new IntentionsWaveOrchestrator().RunRefillWave(
            catalog,
            Snapshot(IntentionsSnapshotRequest.Refill(2), [Candidate(1, 1)]),
            new IntentionsRefillWaveRequest(2, seed: 1),
            registry);

        Assert.That(result.Context.SuccessfulBuilds.Single().ScenarioTemplateId, Is.EqualTo("new"));
    }

    [Test]
    public void RefillBaselineUsesMaxPreviousBaselineAndCurrentCrew()
    {
        var registry = new IntentionsRuntimeRegistry();
        registry.SetWaveContext(new DistributionWaveContext(
            1,
            "previous",
            1,
            TimeSpan.FromMinutes(5),
            waveActiveCrew: 2,
            distributionCrewBaseline: 10), out _);
        var catalog = Catalog(
            [Category("social", QuotaRatio(0.5f), maxPrimary: 5)],
            [],
            [Intention("primary", IntentionsPrototypeConstants.Primary)]);

        var result = new IntentionsWaveOrchestrator().RunRefillWave(
            catalog,
            Snapshot(IntentionsSnapshotRequest.Refill(2), [Candidate(1, 1), Candidate(2, 2)]),
            new IntentionsRefillWaveRequest(2, seed: 1),
            registry);

        var state = result.Context.CategoryStateById["social"];
        Assert.That(result.Context.DistributionCrewBaseline, Is.EqualTo(10));
        Assert.That(state.DesiredQuota, Is.EqualTo(5));
    }

    internal static ValidationCatalog Catalog(
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

    internal static ScenarioCategoryPrototype Category(string id, int quota, int maxPrimary)
    {
        return Category(id, QuotaFixed(quota), maxPrimary);
    }

    internal static ScenarioCategoryPrototype Category(string id, QuotaRule quota, int maxPrimary)
    {
        var category = new ScenarioCategoryPrototype
        {
            Priority = 1,
            QuotaByGameMode = new Dictionary<string, QuotaRule>
            {
                ["default"] = quota,
            },
            MaxPrimaryPerMindByGameMode = new Dictionary<string, int>
            {
                ["default"] = maxPrimary,
            },
        };
        SetId(category, id);
        return category;
    }

    internal static ScenarioTemplatePrototype Scenario(string id, string categoryId)
    {
        var scenario = new ScenarioTemplatePrototype
        {
            Name = id,
            Category = categoryId,
            Enabled = true,
            Weight = 1,
            Entries =
            [
                new ScenarioEntry
                {
                    SlotId = "owner",
                    Kind = IntentionsPrototypeConstants.Primary,
                    IntentionId = "primary",
                    Required = true,
                },
            ],
        };
        SetId(scenario, id);
        return scenario;
    }

    internal static IntentionTemplatePrototype Intention(string id, string kind)
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

    internal static IntentionsSnapshot Snapshot(IntentionsSnapshotRequest request, IReadOnlyList<CandidateFacts> candidates)
    {
        var result = IntentionsSnapshotFactory.Build(
            request,
            "extended",
            TimeSpan.FromMinutes(15),
            "Test Station",
            [],
            candidates,
            TimeSpan.FromMinutes(15),
            $"refill-test-snapshot-{request.WaveId}");

        Assert.That(result.IsSuccess, Is.True);
        return result.Snapshot!;
    }

    internal static CandidateFacts Candidate(int mindId, int userId)
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

    internal static QuotaRule QuotaFixed(int value)
    {
        return new QuotaRule
        {
            Mode = "fixed",
            Value = value,
        };
    }

    internal static QuotaRule QuotaRatio(float ratio)
    {
        return new QuotaRule
        {
            Mode = "ratio",
            Ratio = ratio,
        };
    }

    private static void SetId<T>(T prototype, string id)
    {
        typeof(T).GetProperty("ID", BindingFlags.Instance | BindingFlags.Public)!.SetValue(prototype, id);
    }
}
