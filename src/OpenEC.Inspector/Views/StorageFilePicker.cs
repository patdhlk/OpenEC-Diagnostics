using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Views;

public sealed class StorageFilePicker(Window window) : IFilePicker
{
    public async Task<string?> PickFileAsync(string title, params string[] extensions)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(title)
                {
                    Patterns = extensions.Select(e => $"*.{e}").ToArray(),
                },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string defaultName, string extension)
    {
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultName,
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType(title) { Patterns = [$"*.{extension}"] },
            ],
        });
        return file?.TryGetLocalPath();
    }
}
