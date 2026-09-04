namespace OpenEC.Inspector.ViewModels;

/// <summary>Testable seam over the platform file dialogs. Both methods return null when cancelled.</summary>
public interface IFilePicker
{
    Task<string?> PickFileAsync(string title, params string[] extensions);

    Task<string?> PickSaveFileAsync(string title, string defaultName, string extension);
}
