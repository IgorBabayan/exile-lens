using System.Text.Json;
using System.Text.Json.Serialization;
using ExileLens.Application.History;
using ExileLens.Application.Settings;
using ExileLens.Core.Maps;
using ExileLens.Core.Rewards;

namespace ExileLens.Application.Storage;

/// <summary>Source-generated serialization for every persisted document (no reflection at runtime).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProfileDocument))]
[JsonSerializable(typeof(ModifierProfile))]
[JsonSerializable(typeof(DiscoveredModifiersDocument))]
[JsonSerializable(typeof(HistoryDocument))]
[JsonSerializable(typeof(RewardRuleSet))]
[JsonSerializable(typeof(List<MapModifierDefinition>))]
public sealed partial class AppJsonContext : JsonSerializerContext;

public sealed class ProfileDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<ModifierProfile> Profiles { get; set; } = [];
}

public sealed class DiscoveredModifiersDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<MapModifierDefinition> Modifiers { get; set; } = [];
}
