using System;
using System.Globalization;
using System.Linq;

using UnityEngine;

namespace DvergrWorkers.Core;

/// <summary>
/// Translatable text that can be stored and shown later, by any player in their own language. A worker's status
/// is written to its data by whoever runs the worker and read by everyone, so it is kept as a translation token
/// plus the words for its $1, $2..., and only turned into words when shown.
/// </summary>
internal static class Text
{
    private const char Separator = '|';

    /// <summary>
    /// A token with words for its $1, $2..., e.g. Of("$dw_status_planting", "$piece_sapling_beech"). Words may
    /// be tokens themselves, or text containing tokens.
    /// </summary>
    public static string Of(string token, params object[] words)
    {
        return words.Length == 0
            ? token
            : token + Separator + string.Join(Separator.ToString(), words.Select(w => Convert.ToString(w, CultureInfo.InvariantCulture)));
    }

    /// <summary>Text made by <see cref="Of"/> (or plain text and tokens) in the player's language.</summary>
    public static string Show(string text)
    {
        if (string.IsNullOrEmpty(text) || Localization.instance == null)
        {
            return text;
        }

        string[] parts = text.Split(Separator);
        if (parts.Length == 1)
        {
            return Localization.instance.Localize(text);
        }

        string[] words = [.. parts.Skip(1).Select(Localization.instance.Localize)];
        return Localization.instance.Localize(parts[0], words);
    }

    /// <summary>The name players know an object by: its piece name (a token), or else its prefab name.</summary>
    public static string NameOf(GameObject go)
    {
        Piece piece = go.GetComponentInParent<Piece>();
        return piece != null && !string.IsNullOrEmpty(piece.m_name)
            ? piece.m_name
            : Utils.GetPrefabName(go).Replace('_', ' ');
    }

    /// <summary>A list of words joined as "a, b or c" (tokens are translated when shown).</summary>
    public static string JoinOr(System.Collections.Generic.IReadOnlyList<string> words)
    {
        return Join(words, "$dw_or");
    }

    /// <summary>A list of words joined as "a, b and c" (tokens are translated when shown).</summary>
    public static string JoinAnd(System.Collections.Generic.IReadOnlyList<string> words)
    {
        return Join(words, "$dw_and");
    }

    private static string Join(System.Collections.Generic.IReadOnlyList<string> words, string conjunction)
    {
        return words.Count <= 1
            ? string.Concat(words)
            : string.Join(", ", words.Take(words.Count - 1)) + " " + conjunction + " " + words[words.Count - 1];
    }
}
