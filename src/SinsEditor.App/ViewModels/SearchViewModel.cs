using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Index;

namespace SinsEditor.App.ViewModels;

public sealed record SearchResult(string Group, string Title, string Detail, NavTarget Target)
{
    /// <summary>Set for the first result of each group so the list can show the group name above it.</summary>
    public bool IsFirstInGroup { get; init; }
}

/// <summary>
/// Global search (Ctrl+K): entities by name, strings by text or ID (opening an entity that uses the string),
/// and raw text inside entity files.
/// </summary>
public sealed partial class SearchViewModel(ShellViewModel shell) : ObservableObject
{
    private const int MaxPerGroup = 25;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private SearchResult? _selected;

    [ObservableProperty]
    private string _status = "Type to search entities, strings and file text.";

    public ObservableCollection<SearchResult> Results { get; } = [];

    [RelayCommand]
    public void Open()
    {
        if (shell.Workspace is null) return;
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    public void Reset()
    {
        Query = "";
        Results.Clear();
        IsOpen = false;
    }

    [RelayCommand]
    public void Accept(SearchResult? result)
    {
        result ??= Selected;
        if (result is null) return;
        IsOpen = false;
        shell.Navigate(result.Target);
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        int i = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }

    partial void OnQueryChanged(string value) => _ = SearchAsync(value);

    private async Task SearchAsync(string query)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (shell.Workspace is not { } workspace || query.Trim().Length < 2)
        {
            Results.Clear();
            Status = "Type to search entities, strings and file text.";
            return;
        }
        try
        {
            await Task.Delay(150, cts.Token);
            var results = await Task.Run(() => Find(workspace, query.Trim(), cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            Results.Clear();
            foreach (var r in results) Results.Add(r);
            Selected = Results.FirstOrDefault();
            Status = Results.Count == 0 ? "No matches." : "";
        }
        catch (OperationCanceledException)
        {
            // A newer query replaced this one.
        }
    }

    private static List<SearchResult> Find(ModWorkspace workspace, string query, CancellationToken ct)
    {
        var index = workspace.Index;
        var results = new List<SearchResult>();

        var entities = index.ActiveEntities
            .Select(e => (Entity: e, Name: index.DisplayName(e) ?? e.Name))
            .Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || x.Entity.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(x => x.Name.Length)
            .Take(MaxPerGroup)
            .Select(x => new SearchResult("Entities", x.Name, $"{x.Entity.Name} · {EntityCategories.For(x.Entity.EntityType).Name}", new EntityTarget(x.Entity.Name)));
        AddGroup(results, entities);
        ct.ThrowIfCancellationRequested();

        var strings = index.Strings.Entries
            .Where(s => s.Value.Contains(query, StringComparison.CurrentCultureIgnoreCase) || s.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(s => (String: s, User: index.IncomingReferences(ReferenceKind.String, s.Id).FirstOrDefault()))
            .Where(x => x.User != null)
            .Take(MaxPerGroup)
            .Select(x => new SearchResult("Strings", $"“{Truncate(x.String.Value, 80)}”",
                $"{x.String.Id} · used by {index.DisplayName(x.User!.Source) ?? x.User.Source.Name}", new EntityTarget(x.User.Source.Name)));
        AddGroup(results, strings);
        ct.ThrowIfCancellationRequested();

        if (query.Length >= 3)
        {
            var text = new List<SearchResult>();
            foreach (var entity in index.ActiveEntities.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var lines = entity.Document.Lines;
                for (int i = 1; i < lines.Count && text.Count < MaxPerGroup; i++)
                {
                    if (!lines[i].Value.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    text.Add(new SearchResult("Text in files", lines[i].Text.Trim(), $"{entity.File.RelativePath}:{i + 1}", new EntityTarget(entity.Name)));
                    break;
                }
                if (text.Count >= MaxPerGroup) break;
                ct.ThrowIfCancellationRequested();
            }
            AddGroup(results, text);
        }
        return results;
    }

    private static void AddGroup(List<SearchResult> results, IEnumerable<SearchResult> group)
    {
        bool first = true;
        foreach (var r in group)
        {
            results.Add(first ? r with { IsFirstInGroup = true } : r);
            first = false;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
