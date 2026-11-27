using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ExileLens.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // Shortcut fields record the pressed combination instead of accepting typed text.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
    }

    private static void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox box || !box.Classes.Contains("shortcut"))
        {
            return;
        }

        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None)
        {
            box.Text = string.Empty;
            return;
        }

        var key = KeyName(e.Key);
        if (key is null)
        {
            return;
        }

        var parts = new List<string>(4);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Meta");
        }

        parts.Add(key);
        box.Text = string.Join("+", parts);
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Escape => "Escape",
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Enter => "Enter",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "Pageup",
        Key.PageDown => "Pagedown",
        _ => null,
    };
}
