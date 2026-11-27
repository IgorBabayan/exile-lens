using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace ExileLens.App.Services;

/// <summary>Native open/save dialogs for profile import and export.</summary>
public static class FileDialogService
{
    private static readonly FilePickerFileType JsonType = new("Exile Lens profile") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    private static IStorageProvider? Provider =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.StorageProvider;

    public static async Task<string?> OpenTextAsync(string title)
    {
        if (Provider is not { } provider)
        {
            return null;
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [JsonType] });
        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public static async Task<bool> SaveTextAsync(string title, string suggestedName, string content)
    {
        if (Provider is not { } provider)
        {
            return false;
        }

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [JsonType],
        });
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
        return true;
    }
}
