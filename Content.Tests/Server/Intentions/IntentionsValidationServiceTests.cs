using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Intentions.Validation;
using Content.Shared.Intentions.Validation;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(IntentionsValidationService))]
/// <summary>
/// Covers validation rules for Intentions prototypes, predicates, and slot dependency ordering.
/// </summary>
public sealed class IntentionsValidationServiceTests : ContentUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Server;

    private IPrototypeManager _prototypeManager = default!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        IoCManager.Resolve<ISerializationManager>().Initialize();
        _prototypeManager = IoCManager.Resolve<IPrototypeManager>();
        _prototypeManager.Initialize();
    }

    [Test]
    public void ValidSetPassesAndGetsSlotBuildOrder()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids));

        Assert.That(ErrorsFor(catalog, ids), Is.Empty);
        Assert.That(catalog.ValidCategories, Does.ContainKey(ids.Category));
        Assert.That(catalog.ValidIntentions, Does.ContainKey(ids.PrimaryIntention));
        Assert.That(catalog.ValidIntentions, Does.ContainKey(ids.SecondaryIntention));
        Assert.That(catalog.ValidScenarios, Does.ContainKey(ids.Scenario));
        Assert.That(catalog.ValidScenarios[ids.Scenario].SlotBuildOrder, Is.EqualTo(new[] { "owner", "helper" }));
    }

    [Test]
    public void InvalidCategoryWithoutDefaultQuotaIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, categoryBlock: Category(ids, quotaByGameMode: """
    extended:
      mode: fixed
      value: 1
""")));

        Assert.That(catalog.ValidCategories, Does.Not.ContainKey(ids.Category));
        AssertIssue(catalog, "missing-default-quota");
    }

    [Test]
    public void InvalidIntentionWithMissingRequiredLocKeyIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, intentionsBlock: Intentions(ids, primaryNameLoc: "missing-loc-key")));

        Assert.That(catalog.ValidIntentions, Does.Not.ContainKey(ids.PrimaryIntention));
        AssertIssue(catalog, "unknown-loc");
    }

    [Test]
    public void ValidClampQuotaCategoryIsAccepted()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, categoryBlock: Category(ids, quotaByGameMode: """
    default:
      mode: clamp
      ratio: 0.10
      min: 1
      max: 4
""")));

        Assert.That(ErrorsFor(catalog, ids), Is.Empty);
        Assert.That(catalog.ValidCategories, Does.ContainKey(ids.Category));
    }

    [Test]
    public void RatioQuotaWithClampFieldsIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, categoryBlock: Category(ids, quotaByGameMode: """
    default:
      mode: ratio
      ratio: 0.10
      min: 1
""")));

        Assert.That(catalog.ValidCategories, Does.Not.ContainKey(ids.Category));
        AssertIssue(catalog, "ratio-quota-has-min");
    }

    [Test]
    public void ClampQuotaWithoutMaxIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, categoryBlock: Category(ids, quotaByGameMode: """
    default:
      mode: clamp
      ratio: 0.10
      min: 1
""")));

        Assert.That(catalog.ValidCategories, Does.Not.ContainKey(ids.Category));
        AssertIssue(catalog, "invalid-max-quota");
    }

    [Test]
    public void InvalidIntentionNameLongerThanThirtyFiveCharactersIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, intentionsBlock: Intentions(ids, primaryNameLoc: "intentions-test-primary-name-too-long")));

        Assert.That(catalog.ValidIntentions, Does.Not.ContainKey(ids.PrimaryIntention));
        AssertIssue(catalog, "loc-too-long");
    }

    [Test]
    public void InvalidIntentionSummaryLongerThanThirtyFiveCharactersIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, intentionsBlock: Intentions(ids, primarySummaryLoc: "intentions-test-primary-summary-too-long")));

        Assert.That(catalog.ValidIntentions, Does.Not.ContainKey(ids.PrimaryIntention));
        AssertIssue(catalog, "loc-too-long");
    }

    [Test]
    public void SixDigitHexColorsAreAccepted()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(
            ids,
            categoryBlock: Category(ids).Replace("#12345678", "#123456", StringComparison.Ordinal),
            intentionsBlock: Intentions(ids).Replace("#12345678", "#123456", StringComparison.Ordinal)));

        Assert.That(ErrorsFor(catalog, ids), Is.Empty);
        Assert.That(catalog.ValidCategories, Does.ContainKey(ids.Category));
        Assert.That(catalog.ValidIntentions, Does.ContainKey(ids.PrimaryIntention));
    }

    [Test]
    public void InvalidScenarioWithMissingCategoryIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, categoryId: "MissingCategory")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "invalid-category");
    }

    [Test]
    public void InvalidScenarioWithoutOwnerIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, entries: $$"""
  entries:
  - slotId: helper
    kind: secondary
    intentionId: {{ids.SecondaryIntention}}
    required: true
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "invalid-owner-count");
    }

    [Test]
    public void InvalidScenarioWithTwoOwnersIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, entries: $$"""
  entries:
  - slotId: owner
    kind: primary
    intentionId: {{ids.PrimaryIntention}}
    required: true
  - slotId: owner
    kind: primary
    intentionId: {{ids.PrimaryIntention}}
    required: true
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "invalid-owner-count");
    }

    [Test]
    public void InvalidSlotWithBindToSlotAndAllowSameActorAsIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, helperExtra: """
    bindToSlot: owner
    allowSameActorAs: [owner]
""", helperPredicates: "")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "bind-and-allow-same-actor");
    }

    [Test]
    public void InvalidSlotWithBindToSlotAndCandidatePredicatesIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, helperExtra: """
    bindToSlot: owner
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "bound-slot-has-predicates");
    }

    [Test]
    public void InvalidCompareToWithMissingSlotIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, helperPredicates: """
    candidatePredicates:
    - scope: candidate
      field: department
      operator: sameAs
      compareTo:
        scope: slot
        slotId: ghost
        field: department
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "missing-compare-slot");
    }

    [Test]
    public void InvalidCompareToWithMismatchedFieldIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, helperPredicates: """
    candidatePredicates:
    - scope: candidate
      field: department
      operator: sameAs
      compareTo:
        scope: slot
        slotId: owner
        field: job
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "compare-field-mismatch");
    }

    [Test]
    public void InvalidSlotDependencyCycleIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, entries: $$"""
  entries:
  - slotId: owner
    kind: primary
    intentionId: {{ids.PrimaryIntention}}
    required: true
  - slotId: helperA
    kind: secondary
    intentionId: {{ids.SecondaryIntention}}
    required: true
    allowSameActorAs: [helperB]
  - slotId: helperB
    kind: secondary
    intentionId: {{ids.SecondaryIntention}}
    required: true
    allowSameActorAs: [helperA]
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "slot-dependency-cycle");
    }

    [Test]
    public void StableTopologicalSortPreservesEntryOrderForIndependentSecondarySlots()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, entries: $$"""
  entries:
  - slotId: owner
    kind: primary
    intentionId: {{ids.PrimaryIntention}}
    required: true
  - slotId: alpha
    kind: secondary
    intentionId: {{ids.SecondaryIntention}}
    required: true
  - slotId: beta
    kind: secondary
    intentionId: {{ids.SecondaryIntention}}
    required: true
""")));

        Assert.That(ErrorsFor(catalog, ids), Is.Empty);
        Assert.That(catalog.ValidScenarios[ids.Scenario].SlotBuildOrder, Is.EqualTo(new[] { "owner", "alpha", "beta" }));
    }

    [Test]
    public void PredicateWithWrongGlobalScopeIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, globalPredicates: """
  globalPredicates:
  - scope: candidate
    field: crewCount
    operator: ">="
    value: "1"
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "wrong-predicate-scope");
    }

    [Test]
    public void PredicateWithValueAndValuesIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, helperPredicates: """
    candidatePredicates:
    - scope: candidate
      field: age
      operator: equals
      value: "30"
      values: ["30", "40"]
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "conflicting-predicate-values");
    }

    [Test]
    public void SameAsPredicateWithoutCompareToIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, helperPredicates: """
    candidatePredicates:
    - scope: candidate
      field: department
      operator: sameAs
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "missing-compare-to");
    }

    [Test]
    public void VisibleVisibilityOverrideWithRevealIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, helperExtra: """
    visibilityOverride:
      type: visible
      reveal:
        type: timer
        minutes: 5
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "visible-with-reveal");
    }

    [Test]
    public void TimerRevealWithoutPositiveMinutesIsRejected()
    {
        var ids = TestIds.Create();
        var catalog = Validate(BuildDocument(ids, scenarioBlock: Scenario(ids, helperExtra: """
    visibilityOverride:
      type: hidden
      reveal:
        type: timer
        minutes: 0
""")));

        Assert.That(catalog.ValidScenarios, Does.Not.ContainKey(ids.Scenario));
        AssertIssue(catalog, "invalid-timer-reveal");
    }

    private ValidationCatalog Validate(string prototypes)
    {
        _prototypeManager.LoadString(prototypes);
        _prototypeManager.ResolveResults();

        var service = new IntentionsValidationService(_prototypeManager, ResolveTestLoc);
        return service.ValidateAll();
    }

    private static string? ResolveTestLoc(string key)
    {
        return ValidLocs.TryGetValue(key, out var value) ? value : null;
    }

    private static void AssertIssue(ValidationCatalog catalog, string code)
    {
        Assert.That(catalog.Issues.Any(issue => issue.Code == code), Is.True, $"Expected validation issue '{code}'. Issues: {string.Join(", ", catalog.Issues.Select(issue => issue.Code))}");
    }

    private static IEnumerable<ValidationIssue> ErrorsFor(ValidationCatalog catalog, TestIds ids)
    {
        var ownedIds = new HashSet<string>
        {
            ids.Category,
            ids.PrimaryIntention,
            ids.SecondaryIntention,
            ids.Scenario,
        };

        return catalog.Issues.Where(issue => issue.Severity == ValidationIssueSeverity.Error && ownedIds.Contains(issue.ObjectId));
    }

    private static string BuildDocument(
        TestIds ids,
        string? categoryBlock = null,
        string? intentionsBlock = null,
        string? scenarioBlock = null)
    {
        return $"""
{categoryBlock ?? Category(ids)}

{intentionsBlock ?? Intentions(ids)}

{scenarioBlock ?? Scenario(ids)}
""";
    }

    private static string Category(TestIds ids, string? quotaByGameMode = null)
    {
        var quotaBlock = quotaByGameMode ?? DefaultQuotaByGameMode;

        return $$"""
- type: scenarioCategory
  id: {{ids.Category}}
  color: "#12345678"
  priority: 1
  quotaByGameMode:
{{quotaBlock}}
  maxPrimaryPerMindByGameMode:
    default: 1
""";
    }

    private static string Intentions(
        TestIds ids,
        string primaryNameLoc = "intentions-test-primary-name",
        string primarySummaryLoc = "intentions-test-primary-summary")
    {
        return $$"""
- type: intentionTemplate
  id: {{ids.PrimaryIntention}}
  kind: primary
  nameLoc: {{primaryNameLoc}}
  summaryLoc: {{primarySummaryLoc}}
  descriptionLoc: intentions-test-primary-description
  oocInfoLoc: intentions-test-ooc-info
  copyableTextLoc: intentions-test-copyable-text
  defaultVisibility: visible
  color: "#12345678"
  creationDate: "2026-04-21"

- type: intentionTemplate
  id: {{ids.SecondaryIntention}}
  kind: secondary
  nameLoc: intentions-test-secondary-name
  summaryLoc: intentions-test-secondary-summary
  descriptionLoc: intentions-test-secondary-description
  hiddenLabelLoc: intentions-test-hidden-label
  defaultVisibility: hidden
""";
    }

    private static string Scenario(
        TestIds ids,
        string? categoryId = null,
        string? globalPredicates = null,
        string? entries = null,
        string? helperPredicates = null,
        string? helperExtra = null)
    {
        var predicateBlock = globalPredicates ?? DefaultGlobalPredicates;
        var entriesBlock = entries ?? DefaultEntries(ids, helperPredicates, helperExtra);

        return $$"""
- type: scenarioTemplate
  id: {{ids.Scenario}}
  name: Test scenario
  category: {{categoryId ?? ids.Category}}
  enabled: true
  weight: 1
{{predicateBlock}}
{{entriesBlock}}
""";
    }

    private static string DefaultEntries(TestIds ids, string? helperPredicates, string? helperExtra)
    {
        var predicateBlock = helperPredicates ?? DefaultHelperPredicates;
        var extraBlock = helperExtra ?? string.Empty;

        return $$"""
  entries:
  - slotId: owner
    kind: primary
    intentionId: {{ids.PrimaryIntention}}
    required: true
    candidatePredicates:
    - scope: candidate
      field: age
      operator: ">="
      value: "18"
  - slotId: helper
    kind: secondary
    intentionId: {{ids.SecondaryIntention}}
    required: false
{{predicateBlock}}
{{extraBlock}}
""";
    }

    private const string DefaultQuotaByGameMode = """
    default:
      mode: fixed
      value: 1
""";

    private const string DefaultGlobalPredicates = """
  globalPredicates:
  - scope: round
    field: crewCount
    operator: ">="
    value: "1"
""";

    private const string DefaultHelperPredicates = """
    candidatePredicates:
    - scope: candidate
      field: department
      operator: notSameAs
      compareTo:
        scope: slot
        slotId: owner
        field: department
""";

    private static readonly Dictionary<string, string> ValidLocs = new()
    {
        ["intentions-test-primary-name"] = "Primary title",
        ["intentions-test-primary-summary"] = "Primary summary",
        ["intentions-test-primary-name-too-long"] = "This intention name is definitely too long",
        ["intentions-test-primary-summary-too-long"] = "This intention summary is too long too",
        ["intentions-test-primary-description"] = "Primary description",
        ["intentions-test-ooc-info"] = "OOC info",
        ["intentions-test-copyable-text"] = "Copyable text",
        ["intentions-test-secondary-name"] = "Secondary title",
        ["intentions-test-secondary-summary"] = "Secondary summary",
        ["intentions-test-secondary-description"] = "Secondary description",
        ["intentions-test-hidden-label"] = "Hidden",
    };

    private sealed record TestIds(string Category, string PrimaryIntention, string SecondaryIntention, string Scenario)
    {
        public static TestIds Create()
        {
            var suffix = Guid.NewGuid().ToString("N");
            return new TestIds(
                $"TestCategory{suffix}",
                $"TestPrimaryIntention{suffix}",
                $"TestSecondaryIntention{suffix}",
                $"TestScenario{suffix}");
        }
    }
}
