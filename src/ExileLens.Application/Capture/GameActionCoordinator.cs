using ExileLens.Application.Chat;
using ExileLens.Application.Games;
using ExileLens.Application.History;
using ExileLens.Application.Maps;
using ExileLens.Application.Market;
using ExileLens.Application.Platform;
using ExileLens.Application.Pricing;
using ExileLens.Application.Settings;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;
using Microsoft.Extensions.Logging;

namespace ExileLens.Application.Capture;

public interface IOverlayService
{
    bool IsVisible { get; }

    /// <summary>Raised on the UI thread when an overlay is shown or hidden.</summary>
    event EventHandler? VisibilityChanged;

    void ShowPriceCheck(PriceCheckSession session, GameWindowInfo window, PixelPoint? cursor);
    void ShowMapAnalysis(MapAnalysisResult result, GameWindowInfo window, PixelPoint? cursor);
    void ShowNotice(string title, string message, GameWindowInfo? window, PixelPoint? cursor);
    void Close();
}

public interface IGameDetectionService
{
    /// <summary>Game to use for an action given the detected window and the user's selection.</summary>
    GameId Resolve(GameWindowInfo? window);

    GameWindowInfo? Detect();
}

public sealed class GameDetectionService(IGameWindowService windows, ISettingsService settings) : IGameDetectionService
{
    public GameWindowInfo? Detect() => windows.GetActiveGameWindow() ?? windows.FindGameWindow();

    public GameId Resolve(GameWindowInfo? window) => settings.Current.General.Game switch
    {
        GameSelection.Poe1 => GameId.Poe1,
        GameSelection.Poe2 => GameId.Poe2,
        _ => window?.Game ?? settings.Current.General.PreferredGame,
    };
}

/// <summary>Wires shortcuts → item capture → parsing → overlays. All heavy work stays off the UI thread.</summary>
public sealed class GameActionCoordinator : IDisposable
{
    private static readonly Dictionary<KeyChord, ChatMacro> NoChat = [];

    // Ignores key repeat from a held chat shortcut: 400 ms in Stopwatch ticks.
    private static readonly long ChatCooldownTicks = System.Diagnostics.Stopwatch.Frequency * 2 / 5;

    private readonly IGlobalHotkeyService _hotkeys;
    private readonly IGameWindowService _windows;
    private readonly IGameDetectionService _detection;
    private readonly IItemCaptureService _capture;
    private readonly IItemParser _parser;
    private readonly IGameRegistry _games;
    private readonly IPriceEstimationService _pricing;
    private readonly IMapModifierAnalyzer _mapAnalyzer;
    private readonly IModifierProfileService _profiles;
    private readonly IHistoryService _history;
    private readonly ILeagueService _leagues;
    private readonly ISettingsService _settings;
    private readonly IOverlayService _overlay;
    private readonly IChatCommandService _chat;
    private readonly ILogger<GameActionCoordinator> _logger;
    private int _busy;
    private long _lastChatAt;

    // Rebuilt only when settings or overlay visibility change; read lock-free on the hotkey thread.
    private volatile Dictionary<KeyChord, ChatMacro> _chatMap = NoChat;
    private string? _registeredSignature;
    private IReadOnlyDictionary<KeyChord, string> _platformErrors = new Dictionary<KeyChord, string>();

    public GameActionCoordinator(
        IGlobalHotkeyService hotkeys,
        IGameWindowService windows,
        IGameDetectionService detection,
        IItemCaptureService capture,
        IItemParser parser,
        IGameRegistry games,
        IPriceEstimationService pricing,
        IMapModifierAnalyzer mapAnalyzer,
        IModifierProfileService profiles,
        IHistoryService history,
        ILeagueService leagues,
        ISettingsService settings,
        IOverlayService overlay,
        IChatCommandService chat,
        ILogger<GameActionCoordinator> logger)
    {
        _hotkeys = hotkeys;
        _windows = windows;
        _detection = detection;
        _capture = capture;
        _parser = parser;
        _games = games;
        _pricing = pricing;
        _mapAnalyzer = mapAnalyzer;
        _profiles = profiles;
        _history = history;
        _leagues = leagues;
        _settings = settings;
        _overlay = overlay;
        _chat = chat;
        _logger = logger;
    }

