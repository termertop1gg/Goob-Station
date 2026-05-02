using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Snapshot;

namespace Content.Shared.Intentions.Predicates;

/// <summary>
/// Pure runtime predicate evaluator that reads only immutable snapshot data.
/// </summary>
public sealed class IntentionsPredicateEngine
{
    /// <summary>
    /// Evaluates round-scoped predicates against the provided snapshot.
    /// </summary>
    public PredicateEvaluationResult EvaluateGlobal(
        IReadOnlyList<PredicateDefinition> predicates,
        IntentionsSnapshot snapshot)
    {
        return Evaluate(predicates, snapshot, candidate: null, selectedSlots: null, IntentionsPredicateSchema.RoundScope);
    }

    /// <summary>
    /// Evaluates candidate-scoped predicates against one candidate and the already selected slots.
    /// </summary>
    public PredicateEvaluationResult EvaluateCandidate(
        IReadOnlyList<PredicateDefinition> predicates,
        CandidateFacts candidate,
        IntentionsSnapshot snapshot,
        IReadOnlyDictionary<string, CandidateFacts>? selectedSlots)
    {
        return Evaluate(predicates, snapshot, candidate, selectedSlots, IntentionsPredicateSchema.CandidateScope);
    }

    /// <summary>
    /// Evaluates a homogeneous predicate list and accumulates runtime-safe reject reasons.
    /// </summary>
    private PredicateEvaluationResult Evaluate(
        IReadOnlyList<PredicateDefinition> predicates,
        IntentionsSnapshot snapshot,
        CandidateFacts? candidate,
        IReadOnlyDictionary<string, CandidateFacts>? selectedSlots,
        string expectedScope)
    {
        var reasons = ImmutableArray.CreateBuilder<PredicateRejectReason>();

        for (var i = 0; i < predicates.Count; i++)
        {
            if (EvaluatePredicate(predicates[i], i, snapshot, candidate, selectedSlots, expectedScope) is { } reason)
                reasons.Add(reason);
        }

        return PredicateEvaluationResult.FromReasons(reasons.ToImmutable());
    }

    /// <summary>
    /// Evaluates one predicate and returns a reject reason only when the predicate fails or errors.
    /// </summary>
    private PredicateRejectReason? EvaluatePredicate(
        PredicateDefinition predicate,
        int predicateIndex,
        IntentionsSnapshot snapshot,
        CandidateFacts? candidate,
        IReadOnlyDictionary<string, CandidateFacts>? selectedSlots,
        string expectedScope)
    {
        if (predicate.Scope != expectedScope)
        {
            return Error(predicate, predicateIndex, "wrong-predicate-scope",
                $"Predicate scope must be {expectedScope} here.", candidate);
        }

        if (!IntentionsPredicateSchema.IsValidOperator(predicate.Operator))
            return Error(predicate, predicateIndex, "invalid-predicate-operator", "Predicate operator is not supported.", candidate);

        if (!IntentionsPredicateSchema.TryGetField(predicate.Scope, predicate.Field, out var fieldType))
            return Error(predicate, predicateIndex, "invalid-predicate-field", "Predicate field is not valid for this scope.", candidate);

        if (fieldType == PredicateFieldType.MapStringInt && string.IsNullOrWhiteSpace(predicate.Key))
            return Error(predicate, predicateIndex, "missing-predicate-key", "Map predicate field requires key.", candidate);

        if (fieldType != PredicateFieldType.MapStringInt && predicate.Key is not null)
            return Error(predicate, predicateIndex, "unexpected-predicate-key", "key is only allowed for map predicate fields.", candidate);

        if (!TryResolveField(predicate.Scope, predicate.Field, predicate.Key, snapshot, candidate, out var actual, out var fieldError))
            return Error(predicate, predicateIndex, "invalid-predicate-field", fieldError ?? "Predicate field could not be resolved.", candidate);

        if (!actual.HasValue)
            return Reject(predicate, predicateIndex, "missing-fact", "Predicate fact is missing from snapshot.", candidate, actual: null);

        if (predicate.Operator is "sameAs" or "notSameAs")
            return EvaluateComparePredicate(predicate, predicateIndex, snapshot, candidate, selectedSlots, actual);

        // All other operators compare the resolved value against content-provided runtime literals.
        if (!TryEvaluateValuePredicate(predicate, actual, fieldType, out var matched, out var expected, out var error))
            return Error(predicate, predicateIndex, error ?? "invalid-predicate-runtime", "Predicate value shape is invalid at runtime.", candidate);

        if (matched)
            return null;

        return Reject(predicate, predicateIndex, "predicate-false", "Predicate evaluated to false.", candidate, expected, DisplayValue(actual.Value));
    }

