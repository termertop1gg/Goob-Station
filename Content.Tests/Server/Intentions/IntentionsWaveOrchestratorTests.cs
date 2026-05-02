using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.Intentions.Waves;
using Content.Shared.Intentions.Predicates;
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
/// Covers quota handling, category ordering, and scenario selection in the wave orchestrator.
/// </summary>
public sealed class IntentionsWaveOrchestratorTests
{
    [Test]
    public void CalculatesQuotaFixedRatioClampAndMaxPrimary()
    {
        var fixedCategory = Category("fixed", QuotaFixed(3));
        var ratioCategory = Category("ratio", QuotaRatio(0.3f));
        var clampedCategory = Category("clamped", QuotaClamp(0.9f, min: 2, max: 4));
        var modeCategory = Category("mode", QuotaFixed(1), maxPrimary: new Dictionary<string, int>
        {
            ["default"] = 1,
            ["extended"] = 2,
        });

        Assert.That(IntentionsQuotaCalculator.CalculateTargetQuota(fixedCategory, "extended", 5), Is.EqualTo(3));
        Assert.That(IntentionsQuotaCalculator.CalculateTargetQuota(ratioCategory, "extended", 5), Is.EqualTo(1));
        Assert.That(IntentionsQuotaCalculator.CalculateTargetQuota(clampedCategory, "extended", 5), Is.EqualTo(4));
        Assert.That(IntentionsQuotaCalculator.CalculateTargetQuota(clampedCategory, "extended", 1), Is.EqualTo(2));
        Assert.That(IntentionsQuotaCalculator.CalculateEffectiveMaxPrimaryPerMind(modeCategory, "extended"), Is.EqualTo(2));
        Assert.That(IntentionsQuotaCalculator.CalculateEffectiveMaxPrimaryPerMind(modeCategory, "traitor"), Is.EqualTo(1));
    }

    [Test]
    public void OrdersCategoriesByPriorityThenDeclarationOrder()
    {
        var low = Category("low", QuotaFixed(0), priority: 1);
        var highA = Category("highA", QuotaFixed(0), priority: 10);
        var highB = Category("highB", QuotaFixed(0), priority: 10);
        var catalog = Catalog([low, highA, highB], []);
        var result = new IntentionsWaveOrchestrator().RunStartWave(catalog, Snapshot([]), new IntentionsStartWaveRequest(1, seed: 10));

        Assert.That(result.Context.AllowedCategoryIds, Is.EqualTo(new[] { "highA", "highB", "low" }));
    }

    [Test]
    public void DeterministicWeightedScenarioSelectionUsesSeed()
    {
        var category = Category("social", QuotaFixed(2), maxPrimary: new Dictionary<string, int> { ["default"] = 10 });
        var scenarios = new[]
        {
            Scenario("scenarioA", "social", weight: 1),
            Scenario("scenarioB", "social", weight: 5),
            Scenario("scenarioC", "social", weight: 10),
        };
        var catalog = Catalog([category], scenarios.Select(scenario => Validated(scenario, "owner")));
        var snapshot = Snapshot([
            Candidate(1, 1),
            Candidate(2, 2),
            Candidate(3, 3),
        ]);

        var first = new IntentionsWaveOrchestrator().RunStartWave(catalog, snapshot, new IntentionsStartWaveRequest(1, seed: 9001));
        var second = new IntentionsWaveOrchestrator().RunStartWave(catalog, snapshot, new IntentionsStartWaveRequest(1, seed: 9001));

        Assert.That(first.IsSuccess, Is.True);
        Assert.That(second.IsSuccess, Is.True);
        Assert.That(first.Context.SuccessfulBuilds.Select(build => build.ScenarioTemplateId),
            Is.EqualTo(second.Context.SuccessfulBuilds.Select(build => build.ScenarioTemplateId)));
        Assert.That(first.Context.SuccessfulBuilds, Has.Count.EqualTo(2));
    }

