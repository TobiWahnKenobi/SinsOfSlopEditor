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

public sealed record ProblemGroup(string Code, string Title, ProblemSeverity Severity, int Count, string Example, ProblemsTarget Target, bool CanFixAll)
{
    public string FixText => Code switch
    {
        ProblemCodes.MissingString => "Import from base game",
        ProblemCodes.WrongValueKind => "Fix obvious ones",
        _ => "Fix all",
    };
}

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
                g.First().Message, new ProblemsTarget(g.Key),
                g.Key is ProblemCodes.ConventionMismatch or ProblemCodes.DuplicateString or ProblemCodes.MissingString or ProblemCodes.WrongValueKind))
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
            case ProblemCodes.MissingString:
                await ImportMissingStringsAsync();
                break;
            case ProblemCodes.WrongValueKind:
                await FixValuesAsync();
                break;
        }
    }

    private async Task FixValuesAsync()
    {
        var fixable = ValueFixes.Fixable(_workspace);
        int total = _workspace.Problems.Count(p => p.Code == ProblemCodes.WrongValueKind);
        if (fixable.Count == 0)
        {
            MessageBox.Show("None of these values has an obvious fix. Open them from the Problems page.", "Fix values",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int entities = fixable.Select(f => f.Entity).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var answer = MessageBox.Show(
            $"Fix {fixable.Count:N0} of {total:N0} values in {entities:N0} entities?\n\n" +
            "Quoted TRUE/FALSE lose their quotes, and decimal commas (0,5) become points (0.5). " +
            "Each entity is saved in its current format; entities with unsaved edits are skipped.",
            "Fix values", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var (done, skipped) = await ValueFixes.FixAllAsync(_workspace);
        MessageBox.Show($"Fixed {done:N0} values." + (skipped > 0 ? $" {skipped} entities with unsaved edits were skipped." : ""), "Fix values");
        await _reload();
    }

    private async Task ImportMissingStringsAsync()
    {
        var importable = await Task.Run(() => StringFixes.FindImportable(_workspace));
        int missing = _workspace.Problems.Where(p => p.Code == ProblemCodes.MissingString).Select(p => p.Message).Distinct().Count();
        if (importable.Count == 0)
        {
            MessageBox.Show("None of the missing strings exist in the base game or the base mods in this language.",
                "Import missing strings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var sources = importable.GroupBy(i => i.LayerName).Select(g => $"{g.Count():N0} from {g.Key}");
        var answer = MessageBox.Show(
            $"{importable.Count:N0} of the missing strings exist in a lower layer ({string.Join(", ", sources)}).\n\n" +
            $"Add them with their text to {_workspace.ModName}'s String/{_workspace.Index.Language}.str? " +
            "NumStrings is updated; nothing else in the file changes.",
            "Import missing strings", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        int added = await Task.Run(() => StringFixes.ImportStrings(_workspace, importable));
        MessageBox.Show($"Imported {importable.Count:N0} strings ({added:N0} entries including variants).", "Import missing strings");
        await _reload();
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
