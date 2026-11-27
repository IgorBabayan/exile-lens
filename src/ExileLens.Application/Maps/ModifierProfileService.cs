using System.Text.Json;
using ExileLens.Application.Games;
using ExileLens.Application.Settings;
using ExileLens.Application.Storage;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;
using Microsoft.Extensions.Logging;

namespace ExileLens.Application.Maps;

public interface IModifierProfileService
{
    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<ModifierProfile> GetProfiles(GameId game);

    ModifierProfile GetActiveProfile(GameId game);

    void SetActiveProfile(GameId game, string profileId);

    ModifierProfile Create(GameId game, string name);

    ModifierProfile Duplicate(string profileId, string newName);

    void Rename(string profileId, string newName);

    void Delete(string profileId);

    /// <summary>Sets the base level of one modifier in a profile (Unclassified removes the rule unless it carries notes/thresholds).</summary>
    void SetLevel(string profileId, string modifierId, DangerLevel level);

    void SetLevels(string profileId, IEnumerable<string> modifierIds, DangerLevel level);

    void UpdateRule(string profileId, ModifierRule rule);

    void SetCombinations(string profileId, IEnumerable<CombinationRule> combinations, int? escalateWarningsAt);

    string Export(string profileId);

    ModifierProfile Import(string json, GameId? overrideGame = null);

    /// <summary>Known + discovered modifiers for the Map Modifiers page.</summary>
    MapModifierCatalog GetCatalog(GameId game);

    IReadOnlyList<MapModifierDefinition> GetAllModifiers(GameId game);

    /// <summary>Records modifiers seen on real maps that are not in the shipped catalog, so users can classify them.</summary>
    void RecordDiscovered(GameId game, IEnumerable<ItemModifier> modifiers);

    Task FlushAsync();
}

public sealed class ModifierProfileService : IModifierProfileService, IAsyncDisposable
{
    private readonly IGameRegistry _games;
    private readonly ISettingsService _settings;
    private readonly ILogger<ModifierProfileService> _logger;
    private readonly string _profilesPath;
    private readonly string _discoveredPath;
    private readonly DebouncedSaver _saver;
    private readonly Lock _gate = new();
    private readonly Dictionary<GameId, MapModifierCatalog> _catalogs = [];
    private List<ModifierProfile> _profiles = [];
    private List<MapModifierDefinition> _discovered = [];

