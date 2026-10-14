using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using SinsEditor.App.Views;

namespace SinsEditor.Tests.App;

public sealed class SuggestBoxTests
{
    private sealed class Model : INotifyPropertyChanged
    {
        private string _brush = "";
        public string Brush { get => _brush; set { _brush = value; PropertyChanged?.Invoke(this, new(nameof(Brush))); } }
        public IReadOnlyList<string> Names { get; } = ["Alpha", "TetBeta", "Tetra", "ZetTet"];
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Exception(error.Message, error);
    }

    private static void Pump(int ms = 50)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Typing_suggests_and_choosing_sets_the_bound_text() => RunSta(() =>
    {
        var model = new Model();
        var box = new SuggestBox { DataContext = model };
        box.SetBinding(SuggestBox.ItemsProperty, new Binding(nameof(Model.Names)));
        box.SetBinding(SuggestBox.TextProperty, new Binding(nameof(Model.Brush)) { Delay = 50 });
        var window = new Window { Content = new StackPanel { Children = { box } }, Width = 300, Height = 200, ShowActivated = true };
        window.Show();
        try
        {
            Pump();
            var text = box.Children.OfType<TextBox>().Single();
            var popup = box.Children.OfType<Popup>().Single();
            text.Focus();
            Pump();
            text.Text = "tet";
            Pump(200);
            Assert.True(popup.IsOpen, "suggestions open");
            var list = (ListBox)((Border)popup.Child).Child;
            Assert.Equal(["TetBeta", "Tetra", "ZetTet"], list.Items.Cast<string>());
            Assert.Equal("tet", model.Brush);

            text.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(text)!, 0, System.Windows.Input.Key.Enter) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            Pump(200);
            Assert.False(popup.IsOpen);
            Assert.Equal("TetBeta", text.Text);
            Assert.Equal("TetBeta", model.Brush);

            // The button lists every name, with the current one chosen.
            box.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump();
            Assert.True(popup.IsOpen, "list open");
            Assert.Equal(model.Names, list.Items.Cast<string>());
            Assert.Equal("TetBeta", list.SelectedItem);

            model.Brush = "Alpha";
            Pump();
            Assert.Equal("Alpha", text.Text);
        }
        finally { window.Close(); }
    });
}
