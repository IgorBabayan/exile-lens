using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExileLens.Application.Capture;
using ExileLens.Application.Games;
using ExileLens.Application.Market;
using ExileLens.Application.Platform;
using ExileLens.Application.Pricing;
using ExileLens.Core.Items;
using Microsoft.Extensions.DependencyInjection;

namespace ExileLens.App.ViewModels;

public sealed record GameOption(GameId Game, string Name);

/// <summary>Manual price check: paste item text (useful without global shortcuts, e.g. on some Wayland setups).</summary>
public sealed partial class PriceCheckPageViewModel(
    IItemParser parser,
    IPriceEstimationService pricing,
    IGameRegistry games,
    ILeagueService leagues,
    IGameDetectionService detection,
    IClipboardService clipboard,
    IServiceProvider services) : PageViewModel
{
    public IReadOnlyList<GameOption> GameOptions { get; } = games.All.Select(g => new GameOption(g.Game, g.DisplayName)).ToList();

    [ObservableProperty]
    private GameOption? _selectedGame;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private PriceCheckOverlayViewModel? _result;

    public override void OnActivated()
    {
        var game = detection.Resolve(null);
        SelectedGame ??= GameOptions.FirstOrDefault(o => o.Game == game) ?? GameOptions[0];
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        InputText = await clipboard.GetTextAsync() ?? string.Empty;
        Check();
    }

    [RelayCommand]
    private void Check()
    {
        Error = null;
        var game = SelectedGame?.Game ?? GameId.Poe2;
        if (!parser.TryParse(InputText, game, out var item, out var error) || item is null)
        {
            Error = error ?? "The text is not a Path of Exile item.";
            Result?.Detach();
            Result = null;
            return;
        }

        var module = games.Get(game);
        var session = pricing.CreateSession(item, module, leagues.GetActiveLeague(game));
        Result?.Detach();
        var vm = services.GetRequiredService<PriceCheckOverlayViewModel>();
        vm.IsEmbedded = true;
        vm.Load(session);
        Result = vm;
        vm.Start();
    }

    public override void OnDeactivated() => Result?.Detach();
}
