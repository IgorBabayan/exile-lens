using System.Reflection;
using System.Text.Json;
using ExileLens.Application.Storage;
using ExileLens.Core.Maps;
using ExileLens.Core.Rewards;

namespace ExileLens.Application.Games;

/// <summary>
/// Loads versioned game data shipped as embedded JSON. A user override file with the same name in
/// "&lt;data&gt;/rules/&lt;game&gt;/" takes precedence, so rule updates can ship independently of binaries.
/// </summary>
public static class EmbeddedGameData
{
    public static RewardRuleSet LoadRewards(Assembly assembly, string resourceSuffix, string? overridePath = null)
    {
        using var stream = Open(assembly, resourceSuffix, overridePath);
        return JsonSerializer.Deserialize(stream, AppJsonContext.Default.RewardRuleSet)
               ?? throw new InvalidDataException($"Reward rules '{resourceSuffix}' are empty.");
    }

    public static UniqueDustTable LoadUniqueDust(Assembly assembly, string resourceSuffix, string? overridePath = null)
    {
        using var stream = Open(assembly, resourceSuffix, overridePath);
        var document = JsonSerializer.Deserialize(stream, AppJsonContext.Default.UniqueDustDocument)
                       ?? throw new InvalidDataException($"Unique dust table '{resourceSuffix}' is empty.");
        return new UniqueDustTable(document);
    }

    public static IReadOnlyList<MapModifierDefinition> LoadMapModifiers(Assembly assembly, string resourceSuffix, string? overridePath = null)
    {
        using var stream = Open(assembly, resourceSuffix, overridePath);
        return JsonSerializer.Deserialize(stream, AppJsonContext.Default.ListMapModifierDefinition) ?? [];
    }

    private static Stream Open(Assembly assembly, string resourceSuffix, string? overridePath)
    {
        if (overridePath is not null && File.Exists(overridePath))
        {
            return File.OpenRead(overridePath);
        }

        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.Ordinal))
                   ?? throw new InvalidOperationException($"Embedded resource '{resourceSuffix}' not found in {assembly.GetName().Name}.");
        return assembly.GetManifestResourceStream(name)!;
    }
}
