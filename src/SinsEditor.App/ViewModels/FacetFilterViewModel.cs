using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.Core.Index;

namespace SinsEditor.App.ViewModels;

public sealed partial class FacetValueViewModel(string value, int count, Action changed) : ObservableObject
{
    public const string None = "(none)";

    public string Value { get; } = value;
    public int Count { get; } = count;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => changed();
}

/// <summary>One filter dropdown: the values of a facet with counts; rows match if they have any selected value.</summary>
public sealed partial class FacetFilterViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _suspend;

    public FacetFilterViewModel(Facet facet, IEnumerable<IReadOnlyList<string>> rowValues, Action changed)
    {
        Facet = facet;
        _changed = changed;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var values in rowValues)
        {
            if (values.Count == 0) counts[FacetValueViewModel.None] = counts.GetValueOrDefault(FacetValueViewModel.None) + 1;
            foreach (string v in values) counts[v] = counts.GetValueOrDefault(v) + 1;
        }
        Values = counts
            .OrderBy(kv => kv.Key == FacetValueViewModel.None)
            .ThenBy(kv => int.TryParse(kv.Key, out int n) ? n : int.MaxValue)
            .ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(kv => new FacetValueViewModel(kv.Key, kv.Value, OnValueChanged))
            .ToList();
    }

    public Facet Facet { get; }
    public string Title => Facet.Title;
    public IReadOnlyList<FacetValueViewModel> Values { get; }
    public bool HasSelection => Values.Any(v => v.IsSelected);

    /// <summary>"Armor: Light, Heavy" or just "Armor".</summary>
    public string Summary
    {
        get
        {
            var selected = Values.Where(v => v.IsSelected).Select(v => v.Value).ToList();
            return selected.Count switch
            {
                0 => Title,
                <= 2 => $"{Title}: {string.Join(", ", selected)}",
                _ => $"{Title}: {selected.Count} selected",
            };
        }
    }

    public bool Matches(IReadOnlyList<string> rowValues)
    {
        if (!HasSelection) return true;
        if (rowValues.Count == 0) return Values.Any(v => v.IsSelected && v.Value == FacetValueViewModel.None);
        return Values.Any(v => v.IsSelected && rowValues.Contains(v.Value));
    }

    [RelayCommand]
    public void Clear()
    {
        _suspend = true;
        foreach (var v in Values) v.IsSelected = false;
        _suspend = false;
        OnValueChanged();
    }

    private void OnValueChanged()
    {
        if (_suspend) return;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSelection));
        _changed();
    }
}
