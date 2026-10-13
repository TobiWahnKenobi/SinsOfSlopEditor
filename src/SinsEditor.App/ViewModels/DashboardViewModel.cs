using System.Windows;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;
using SinsEditor.Core.Validation;

namespace SinsEditor.App.ViewModels;

public sealed record MetricCard(string Label, string Value, NavTarget Target);

public sealed record RaceRow(string DisplayName, string Name, int CapitalShips, int Titans, int Frigates, int PlanetModules, int Research, EntityTarget Target);

public sealed record ProblemGroup(string Code, string Title, ProblemSeverity Severity, int Count, string Example, ProblemsTarget Target, bool CanFixAll);

public sealed record LayerRow(string Name, string Kind, int Files, int ActiveEntities, int Overrides);

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly ModWorkspace _workspace;
    private readonly Action<NavTarget> _navigate;
    private readonly Func<Task> _reload;

    public DashboardViewModel(ModWorkspace workspace, Action<NavTarget> navigate, Func<Task> reload)
    {
        _workspace = workspace;
        _navigate = navigate;
        _reload = reload;
        var index = workspace.Index;

        int Count(EntityCategory c) => index.EntitiesIn(c).Count();
        Metrics =
        [
            new("Races", Count(EntityCategories.Races).ToString("N0"), new CategoryTarget(EntityCategories.Races.Id)),
            new("Ships", (Count(EntityCategories.CapitalShips) + Count(EntityCategories.Titans) + Count(EntityCategories.Frigates)).ToString("N0"),
                new CategoryTarget(EntityCategories.Frigates.Id)),
            new("Abilities", Count(EntityCategories.Abilities).ToString("N0"), new CategoryTarget(EntityCategories.Abilities.Id)),
            new("Research", Count(EntityCategories.Research).ToString("N0"), new CategoryTarget(EntityCategories.Research.Id)),
            new("Problems", workspace.ProblemCount.ToString("N0"), new ProblemsTarget()),
        ];

        Races = index.Races
            .Select(race =>
            {
                var items = index.ItemsOf(race).ToList();
                int In(string section) => items.Count(i => i.Section == section);
                return new RaceRow(index.DisplayName(race) ?? race.Name, race.Name,
                    In("capitalShipInfo"), In("titanInfo"), In("frigateInfo"), In("planetModuleInfo"), In("researchInfo"),
                    new EntityTarget(race.Name));
            })
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ProblemGroups = workspace.Problems
            .GroupBy(p => p.Code)
            .Select(g => new ProblemGroup(g.Key, ProblemCodes.Title(g.Key), g.Max(p => p.Severity),
                g.Key == ProblemCodes.ConventionMismatch ? workspace.ConventionMismatches.Count : g.Count(),
                g.First().Message, new ProblemsTarget(g.Key), g.Key is ProblemCodes.ConventionMismatch or ProblemCodes.DuplicateString))
            .OrderByDescending(g => g.Severity)
            .ThenByDescending(g => g.Count)
            .ToList();

        Layers = workspace.Stack.Layers
            .Reverse()
            .Select(layer => new LayerRow(
                layer.Name,
                layer.Kind switch { LayerKind.EditedMod => "Edited", LayerKind.BaseMod => "Base mod", _ => "Base game" },
                layer.Files.Count,
                index.ActiveEntities.Count(e => e.File.Layer == layer),
                index.ActiveEntities.Count(e => e.File.Layer == layer && e.File.IsOverride)))
            .ToList();
    }

    public override string Title => "Dashboard";

    public string ModName => _workspace.ModName;
    public string LanguageText => $"Language: {_workspace.Index.Language}";
    public IReadOnlyList<MetricCard> Metrics { get; }
    public IReadOnlyList<RaceRow> Races { get; }
    public IReadOnlyList<ProblemGroup> ProblemGroups { get; }
    public IReadOnlyList<LayerRow> Layers { get; }
    public bool HasNoProblems => ProblemGroups.Count == 0;

    [RelayCommand]
    private void Open(NavTarget target) => _navigate(target);

    [RelayCommand]
    private async Task FixAllAsync(ProblemGroup? group)
    {
        switch (group?.Code)
        {
            case ProblemCodes.ConventionMismatch:
                await FixConventionsAsync();
                break;
            case ProblemCodes.DuplicateString:
                await FixDuplicateStringsAsync();
                break;
        }
    }

    private async Task FixDuplicateStringsAsync()
    {
        int copies = StringFixes.CountIdenticalCopies(_workspace.Index.Strings);
        var answer = MessageBox.Show(
            $"Remove {copies} string entries that repeat an ID with the same text?\n\n" +
            "The first entry of each is kept, and NumStrings is updated. Entries that share an ID but have different text " +
            "(like the game's taunt variants) are kept.",
            "Remove duplicate strings", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        int removed = await Task.Run(() => StringFixes.RemoveIdenticalCopies(_workspace));
        MessageBox.Show($"Removed {removed} entries.", "Remove duplicate strings");
        await _reload();
    }

    private async Task FixConventionsAsync()
    {
        var mismatches = _workspace.ConventionMismatches;
        var answer = MessageBox.Show(
            $"Convert {mismatches.Count} files in {_workspace.ModName} to the game's encoding and CRLF line endings?\n\n" +
            "The files are rewritten in place. Their content doesn't change.",
            "Fix encoding and line endings", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        int count = await Task.Run(() => ConventionCheck.Fix(mismatches));
        MessageBox.Show($"Converted {count} files.", "Fix encoding and line endings");
        await _reload();
    }
}
