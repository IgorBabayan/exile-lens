using ExileLens.Application.Capture;
using ExileLens.Application.Platform;
using ExileLens.Application.Settings;
using Microsoft.Extensions.Logging;

namespace ExileLens.Application.Chat;

/// <summary>Immutable snapshot of a chat command, safe to read from the hotkey thread.</summary>
public sealed record ChatMacro(string Text, bool Send);

public static class ChatCommandRules
{
    /// <summary>
    /// Validates every chat command against the action shortcuts of both games and against each other.
    /// Returns an error per invalid command (keyed by reference); <paramref name="map"/> receives the usable
    /// chords. Disabled and empty commands are skipped silently.
    /// </summary>
    public static Dictionary<ChatCommand, string> Validate(AppSettings settings, out Dictionary<KeyChord, ChatMacro> map)
    {
        var commands = settings.Chat.Commands;
        var errors = new Dictionary<ChatCommand, string>(ReferenceEqualityComparer.Instance);
        map = new Dictionary<KeyChord, ChatMacro>(commands.Count);
        Dictionary<HotkeyAction, KeyChord>? poe1 = null;
        Dictionary<HotkeyAction, KeyChord>? poe2 = null;

        foreach (var command in commands)
        {
            if (!command.Enabled || string.IsNullOrWhiteSpace(command.Hotkey))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(command.Text))
            {
                errors[command] = "Enter the text to paste into the chat.";
                continue;
            }

            if (!KeyChord.TryParse(command.Hotkey, out var chord))
            {
                errors[command] = $"\"{command.Hotkey}\" is not a valid shortcut.";
                continue;
            }

            if (IsReserved(chord))
            {
                errors[command] = $"{chord} is used while typing into the chat.";
                continue;
            }

            // Action shortcuts are only parsed when a chat command actually needs the comparison.
            poe1 ??= Actions(settings.Poe1);
            poe2 ??= Actions(settings.Poe2);
            if (Clash(poe1, chord) is { } a1)
            {
                errors[command] = $"{chord} is already used for {GameActionCoordinator.Describe(a1)} (Path of Exile).";
                continue;
            }

            if (Clash(poe2, chord) is { } a2)
            {
                errors[command] = $"{chord} is already used for {GameActionCoordinator.Describe(a2)} (Path of Exile 2).";
                continue;
            }

            if (map.TryGetValue(chord, out var existing))
            {
                errors[command] = $"{chord} is already used for {existing.Text}.";
                continue;
            }

            map[chord] = new ChatMacro(command.Text.Trim(), command.Send);
        }

        return errors;
    }

    /// <summary>Chords the chat sequence itself injects (Enter, select all, paste, copy) can never be chat shortcuts.</summary>
    public static bool IsReserved(KeyChord chord) =>
        chord.Key == "Enter"
        || ((chord.Modifiers.HasFlag(HotkeyModifiers.Control) || chord.Modifiers.HasFlag(HotkeyModifiers.Meta)) && chord.Key is "A" or "V" or "C");

    private static Dictionary<HotkeyAction, KeyChord> Actions(GameSettings game)
    {
        GameActionCoordinator.Validate(game, out var chords);
        return chords;
    }

    private static HotkeyAction? Clash(Dictionary<HotkeyAction, KeyChord> actions, KeyChord chord)
    {
        foreach (var (action, c) in actions)
        {
            if (c == chord)
            {
                return action;
            }
        }

        return null;
    }
}

public interface IChatCommandService
{
    bool IsSupported { get; }
    string? Limitation { get; }

    /// <summary>Opens the game chat, replaces its input with <paramref name="text"/> and optionally sends it.</summary>
    Task SendAsync(string text, bool send, CancellationToken cancellationToken);
}

public sealed class ChatCommandService(
    IClipboardService clipboard,
    IKeyboardInputService keyboard,
    TimeProvider time,
    ILogger<ChatCommandService> logger) : IChatCommandService
{
    // Gives the game one frame or two to open the chat input before pasting / sending.
    private static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(250);

    public bool IsSupported => keyboard.IsSupported;

    public string? Limitation => keyboard.Limitation;

    public async Task SendAsync(string text, bool send, CancellationToken cancellationToken)
    {
        string? previous = null;
        try
        {
            previous = await clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Clipboard read skipped");
        }

        await clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false);

        keyboard.SendChatKeystroke(ChatKeystroke.Enter);
        await Task.Delay(StepDelay, time, cancellationToken).ConfigureAwait(false);
        keyboard.SendChatKeystroke(ChatKeystroke.SelectAll);
        keyboard.SendChatKeystroke(ChatKeystroke.Paste);
        if (send)
        {
            await Task.Delay(StepDelay, time, cancellationToken).ConfigureAwait(false);
            keyboard.SendChatKeystroke(ChatKeystroke.Enter);
        }

        if (previous is not null && !string.Equals(previous, text, StringComparison.Ordinal))
        {
            _ = RestoreAsync(previous, text);
        }
    }

    /// <summary>Restores the user's clipboard only if it still holds the chat text (never clobbers newer user copies).</summary>
    private async Task RestoreAsync(string previous, string pasted)
    {
        try
        {
            await Task.Delay(RestoreDelay, time).ConfigureAwait(false);
            var now = await clipboard.GetTextAsync().ConfigureAwait(false);
            if (string.Equals(now, pasted, StringComparison.Ordinal))
            {
                await clipboard.SetTextAsync(previous).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Never log clipboard contents.
            logger.LogDebug(ex, "Clipboard restore skipped");
        }
    }
}
