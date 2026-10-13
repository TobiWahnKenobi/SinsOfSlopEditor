using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Navigation;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.Index;

namespace SinsEditor.App.Editing;

public enum RowKind
{
    /// <summary>A line without value that opens a block, e.g. <c>Weapon</c> or <c>Page:0</c>.</summary>
    Section,
    Bool,
    Number,
    /// <summary>Free text (quoted in the file).</summary>
    Text,
    /// <summary>Text with values seen elsewhere offered in a dropdown.</summary>
    Choice,
    Reference,
    /// <summary>Anything else (vectors, unquoted words), edited as written in the file.</summary>
    Raw,
}

/// <summary>One line of an entity in the form editor.</summary>
public sealed partial class PropertyRowViewModel : ObservableObject
{
    private readonly PropertyEditorViewModel _owner;
    private string _value = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    private bool _isPickerOpen;

    [ObservableProperty]
    private string _pickerQuery = "";

    [ObservableProperty]
    private IReadOnlyList<PickerItem> _pickerResults = [];

    public PropertyRowViewModel(PropertyEditorViewModel owner, int lineIndex, string context, RowKind kind, PropertyHint? hint,
        CountedList? list, int itemIndex, bool isCounter)
    {
        _owner = owner;
        LineIndex = lineIndex;
        Context = context;
        Kind = kind;
        Hint = hint;
        List = list;
        ItemIndex = itemIndex;
        IsCounter = isCounter;
        Reload();
    }

    public int LineIndex { get; }
    public string Context { get; }
    public RowKind Kind { get; }
    public PropertyHint? Hint { get; }
    public CountedList? List { get; }
    /// <summary>Position in <see cref="List"/> when this row is a list item, otherwise -1.</summary>
    public int ItemIndex { get; }
    /// <summary>Counters are managed by the editor: they change when items are added or removed.</summary>
    public bool IsCounter { get; }

    private SinsLine Line => _owner.Session.Document.Lines[LineIndex];
    public string Key => Line.Key;
    public int Depth => Line.Depth;
    public bool IsListItem => ItemIndex >= 0;
    public bool CanAddItem => IsCounter && (List!.Items.Count > 0 || List.ItemKey != null || _owner.GuessItemKey(this) != null);
    public bool HasError => Error != null;
    public IReadOnlyList<string> Choices => Hint?.KnownValues ?? [];
    public bool CanGoTo => Kind == RowKind.Reference && Hint?.Reference == ReferenceKind.Entity && Error is null && Value.Length > 0;
    public bool IsReadOnly => IsCounter;

    /// <summary>What a reference's value means, e.g. the display name of an entity or the text of a string.</summary>
    public string? ValueDescription => Kind == RowKind.Reference ? _owner.Describe(Hint!.Reference!.Value, Value) : null;

    /// <summary>The value as edited: strings without quotes, everything else as written.</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (value == _value) return;
            _value = value;
            OnPropertyChanged();
            Commit();
        }
    }

    public bool BoolValue
    {
        get => Line.Value == "TRUE";
        set => _owner.Session.SetValue(LineIndex, SinsValue.FormatBool(value));
    }

    /// <summary>Re-reads the value after undo/redo or an edit elsewhere.</summary>
    public void Reload()
    {
        var raw = Line.Value;
        _value = Kind is RowKind.Text or RowKind.Choice or RowKind.Reference && SinsValue.TryGetString(raw, out var s) ? s : raw;
        Error = Validate(_value);
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(ValueDescription));
        OnPropertyChanged(nameof(CanGoTo));
    }

    private void Commit()
    {
        Error = Validate(_value);
        if (Error != null && Kind is RowKind.Number or RowKind.Text or RowKind.Choice) return;
        string raw = Kind switch
        {
            RowKind.Text or RowKind.Choice or RowKind.Reference => SinsValue.FormatString(_value),
            RowKind.Number => _value.Trim(),
            _ => _value,
        };
        if (Kind == RowKind.Reference && _value.Contains('"')) return;
        _owner.Session.SetValue(LineIndex, raw);
        OnPropertyChanged(nameof(ValueDescription));
        OnPropertyChanged(nameof(CanGoTo));
    }

    private string? Validate(string value) => Kind switch
    {
        RowKind.Number when !SinsValue.TryGetDouble(value.Trim(), out _) => "Not a number",
        RowKind.Number when Hint?.Kind == SinsValueKind.Integer && !SinsValue.TryGetInt(value.Trim(), out _) => "Needs a whole number",
        RowKind.Text or RowKind.Choice or RowKind.Reference when value.Contains('"') => "Quotes aren't allowed",
        RowKind.Reference when value.Length > 0 && !_owner.Exists(Hint!.Reference!.Value, value) =>
            $"No {Hint.Reference.Value.ToString().ToLowerInvariant()} with this name",
        _ => null,
    };

    [RelayCommand]
    private void OpenPicker()
    {
        PickerQuery = "";
        UpdatePickerResults();
        IsPickerOpen = true;
    }

    partial void OnPickerQueryChanged(string value) => UpdatePickerResults();

    private void UpdatePickerResults() =>
        PickerResults = _owner.Candidates(this).Where(c => c.Matches(PickerQuery)).Take(300).ToList();

    [RelayCommand]
    private void Choose(PickerItem? item)
    {
        if (item is null) return;
        IsPickerOpen = false;
        Value = item.Value;
    }

    [RelayCommand]
    private void GoTo()
    {
        if (CanGoTo) _owner.Navigate(new EntityTarget(Value));
    }

    [RelayCommand]
    private void Duplicate() => ListEdits.DuplicateItem(_owner.Session, List!, ItemIndex);

    [RelayCommand]
    private void Remove() => ListEdits.RemoveItem(_owner.Session, List!, ItemIndex);

    [RelayCommand]
    private void AddItem() => ListEdits.AddItem(_owner.Session, List!, _owner.GuessItemKey(this));
}
