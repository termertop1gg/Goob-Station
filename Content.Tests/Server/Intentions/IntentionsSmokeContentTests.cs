using System;
using System.Collections.Generic;
using System.IO;
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
/// Loads packaged Intentions content to ensure the smoke content remains valid and localized.
/// </summary>
public sealed class IntentionsSmokeContentTests : ContentUnitTest
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
    public void SmokeContentPackPassesIntentionsValidation()
    {
        var path = FindRepoFile(Path.Combine("Content.Tests", "Server", "Intentions", "TestData", "smoke_intentions.yml"));
        var prefix = $"IntentionsSmoke{Guid.NewGuid():N}";
        var yaml = File.ReadAllText(path)
            .Replace("IntentionsSmoke", prefix, StringComparison.Ordinal);

        _prototypeManager.LoadString(yaml);
        _prototypeManager.ResolveResults();

        var catalog = new IntentionsValidationService(_prototypeManager, ResolveSmokeLoc).ValidateAll();
        var smokeErrors = catalog.Issues
            .Where(issue => issue.Severity == ValidationIssueSeverity.Error && issue.ObjectId.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

        Assert.That(smokeErrors, Is.Empty, string.Join(", ", smokeErrors.Select(issue => $"{issue.ObjectId}:{issue.Code}")));
        Assert.That(catalog.ValidCategories, Does.ContainKey($"{prefix}Social"));
        Assert.That(catalog.ValidScenarios, Does.ContainKey($"{prefix}HappyPath"));
        Assert.That(catalog.ValidScenarios, Does.ContainKey($"{prefix}SameActor"));
        Assert.That(catalog.ValidScenarios, Does.ContainKey($"{prefix}HiddenTimer"));
    }

    [Test]
    public void TestContentPackPassesIntentionsValidation()
    {
        var path = FindRepoFile(Path.Combine("Content.Tests", "Server", "Intentions", "TestData", "test_intentions.yml"));
        var prefix = $"IntentionsTest{Guid.NewGuid():N}";
        var yaml = File.ReadAllText(path)
            .Replace("IntentionsTest", prefix, StringComparison.Ordinal);

        _prototypeManager.LoadString(yaml);
        _prototypeManager.ResolveResults();

        var catalog = new IntentionsValidationService(_prototypeManager, ResolveSmokeLoc).ValidateAll();
        var testErrors = catalog.Issues
            .Where(issue => issue.Severity == ValidationIssueSeverity.Error && issue.ObjectId.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        var categories = catalog.ValidCategories.Keys
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        var scenarios = catalog.ValidScenarios.Keys
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

        Assert.That(testErrors, Is.Empty, string.Join(", ", testErrors.Select(issue => $"{issue.ObjectId}:{issue.Code}")));
        Assert.That(categories, Has.Length.EqualTo(3));
        Assert.That(scenarios, Has.Length.EqualTo(28));
        Assert.That(catalog.ValidScenarios, Does.ContainKey($"{prefix}SocialGreetingRelay"));
        Assert.That(catalog.ValidScenarios, Does.ContainKey($"{prefix}SocialSoloCheckIn"));
        Assert.That(catalog.ValidScenarios, Does.ContainKey($"{prefix}OperationsToolAudit"));
        Assert.That(catalog.ValidScenarios, Does.ContainKey($"{prefix}OperationsSoloInspection"));
        Assert.That(catalog.ValidScenarios, Does.ContainKey($"{prefix}MysteryStrangeSignal"));
        Assert.That(catalog.ValidScenarios, Does.ContainKey($"{prefix}MysterySoloSignal"));
    }

    private static string? ResolveSmokeLoc(string key)
    {
        if (key.StartsWith("intentions-test-", StringComparison.Ordinal))
            return "Test localized text.";

        return SmokeLocs.TryGetValue(key, out var value) ? value : null;
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        Assert.Fail($"Could not find {relativePath} from {AppContext.BaseDirectory}");
        return string.Empty;
    }

    private static readonly Dictionary<string, string> SmokeLocs = new()
    {
        ["intentions-smoke-category-social-name"] = "Smoke: Social Intentions",
        ["intentions-smoke-category-social-description"] = "Disabled smoke examples for Intentions validation and dry-run tooling.",
        ["intentions-smoke-primary-coordinate-name"] = "Coordinate a small scene",
        ["intentions-smoke-primary-coordinate-summary"] = "Find a partner for a short beat.",
        ["intentions-smoke-primary-coordinate-description"] = "Invite someone into a small scene aboard a station. Keep it optional and light.",
        ["intentions-smoke-primary-coordinate-copy"] = "Coordinate a scene at a station. Note: smoke-happy-path",
        ["intentions-smoke-secondary-assist-name"] = "Assist a small scene",
        ["intentions-smoke-secondary-assist-summary"] = "Help another player in a scene.",
        ["intentions-smoke-secondary-assist-description"] = "Join a small scene when invited. Keep the interaction optional and friendly.",
        ["intentions-smoke-secondary-assist-copy"] = "Assist another player aboard a station.",
        ["intentions-smoke-secondary-hidden-name"] = "Timed reveal helper",
        ["intentions-smoke-secondary-hidden-summary"] = "This helper reveals after a timer.",
        ["intentions-smoke-secondary-hidden-description"] = "Once revealed, help another player finish a small timed scene aboard a station.",
        ["intentions-smoke-secondary-hidden-copy"] = "Timed helper for another player at a station.",
        ["intentions-smoke-hidden-label"] = "Hidden smoke intention",
        ["intentions-smoke-ooc-info"] = "This is disabled smoke content for validation and tooling checks.",
    };
}
