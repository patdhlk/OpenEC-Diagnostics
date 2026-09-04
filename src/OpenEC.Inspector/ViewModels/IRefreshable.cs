namespace OpenEC.Inspector.ViewModels;

/// <summary>A view-model the shell's 4 Hz timer refreshes while its view is active.</summary>
public interface IRefreshable
{
    void Refresh();
}
