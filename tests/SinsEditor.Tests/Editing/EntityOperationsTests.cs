using SinsEditor.Core;
using SinsEditor.Core.Editing;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;
using SinsEditor.Core.Validation;

namespace SinsEditor.Tests.Editing;

/// <summary>Structural operations against a throwaway mod on top of the real base game. Each test gets a fresh mod.</summary>
public sealed class EntityOperationsTests : IAsyncLifetime
{
    private const string Scout = "FrigateTechScout";
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "OpsMod-" + Guid.NewGuid().ToString("N"));
    private ModWorkspace? _workspace;

    public async Task InitializeAsync()
    {
        if (TestPaths.GameDir is null) return;
        Directory.CreateDirectory(_mod);
        File.Copy(Path.Combine(TestPaths.GameDir, "entity.manifest"), Path.Combine(_mod, "Entity.manifest"));
        _workspace = await Open();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private Task<ModWorkspace> Open() => ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, [], "English"),
        cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));

    private ModWorkspace Workspace
    {
        get
        {
            Skip.If(_workspace is null, "Game directory not configured.");
            return _workspace!;
        }
    }

    private static int ManifestCount(ModWorkspace ws) =>
        int.Parse(File.ReadLines(ws.Stack.Resolve("entity.manifest")!.AbsolutePath).First(l => l.StartsWith("entityNameCount")).Split(' ')[1]);

    private static IEnumerable<CountedList> RaceLists(EntityRecord race)
    {
        var doc = race.Document;
        for (int i = 1; i < doc.Lines.Count; i++)
        {
            if (doc.Lines[i].Key == "count" && CountedList.Find(doc, i) is { } list && list.CounterLine == i) yield return list;
        }
    }

    [SkippableFact]
    public void Dialog_previews_changes_and_blocks_taken_names()
    {
        var ws = Workspace;
        var dialog = new SinsEditor.App.Editing.EntityOperationViewModel(ws, SinsEditor.App.Editing.EntityOperationKind.Duplicate, ws.Index.Entities[Scout]);

        Assert.Equal(Scout + "_Copy", dialog.NewName);
        Assert.True(dialog.CanApply);
        Assert.Contains(dialog.Changes, c => c.Path == $"GameInfo/{Scout}_Copy.entity");
        Assert.Contains(dialog.Changes, c => c.Path.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dialog.Changes, c => c.Path.StartsWith("GameInfo/Player", StringComparison.OrdinalIgnoreCase));

        dialog.AddToRaces = false;
        Assert.DoesNotContain(dialog.Changes, c => c.Path.StartsWith("GameInfo/Player", StringComparison.OrdinalIgnoreCase));

        dialog.NewName = Scout;
        Assert.NotNull(dialog.NameError);
        Assert.False(dialog.CanApply);
        Assert.Empty(dialog.Changes);
    }

    [SkippableFact]
    public async Task Delete_dialog_offers_restoring_an_override()
    {
        var ws = Workspace;
        var session = ws.SessionFor(ws.Index.Entities[Scout]);
        session.SetValue(session.Document.Lines.FindIndex(l => l.Key == "armorType"), "\"Heavy\"");
        await ws.SaveAsync(session, SaveFormat.Text);

        var dialog = new SinsEditor.App.Editing.EntityOperationViewModel(ws, SinsEditor.App.Editing.EntityOperationKind.Delete, ws.Index.Entities[Scout]);
        Assert.True(dialog.CanRestoreLowerLayer);

        dialog.RestoreLowerLayer = true;

        var change = Assert.Single(dialog.Changes);
        Assert.True(change.IsDelete);
        Assert.False(dialog.CanRemoveReferences);
        Assert.Equal(Scout, dialog.ResultEntity);
    }

    [SkippableFact]
    public void Validates_new_names()
    {
        var ws = Workspace;

        Assert.NotNull(EntityOperations.ValidateNewName(ws, ""));
        Assert.NotNull(EntityOperations.ValidateNewName(ws, "Has Space"));
        Assert.NotNull(EntityOperations.ValidateNewName(ws, Scout));
        Assert.NotNull(EntityOperations.ValidateNewName(ws, Scout.ToLowerInvariant()));
        Assert.NotNull(EntityOperations.ValidateNewName(ws, "New.entity"));
        Assert.Null(EntityOperations.ValidateNewName(ws, "Frigate_My_New_Ship"));
    }

    [SkippableFact]
    public async Task Duplicate_adds_entity_manifest_race_entries_and_strings()
    {
        var ws = Workspace;
        int manifestBefore = ManifestCount(ws);
        var races = ws.Index.RacesOwning(Scout).Select(r => r.Name).ToList();
        Assert.NotEmpty(races);

        var changes = EntityOperations.PlanDuplicate(ws, ws.Index.Entities[Scout], "FrigateMyScout", addToRaces: true, copyStrings: true);
        changes.Apply(ws);
        var after = await Open();

        var copy = after.Index.Entities["FrigateMyScout"];
        Assert.True(copy.InManifest);
        Assert.Equal(manifestBefore + 1, ManifestCount(after));
        Assert.Equal(races, after.Index.RacesOwning("FrigateMyScout").Select(r => r.Name).ToList());
        Assert.Equal("IDS_FRIGATEMYSCOUT_NAME", copy.Document.Lines.First(l => l.Key == "NameStringID").Value.Trim('"'));
        Assert.Equal(after.Index.Strings.Get(ws.Index.Entities[Scout].Document.Lines.First(l => l.Key == "NameStringID").Value.Trim('"')),
            after.Index.DisplayName(copy));
        Assert.DoesNotContain(after.Problems, p => p.Code == ProblemCodes.StringCountMismatch);

        foreach (var race in after.Index.Races) Assert.All(RaceLists(race), l => Assert.True(l.IsConsistent(race.Document)));
    }

    [SkippableFact]
    public async Task Rename_updates_manifest_and_every_reference()
    {
        var ws = Workspace;
        int users = ws.Index.IncomingReferences(ReferenceKind.Entity, Scout).Count;
        int manifestBefore = ManifestCount(ws);
        Assert.True(users > 0);

        EntityOperations.PlanRename(ws, ws.Index.Entities[Scout], "FrigateRenamedScout").Apply(ws);
        var after = await Open();

        Assert.False(after.Index.Exists(ReferenceKind.Entity, Scout));
        Assert.True(after.Index.Exists(ReferenceKind.Entity, "FrigateRenamedScout"));
        Assert.Equal(users, after.Index.IncomingReferences(ReferenceKind.Entity, "FrigateRenamedScout").Count);
        Assert.Empty(after.Index.IncomingReferences(ReferenceKind.Entity, Scout));
        Assert.Equal(manifestBefore, ManifestCount(after));
    }

    [SkippableFact]
    public async Task Delete_with_cleanup_removes_list_entries_and_keeps_counts_right()
    {
        var ws = Workspace;
        var races = ws.Index.RacesOwning(Scout).ToList();
        int manifestBefore = ManifestCount(ws);

        var changes = EntityOperations.PlanDelete(ws, ws.Index.Entities[Scout], DeleteMode.RemoveEntity, removeReferences: true);
        changes.Apply(ws);
        var after = await Open();

        Assert.False(after.Index.Exists(ReferenceKind.Entity, Scout));
        Assert.Equal(manifestBefore - 1, ManifestCount(after));
        Assert.Empty(after.Index.RacesOwning(Scout));
        foreach (var race in races.Select(r => after.Index.Entities[r.Name]))
            Assert.All(RaceLists(race), l => Assert.True(l.IsConsistent(race.Document)));
        Assert.DoesNotContain(after.Problems, p => p.Code == ProblemCodes.MissingReference && p.Message.Contains($"'{Scout}'"));
    }

    [SkippableFact]
    public async Task Delete_without_cleanup_notes_dangling_references()
    {
        var ws = Workspace;

        var changes = EntityOperations.PlanDelete(ws, ws.Index.Entities[Scout], DeleteMode.RemoveEntity, removeReferences: false);

        Assert.Contains(changes.Notes, n => n.Contains("will point to a missing entity"));
        Assert.Single(changes.Changes); // only the manifest: the entity's file is in the base game
        changes.Apply(ws);
        var after = await Open();
        Assert.Contains(after.Problems, p => p.Code == ProblemCodes.MissingReference && p.Message.Contains($"'{Scout}'"));
    }

    [SkippableFact]
    public async Task Deleting_an_override_restores_the_base_game_version()
    {
        var ws = Workspace;
        var session = ws.SessionFor(ws.Index.Entities[Scout]);
        session.SetValue(session.Document.Lines.FindIndex(l => l.Key == "armorType"), "\"Heavy\"");
        await ws.SaveAsync(session, SaveFormat.Text);
        Assert.Equal(LayerKind.EditedMod, ws.Index.Entities[Scout].File.Layer.Kind);

        EntityOperations.PlanDelete(ws, ws.Index.Entities[Scout], DeleteMode.RestoreLowerLayer, removeReferences: false).Apply(ws);
        var after = await Open();

        Assert.Equal(LayerKind.BaseGame, after.Index.Entities[Scout].File.Layer.Kind);
        Assert.True(after.Index.Entities[Scout].InManifest);
    }
}
