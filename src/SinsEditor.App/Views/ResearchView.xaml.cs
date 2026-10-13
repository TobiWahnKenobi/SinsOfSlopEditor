using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using SinsEditor.App.Research;

namespace SinsEditor.App.Views;

/// <summary>
/// Mouse handling for the research editor: click selects, dragging an icon (or a tray entry) moves it to the cell
/// under the mouse, Shift+drag onto another icon adds a prerequisite.
/// </summary>
public partial class ResearchView : UserControl
{
    private const string SubjectFormat = "SinsEditor.ResearchSubject";
    private Point? _dragStart;
    private SubjectVisual? _pressed;

    public ResearchView() => InitializeComponent();

    private ResearchViewModel? VM => DataContext as ResearchViewModel;

    private void Subject_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SubjectVisual subject } || VM is null) return;
        VM.SelectedSubject = subject;
        _pressed = subject;
        _dragStart = e.GetPosition(Board);
        Focus();
        e.Handled = true;
    }

    private void Subject_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is null || _dragStart is not { } start) return;
        var delta = e.GetPosition(Board) - start;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        var subject = _pressed;
        _pressed = null;
        _dragStart = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(SubjectFormat, subject.Subject.Name), DragDropEffects.Move | DragDropEffects.Link);
        DropMarker.Visibility = Visibility.Collapsed;
    }

    private void Tray_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Tray.SelectedItem is not SubjectChoice choice) return;
        DragDrop.DoDragDrop(Tray, new DataObject(SubjectFormat, choice.Name), DragDropEffects.Move);
        DropMarker.Visibility = Visibility.Collapsed;
    }

    private void Board_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (VM is null || !e.Data.GetDataPresent(SubjectFormat))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        bool link = e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey);
        var point = e.GetPosition(Board);
        if (link)
        {
            var target = SubjectAt(point);
            e.Effects = target != null ? DragDropEffects.Link : DragDropEffects.None;
            ShowMarker(target?.Bounds);
            return;
        }
        if (VM.CellAt(point) is { } cell && VM.Tree.Layouts.TryGetValue(VM.SelectedField, out var layout))
        {
            var o = cell.Block.CellOrigin(layout, cell.Column, cell.Row);
            ShowMarker(new Rect(o.X, o.Y, layout.ButtonSize.X, layout.ButtonSize.Y));
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            ShowMarker(null);
            e.Effects = DragDropEffects.None;
        }
    }

    private void Board_DragLeave(object sender, DragEventArgs e) => DropMarker.Visibility = Visibility.Collapsed;

    private void Board_Drop(object sender, DragEventArgs e)
    {
        DropMarker.Visibility = Visibility.Collapsed;
        if (VM is null || e.Data.GetData(SubjectFormat) is not string name) return;
        var point = e.GetPosition(Board);
        if (e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey))
        {
            if (SubjectAt(point) is { } dependent) VM.AddLink(name, dependent.Subject.Name);
        }
        else if (VM.CellAt(point) is { } cell)
        {
            VM.MoveSubject(name, cell.Block, cell.Column, cell.Row);
        }
        e.Handled = true;
    }

    /// <summary>A click on empty space clears the selection.</summary>
    private void Board_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (VM is null || e.Handled) return;
        VM.SelectedSubject = null;
        VM.SelectedArrow = null;
        Focus();
    }

    private void Arrow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (VM is null || sender is not Polyline { DataContext: ArrowVisual arrow }) return;
        VM.SelectedSubject = null;
        VM.SelectedArrow = arrow;
        Focus();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (VM is null || e.OriginalSource is TextBox or ComboBox) return;
        if (e.Key == Key.Delete && VM.SelectedArrow != null)
        {
            VM.DeleteSelectedArrow();
            e.Handled = true;
        }
    }

    private SubjectVisual? SubjectAt(Point point) => VM?.Subjects.FirstOrDefault(s => s.Bounds.Contains(point));

    private void ShowMarker(Rect? bounds)
    {
        if (bounds is not { } r)
        {
            DropMarker.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(DropMarker, r.X - 2);
        Canvas.SetTop(DropMarker, r.Y - 2);
        DropMarker.Width = r.Width + 4;
        DropMarker.Height = r.Height + 4;
        DropMarker.Visibility = Visibility.Visible;
    }
}
