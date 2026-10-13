using CommunityToolkit.Mvvm.ComponentModel;

namespace SinsEditor.App.Services;

/// <summary>
/// The action the editor is busy with, shown as a progress overlay over the content (see MainWindow). Actions that take
/// more than a moment run through <see cref="RunAsync{T}"/>: the overlay shows their text and progress, or a moving bar
/// until the first progress report, and keeps clicks away until they're done. Nested actions update the text.
/// </summary>
public sealed partial class Busy : ObservableObject
{
    public static Busy Current { get; } = new();

    private int _depth;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private double _progress;

    private Busy() { }

    /// <summary>Runs <paramref name="work"/> with the overlay up. Must be called on the UI thread.</summary>
    public async Task<T> RunAsync<T>(string text, Func<IProgress<double>, Task<T>> work)
    {
        string previous = Text;
        bool wasIndeterminate = IsIndeterminate;
        double previousProgress = Progress;
        Text = text;
        IsIndeterminate = true;
        Progress = 0;
        _depth++;
        IsBusy = true;
        // Progress<T> captures the UI context here, so reports from background threads update the bar safely.
        var progress = new Progress<double>(p =>
        {
            IsIndeterminate = false;
            Progress = Math.Clamp(p, 0, 1);
        });
        try
        {
            // Let the overlay render before the work starts, even if the work begins synchronously.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            return await work(progress);
        }
        finally
        {
            if (--_depth == 0) IsBusy = false;
            else
            {
                Text = previous;
                IsIndeterminate = wasIndeterminate;
                Progress = previousProgress;
            }
        }
    }

    public Task RunAsync(string text, Func<IProgress<double>, Task> work) =>
        RunAsync<bool>(text, async p => { await work(p); return true; });

    /// <summary>Runs synchronous work on a background thread with the overlay up.</summary>
    public Task<T> RunInBackground<T>(string text, Func<IProgress<double>, T> work) =>
        RunAsync(text, p => Task.Run(() => work(p)));
}
