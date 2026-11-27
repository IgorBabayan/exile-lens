using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using ExileLens.Application.Settings;

namespace ExileLens.App.ViewModels;

/// <summary>One editable row of the Chat settings. Writes straight through to the settings model it wraps.</summary>
public sealed partial class ChatCommandViewModel(ChatCommand model, ISettingsService settings, Action changed) : ObservableObject
{
    internal ChatCommand Model { get; } = model;

    public string Text
    {
        get => Model.Text;
        set => Apply(value?.Trim() ?? string.Empty, Model.Text, v => Model.Text = v);
    }

    public string Hotkey
    {
        get => Model.Hotkey;
        set => Apply(value ?? string.Empty, Model.Hotkey, v => Model.Hotkey = v);
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set => Apply(value, Model.Enabled, v => Model.Enabled = v);
    }

    public bool Send
    {
        get => Model.Send;
        set => Apply(value, Model.Send, v => Model.Send = v);
    }

    [ObservableProperty]
    private string? _error;

    private void Apply<T>(T value, T current, Action<T> write, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
        {
            return;
        }

        settings.Update(_ => write(value));
        OnPropertyChanged(name);
        changed();
    }
}
