using System.Collections.Frozen;
using ExileLens.Core.Items;

namespace ExileLens.Core.Maps;

/// <summary>
/// Lookup of known map modifiers by normalized pattern. Unknown patterns get a deterministic identifier
/// ("auto:&lt;pattern&gt;") so they can still be classified by the user and remain stable across sessions.
/// </summary>
public sealed class MapModifierCatalog
{
    private readonly FrozenDictionary<string, MapModifierDefinition> _byPattern;
    private readonly FrozenDictionary<string, MapModifierDefinition> _byId;

    public MapModifierCatalog(GameId game, IEnumerable<MapModifierDefinition> definitions)
    {
        Game = game;
        var list = definitions.Where(d => d.Game == game).ToList();
        _byPattern = list
            .GroupBy(d => Key(d.Pattern), StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _byId = list.ToFrozenDictionary(d => d.Id, StringComparer.Ordinal);
        All = list;
    }

    public GameId Game { get; }

    public IReadOnlyList<MapModifierDefinition> All { get; }

    public bool TryResolve(string pattern, out MapModifierDefinition definition) =>
        _byPattern.TryGetValue(Key(pattern), out definition!);

    public MapModifierDefinition? FindById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Resolves a modifier to its stable id; returns false for unknown modifiers (id is still produced).</summary>
    public bool ResolveId(string pattern, out string id)
    {
        if (TryResolve(pattern, out var def))
        {
            id = def.Id;
            return true;
        }

        id = AutoId(pattern);
        return false;
    }

    public static string AutoId(string pattern) => "auto:" + Key(pattern);

    // Normalizing here makes hand-written catalog patterns ("+#% ...") and parsed item patterns comparable.
    private static string Key(string pattern) => ModifierText.Normalize(pattern.Trim()).ToLowerInvariant();
}
