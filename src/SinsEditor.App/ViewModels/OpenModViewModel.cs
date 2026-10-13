using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SinsEditor.Core;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Settings;

namespace SinsEditor.App.ViewModels;

public sealed record ModChoice(string Name, string Path, string Group)
{
    public override string ToString() => Name;
}

public sealed partial class OpenModViewModel : ObservableObject
{
    private readonly EditorSettings _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameDirectoryError))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private string _gameDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private ModChoice? _selectedMod;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(RemoveBaseModCommand))]
    private ModChoice? _selectedBaseMod;

    [ObservableProperty]
    private string? _selectedLanguage;

    public OpenModViewModel(EditorSettings settings)
    {
        _settings = settings;
        _gameDirectory = GameLocator.IsGameDirectory(settings.GameDirectory)
            ? settings.GameDirectory!
            : GameLocator.FindGameDirectory(SteamPath()) ?? settings.GameDirectory ?? "";

        foreach (string recent in settings.RecentMods.Where(Directory.Exists))
            Mods.Add(new ModChoice(Path.GetFileName(recent), recent, "Recent"));
        foreach (string folder in GameLocator.FindModsFolders())
        {
            foreach (string mod in GameLocator.ListMods(folder))
            {
                if (!Mods.Any(m => string.Equals(m.Path, mod, StringComparison.OrdinalIgnoreCase)))
                    Mods.Add(new ModChoice(Path.GetFileName(mod), mod, Path.GetFileName(folder)));
            }
        }
        SelectedMod = Mods.FirstOrDefault();
    }

    public ObservableCollection<ModChoice> Mods { get; } = [];
    public ObservableCollection<ModChoice> BaseMods { get; } = [];
    public ObservableCollection<string> Languages { get; } = [];

    public string? GameDirectoryError => GameLocator.IsGameDirectory(GameDirectory)
        ? null
        : $"{Core.Conversion.ConvertDataTool.ExeName} wasn't found in this folder.";

    /// <summary>Set when the dialog closes with Open.</summary>
    public WorkspaceOptions? Result { get; private set; }

    public event Action<bool>? CloseRequested;

    partial void OnSelectedModChanged(ModChoice? value)
    {
        BaseMods.Clear();
        if (value is null) return;
        string modsFolder = Path.GetDirectoryName(value.Path)!;
        var baseMods = _settings.Mods.TryGetValue(value.Path, out var saved)
            ? saved.BaseMods.Where(Directory.Exists)
            : EnabledMods.SuggestBaseMods(modsFolder, value.Name).Select(n => Path.Combine(modsFolder, n));
        foreach (string path in baseMods) BaseMods.Add(new ModChoice(Path.GetFileName(path), path, ""));
        RefreshLanguages(saved?.Language);
    }

    [RelayCommand]
    private void BrowseGame()
    {
        var dialog = new OpenFolderDialog { Title = "Select the Sins of a Solar Empire: Rebellion install folder" };
        if (dialog.ShowDialog() == true) GameDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseMod()
    {
        var dialog = new OpenFolderDialog { Title = "Select the mod to edit" };
        if (dialog.ShowDialog() != true) return;
        var choice = Mods.FirstOrDefault(m => string.Equals(m.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase))
                     ?? new ModChoice(Path.GetFileName(dialog.FolderName), dialog.FolderName, "Other");
        if (!Mods.Contains(choice)) Mods.Insert(0, choice);
        SelectedMod = choice;
    }

    [RelayCommand]
    private void AddBaseMod()
    {
        var dialog = new OpenFolderDialog { Title = "Select a base mod" };
        if (SelectedMod != null) dialog.InitialDirectory = Path.GetDirectoryName(SelectedMod.Path);
        if (dialog.ShowDialog() != true) return;
        if (string.Equals(dialog.FolderName, SelectedMod?.Path, StringComparison.OrdinalIgnoreCase)) return;
        if (BaseMods.Any(m => string.Equals(m.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase))) return;
        BaseMods.Add(new ModChoice(Path.GetFileName(dialog.FolderName), dialog.FolderName, ""));
        RefreshLanguages(SelectedLanguage);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedBaseMod))]
    private void RemoveBaseMod()
    {
        BaseMods.Remove(SelectedBaseMod!);
        RefreshLanguages(SelectedLanguage);
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private bool HasSelectedBaseMod() => SelectedBaseMod != null;
    private bool CanMoveUp() => SelectedBaseMod != null && BaseMods.IndexOf(SelectedBaseMod) > 0;
    private bool CanMoveDown() => SelectedBaseMod != null && BaseMods.IndexOf(SelectedBaseMod) < BaseMods.Count - 1;

    private void Move(int delta)
    {
        var item = SelectedBaseMod!;
        int i = BaseMods.IndexOf(item);
        BaseMods.Move(i, i + delta);
        SelectedBaseMod = item;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        var mod = SelectedMod!;
        _settings.GameDirectory = GameDirectory;
        _settings.AddRecent(mod.Path);
        var modSettings = _settings.ForMod(mod.Path);
        modSettings.BaseMods = BaseMods.Select(m => m.Path).ToList();
        modSettings.Language = SelectedLanguage;
        _settings.Save();

        Result = new WorkspaceOptions(GameDirectory, mod.Path, modSettings.BaseMods, SelectedLanguage);
        CloseRequested?.Invoke(true);
    }

    private bool CanOpen() => SelectedMod != null && GameDirectoryError is null;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private void RefreshLanguages(string? preferred)
    {
        Languages.Clear();
        if (SelectedMod is not { } mod || !Directory.Exists(GameDirectory)) return;
        var stack = ModWorkspace.CreateStack(new WorkspaceOptions(GameDirectory, mod.Path, BaseMods.Select(m => m.Path).ToList(), null));
        foreach (string language in Core.FileSystem.Languages.Available(stack)) Languages.Add(language);
        SelectedLanguage = Languages.FirstOrDefault(l => string.Equals(l, preferred, StringComparison.OrdinalIgnoreCase))
                           ?? Languages.FirstOrDefault(l => l.Equals("English", StringComparison.OrdinalIgnoreCase))
                           ?? Languages.FirstOrDefault();
    }

    partial void OnGameDirectoryChanged(string value) => RefreshLanguages(SelectedLanguage);

    private static string? SteamPath() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
}