    public ModifierProfileService(IGameRegistry games, ISettingsService settings, IAppPaths paths, ILogger<ModifierProfileService> logger)
    {
        _games = games;
        _settings = settings;
        _logger = logger;
        _profilesPath = Path.Combine(paths.DataDirectory, "profiles.json");
        _discoveredPath = Path.Combine(paths.DataDirectory, "discovered-modifiers.json");
        _saver = new DebouncedSaver(SaveAsync, TimeSpan.FromMilliseconds(300));
    }

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var doc = await AtomicFile.ReadJsonAsync(_profilesPath, AppJsonContext.Default.ProfileDocument, cancellationToken).ConfigureAwait(false);
            _profiles = doc?.Profiles ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "profiles.json unreadable; moved aside");
            AtomicFile.Quarantine(_profilesPath);
            _profiles = [];
        }

        try
        {
            var disc = await AtomicFile.ReadJsonAsync(_discoveredPath, AppJsonContext.Default.DiscoveredModifiersDocument, cancellationToken).ConfigureAwait(false);
            _discovered = disc?.Modifiers ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "discovered-modifiers.json unreadable; moved aside");
            AtomicFile.Quarantine(_discoveredPath);
            _discovered = [];
        }

        foreach (var module in _games.All)
        {
            if (!_profiles.Exists(p => p.Game == module.Game))
            {
                _profiles.Add(new ModifierProfile { Name = "Default", Game = module.Game });
            }
        }

        foreach (var p in _profiles)
        {
            // Re-key dictionaries with the expected comparer after deserialization.
            p.Rules = new Dictionary<string, ModifierRule>(p.Rules, StringComparer.Ordinal);
        }

        lock (_gate)
        {
            _catalogs.Clear();
        }
    }

    public IReadOnlyList<ModifierProfile> GetProfiles(GameId game)
    {
        lock (_gate)
        {
            return _profiles.Where(p => p.Game == game).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public ModifierProfile GetActiveProfile(GameId game)
    {
        var id = _settings.Current.For(game).ActiveProfileId;
        lock (_gate)
        {
            return _profiles.Find(p => p.Id == id && p.Game == game)
                   ?? _profiles.Find(p => p.Game == game)
                   ?? AddLocked(new ModifierProfile { Name = "Default", Game = game });
        }
    }

    public void SetActiveProfile(GameId game, string profileId)
    {
        _settings.Update(s => s.For(game).ActiveProfileId = profileId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ModifierProfile Create(GameId game, string name)
    {
        ModifierProfile profile;
        lock (_gate)
        {
            profile = AddLocked(new ModifierProfile { Name = UniqueName(game, name), Game = game });
        }

        Commit();
        return profile;
    }

    public ModifierProfile Duplicate(string profileId, string newName)
    {
        ModifierProfile copy;
        lock (_gate)
        {
            var source = Find(profileId);
            copy = AddLocked(source.Clone(UniqueName(source.Game, newName)));
        }

        Commit();
        return copy;
    }

    public void Rename(string profileId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Profile name cannot be empty.", nameof(newName));
        }

        lock (_gate)
        {
            var p = Find(profileId);
            p.Name = newName.Trim();
        }

        Commit();
    }

    public void Delete(string profileId)
    {
        lock (_gate)
        {
            var p = Find(profileId);
            if (_profiles.Count(x => x.Game == p.Game) <= 1)
            {
                throw new InvalidOperationException("At least one profile per game is required.");
            }

            _profiles.Remove(p);
        }

        Commit();
    }

    public void SetLevel(string profileId, string modifierId, DangerLevel level) => SetLevels(profileId, [modifierId], level);

    public void SetLevels(string profileId, IEnumerable<string> modifierIds, DangerLevel level)
    {
        lock (_gate)
        {
            var p = Find(profileId);
            foreach (var id in modifierIds)
            {
                if (p.Rules.TryGetValue(id, out var rule))
                {
                    rule.Level = level;
                    if (level == DangerLevel.Unclassified && rule.Thresholds.Count == 0 && string.IsNullOrEmpty(rule.Note))
                    {
                        p.Rules.Remove(id);
                    }
                }
                else if (level != DangerLevel.Unclassified)
                {
                    p.Rules[id] = new ModifierRule { ModifierId = id, Level = level };
                }
            }
        }

        Commit();
    }

    public void UpdateRule(string profileId, ModifierRule rule)
    {
        lock (_gate)
        {
            var p = Find(profileId);
            if (rule.Level == DangerLevel.Unclassified && rule.Thresholds.Count == 0 && string.IsNullOrWhiteSpace(rule.Note))
            {
                p.Rules.Remove(rule.ModifierId);
            }
            else
            {
                p.Rules[rule.ModifierId] = rule;
            }
        }

        Commit();
    }

    public void SetCombinations(string profileId, IEnumerable<CombinationRule> combinations, int? escalateWarningsAt)
    {
        lock (_gate)
        {
            var p = Find(profileId);
            p.Combinations = combinations.ToList();
            p.EscalateWarningsAt = escalateWarningsAt is > 0 ? escalateWarningsAt : null;
        }

        Commit();
    }

    public string Export(string profileId)
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(Find(profileId), AppJsonContext.Default.ModifierProfile);
        }
    }

    public ModifierProfile Import(string json, GameId? overrideGame = null)
    {
        var profile = JsonSerializer.Deserialize(json, AppJsonContext.Default.ModifierProfile)
                      ?? throw new InvalidDataException("The file does not contain a modifier profile.");
        profile.Id = Guid.NewGuid().ToString("N");
        if (overrideGame is { } g)
        {
            profile.Game = g;
        }

        profile.Rules = new Dictionary<string, ModifierRule>(profile.Rules, StringComparer.Ordinal);
        lock (_gate)
        {
            profile.Name = UniqueName(profile.Game, string.IsNullOrWhiteSpace(profile.Name) ? "Imported" : profile.Name);
            AddLocked(profile);
        }

        Commit();
        return profile;
    }

    public MapModifierCatalog GetCatalog(GameId game)
    {
        lock (_gate)
        {
            if (!_catalogs.TryGetValue(game, out var catalog))
            {
                catalog = new MapModifierCatalog(game, _games.Get(game).MapModifiers.Concat(_discovered));
                _catalogs[game] = catalog;
            }

            return catalog;
        }
    }

    public IReadOnlyList<MapModifierDefinition> GetAllModifiers(GameId game) => GetCatalog(game).All;

    public void RecordDiscovered(GameId game, IEnumerable<ItemModifier> modifiers)
    {
        var catalog = GetCatalog(game);
        var added = false;
        lock (_gate)
        {
            foreach (var m in modifiers)
            {
                if (m.Kind is ModifierKind.Enchant or ModifierKind.Rune || catalog.TryResolve(m.Pattern, out _))
                {
                    continue;
                }

                var id = MapModifierCatalog.AutoId(m.Pattern);
                if (_discovered.Exists(d => d.Id == id))
                {
                    continue;
                }

                _discovered.Add(new MapModifierDefinition(id, game, m.Pattern, "Discovered"));
                added = true;
            }

            if (added)
            {
                _catalogs.Remove(game);
            }
        }

        if (added)
        {
            Commit();
        }
    }

    public Task FlushAsync() => _saver.FlushAsync();

    public ValueTask DisposeAsync() => _saver.DisposeAsync();

    private void Commit()
    {
        _saver.Request();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        ProfileDocument doc;
        DiscoveredModifiersDocument disc;
        lock (_gate)
        {
            doc = new ProfileDocument { Profiles = [.. _profiles] };
            disc = new DiscoveredModifiersDocument { Modifiers = [.. _discovered] };
        }

        await AtomicFile.WriteJsonAsync(_profilesPath, doc, AppJsonContext.Default.ProfileDocument, cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteJsonAsync(_discoveredPath, disc, AppJsonContext.Default.DiscoveredModifiersDocument, cancellationToken).ConfigureAwait(false);
    }

    private ModifierProfile Find(string id) =>
        _profiles.Find(p => p.Id == id) ?? throw new KeyNotFoundException($"Profile {id} not found.");

    private ModifierProfile AddLocked(ModifierProfile profile)
    {
        _profiles.Add(profile);
        return profile;
    }

    private string UniqueName(GameId game, string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Profile" : name.Trim();
        var candidate = baseName;
        var n = 2;
        while (_profiles.Exists(p => p.Game == game && string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} ({n++})";
        }

        return candidate;
    }
}