    /// <summary>
    /// Evaluates sameAs and notSameAs by reading facts from an already selected slot.
    /// </summary>
    private PredicateRejectReason? EvaluateComparePredicate(
        PredicateDefinition predicate,
        int predicateIndex,
        IntentionsSnapshot snapshot,
        CandidateFacts? candidate,
        IReadOnlyDictionary<string, CandidateFacts>? selectedSlots,
        ResolvedPredicateField actual)
    {
        if (candidate is null)
            return Error(predicate, predicateIndex, "compare-without-candidate", "Compare predicate requires candidate facts.", candidate);

        if (predicate.CompareTo is not { } compareTo)
            return Error(predicate, predicateIndex, "missing-compare-to", "sameAs and notSameAs require compareTo.", candidate);

        if (compareTo.Scope != IntentionsPredicateSchema.SlotScope)
            return Error(predicate, predicateIndex, "invalid-compare-scope", "compareTo scope must be slot.", candidate);

        if (selectedSlots is null || !selectedSlots.TryGetValue(compareTo.SlotId, out var comparedCandidate))
        {
            return Reject(predicate, predicateIndex, "missing-selected-slot", "Compared slot has not been selected.",
                candidate, comparedSlotId: compareTo.SlotId, actual: DisplayValue(actual.Value));
        }

        if (!TryResolveField(IntentionsPredicateSchema.CandidateScope, compareTo.Field, key: null, snapshot, comparedCandidate, out var compared, out var fieldError))
            return Error(predicate, predicateIndex, "invalid-compare-field", fieldError ?? "Compared field could not be resolved.", candidate);

        if (!compared.HasValue)
        {
            return Reject(predicate, predicateIndex, "missing-compared-fact", "Compared slot fact is missing from snapshot.",
                candidate, comparedSlotId: compareTo.SlotId, actual: DisplayValue(actual.Value));
        }

        var same = ValuesEqual(actual.Value, compared.Value);
        var matched = predicate.Operator == "sameAs" ? same : !same;

        if (matched)
            return null;

        return Reject(predicate, predicateIndex, "predicate-false", "Predicate evaluated to false.",
            candidate, comparedSlotId: compareTo.SlotId, expected: DisplayValue(compared.Value), actual: DisplayValue(actual.Value));
    }

    /// <summary>
    /// Resolves a predicate field from either round facts or one candidate snapshot entry.
    /// </summary>
    private static bool TryResolveField(
        string scope,
        string field,
        string? key,
        IntentionsSnapshot snapshot,
        CandidateFacts? candidate,
        out ResolvedPredicateField resolved,
        out string? error)
    {
        resolved = default;
        error = null;

        if (scope == IntentionsPredicateSchema.RoundScope)
        {
            resolved = ResolveRoundField(field, key, snapshot.RoundFacts);
            return resolved.IsValid;
        }

        if (candidate is null)
        {
            error = "Candidate predicate requires candidate facts.";
            return false;
        }

        resolved = ResolveCandidateField(field, candidate);
        return resolved.IsValid;
    }

