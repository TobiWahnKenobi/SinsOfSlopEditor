using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Validation;

namespace SinsEditor.App.ViewModels;

public sealed record ProblemRow(Problem Problem, string Title, EntityTarget? Target)
{
    public string Severity => Problem.Severity.ToString();
    public string Message => Problem.Message;
    public string Location => Problem.RelativePath is null ? "" :
        Problem.LineIndex is { } line ? $"{Problem.RelativePath}:{line + 1}" : Problem.RelativePath;
}

public sealed record CodeFilter(string? Code, string Title);

public sealed partial class ProblemsViewModel : PageViewModel
{
    private readonly Action<NavTarget> _navigate;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private CodeFilter _selectedCode;

    public ProblemsViewModel(ModWorkspace workspace, string? code, Action<NavTarget> navigate)
    {
        _navigate = navigate;
        var problems = workspace.Problems.Where(p => p.Code != ProblemCodes.ConventionMismatch)
            .Concat(workspace.ConventionMismatches.Select(m => new Problem(ProblemSeverity.Warning, ProblemCodes.ConventionMismatch,
                m.Reason, m.RelativePath)));
        Items = problems
            .Select(p => new ProblemRow(p, ProblemCodes.Title(p.Code), EntityFor(workspace, p)))
            .OrderByDescending(r => r.Problem.Severity)
            .ThenBy(r => r.Title)
            .ThenBy(r => r.Location, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Codes = [new CodeFilter(null, "All problems"), .. Items.Select(i => i.Problem.Code).Distinct().Select(c => new CodeFilter(c, ProblemCodes.Title(c)))];
        _selectedCode = Codes.FirstOrDefault(c => c.Code == code) ?? Codes[0];
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is ProblemRow r && Matches(r);
    }

    public override string Title => SelectedCode.Code is null ? "Problems" : SelectedCode.Title;
    public override string? Breadcrumb => SelectedCode.Code is null ? null : "Problems";
    public IReadOnlyList<ProblemRow> Items { get; }
    public IReadOnlyList<CodeFilter> Codes { get; }
    public ICollectionView View { get; }
    public string CountText => $"{View.Cast<object>().Count():N0} of {Items.Count:N0}";

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSelectedCodeChanged(CodeFilter value) => Refresh();

    private void Refresh()
    {
        View.Refresh();
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand]
    private void Open(ProblemRow? row)
    {
        if (row?.Target != null) _navigate(row.Target);
    }

    private bool Matches(ProblemRow row) =>
        (SelectedCode.Code is null || row.Problem.Code == SelectedCode.Code)
        && (SearchText.Length == 0
            || row.Message.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || row.Location.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    private static EntityTarget? EntityFor(ModWorkspace workspace, Problem problem)
    {
        if (problem.RelativePath is not { } path || !path.EndsWith(".entity", StringComparison.OrdinalIgnoreCase)) return null;
        string name = Path.GetFileNameWithoutExtension(path);
        return workspace.Index.Entities.ContainsKey(name) ? new EntityTarget(name, problem.LineIndex) : null;
    }
}