    public IReadOnlyDictionary<HotkeyAction, string> RegistrationErrors { get; private set; } = new Dictionary<HotkeyAction, string>();

    public event EventHandler? RegistrationChanged;

    public void Start()
    {
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        _settings.Changed += OnSettingsChanged;
        _overlay.VisibilityChanged += OnSettingsChanged;
        RegisterHotkeys();
    }

    /// <summary>Validates bindings: unparseable chords and duplicates are reported as conflicts.</summary>
    public static IReadOnlyDictionary<HotkeyAction, string> Validate(GameSettings g, out Dictionary<HotkeyAction, KeyChord> chords)
    {
        var errors = new Dictionary<HotkeyAction, string>();
        chords = [];
        foreach (var (action, text) in new[]
                 {
                     (HotkeyAction.PriceCheck, g.PriceCheckHotkey),
                     (HotkeyAction.MapCheck, g.MapCheckHotkey),
                     (HotkeyAction.CloseOverlay, g.CloseOverlayHotkey),
                 })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!KeyChord.TryParse(text, out var chord))
            {
                errors[action] = $"\"{text}\" is not a valid shortcut.";
                continue;
            }

            var clash = chords.FirstOrDefault(kv => kv.Value == chord);
            if (clash.Value != default)
            {
                errors[action] = $"{chord} is already used for {Describe(clash.Key)}.";
                continue;
            }

            if (chord.Modifiers == HotkeyModifiers.Control && chord.Key == "C")
            {
                errors[action] = "Ctrl + C is used by the game to copy items.";
                continue;
            }