    [Test]
    public void GlobalPredicatesExcludeScenarioAndWriteRejectReason()
    {
        var category = Category("social", QuotaFixed(1));
        var rejected = Scenario("rejected", "social", globalPredicates: [
            RoundPredicate("crewCount", ">=", value: "99"),
        ]);
        var accepted = Scenario("accepted", "social");
        var catalog = Catalog([category], [Validated(rejected, "owner"), Validated(accepted, "owner")]);

        var result = new IntentionsWaveOrchestrator().RunStartWave(
            catalog,
            Snapshot([Candidate(1, 1)]),
            new IntentionsStartWaveRequest(1, seed: 42));

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Context.SuccessfulBuilds.Single().ScenarioTemplateId, Is.EqualTo("accepted"));
        Assert.That(result.Context.RejectReasons.Any(reason =>
            reason.ScenarioTemplateId == "rejected" && reason.Code == "global-predicates-failed"), Is.True);
    }

    [Test]
    public void AlreadyAssignedScenarioIdIsExcludedFromPool()
    {
        var category = Category("social", QuotaFixed(1));
        var scenario = Scenario("alreadyAssigned", "social");
        var catalog = Catalog([category], [Validated(scenario, "owner")]);

        var result = new IntentionsWaveOrchestrator().RunStartWave(
            catalog,
            Snapshot([Candidate(1, 1)]),
            new IntentionsStartWaveRequest(1, seed: 42, assignedScenarioIds: ["alreadyAssigned"]));

        var state = result.Context.CategoryStateById["social"];
        Assert.That(result.Context.SuccessfulBuilds, Is.Empty);
        Assert.That(state.CandidateScenarioIds, Is.Empty);
        Assert.That(state.ExhaustReason, Is.EqualTo(CategoryExhaustReason.PoolEmpty));
    }

    [Test]
    public void RunStartWaveDoesNotMutateRequestCollectionsOrCatalog()
    {
        var category = Category("social", QuotaFixed(1), maxPrimary: new Dictionary<string, int> { ["default"] = 10 });
        var scenario = Scenario("scenario", "social");
        var catalog = Catalog([category], [Validated(scenario, "owner")]);
        var assignedScenarioIds = new HashSet<string>(StringComparer.Ordinal) { "external" };
        var assignedPrimaryByMind = new Dictionary<EntityUid, IReadOnlyDictionary<string, int>>
        {
            [new EntityUid(99)] = new Dictionary<string, int> { ["social"] = 1 },
        };
        var request = new IntentionsStartWaveRequest(1, seed: 42, assignedScenarioIds, assignedPrimaryByMind);

        new IntentionsWaveOrchestrator().RunStartWave(catalog, Snapshot([Candidate(1, 1)]), request);

        Assert.That(assignedScenarioIds, Is.EqualTo(new[] { "external" }));
        Assert.That(assignedPrimaryByMind[new EntityUid(99)]["social"], Is.EqualTo(1));
        Assert.That(catalog.ValidCategoryOrder, Is.EqualTo(new[] { "social" }));
        Assert.That(catalog.ValidScenarioOrder, Is.EqualTo(new[] { "scenario" }));
    }

    private static ValidationCatalog Catalog(
        IReadOnlyList<ScenarioCategoryPrototype> categories,
        IEnumerable<ValidatedScenarioTemplate> scenarios)
    {
        var catalog = new ValidationCatalog();

        foreach (var category in categories)
        {
            catalog.ValidCategories[category.ID] = category;
            catalog.ValidCategoryOrder.Add(category.ID);
        }

        foreach (var scenario in scenarios)
        {
            catalog.ValidScenarios[scenario.Template.ID] = scenario;
            catalog.ValidScenarioOrder.Add(scenario.Template.ID);
        }

        return catalog;
    }

    private static ScenarioCategoryPrototype Category(
        string id,
        QuotaRule quota,
        int priority = 1,
        Dictionary<string, int>? maxPrimary = null)
    {
        var category = new ScenarioCategoryPrototype
        {
            Priority = priority,
            QuotaByGameMode = new Dictionary<string, QuotaRule>
            {
                ["default"] = quota,
            },
            MaxPrimaryPerMindByGameMode = maxPrimary ?? new Dictionary<string, int>
            {
                ["default"] = 1,
            },
        };

        SetId(category, id);
        return category;
    }

    private static QuotaRule QuotaFixed(int value)
    {
        return new QuotaRule
        {
            Mode = "fixed",
            Value = value,
        };
    }

    private static QuotaRule QuotaRatio(float ratio)
    {
        return new QuotaRule
        {
            Mode = "ratio",
            Ratio = ratio,
        };
    }

    private static QuotaRule QuotaClamp(float ratio, int min, int max)
    {
        return new QuotaRule
        {
            Mode = "clamp",
            Ratio = ratio,
            Min = min,
            Max = max,
        };
    }

    private static ScenarioTemplatePrototype Scenario(
        string id,
        string categoryId,
        int weight = 1,
        List<PredicateDefinition>? globalPredicates = null)
    {
        var scenario = new ScenarioTemplatePrototype
        {
            Name = id,
            Category = categoryId,
            Enabled = true,
            Weight = weight,
            GlobalPredicates = globalPredicates ?? [],
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

    private static ValidatedScenarioTemplate Validated(ScenarioTemplatePrototype scenario, params string[] slotBuildOrder)
    {
        return new ValidatedScenarioTemplate(scenario, slotBuildOrder);
    }

    private static PredicateDefinition RoundPredicate(string field, string op, string? value = null)
    {
        return new PredicateDefinition
        {
            Scope = IntentionsPredicateSchema.RoundScope,
            Field = field,
            Operator = op,
            Value = value,
        };
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
            "wave-test-snapshot");

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