    /// <summary>
    /// Resolves one round-level field from immutable round facts.
    /// </summary>
    private static ResolvedPredicateField ResolveRoundField(string field, string? key, RoundFacts facts)
    {
        return field switch
        {
            "gameMode" => ResolvedPredicateField.WithValue(facts.GameMode),
            "stationTime" => ResolvedPredicateField.WithValue(facts.StationTime),
            "crewCount" => ResolvedPredicateField.WithValue(facts.CrewCount),
            "securityCount" => ResolvedPredicateField.WithValue(facts.SecurityCount),
            "eventTags" => ResolvedPredicateField.WithValue(facts.EventTags),
            "antagSummary.totalCount" => ResolvedPredicateField.WithValue(facts.AntagSummary.TotalCount),
            "antagSummary.gameModeAntagCount" => ResolvedPredicateField.WithValue(facts.AntagSummary.GameModeAntagCount),
            "antagSummary.ghostRoleAntagCount" => ResolvedPredicateField.WithValue(facts.AntagSummary.GhostRoleAntagCount),
            "antagSummary.byRole" => ResolvedPredicateField.WithValue(GetMapValue(facts.AntagSummary.ByRole, key)),
            "antagSummary.byObjectiveType" => ResolvedPredicateField.WithValue(GetMapValue(facts.AntagSummary.ByObjectiveType, key)),
            _ => ResolvedPredicateField.Invalid,
        };
    }

    /// <summary>
    /// Resolves one candidate-level field from immutable candidate facts.
    /// </summary>
    private static ResolvedPredicateField ResolveCandidateField(string field, CandidateFacts candidate)
    {
        return field switch
        {
            "job" => ResolvedPredicateField.Optional(candidate.Job),
            "department" => ResolvedPredicateField.Optional(candidate.Department),
            "age" => ResolvedPredicateField.Optional(candidate.Age),
            "species" => ResolvedPredicateField.Optional(candidate.Species),
            "sex" => ResolvedPredicateField.Optional(candidate.Sex),
            "traits" => ResolvedPredicateField.WithValue(candidate.Traits),
            "hasMindshield" => ResolvedPredicateField.WithValue(candidate.HasMindshield),
            "antagRole" => ResolvedPredicateField.WithValue(candidate.AntagRoles),
            "antagObjectiveType" => ResolvedPredicateField.WithValue(candidate.AntagObjectiveTypes),
            _ => ResolvedPredicateField.Invalid,
        };
    }

    /// <summary>
    /// Returns the count for a keyed map field, defaulting missing entries to zero.
    /// </summary>
    private static int GetMapValue(ImmutableDictionary<string, int> map, string? key)
    {
        return key is not null && map.TryGetValue(key, out var value) ? value : 0;
    }

