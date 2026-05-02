using System.Collections.Generic;
using System.Reflection;
using Content.Server.GameTicking.Presets;
using Content.Server.Holiday;
using Content.Server.Intentions.Debug;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Roles;
using Content.Shared.Traits;
using Moq;
using NUnit.Framework;
using Robust.Shared.Prototypes;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsPredicateDictionaryService))]
/// <summary>
/// Covers the read-only dictionary service used by Intentions debug tooling and authoring docs.
/// </summary>
public sealed class IntentionsPredicateDictionaryServiceTests
{
    [Test]
    public void EventTagsComeFromHolidayPrototypes()
    {
        var service = new IntentionsPredicateDictionaryService(CreatePrototypeManager(
            holidays:
            [
                CreatePrototypeWithId<HolidayPrototype>("NewYear"),
                CreatePrototypeWithId<HolidayPrototype>("FestiveSeason"),
            ]));

        var found = service.TryGetDictionary("EventTags", out var dictionary);

        Assert.That(found, Is.True);
        Assert.That(dictionary.Name, Is.EqualTo("EventTags"));
        Assert.That(dictionary.Values, Is.EqualTo(new[] { "FestiveSeason", "NewYear" }));
        Assert.That(dictionary.Note, Does.Contain("active right now"));
    }

    [Test]
    public void SexDictionaryUsesSharedEnumNames()
    {
        var service = new IntentionsPredicateDictionaryService(CreatePrototypeManager());

        var found = service.TryGetDictionary("Sex", out var dictionary);

        Assert.That(found, Is.True);
        Assert.That(dictionary.Values, Is.EqualTo(new[] { "Female", "Male", "Unsexed" }));
    }

    [Test]
    public void ObjectiveTypesUseInjectedProviderWithDistinctStableOrder()
    {
        var service = new IntentionsPredicateDictionaryService(
            CreatePrototypeManager(),
            () => ["KillTarget", "EscapeAlive", "KillTarget"]);

        var found = service.TryGetDictionary("AntagObjectiveTypes", out var dictionary);

        Assert.That(found, Is.True);
        Assert.That(dictionary.Values, Is.EqualTo(new[] { "EscapeAlive", "KillTarget" }));
    }

    [Test]
    public void PrototypeBackedDictionariesExposeLoadedIds()
    {
        var service = new IntentionsPredicateDictionaryService(CreatePrototypeManager(
            gameModes:
            [
                CreatePrototypeWithId<GamePresetPrototype>("Extended"),
            ],
            jobs:
            [
                CreatePrototypeWithId<JobPrototype>("Passenger"),
            ],
            departments:
            [
                CreatePrototypeWithId<DepartmentPrototype>("Security"),
            ],
            species:
            [
                CreatePrototypeWithId<SpeciesPrototype>("Human"),
            ],
            traits:
            [
                CreatePrototypeWithId<TraitPrototype>("Blindness"),
            ],
            antagRoles:
            [
                CreatePrototypeWithId<AntagPrototype>("Traitor"),
            ]));

        Assert.That(service.TryGetDictionary("GameMode", out var gameModes), Is.True);
        Assert.That(gameModes.Values, Does.Contain("Extended"));

        Assert.That(service.TryGetDictionary("Job", out var jobs), Is.True);
        Assert.That(jobs.Values, Does.Contain("Passenger"));

        Assert.That(service.TryGetDictionary("Department", out var departments), Is.True);
        Assert.That(departments.Values, Does.Contain("Security"));

        Assert.That(service.TryGetDictionary("Species", out var species), Is.True);
        Assert.That(species.Values, Does.Contain("Human"));

        Assert.That(service.TryGetDictionary("Traits", out var traits), Is.True);
        Assert.That(traits.Values, Does.Contain("Blindness"));

        Assert.That(service.TryGetDictionary("AntagRoles", out var antagRoles), Is.True);
        Assert.That(antagRoles.Values, Does.Contain("Traitor"));
    }

    [Test]
    public void UnknownDictionaryIsRejected()
    {
        var service = new IntentionsPredicateDictionaryService(CreatePrototypeManager());

        var found = service.TryGetDictionary("UnknownDictionary", out _);

        Assert.That(found, Is.False);
    }

    /// <summary>
    /// Creates a minimal prototype manager stub that serves just the prototype kinds used by the dictionary service.
    /// </summary>
    private static IPrototypeManager CreatePrototypeManager(
        IEnumerable<GamePresetPrototype>? gameModes = null,
        IEnumerable<HolidayPrototype>? holidays = null,
        IEnumerable<JobPrototype>? jobs = null,
        IEnumerable<DepartmentPrototype>? departments = null,
        IEnumerable<SpeciesPrototype>? species = null,
        IEnumerable<TraitPrototype>? traits = null,
        IEnumerable<AntagPrototype>? antagRoles = null)
    {
        var prototypes = new Mock<IPrototypeManager>(MockBehavior.Strict);

        prototypes.Setup(manager => manager.EnumeratePrototypes<GamePresetPrototype>())
            .Returns(gameModes ?? []);
        prototypes.Setup(manager => manager.EnumeratePrototypes<HolidayPrototype>())
            .Returns(holidays ?? []);
        prototypes.Setup(manager => manager.EnumeratePrototypes<JobPrototype>())
            .Returns(jobs ?? []);
        prototypes.Setup(manager => manager.EnumeratePrototypes<DepartmentPrototype>())
            .Returns(departments ?? []);
        prototypes.Setup(manager => manager.EnumeratePrototypes<SpeciesPrototype>())
            .Returns(species ?? []);
        prototypes.Setup(manager => manager.EnumeratePrototypes<TraitPrototype>())
            .Returns(traits ?? []);
        prototypes.Setup(manager => manager.EnumeratePrototypes<AntagPrototype>())
            .Returns(antagRoles ?? []);

        return prototypes.Object;
    }

    /// <summary>
    /// Creates a prototype instance with only its identifier populated, which is all the service needs.
    /// </summary>
#pragma warning disable RA0039
    private static TPrototype CreatePrototypeWithId<TPrototype>(string id)
        where TPrototype : class, IPrototype, new()
    {
        var prototype = new TPrototype();
        var property = typeof(TPrototype).GetProperty(
            nameof(IPrototype.ID),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.That(property, Is.Not.Null, $"Prototype '{typeof(TPrototype).Name}' must expose an ID property for tests.");
        property!.SetValue(prototype, id);
        return prototype;
    }
#pragma warning restore RA0039
}