            chords[action] = chord;
        }

        return errors;
    }

    public static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.PriceCheck => "Price check",
        HotkeyAction.MapCheck => "Map modifier check",
        _ => "Close overlay",
    };

    private void OnSettingsChanged(object? sender, EventArgs e) => RegisterHotkeys();

    /// <summary>Registers the union of both games' shortcuts; the action is resolved per press for the active game.</summary>
    private void RegisterHotkeys()
    {
        var general = _settings.Current.General;
        var errors = new Dictionary<HotkeyAction, string>();
        var chords = new HashSet<KeyChord>();
        var gameForErrors = _detection.Resolve(_windows.GetActiveGameWindow());
        foreach (var module in _games.All)
        {
            var gameErrors = Validate(_settings.Current.For(module.Game), out var map);
            if (module.Game == gameForErrors)
            {
                foreach (var (k, v) in gameErrors)
                {
                    errors[k] = v;
                }
            }

            foreach (var (action, chord) in map)
            {
                if ((action == HotkeyAction.PriceCheck && !general.PriceCheckEnabled) || (action == HotkeyAction.MapCheck && !general.MapCheckEnabled))
                {
                    continue;
                }

                // The close key (Escape by default) is only grabbed while an overlay is visible so the
                // game's own use of the key is never blocked.
                if (action == HotkeyAction.CloseOverlay && !_overlay.IsVisible)
                {
                    continue;
                }

                chords.Add(chord);
            }
        }

        var chatMap = NoChat;
        if (_settings.Current.Chat.Enabled)
        {
            // Invalid or conflicting chat commands are left out; the settings page reports them.
            ChatCommandRules.Validate(_settings.Current, out var macros);
            foreach (var chord in macros.Keys)
            {
                chords.Add(chord);
            }

            chatMap = macros;
        }

        _chatMap = chatMap;

        var signature = string.Join('|', chords.Select(c => c.ToString()).Order(StringComparer.Ordinal));
        if (signature != _registeredSignature)
        {
            _registeredSignature = signature;
            _platformErrors = _hotkeys.Register(chords);
        }

        var active = _settings.Current.For(gameForErrors);
        Validate(active, out var activeMap);
        foreach (var (action, chord) in activeMap)
        {
            if (_platformErrors.TryGetValue(chord, out var err))
            {
                errors[action] = err;
            }
        }

        if (!_hotkeys.IsSupported && _hotkeys.Limitation is { } limitation)
        {
            errors.TryAdd(HotkeyAction.PriceCheck, limitation);
        }

        RegistrationErrors = errors;
        RegistrationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        var window = _windows.GetActiveGameWindow();
        var game = _detection.Resolve(window);
        Validate(_settings.Current.For(game), out var map);
        HotkeyAction? action = null;
        foreach (var (a, chord) in map)
        {
            if (chord == e.Chord)
            {
                action = a;
                break;
            }
        }

        if (action is null)
        {
            if (window is not null && _chatMap.TryGetValue(e.Chord, out var macro))
            {
                TriggerChat(macro, window);
            }

            return;
        }

        if (action == HotkeyAction.CloseOverlay)
        {
            _overlay.Close();
            return;
        }

        if (window is null)
        {
            return; // capture actions only run while the game is focused
        }

        if ((action == HotkeyAction.PriceCheck && !_settings.Current.General.PriceCheckEnabled)
            || (action == HotkeyAction.MapCheck && !_settings.Current.General.MapCheckEnabled))
        {
            return;
        }

        Trigger(action.Value);
    }

    /// <summary>
    /// Triggers an action without a global shortcut (e.g. from the command line / compositor key binding on
    /// Wayland, or the tray menu). Capture still only happens while the game is the focused window.
    /// </summary>
    public void Trigger(HotkeyAction action)
    {
        if (action == HotkeyAction.CloseOverlay)
        {
            _overlay.Close();
            return;
        }

        // Ignore key repeat / double presses while a capture is in flight.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(action).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Action {Action} failed", action);
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        });
    }

    /// <summary>Pastes a chat command into the focused game. Key repeat from a held shortcut is ignored.</summary>
    private void TriggerChat(ChatMacro macro, GameWindowInfo window)
    {
        if (!_chat.IsSupported)
        {
            _overlay.ShowNotice("Chat commands unavailable", _chat.Limitation ?? "Keystrokes cannot be sent to the game on this platform.", window, _windows.GetCursorPosition());
            return;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref _lastChatAt) < ChatCooldownTicks)
        {
            return;
        }

        // Shares the busy gate with item capture: both drive the clipboard and the game's input.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        Interlocked.Exchange(ref _lastChatAt, now);
        _ = Task.Run(async () =>
        {
            try
            {
                if (_windows.GetActiveGameWindow() is null)
                {
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _chat.SendAsync(macro.Text, macro.Send, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chat command failed");
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        });
    }

    private async Task HandleAsync(HotkeyAction action)
    {
        var window = _windows.GetActiveGameWindow();
        if (window is null)
        {
            return; // only act while the game is focused
        }

        var cursor = _windows.GetCursorPosition();
        var gameId = _detection.Resolve(window);
        var module = _games.Get(gameId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var text = await _capture.CaptureAsync(_settings.Current.General.UseAdvancedCopy, cts.Token).ConfigureAwait(false);
        if (text is null)
        {
            _overlay.ShowNotice("No item detected", "Hover an item in the game, then press the shortcut again.", window, cursor);
            return;
        }

        if (!_parser.TryParse(text, gameId, out var item, out var error) || item is null)
        {
            _overlay.ShowNotice("Unrecognized item", error ?? "The copied text could not be parsed.", window, cursor);
            return;
        }

        if (action == HotkeyAction.MapCheck)
        {
            if (!MapModifierAnalyzer.IsMapLike(item))
            {
                _overlay.ShowNotice("Not a map", $"{item.DisplayName} is not a map or waystone.", window, cursor);
                return;
            }

            var profile = _profiles.GetActiveProfile(gameId);
            _profiles.RecordDiscovered(gameId, item.Modifiers);
            var result = _mapAnalyzer.Analyze(item, profile, _profiles.GetCatalog(gameId));
            _overlay.ShowMapAnalysis(result, window, cursor);
            _history.Add(new MapCheckHistoryEntry
            {
                Timestamp = DateTimeOffset.Now,
                Game = gameId,
                MapName = item.DisplayName,
                Tier = item.MapTier,
                ProfileName = profile.Name,
                Overall = result.Overall,
                Modifiers = result.Modifiers.Select(m => m.Modifier.Text).ToList(),
            });
            return;
        }

        var league = _leagues.GetActiveLeague(gameId);
        var session = _pricing.CreateSession(item, module, league);
        _overlay.ShowPriceCheck(session, window, cursor);
    }

    public void Dispose()
    {
        _hotkeys.HotkeyPressed -= OnHotkeyPressed;
        _settings.Changed -= OnSettingsChanged;
        _overlay.VisibilityChanged -= OnSettingsChanged;
    }
}