    /// <summary>
    /// Evaluates content-provided literal values against one already-resolved field value.
    /// </summary>
    private static bool TryEvaluateValuePredicate(
        PredicateDefinition predicate,
        ResolvedPredicateField actual,
        PredicateFieldType fieldType,
        out bool matched,
        out string? expected,
        out string? error)
    {
        matched = false;
        expected = null;
        error = null;

        switch (predicate.Operator)
        {
            case "equals":
            case "notEquals":
            case "contains":
            case "notContains":
            case ">":
            case ">=":
            case "<":
            case "<=":
                if (predicate.Operator is "contains" or "notContains" && fieldType != PredicateFieldType.ListString)
                {
                    error = "operator-field-mismatch";
                    return false;
                }

                if (predicate.Operator is ">" or ">=" or "<" or "<="
                    && fieldType is not (PredicateFieldType.Int or PredicateFieldType.Float or PredicateFieldType.TimeSpan or PredicateFieldType.MapStringInt))
                {
                    error = "operator-field-mismatch";
                    return false;
                }

                if (predicate.Value is null || predicate.Values is not null || predicate.ValueFrom is not null || predicate.ValueTo is not null)
                {
                    error = "invalid-predicate-value-shape";
                    return false;
                }

                if (!TryParseValue(predicate.Value, fieldType, out var value))
                {
                    error = "invalid-predicate-value";
                    return false;
                }

                expected = predicate.Value;
                matched = EvaluateSingleValueOperator(predicate.Operator, actual.Value, value);
                return true;

            case "in":
            case "notIn":
                if (predicate.Values is not { Count: > 0 } || predicate.Value is not null || predicate.ValueFrom is not null || predicate.ValueTo is not null)
                {
                    error = "invalid-predicate-value-shape";
                    return false;
                }

                var parsedValues = ImmutableArray.CreateBuilder<object>();
                foreach (var item in predicate.Values)
                {
                    if (!TryParseValue(item, fieldType, out var parsed))
                    {
                        error = "invalid-predicate-value";
                        return false;
                    }

                    parsedValues.Add(parsed);
                }

                expected = string.Join(", ", predicate.Values);
                matched = EvaluateSetOperator(predicate.Operator, actual.Value, parsedValues.ToImmutable());
                return true;

            case "between":
                if (fieldType is not (PredicateFieldType.Int or PredicateFieldType.Float or PredicateFieldType.TimeSpan or PredicateFieldType.MapStringInt))
                {
                    error = "operator-field-mismatch";
                    return false;
                }

                if (predicate.ValueFrom is null || predicate.ValueTo is null || predicate.Value is not null || predicate.Values is not null)
                {
                    error = "invalid-predicate-value-shape";
                    return false;
                }

                if (!TryParseValue(predicate.ValueFrom, fieldType, out var from) || !TryParseValue(predicate.ValueTo, fieldType, out var to))
                {
                    error = "invalid-predicate-value";
                    return false;
                }

                expected = $"{predicate.ValueFrom}..{predicate.ValueTo}";
                matched = Compare(actual.Value, from) >= 0 && Compare(actual.Value, to) <= 0;
                return true;

            default:
                error = "invalid-predicate-operator";
                return false;
        }
    }

    /// <summary>
    /// Evaluates single-value operators such as equals and greater-than.
    /// </summary>
    private static bool EvaluateSingleValueOperator(string op, object actual, object expected)
    {
        return op switch
        {
            "equals" => ValuesEqual(actual, expected),
            "notEquals" => !ValuesEqual(actual, expected),
            "contains" => Contains(actual, expected),
            "notContains" => !Contains(actual, expected),
            ">" => Compare(actual, expected) > 0,
            ">=" => Compare(actual, expected) >= 0,
            "<" => Compare(actual, expected) < 0,
            "<=" => Compare(actual, expected) <= 0,
            _ => false,
        };
    }

    /// <summary>
    /// Evaluates in and notIn for both scalar and list-valued fields.
    /// </summary>
    private static bool EvaluateSetOperator(string op, object actual, ImmutableArray<object> expectedValues)
    {
        var contains = actual switch
        {
            ImmutableArray<string> actualList => actualList.Any(value => expectedValues.Any(expected => ValuesEqual(value, expected))),
            _ => expectedValues.Any(expected => ValuesEqual(actual, expected)),
        };

        return op == "in" ? contains : !contains;
    }

