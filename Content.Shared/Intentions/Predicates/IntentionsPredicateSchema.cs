using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Content.Shared.Intentions.Predicates;

/// <summary>
/// Central schema for supported Intentions predicate scopes, operators, and fields.
/// </summary>
public static class IntentionsPredicateSchema
{
    /// <summary>
    /// Scope used by predicates that read round-level facts.
    /// </summary>
    public const string RoundScope = "round";

    /// <summary>
    /// Scope used by predicates that read candidate-level facts.
    /// </summary>
    public const string CandidateScope = "candidate";

    /// <summary>
    /// Scope token used by compare-to metadata when referencing a selected slot.
    /// </summary>
    public const string SlotScope = "slot";

    /// <summary>
    /// Allowed predicate scopes in content.
    /// </summary>
    public static readonly ImmutableHashSet<string> Scopes =
        ImmutableHashSet.CreateRange(
            StringComparer.Ordinal,
            new[]
            {
                RoundScope,
                CandidateScope,
            });

    /// <summary>
    /// Allowed predicate operators in content.
    /// </summary>
    public static readonly ImmutableHashSet<string> Operators =
        ImmutableHashSet.CreateRange(
            StringComparer.Ordinal,
            new[]
            {
                "equals",
                "notEquals",
                "in",
                "notIn",
                "contains",
                "notContains",
                ">",
                ">=",
                "<",
                "<=",
                "between",
                "sameAs",
                "notSameAs",
            });

    /// <summary>
    /// Supported round-level fields and their runtime value types.
    /// </summary>
    public static readonly ImmutableDictionary<string, PredicateFieldType> RoundFields =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, PredicateFieldType>
        {
            ["gameMode"] = PredicateFieldType.String,
            ["stationTime"] = PredicateFieldType.TimeSpan,
            ["crewCount"] = PredicateFieldType.Int,
            ["securityCount"] = PredicateFieldType.Int,
            ["eventTags"] = PredicateFieldType.ListString,
            ["antagSummary.totalCount"] = PredicateFieldType.Int,
            ["antagSummary.gameModeAntagCount"] = PredicateFieldType.Int,
            ["antagSummary.ghostRoleAntagCount"] = PredicateFieldType.Int,
            ["antagSummary.byRole"] = PredicateFieldType.MapStringInt,
            ["antagSummary.byObjectiveType"] = PredicateFieldType.MapStringInt,
        });

    /// <summary>
    /// Supported candidate-level fields and their runtime value types.
    /// </summary>
    public static readonly ImmutableDictionary<string, PredicateFieldType> CandidateFields =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, PredicateFieldType>
        {
            ["job"] = PredicateFieldType.String,
            ["department"] = PredicateFieldType.String,
            ["age"] = PredicateFieldType.Int,
            ["species"] = PredicateFieldType.String,
            ["sex"] = PredicateFieldType.String,
            ["traits"] = PredicateFieldType.ListString,
            ["hasMindshield"] = PredicateFieldType.Bool,
            ["antagRole"] = PredicateFieldType.ListString,
            ["antagObjectiveType"] = PredicateFieldType.ListString,
        });

    /// <summary>
    /// Returns whether the provided scope name is supported by the schema.
    /// </summary>
    public static bool IsValidScope(string scope)
    {
        return Scopes.Contains(scope);
    }

    /// <summary>
    /// Returns whether the provided operator name is supported by the schema.
    /// </summary>
    public static bool IsValidOperator(string op)
    {
        return Operators.Contains(op);
    }

    /// <summary>
    /// Resolves the value type for a field within the requested predicate scope.
    /// </summary>
    public static bool TryGetField(string scope, string field, out PredicateFieldType fieldType)
    {
        var fields = scope == RoundScope ? RoundFields : CandidateFields;
        return fields.TryGetValue(field, out fieldType);
    }
}

/// <summary>
/// Runtime value categories used when validating and evaluating predicates.
/// </summary>
public enum PredicateFieldType : byte
{
    String,
    Bool,
    Int,
    Float,
    TimeSpan,
    ListString,
    MapStringInt,
}
