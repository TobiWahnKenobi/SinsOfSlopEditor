using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SinsEditor.App.Views;

/// <summary>
/// A text box that suggests matching names from a long list (e.g. thousands of brushes) while typing: names starting with
/// the text first, then names containing it. The button next to it lists everything (or the matches). The list is
/// virtualized, so it stays fast however long it is. Down/Enter/Escape work as expected.
/// </summary>
public sealed class SuggestBox : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SuggestBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((SuggestBox)d).OnTextChanged((string?)e.NewValue)));

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<string>), typeof(SuggestBox),
        new PropertyMetadata(null));

    private readonly TextBox _box = new();
    private readonly Button _button = new()
    {
        Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Padding = new Thickness(8, 4, 8, 4),
        MinWidth = 0, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Show the list", Focusable = false,
    };
    private readonly ListBox _list = new() { MaxHeight = 320, MinWidth = 200, BorderThickness = new Thickness(0) };
    private readonly Popup _popup;
    private bool _updating;

    public SuggestBox()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(_button, 1);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        // The theme's colours: the system's window brush is white even in dark mode, where the items' text is white too.
        var border = new Border { Child = _list, BorderThickness = new Thickness(1), Padding = new Thickness(2) };
        border.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        _popup = new Popup { Child = border, PlacementTarget = _box, Placement = PlacementMode.Bottom, StaysOpen = false };
        Children.Add(_box);
        Children.Add(_button);
        Children.Add(_popup);
        _box.TextChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            Text = _box.Text;
            _updating = false;
            Suggest(all: false);
        };
        _button.Click += (_, _) =>
        {
            if (_popup.IsOpen) { _popup.IsOpen = false; return; }
            _box.Focus();
            Suggest(all: true);
        };
        _box.PreviewKeyDown += OnBoxKeyDown;
        _box.LostKeyboardFocus += (_, e) => { if (e.NewFocus is not DependencyObject d || !IsInList(d)) _popup.IsOpen = false; };
        _list.PreviewMouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListBoxItem) Accept(); };
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

    private bool IsInList(DependencyObject d)
    {
        for (var e = d; e != null; e = e is Visual ? VisualTreeHelper.GetParent(e) : LogicalTreeHelper.GetParent(e))
            if (e == _list) return true;
        return false;
    }

    private void OnTextChanged(string? value)
    {
        if (_updating || _box.Text == value) return;
        _updating = true;
        _box.Text = value ?? "";
        _updating = false;
    }

    /// <summary>Opens the list with the names matching the text; <paramref name="all"/> lists everything if the text is empty or a whole name.</summary>
    private void Suggest(bool all)
    {
        string text = _box.Text.Trim();
        if (Items is not { } items || (!all && (text.Length == 0 || !_box.IsKeyboardFocusWithin)))
        {
            _popup.IsOpen = false;
            return;
        }
        bool exact = items.Contains(text, StringComparer.OrdinalIgnoreCase);
        List<string> matches = all && (text.Length == 0 || exact) ? [.. items]
            : [.. items.Where(i => i.StartsWith(text, StringComparison.OrdinalIgnoreCase)),
               .. items.Where(i => i.Contains(text, StringComparison.OrdinalIgnoreCase) && !i.StartsWith(text, StringComparison.OrdinalIgnoreCase))];
        // Nothing to suggest while typing once the text is exactly a name.
        if (matches.Count == 0 || (!all && matches.Count == 1 && exact))
        {
            _popup.IsOpen = false;
            return;
        }
        _list.ItemsSource = matches;
        int current = matches.FindIndex(m => m.Equals(text, StringComparison.OrdinalIgnoreCase));
        _list.SelectedIndex = current;
        if (current >= 0) _list.ScrollIntoView(matches[current]);
        _popup.MinWidth = ActualWidth;
        _popup.IsOpen = true;
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && !_popup.IsOpen)
        {
            Suggest(all: true);
            e.Handled = true;
            return;
        }
        if (!_popup.IsOpen) return;
        if (e.Key == Key.Down)
        {
            int index = Math.Max(0, _list.SelectedIndex);
            _list.SelectedIndex = index;
            _list.ScrollIntoView(_list.Items[index]);
            _list.UpdateLayout();
            (_list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _popup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _list.Items.Count > 0)
        {
            if (_list.SelectedIndex < 0) _list.SelectedIndex = 0;
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
