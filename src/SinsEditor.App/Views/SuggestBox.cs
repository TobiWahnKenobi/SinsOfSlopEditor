using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SinsEditor.App.Views;

/// <summary>
/// A text box that suggests matching names from a long list (e.g. thousands of brushes) while typing: names starting with
/// the text first, then names containing it, at most <see cref="MaxSuggestions"/>. Down/Enter/Escape work as expected.
/// Only matches are put in the drop-down, so it stays fast however long the list is.
/// </summary>
public sealed class SuggestBox : Grid
{
    public const int MaxSuggestions = 40;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SuggestBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((SuggestBox)d).OnTextChanged((string?)e.NewValue)));

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<string>), typeof(SuggestBox),
        new PropertyMetadata(null));

    private readonly TextBox _box = new();
    private readonly ListBox _list = new() { MaxHeight = 280, MinWidth = 200 };
    private readonly Popup _popup;
    private bool _updating;

    public SuggestBox()
    {
        _popup = new Popup
        {
            Child = new Border { Child = _list, BorderThickness = new Thickness(1), Background = SystemColors.WindowBrush },
            PlacementTarget = _box, Placement = PlacementMode.Bottom, StaysOpen = false,
        };
        Children.Add(_box);
        Children.Add(_popup);
        _box.TextChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            Text = _box.Text;
            _updating = false;
            Suggest();
        };
        _box.PreviewKeyDown += OnBoxKeyDown;
        _box.LostKeyboardFocus += (_, e) => { if (e.NewFocus is not ListBoxItem) _popup.IsOpen = false; };
        _list.PreviewMouseLeftButtonUp += (_, _) => Accept();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _popup.IsOpen = false; _box.Focus(); e.Handled = true; }
        };
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IReadOnlyList<string>? Items
    {
        get => (IReadOnlyList<string>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    private void OnTextChanged(string? value)
    {
        if (_updating || _box.Text == value) return;
        _updating = true;
        _box.Text = value ?? "";
        _updating = false;
    }

    private void Suggest()
    {
        string text = _box.Text.Trim();
        if (text.Length == 0 || Items is not { } items || !_box.IsKeyboardFocusWithin)
        {
            _popup.IsOpen = false;
            return;
        }
        var matches = items.Where(i => i.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .Concat(items.Where(i => i.Contains(text, StringComparison.OrdinalIgnoreCase) && !i.StartsWith(text, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxSuggestions).ToList();
        // Nothing to suggest once the text is exactly a name.
        if (matches.Count == 0 || (matches.Count == 1 && matches[0].Equals(text, StringComparison.OrdinalIgnoreCase)))
        {
            _popup.IsOpen = false;
            return;
        }
        _list.ItemsSource = matches;
        _popup.MinWidth = _box.ActualWidth;
        _popup.IsOpen = true;
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen) return;
        if (e.Key == Key.Down)
        {
            _list.SelectedIndex = 0;
            (_list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _popup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
            Accept();
            e.Handled = true;
        }
    }

    private void Accept()
    {
        if (_list.SelectedItem is not string choice) return;
        _popup.IsOpen = false;
        _updating = true;
        _box.Text = choice;
        _box.CaretIndex = choice.Length;
        _updating = false;
        Text = choice;
        _box.Focus();
    }
}
