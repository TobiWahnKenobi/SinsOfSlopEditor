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

    /// <summary>Problems reported for this line (from the last validation), shown as a marker with tooltip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problemText;

    [ObservableProperty]
    private bool _problemIsError;

    public bool HasProblem => ProblemText != null;

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
    public bool CanEditBrush => Kind == RowKind.Reference && Hint?.Reference == ReferenceKind.Brush && Error is null && Value.Length > 0;
    public bool CanView => Kind == RowKind.Reference && Hint?.Reference is ReferenceKind.Mesh or ReferenceKind.Texture && Error is null && Value.Length > 0;

    /// <summary>What a reference's value means, e.g. the display name of an entity (strings show their text in a box instead).</summary>
    public string? ValueDescription => Kind == RowKind.Reference && !IsStringReference ? _owner.Describe(Hint!.Reference!.Value, Value) : null;

    // ---- String references: the string's text is edited right here, and a missing string can be created ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStringText), nameof(CanCreateString))]
    private bool _isCreatingString;

    [ObservableProperty]
    private string? _stringError;

    private string _newStringText = "";

    public bool IsStringReference => Kind == RowKind.Reference && Hint?.Reference == ReferenceKind.String;
    public bool HasString => IsStringReference && Value.Length > 0 && _owner.Exists(ReferenceKind.String, Value);
    public bool ShowStringText => HasString || IsCreatingString;

    /// <summary>How many places use this string (entities, as last saved); shown when it's more than one, since editing changes all.</summary>
    public int StringUseCount => IsStringReference && Value.Length > 0 ? _owner.StringUseCount(Value) : 0;
    public bool IsSharedString => StringUseCount > 1;
    public string StringUseText => $"used {StringUseCount}×";
    public bool CanCreateString => IsStringReference && Value.Trim().Length > 0 && !Value.Contains('"') && !HasString && !IsCreatingString;

    /// <summary>The referenced string's text; setting it writes the mod's string file (adding the string if it's new).</summary>
    public string StringText
    {
        get => HasString ? _owner.StringText(Value) : _newStringText;
        set
        {
            if (HasString && value == _owner.StringText(Value)) return;
            if (!HasString)
            {
                _newStringText = value;
                // An empty new string isn't created yet.
                if (value.Length == 0) return;
            }
            StringError = _owner.SetString(Value.Trim(), value);
            if (StringError is null)
            {
                IsCreatingString = false;
                _newStringText = "";
            }
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void NewString()
    {
        _newStringText = "";
        StringError = null;
        IsCreatingString = true;
        OnPropertyChanged(nameof(StringText));
    }

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
        OnPropertyChanged(nameof(CanEditBrush));
        OnPropertyChanged(nameof(CanView));
        if (IsStringReference) NotifyString();
    }

    private void NotifyString()
    {
        OnPropertyChanged(nameof(HasString));
        OnPropertyChanged(nameof(ShowStringText));
        OnPropertyChanged(nameof(CanCreateString));
        OnPropertyChanged(nameof(StringText));
        OnPropertyChanged(nameof(StringUseCount));
        OnPropertyChanged(nameof(IsSharedString));
        OnPropertyChanged(nameof(StringUseText));
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
        if (IsStringReference)
        {
            IsCreatingString = false;
            StringError = null;
            NotifyString();
        }
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
    private void EditBrush()
    {
        if (CanEditBrush) _owner.Navigate(new BrushTarget(Value));
    }

    [RelayCommand]
    private void View()
    {
        if (!CanView) return;
        _owner.Navigate(Hint!.Reference == ReferenceKind.Mesh ? new MeshTarget(Value) : new TextureTarget(Value));
    }

    [RelayCommand]
    private void Duplicate() => ListEdits.DuplicateItem(_owner.Session, List!, ItemIndex);

    [RelayCommand]
    private void Remove() => ListEdits.RemoveItem(_owner.Session, List!, ItemIndex);

    [RelayCommand]
    private void AddItem() => ListEdits.AddItem(_owner.Session, List!, _owner.GuessItemKey(this));
}
