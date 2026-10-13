using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Logs;

namespace SinsEditor.App.ViewModels;

public sealed record LogChoice(GameLogInfo Info, bool HasMod)
{
    public string Text => $"{Info.Written:g}  ·  {Info.FileName}  ·  {Size}" +
                          (Info.EnabledMods.Count > 0 ? $"  ·  {string.Join(", ", Info.EnabledMods)}" : "  ·  no mods");
    private string Size => Info.Size >= 1 << 20 ? $"{Info.Size / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, Info.Size / 1024)} KB";
}

public sealed record LogRow(LogGroup Group, LogLink Link, bool ConcernsMod, NavTarget? Target)
{
    public string Severity => Group.Severity.ToString();
    public string Kind => GameLogViewModel.KindTitle(Group.Kind);
    public string Message => Group.Pattern;
    public string? Detail => Group.Detail;
    public int Count => Group.Count;
    public string Location =>
        Link.RelativePath is { } path ? (Link.LineIndex is { } line ? $"{path}:{line + 1}" : path) + (Link.Layer is { IsEditable: false } l ? $" ({l.Name})" : "")
        : Link.Entity ?? Link.Brush ?? "";
}

public sealed record LogKindFilter(LogKind? Kind, string Title);

/// <summary>
/// The game's debug log: pick a log (the newest one that had this mod enabled comes first), read it in the background,
/// and list its messages grouped, with links to the files, lines, entities and brushes they name.
/// </summary>
public sealed partial class GameLogViewModel : PageViewModel
{
    // Reading a big log takes a couple of seconds; keep results while the editor runs.
    private static readonly Dictionary<(string Path, long Size, DateTime Written), GameLogResult> Cache = [];

    private readonly ModWorkspace _workspace;
    private readonly Action<NavTarget> _navigate;
    private CancellationTokenSource? _reading;

    [ObservableProperty]
    private LogChoice? _selectedLog;

    [ObservableProperty]
    private bool _isReading;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _onlyMod = true;

    [ObservableProperty]
    private bool _showEngine;

    [ObservableProperty]
    private LogKindFilter _selectedKind;

    [ObservableProperty]
    private IReadOnlyList<LogRow> _items = [];

    [ObservableProperty]
    private ICollectionView? _view;

    public GameLogViewModel(ModWorkspace workspace, string? logPath, Action<NavTarget> navigate)
    {
        _workspace = workspace;
        _navigate = navigate;
        Folder = GameLog.FindFolder(workspace.Stack.EditedMod?.RootPath);
        var logs = Folder is null ? [] : GameLog.List(Folder);
        string mod = workspace.ModName;
        Logs = new(logs.Select(l => new LogChoice(l, l.EnabledMods.Contains(mod, StringComparer.OrdinalIgnoreCase))));
        Kinds = [new LogKindFilter(null, "All kinds"), .. Enum.GetValues<LogKind>().Select(k => new LogKindFilter(k, KindTitle(k)))];
        _selectedKind = Kinds[0];

        if (logPath != null && File.Exists(logPath) && !Logs.Any(l => l.Info.Path.Equals(logPath, StringComparison.OrdinalIgnoreCase)))
            Logs.Insert(0, new LogChoice(GameLog.Describe(logPath), false));
        var pick = logPath != null ? Logs.FirstOrDefault(l => l.Info.Path.Equals(logPath, StringComparison.OrdinalIgnoreCase)) : null;
        pick ??= GameLog.Pick(logs, mod) is { } best ? Logs.First(l => l.Info == best) : null;
        if (pick is null) Status = Folder is null ? "The game's Debug folder wasn't found. Use Browse to open a log." : "The Debug folder has no logs.";
        SelectedLog = pick;
    }

    public override string Title => "Game log";
    public string? Folder { get; }
    public System.Collections.ObjectModel.ObservableCollection<LogChoice> Logs { get; }
    public IReadOnlyList<LogKindFilter> Kinds { get; }
    public string CountText => View is null ? "" : $"{View.Cast<object>().Count():N0} of {Items.Count:N0} groups";
    public string? ModWarning => SelectedLog is { HasMod: false } log
        ? $"{_workspace.ModName} wasn't enabled when this log was written{(log.Info.EnabledMods.Count > 0 ? $" (mods: {string.Join(", ", log.Info.EnabledMods)})" : "")}."
        : null;

