using CommunityToolkit.Mvvm.ComponentModel;

namespace SinsEditor.App.ViewModels;

/// <summary>Base for everything shown in the main content area.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Path shown above the page, e.g. "Frigates".</summary>
    public virtual string? Breadcrumb => null;

    /// <summary>Whether the page shows unsaved changes (marks its tab).</summary>
    public virtual bool IsDirty => false;

    /// <summary>Called when the page is discarded, to stop listening to shared objects.</summary>
    public virtual void Detach() { }
}
