using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SinsEditor.App.Navigation;
using SinsEditor.App.ViewModels;
using SinsEditor.App.Views;
using SinsEditor.Core;
using SinsEditor.Core.Settings;

namespace SinsEditor.App;

/// <summary>
/// Command line (all optional):
/// <c>--mod &lt;path&gt; [--base &lt;path&gt;]… [--language &lt;name&gt;] [--game &lt;dir&gt;]</c> opens a mod directly;
/// <c>--target dashboard|category:&lt;id&gt;|entity:&lt;name&gt;|problems[:&lt;code&gt;]</c> picks the first page;
/// <c>--search &lt;text&gt;</c> opens global search with a query;
/// <c>--show-first-problem</c> clicks the first problem link of an entity page;
/// <c>--screenshot &lt;file.png&gt;</c> renders that page to a PNG and exits.
/// </summary>
public partial class App : Application
{
    private EditorSettings _settings = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _settings = EditorSettings.Load();
        var args = new CommandLine(e.Args);

        WorkspaceOptions? options = args.Mod is { } mod
            ? new WorkspaceOptions(args.Game ?? _settings.GameDirectory ?? "", mod, args.BaseMods, args.Language)
            : ShowOpenModDialog(null);
        if (options is null)
        {
            Shutdown();
            return;
        }

        var shell = new ShellViewModel();
        var window = new MainWindow(shell);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        await shell.OpenAsync(options);
        if (args.Target is { } target) shell.Navigate(target);
        if (args.ShowFirstProblem)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var editor = shell.CurrentPage switch { EntityViewModel page => page, RaceViewModel race => race.Detail, _ => null };
            if (editor?.Problems.FirstOrDefault(p => p.HasLine) is { } problem) editor.ShowProblemCommand.Execute(problem);
            await Task.Delay(500);
        }
        if (args.Search is { } query)
        {
            shell.Search.Open();
            shell.Search.Query = query;
            await Task.Delay(800);
        }

        if (args.Screenshot is { } file)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(500);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveScreenshot(window, file);
            Shutdown(shell.LoadError is null ? 0 : 1);
        }
    }

    public WorkspaceOptions? ShowOpenModDialog(Window? owner)
    {
        var viewModel = new OpenModViewModel(_settings);
        var dialog = new OpenModWindow(viewModel) { Owner = owner };
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? viewModel.Result : null;
    }

    private static void SaveScreenshot(Window window, string file)
    {
        var content = (FrameworkElement)window.Content;
        int w = (int)content.ActualWidth, h = (int)content.ActualHeight;
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            // The Fluent window backdrop isn't part of the visual tree; paint a solid background behind the content.
            var background = window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
            ctx.DrawRectangle(background, null, new Rect(0, 0, w, h));
            ctx.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, w, h));
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    private sealed class CommandLine
    {
        public CommandLine(string[] args)
        {
            ShowFirstProblem = args.Contains("--show-first-problem");
            for (int i = 0; i < args.Length - 1; i++)
            {
                string value = args[i + 1];
                switch (args[i])
                {
                    case "--mod": Mod = value; i++; break;
                    case "--base": BaseMods.Add(value); i++; break;
                    case "--language": Language = value; i++; break;
                    case "--game": Game = value; i++; break;
                    case "--screenshot": Screenshot = value; i++; break;
                    case "--target": Target = ParseTarget(value); i++; break;
                    case "--search": Search = value; i++; break;
                }
            }
        }

        public string? Mod { get; }
        public List<string> BaseMods { get; } = [];
        public string? Language { get; }
        public string? Game { get; }
        public string? Screenshot { get; }
        public NavTarget? Target { get; }
        public string? Search { get; }
        public bool ShowFirstProblem { get; }

        private static NavTarget ParseTarget(string value)
        {
            string[] parts = value.Split(':', 2);
            return parts[0] switch
            {
                "category" => new CategoryTarget(parts[1]),
                "entity" => new EntityTarget(parts[1]),
                "problems" => new ProblemsTarget(parts.Length > 1 ? parts[1] : null),
                _ => new DashboardTarget(),
            };
        }
    }
}