    public static string KindTitle(LogKind kind) => kind switch
    {
        LogKind.ParseError => "Can't read line",
        LogKind.MissingFile => "Missing file",
        LogKind.MissingBrush => "Missing brush",
        LogKind.MissingEntity => "Missing entity",
        LogKind.TextureError => "Texture error",
        LogKind.DataError => "Data error",
        LogKind.DataWarning => "Data warning",
        LogKind.EngineAssert => "Engine check",
        LogKind.Shader => "Shader",
        _ => "Other",
    };

    partial void OnSelectedLogChanged(LogChoice? value)
    {
        OnPropertyChanged(nameof(ModWarning));
        if (value != null) _ = ReadAsync(value.Info);
    }

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnOnlyModChanged(bool value) => Refresh();
    partial void OnShowEngineChanged(bool value) => Refresh();
    partial void OnSelectedKindChanged(LogKindFilter value) => Refresh();

    private async Task ReadAsync(GameLogInfo info)
    {
        _reading?.Cancel();
        var cts = _reading = new CancellationTokenSource();
        var key = (info.Path, info.Size, info.Written);
        try
        {
            IsReading = true;
            Progress = 0;
            Status = $"Reading {info.FileName}…";
            if (!Cache.TryGetValue(key, out var result))
            {
                var progress = new Progress<double>(p => Progress = p);
                result = await Task.Run(() => GameLog.Read(info, progress, cts.Token), cts.Token);
                Cache[key] = result;
            }
            if (cts.IsCancellationRequested) return;
            Items = result.Groups.Select(g =>
            {
                var link = LogLinks.Resolve(g, _workspace);
                return new LogRow(g, link, LogLinks.ConcernsMod(g, link, _workspace), TargetFor(link));
            }).ToList();
            var view = CollectionViewSource.GetDefaultView(Items);
            view.Filter = o => o is LogRow r && Matches(r);
            View = view;
            int errors = Items.Where(r => r.ConcernsMod && r.Group.Severity == LogSeverity.Error).Sum(r => r.Count);
            Status = $"{result.Lines:N0} lines, {Items.Count:N0} kinds of message; {errors:N0} errors concern {_workspace.ModName}.";
            OnPropertyChanged(nameof(CountText));
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException e)
        {
            Status = $"Couldn't read {info.FileName}: {e.Message}";
        }
        finally
        {
            if (_reading == cts) IsReading = false;
        }
    }

    private NavTarget? TargetFor(LogLink link)
    {
        if (link.Entity is { } entity) return new EntityTarget(entity, link.LineIndex);
        if (link.Brush is { } brush) return new BrushTarget(brush);
        return null;
    }

    private bool Matches(LogRow row) =>
        (!OnlyMod || row.ConcernsMod)
        && (ShowEngine || SelectedKind.Kind is LogKind.EngineAssert or LogKind.Shader || row.Group.Kind is not (LogKind.EngineAssert or LogKind.Shader))
        && (SelectedKind.Kind is null || row.Group.Kind == SelectedKind.Kind)
        && (SearchText.Length == 0
            || row.Message.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || row.Location.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    private void Refresh()
    {
        View?.Refresh();
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand]
    private void Open(LogRow? row)
    {
        if (row?.Target != null) _navigate(row.Target);
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFileDialog { Filter = "Game logs (log-*.txt)|log-*.txt|Text files (*.txt)|*.txt", InitialDirectory = Folder ?? "" };
        if (dialog.ShowDialog() != true) return;
        var choice = Logs.FirstOrDefault(l => l.Info.Path.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            var info = GameLog.Describe(dialog.FileName);
            choice = new LogChoice(info, info.EnabledMods.Contains(_workspace.ModName, StringComparer.OrdinalIgnoreCase));
            Logs.Insert(0, choice);
        }
        SelectedLog = choice;
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (SelectedLog is { } log) Process.Start(new ProcessStartInfo(log.Info.Path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Reload()
    {
        if (SelectedLog is not { } log) return;
        Cache.Remove((log.Info.Path, log.Info.Size, log.Info.Written));
        var fresh = new LogChoice(GameLog.Describe(log.Info.Path), log.HasMod);
        int i = Logs.IndexOf(log);
        Logs[i] = fresh;
        SelectedLog = fresh;
    }
}
