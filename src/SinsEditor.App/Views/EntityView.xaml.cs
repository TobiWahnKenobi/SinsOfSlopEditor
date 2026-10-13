using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using SinsEditor.App.Editing;
using SinsEditor.App.ViewModels;

namespace SinsEditor.App.Views;

public partial class EntityView : UserControl
{
    private static readonly Lazy<IHighlightingDefinition> SinsHighlighting = new(LoadHighlighting);

    private EntityViewModel? _vm;
    /// <summary>Text last loaded into the raw editor, to tell user edits from reloads.</summary>
    private string _loadedText = "";

    public EntityView()
    {
        InitializeComponent();
        RawEditor.SyntaxHighlighting = SinsHighlighting.Value;
        RawEditor.Options.ConvertTabsToSpaces = false;
        RawEditor.Options.ShowTabs = false;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            FlushRawText();
            _vm.TextReloadRequested -= ReloadRawText;
            _vm.FlushRawText = null;
        }
        _vm = e.NewValue as EntityViewModel;
        if (_vm != null)
        {
            _vm.TextReloadRequested += ReloadRawText;
            _vm.FlushRawText = FlushRawText;
            ReloadRawText();
        }
    }

    private bool RawTabSelected => Tabs.SelectedIndex == 2;

    /// <summary>Loads the session's text unless the user is typing in the editor.</summary>
    private void ReloadRawText()
    {
        if (_vm is null || RawEditor.IsKeyboardFocusWithin) return;
        string text = _vm.Session.Text;
        if (text == _loadedText) return;
        _loadedText = text;
        RawEditor.Document.Text = text;
    }

    /// <summary>Applies text typed in the raw editor to the session (one undo step).</summary>
    private void FlushRawText()
    {
        if (_vm is null || RawEditor.Text == _loadedText) return;
        _loadedText = RawEditor.Text;
        _vm.ApplyRawText(RawEditor.Text);
        _loadedText = _vm.Session.Text;
    }

    private void RawEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => FlushRawText();

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        if (RawTabSelected) ReloadRawText();
        else FlushRawText();
    }

    private void MoreItem_Click(object sender, RoutedEventArgs e) => MoreToggle.IsChecked = false;

    // ---- Reference picker ----

    private void Picker_Opened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: Border { Child: DockPanel panel } } && panel.Children[0] is TextBox query)
            Dispatcher.BeginInvoke(() => query.Focus(), DispatcherPriority.Input);
    }

    private void PickerQuery_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PropertyRowViewModel row }) return;
        if (e.Key == Key.Enter && row.PickerResults.Count > 0)
        {
            row.ChooseCommand.Execute(row.PickerResults[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.IsPickerOpen = false;
            e.Handled = true;
        }
    }

    private void PickerList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { DataContext: PropertyRowViewModel row, SelectedItem: PickerItem item }) row.ChooseCommand.Execute(item);
    }

    // ---- Highlighting ----

    /// <summary>Keys (first word), quoted strings, numbers and TRUE/FALSE, in colours readable on light and dark backgrounds.</summary>
    private static IHighlightingDefinition LoadHighlighting()
    {
        const string xshd = """
            <SyntaxDefinition name="Sins" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Key" foreground="#3A78C3" />
              <Color name="String" foreground="#B8622B" />
              <Color name="Number" foreground="#2E9A6B" />
              <Color name="Bool" foreground="#9A4FC0" fontWeight="bold" />
              <RuleSet>
                <Span color="String"><Begin>"</Begin><End>"</End></Span>
                <Rule color="Key">^\s*[^\s"]+</Rule>
                <Keywords color="Bool"><Word>TRUE</Word><Word>FALSE</Word></Keywords>
                <Rule color="Number">-?\b\d+(\.\d+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
