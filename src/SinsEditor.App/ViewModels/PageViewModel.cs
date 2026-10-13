using CommunityToolkit.Mvvm.ComponentModel;

namespace SinsEditor.App.ViewModels;

/// <summary>Base for everything shown in the main content area.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Path shown above the page, e.g. "Frigates".</summary>
    public virtual string? Breadcrumb => null;
}
