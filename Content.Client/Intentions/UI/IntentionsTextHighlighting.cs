using System;
using System.Collections.Generic;
using System.Linq;
using Robust.Shared.Utility;

namespace Content.Client.Intentions.UI;

/// <summary>
/// Applies lightweight rich-text highlighting to runtime values that were resolved during commit.
/// </summary>
public static class IntentionsTextHighlighting
{
    /// <summary>
    /// Formats one text block and highlights resolved runtime parameter values in bold.
    /// </summary>
    public static FormattedMessage Format(string text, IReadOnlyDictionary<string, string>? parameters)
    {
        var message = new FormattedMessage();

        if (string.IsNullOrEmpty(text))
            return message;

        var matches = FindHighlightRanges(text, parameters);
        if (matches.Count == 0)
        {
            message.AddText(text);
            return message;
        }

        var currentIndex = 0;
        foreach (var (start, length) in matches)
        {
            if (start > currentIndex)
                message.AddText(text[currentIndex..start]);

            message.PushTag(new MarkupNode("bold", null, null));
            message.AddText(text.Substring(start, length));
            message.Pop();
            currentIndex = start + length;
        }

        if (currentIndex < text.Length)
            message.AddText(text[currentIndex..]);

        return message;
    }

    /// <summary>
    /// Finds non-overlapping highlight ranges for resolved parameter values.
    /// </summary>
    private static List<(int Start, int Length)> FindHighlightRanges(string text, IReadOnlyDictionary<string, string>? parameters)
    {
        var values = parameters?
            .Values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(EqualityComparer<string>.Default)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, Comparer<string>.Default)
            .ToArray()
            ?? Array.Empty<string>();

        if (values.Length == 0)
            return [];

        var reserved = new bool[text.Length];
        var matches = new List<(int Start, int Length)>();

        foreach (var value in values)
        {
            var searchIndex = 0;
            while (searchIndex < text.Length)
            {
                var found = text.IndexOf(value, searchIndex, StringComparison.Ordinal);
                if (found < 0)
                    break;

                var overlaps = false;
                for (var i = found; i < found + value.Length; i++)
                {
                    if (reserved[i])
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (!overlaps)
                {
                    for (var i = found; i < found + value.Length; i++)
                    {
                        reserved[i] = true;
                    }

                    matches.Add((found, value.Length));
                }

                searchIndex = found + value.Length;
            }
        }

        matches.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return matches;
    }

    /// <summary>
    /// Formats a heading and preserves the same runtime-parameter highlighting.
    /// </summary>
    public static FormattedMessage FormatHeading(string text, IReadOnlyDictionary<string, string>? parameters, long level = 2)
    {
        var inner = Format(text, parameters);
        var message = new FormattedMessage();
        message.PushTag(new MarkupNode("head", new MarkupParameter(level), null));
        message.AddMessage(inner);
        message.Pop();
        return message;
    }
}
