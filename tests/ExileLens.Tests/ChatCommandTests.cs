using System.Text.Json;
using ExileLens.Application.Chat;
using ExileLens.Application.Platform;
using ExileLens.Application.Settings;
using ExileLens.Application.Storage;

namespace ExileLens.Tests;

[TestClass]
public sealed class ChatCommandTests
{
    [TestMethod]
    public void DefaultCommandsAreValid()
    {
        var settings = new AppSettings();
        var errors = ChatCommandRules.Validate(settings, out var map);

        Assert.AreEqual(0, errors.Count);
        Assert.AreEqual(6, map.Count);
        Assert.AreEqual("/heist", map[new KeyChord(HotkeyModifiers.None, "F8")].Text);
        Assert.AreEqual("/menagerie", map[new KeyChord(HotkeyModifiers.None, "F10")].Text);
        Assert.AreEqual("/delve", map[new KeyChord(HotkeyModifiers.None, "F9")].Text);
        Assert.AreEqual("/sanctum", map[new KeyChord(HotkeyModifiers.None, "F11")].Text);
        Assert.AreEqual("/kingsmarch", map[new KeyChord(HotkeyModifiers.None, "F6")].Text);
        Assert.AreEqual("/monastery", map[new KeyChord(HotkeyModifiers.None, "F7")].Text);
    }

    [TestMethod]
    public void ConflictsAreReportedAndExcluded()
    {
        var settings = new AppSettings();
        var custom = new ChatCommand { Text = "/hideout", Hotkey = "F8" };
        var action = new ChatCommand { Text = "/exit", Hotkey = "Ctrl+D" };
        var reserved = new ChatCommand { Text = "/kick", Hotkey = "Ctrl+V" };
        var empty = new ChatCommand { Hotkey = "F5" };
        settings.Chat.Commands = [.. settings.Chat.Commands, custom, action, reserved, empty, new ChatCommand()];

        var errors = ChatCommandRules.Validate(settings, out var map);

        Assert.AreEqual(4, errors.Count);
        Assert.IsTrue(errors.ContainsKey(custom));
        StringAssert.Contains(errors[action], "Price check");
        Assert.IsTrue(errors.ContainsKey(reserved));
        Assert.IsTrue(errors.ContainsKey(empty));
        Assert.AreEqual("/heist", map[new KeyChord(HotkeyModifiers.None, "F8")].Text);
        Assert.AreEqual(6, map.Count);
    }

    [TestMethod]
    public void DisabledCommandsAreSkipped()
    {
        var settings = new AppSettings();
        settings.Chat.Commands[0].Enabled = false;

        var errors = ChatCommandRules.Validate(settings, out var map);

        Assert.AreEqual(0, errors.Count);
        Assert.IsFalse(map.ContainsKey(new KeyChord(HotkeyModifiers.None, "F8")));
    }

    [TestMethod]
    public void SavedCommandsReplaceDefaults()
    {
        const string json = """{ "schemaVersion": 1, "chat": { "commands": [ { "text": "/hideout", "hotkey": "F5", "send": false } ] } }""";

        var settings = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings)!;

        Assert.AreEqual(1, settings.Chat.Commands.Count);
        Assert.AreEqual("/hideout", settings.Chat.Commands[0].Text);
        Assert.IsFalse(settings.Chat.Commands[0].Send);
    }

    [TestMethod]
    public void OlderSettingsGetDefaultCommands()
    {
        var settings = JsonSerializer.Deserialize("""{ "schemaVersion": 1 }""", AppJsonContext.Default.AppSettings)!;

        Assert.IsTrue(settings.Chat.Enabled);
        Assert.AreEqual(6, settings.Chat.Commands.Count);
    }
}
