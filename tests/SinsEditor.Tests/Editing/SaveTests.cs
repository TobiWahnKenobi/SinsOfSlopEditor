using SinsEditor.Core;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;

namespace SinsEditor.Tests.Editing;

/// <summary>Saves into a throwaway mod folder on top of the real base game; the test mod is never written to.</summary>
public sealed class SaveTests : IAsyncLifetime
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "SaveMod-" + Guid.NewGuid().ToString("N"));
    private ModWorkspace? _workspace;

    public async Task InitializeAsync()
    {
        if (TestPaths.GameDir is not { } game) return;
        Directory.CreateDirectory(_mod);
        // The game's own manifest, so the mod sees the same entities as the base game.
        File.Copy(Path.Combine(game, "entity.manifest"), Path.Combine(_mod, "Entity.manifest"));
        _workspace = await ModWorkspace.OpenAsync(new WorkspaceOptions(game, _mod, [], "English"),
            cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private ModWorkspace Workspace
    {
        get
        {
            Skip.If(_workspace is null, "Game directory not configured.");
            return _workspace!;
        }
    }

    [SkippableFact]
    public async Task Saving_a_base_game_entity_creates_an_override_in_the_mod()
    {
        var ws = Workspace;
        var entity = ws.Index.Entities["FrigateTechScout"];
        Assert.Equal(LayerKind.BaseGame, entity.File.Layer.Kind);
        var session = ws.SessionFor(entity);
        int armor = session.Document.Lines.FindIndex(l => l.Key == "armorType");

        session.SetValue(armor, "\"Heavy\"");
        await ws.SaveAsync(session, SaveFormat.Text);

        string path = Path.Combine(_mod, "GameInfo", "FrigateTechScout.entity");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(SinsFileFormat.Text, SinsFileFormatDetector.Detect(bytes));
        // Binary in the game, so the text copy follows ConvertData's convention: UTF-8 with BOM and CRLF.
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.DoesNotContain("\n", System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", ""));
        Assert.Contains("armorType \"Heavy\"", System.Text.Encoding.UTF8.GetString(bytes));

        var updated = ws.Index.Entities["FrigateTechScout"];
        Assert.Equal(LayerKind.EditedMod, updated.File.Layer.Kind);
        Assert.True(updated.File.IsOverride);
        Assert.False(session.IsDirty);
        Assert.Same(updated, session.Original);
    }

    [SkippableFact]
    public async Task Saving_as_binary_uses_convertdata()
    {
        var ws = Workspace;
        var session = ws.SessionFor(ws.Index.Entities["FrigateTechScout"]);
        int armor = session.Document.Lines.FindIndex(l => l.Key == "armorType");
        session.SetValue(armor, "\"Medium\"");

        await ws.SaveAsync(session, SaveFormat.Binary);

        string path = Path.Combine(_mod, "GameInfo", "FrigateTechScout.entity");
        Assert.Equal(SinsFileFormat.Binary, SinsFileFormatDetector.Detect(path));
        Assert.Equal(SaveFormat.Binary, ws.DefaultSaveFormat(ws.Index.Entities["FrigateTechScout"]));
        Assert.Contains(ws.Index.Entities["FrigateTechScout"].Document.Lines, l => l.Key == "armorType" && l.Value == "\"Medium\"");
    }

    [SkippableFact]
    public async Task Form_editing_flow_edit_duplicate_pick_and_save()
    {
        var ws = Workspace;
        var vm = new SinsEditor.App.ViewModels.EntityViewModel(ws, ws.Index.Entities["FrigateTechScout"], _ => { });
        Assert.NotNull(vm.OverrideNotice);

        // Typed value: choice row for armorType.
        var armor = vm.Editor.Rows.Single(r => r.Key == "armorType");
        Assert.Equal(SinsEditor.App.Editing.RowKind.Choice, armor.Kind);
        armor.Value = "Heavy";
        Assert.True(vm.IsDirty);

        // Counted list: duplicating a role updates typeCount.
        var role = vm.Editor.Rows.First(r => r.Key == "frigateRoleType");
        int before = int.Parse(vm.Editor.Rows.Single(r => r.Key == "typeCount").Value);
        role.DuplicateCommand.Execute(null);
        Assert.Equal((before + 1).ToString(), vm.Editor.Rows.Single(r => r.Key == "typeCount").Value);

        // Reference picker offers brushes and choosing one sets the value.
        var icon = vm.Editor.Rows.Single(r => r.Key == "hudIcon");
        Assert.Equal(SinsEditor.App.Editing.RowKind.Reference, icon.Kind);
        icon.OpenPickerCommand.Execute(null);
        icon.PickerQuery = "HUDICON_FRIGATE";
        Assert.NotEmpty(icon.PickerResults);
        var chosen = icon.PickerResults[0];
        icon.ChooseCommand.Execute(chosen);
        Assert.Equal(chosen.Value, icon.Value);
        Assert.False(icon.IsPickerOpen);
        Assert.Null(icon.Error);

        // An unknown reference is flagged but kept.
        icon.Value = "NO_SUCH_BRUSH";
        Assert.NotNull(icon.Error);

        // Undo all the way back to clean.
        while (vm.CanUndo) vm.Undo();
        Assert.False(vm.IsDirty);

        armor.Value = "Medium";
        await vm.SaveAsync();
        Assert.False(vm.IsDirty);
        Assert.Null(vm.OverrideNotice);
        Assert.Contains("armorType \"Medium\"", await File.ReadAllTextAsync(Path.Combine(_mod, "GameInfo", "FrigateTechScout.entity")));
    }

    [SkippableFact]
    public async Task Saving_updates_references_and_their_problems()
    {
        var ws = Workspace;
        var entity = ws.Index.Entities["FrigateTechScout"];
        var session = ws.SessionFor(entity);
        int mesh = session.Document.Lines.FindIndex(l => l.Key == "meshName");
        string meshTarget = ws.Index.OutgoingReferences(entity).First(r => r.Key == "meshName").Target;

        session.SetValue(mesh, "\"NoSuchMesh\"");
        await ws.SaveAsync(session, SaveFormat.Text);

        var updated = ws.Index.Entities["FrigateTechScout"];
        Assert.Contains(ws.Index.OutgoingReferences(updated), r => r.Target == "NoSuchMesh" && !r.Resolved);
        Assert.DoesNotContain(ws.Index.IncomingReferences(ReferenceKind.Mesh, meshTarget), r => r.Source.Name == "FrigateTechScout");
        Assert.Contains(ws.Problems, p => p.Message.Contains("NoSuchMesh"));
        Assert.True(ws.HasProblems(updated));

        // The entity page lists the problem in its banner, and clicking it asks to show the meshName line.
        var vm = new SinsEditor.App.ViewModels.EntityViewModel(ws, updated, _ => { });
        var row = Assert.Single(vm.Problems, p => p.Message.Contains("NoSuchMesh"));
        Assert.StartsWith("meshName points to", row.Message);
        Assert.Equal(mesh, row.LineIndex);
        int? shown = null;
        vm.ShowLineRequested += line => shown = line;
        vm.ShowProblemCommand.Execute(row);
        Assert.Equal(mesh, shown);
        Assert.True(vm.Editor.RowForLine(mesh)!.HasProblem);
    }
}