    /// <summary>
    /// Evaluates contains and notContains using the runtime shape of the resolved field.
    /// </summary>
    private static bool Contains(object actual, object expected)
    {
        return actual switch
        {
            ImmutableArray<string> actualList when expected is string expectedString => actualList.Contains(expectedString, StringComparer.Ordinal),
            string actualString when expected is string expectedString => actualString.Contains(expectedString, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Compares two runtime values using field-type-aware equality.
    /// </summary>
    private static bool ValuesEqual(object actual, object expected)
    {
        return actual switch
        {
            string actualString when expected is string expectedString => string.Equals(actualString, expectedString, StringComparison.Ordinal),
            bool actualBool when expected is bool expectedBool => actualBool == expectedBool,
            int actualInt when expected is int expectedInt => actualInt == expectedInt,
            float actualFloat when expected is float expectedFloat => actualFloat.Equals(expectedFloat),
            TimeSpan actualTime when expected is TimeSpan expectedTime => actualTime == expectedTime,
            ImmutableArray<string> actualList when expected is ImmutableArray<string> expectedList => actualList.SequenceEqual(expectedList),
            ImmutableArray<string> actualList when expected is string expectedString => actualList.Length == 1 && actualList[0] == expectedString,
            _ => actual.Equals(expected),
        };
    }

    /// <summary>
    /// Converts two supported runtime values into a common comparable shape.
    /// </summary>
    private static int Compare(object actual, object expected)
    {
        return ToComparable(actual).CompareTo(ToComparable(expected));
    }

    /// <summary>
    /// Converts supported ordered predicate values into a comparable numeric representation.
    /// </summary>
    private static double ToComparable(object value)
    {
        return value switch
        {
            int intValue => intValue,
            float floatValue => floatValue,
            TimeSpan timeValue => timeValue.Ticks,
            _ => double.NaN,
        };
    }

    /// <summary>
    /// Parses one string literal from content into the runtime type expected by the field schema.
    /// </summary>
    private static bool TryParseValue(string value, PredicateFieldType fieldType, out object parsed)
    {
        switch (fieldType)
        {
            case PredicateFieldType.String:
            case PredicateFieldType.ListString:
                parsed = value;
                return true;
            case PredicateFieldType.Bool:
                if (bool.TryParse(value, out var boolValue))
                {
                    parsed = boolValue;
                    return true;
                }
                break;
            case PredicateFieldType.Int:
            case PredicateFieldType.MapStringInt:
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    parsed = intValue;
                    return true;
                }
                break;
            case PredicateFieldType.Float:
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue))
                {
                    parsed = floatValue;
                    return true;
                }
                break;
            case PredicateFieldType.TimeSpan:
                if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timeValue))
                {
                    parsed = timeValue;
                    return true;
                }
                break;
        }

        parsed = string.Empty;
        return false;
    }

    /// <summary>
    /// Converts runtime values into a stable diagnostic string for reject reasons.
    /// </summary>
    private static string DisplayValue(object? value)
    {
        return value switch
        {
            null => "<missing>",
            ImmutableArray<string> list => string.Join(", ", list),
            TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Creates an error reject reason for invalid runtime predicate shapes.
    /// </summary>
    private static PredicateRejectReason Error(
        PredicateDefinition predicate,
        int predicateIndex,
        string code,
        string message,
        CandidateFacts? candidate)
    {
        return new PredicateRejectReason(
            code,
            predicate.Scope,
            predicate.Field,
            predicate.Operator,
            predicateIndex,
            message,
            candidate?.MindId,
            IsError: true);
    }

    /// <summary>
    /// Creates a non-error reject reason for predicates that evaluated to false.
    /// </summary>
    private static PredicateRejectReason Reject(
        PredicateDefinition predicate,
        int predicateIndex,
        string code,
        string message,
        CandidateFacts? candidate,
        string? expected = null,
        string? actual = null,
        string? comparedSlotId = null)
    {
        return new PredicateRejectReason(
            code,
            predicate.Scope,
            predicate.Field,
            predicate.Operator,
            predicateIndex,
            message,
            candidate?.MindId,
            comparedSlotId,
            expected,
            actual);
    }

    /// <summary>
    /// Internal wrapper for one resolved predicate field value.
    /// </summary>
    private readonly record struct ResolvedPredicateField(bool IsValid, bool HasValue, object Value)
    {
        public static readonly ResolvedPredicateField Invalid = new(false, false, string.Empty);

        /// <summary>
        /// Creates a resolved field with a required value.
        /// </summary>
        public static ResolvedPredicateField WithValue(object value)
        {
            return new ResolvedPredicateField(true, true, value);
        }

        /// <summary>
        /// Creates a resolved optional field, preserving missing optional facts.
        /// </summary>
        public static ResolvedPredicateField Optional(object? value)
        {
            return value is null
                ? new ResolvedPredicateField(true, false, string.Empty)
                : new ResolvedPredicateField(true, true, value);
        }
    }
}
