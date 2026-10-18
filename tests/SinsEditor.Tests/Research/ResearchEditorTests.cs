using System.Windows;
using SinsEditor.App.Research;
using SinsEditor.Core;
using SinsEditor.Core.Research;

namespace SinsEditor.Tests.Research;

/// <summary>The research editor's view model on a throwaway mod over the base game (a base-game race, nothing saved).</summary>
public sealed class ResearchEditorTests : IAsyncLifetime
{
    private const string Race = "PlayerTechLoyalist";
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "ResearchMod-" + Guid.NewGuid().ToString("N"));
    private ModWorkspace? _workspace;

    public async Task InitializeAsync()
    {
        if (TestPaths.GameDir is not { } game) return;
        Directory.CreateDirectory(_mod);
        File.Copy(Path.Combine(game, "entity.manifest"), Path.Combine(_mod, "Entity.manifest"));
        _workspace = await ModWorkspace.OpenAsync(new WorkspaceOptions(game, _mod, [], "English"),
            cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private ResearchViewModel Editor()
    {
        Skip.If(_workspace is null, "Game directory not configured.");
        return new ResearchViewModel(_workspace!, _workspace!.Index.Entities[Race], _ => { });
    }

    private static (ResearchBlock Block, int Column, int Row) FreeCell(ResearchViewModel vm)
    {
        var layout = vm.Tree.Layouts[vm.SelectedField];
        foreach (var block in vm.Tree.BlocksOf(vm.SelectedField))
        {
            var (cols, rows) = block.GridSize(layout);
            for (int c = 0; c < cols; c++)
            for (int r = 0; r < rows; r++)
                if (!vm.Tree.Subjects.Any(s => s.Field == block.Field && s.Block == block.Index && s.Column == c && s.Row == r))
                    return (block, c, r);
        }
        throw new InvalidOperationException("No free cell.");
    }

    [SkippableFact]
    public void Shows_the_selected_field_with_blocks_buttons_and_arrows()
    {
        var vm = Editor();

        Assert.NotEmpty(vm.Blocks);
        Assert.NotEmpty(vm.Subjects);
        Assert.NotEmpty(vm.Arrows);
        Assert.All(vm.Subjects, s => Assert.Equal(vm.SelectedField, s.Subject.Field));
        Assert.Contains(vm.FieldTabs, t => t.Field == vm.SelectedField);
    }

    [SkippableFact]
    public void Maps_canvas_points_to_cells()
    {
        var vm = Editor();
        var subject = vm.Subjects[0];

        var cell = vm.CellAt(new Point(subject.Bounds.X + 5, subject.Bounds.Y + 5))!.Value;

        Assert.Equal((subject.Subject.Block, subject.Subject.Column, subject.Subject.Row), (cell.Block.Index, cell.Column, cell.Row));
        Assert.Null(vm.CellAt(new Point(1, 1)));
    }

    [SkippableFact]
    public void Moving_to_a_free_cell_edits_the_subject_and_undo_restores_it()
    {
        var vm = Editor();
        var subject = vm.Subjects[0].Subject;
        var free = FreeCell(vm);

        vm.MoveSubject(subject.Name, free.Block, free.Column, free.Row);

        var moved = vm.Tree.Subject(subject.Name)!;
        Assert.Equal((free.Block.Index, free.Column, free.Row), (moved.Block!.Value, moved.Column!.Value, moved.Row!.Value));
        Assert.True(vm.IsDirty);
        Assert.Equal(subject.Name, vm.SelectedSubject?.Subject.Name);

        vm.Undo();
        var back = vm.Tree.Subject(subject.Name)!;
        Assert.Equal((subject.Block, subject.Column, subject.Row), (back.Block, back.Column, back.Row));
        Assert.False(vm.IsDirty);
    }

    [SkippableFact]
    public void Dropping_on_an_occupied_cell_swaps()
    {
        var vm = Editor();
        var a = vm.Subjects[0].Subject;
        var b = vm.Subjects[1].Subject;
        var block = vm.Tree.BlocksOf(vm.SelectedField).Single(x => x.Index == b.Block);

        vm.MoveSubject(a.Name, block, b.Column!.Value, b.Row!.Value);

        var newA = vm.Tree.Subject(a.Name)!;
        var newB = vm.Tree.Subject(b.Name)!;
        Assert.Equal((b.Block, b.Column, b.Row), (newA.Block, newA.Column, newA.Row));
        Assert.Equal((a.Block, a.Column, a.Row), (newB.Block, newB.Column, newB.Row));
        Assert.NotNull(vm.StatusMessage);

        vm.Undo();
        vm.Undo();
        Assert.Equal((a.Block, a.Column, a.Row), (vm.Tree.Subject(a.Name)!.Block, vm.Tree.Subject(a.Name)!.Column, vm.Tree.Subject(a.Name)!.Row));
    }

    [SkippableFact]
    public void Unassigned_list_offers_other_subjects_with_filter()
    {
        var vm = Editor();
        int ownOnly = vm.Unassigned.Count;

        vm.IncludeOtherRaces = true;
        vm.IncludeBaseGame = true;
        Assert.True(vm.Unassigned.Count > ownOnly);
        Assert.DoesNotContain(vm.Unassigned, r => vm.IsOnTree(r.Name) && !r.IsOnRaceList);

        string name = vm.Unassigned.First(r => !r.IsOnRaceList).Name;
        vm.UnassignedFilter = name;
        Assert.Contains(vm.Unassigned, r => r.Name == name);
        Assert.All(vm.Unassigned, r => Assert.True(r.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || r.Display.Contains(name, StringComparison.CurrentCultureIgnoreCase)));
    }

    [SkippableFact]
    public void Pacts_artifacts_and_unlocks_are_never_offered()
    {
        var vm = Editor();
        vm.IncludeOtherRaces = true;
        vm.IncludeBaseGame = true;

        var fields = vm.Unassigned
            .Select(r => Core.Index.Facets.TopLevelValues(_workspace!.Index.Entities[r.Name], "ResearchField").FirstOrDefault())
            .Distinct().ToList();

        Assert.All(fields, f => Assert.True(ResearchTree.IsTreeField(f), $"{f} offered"));
        Assert.DoesNotContain(vm.FieldTabs, t => t.Field == "Artifact");
    }

    [SkippableFact]
    public void Dropping_an_unassigned_subject_adds_and_places_it()
    {
        var vm = Editor();
        vm.IncludeOtherRaces = true;
        vm.IncludeBaseGame = true;
        var row = vm.Unassigned.First(r => !r.IsOnRaceList);
        int listBefore = ResearchTree.ResearchList(_workspace!.SessionFor(vm.Race).Document).Count;
        var free = FreeCell(vm);

        vm.MoveSubject(row.Name, free.Block, free.Column, free.Row);

        Assert.True(vm.IsOnTree(row.Name));
        var placed = vm.Tree.Subject(row.Name)!;
        Assert.Equal((vm.SelectedField, free.Block.Index, free.Column, free.Row), (placed.Field, placed.Block!.Value, placed.Column!.Value, placed.Row!.Value));
        Assert.Equal(listBefore + 1, ResearchTree.ResearchList(_workspace.SessionFor(vm.Race).Document).Count);
        Assert.DoesNotContain(vm.Unassigned, r => r.Name == row.Name);

        vm.Undo();
        vm.Undo();
        Assert.False(vm.IsOnTree(row.Name));
        Assert.Equal(listBefore, ResearchTree.ResearchList(_workspace.SessionFor(vm.Race).Document).Count);
        Assert.False(vm.IsDirty);
    }

    [SkippableFact]
    public void Dropping_an_unassigned_subject_on_a_taken_cell_is_refused()
    {
        var vm = Editor();
        vm.IncludeOtherRaces = true;
        vm.IncludeBaseGame = true;
        var row = vm.Unassigned.First(r => !r.IsOnRaceList);
        var occupant = vm.Subjects[0].Subject;
        var block = vm.Tree.BlocksOf(vm.SelectedField).Single(b => b.Index == occupant.Block);

        vm.MoveSubject(row.Name, block, occupant.Column!.Value, occupant.Row!.Value);

        Assert.False(vm.IsOnTree(row.Name));
        Assert.False(vm.IsDirty);
        Assert.Contains("taken", vm.StatusMessage);
    }

    [SkippableFact]
    public void Dragging_off_the_tree_removes_it_from_the_race_list()
    {
        var vm = Editor();
        var subject = vm.Subjects[0].Subject;

        vm.RemoveFromTree(subject.Name);

        Assert.False(vm.IsOnTree(subject.Name));
        Assert.DoesNotContain(subject.Name, ResearchTree.ResearchList(_workspace!.SessionFor(vm.Race).Document));
        Assert.True(_workspace.Index.Entities.ContainsKey(subject.Name));
        vm.IncludeOtherRaces = true;
        vm.IncludeBaseGame = true;
        vm.UnassignedFilter = subject.Name;
        Assert.Contains(vm.Unassigned, r => r.Name == subject.Name);

        vm.Undo();
        Assert.True(vm.IsOnTree(subject.Name));
    }

    [SkippableFact]
    public void Linking_and_deleting_an_arrow()
    {
        var vm = Editor();
        var from = vm.Subjects[0].Subject;
        var to = vm.Subjects.Select(s => s.Subject).First(s => s != from && s.Prerequisites.All(p => p.Subject != from.Name));

        vm.AddLink(from.Name, to.Name);

        Assert.Contains(vm.Tree.Subject(to.Name)!.Prerequisites, p => p.Subject == from.Name);
        var arrow = vm.Arrows.Single(a => a.Link.From.Name == from.Name && a.Link.To.Name == to.Name);
        vm.SelectedArrow = arrow;
        vm.DeleteSelectedArrow();
        Assert.DoesNotContain(vm.Tree.Subject(to.Name)!.Prerequisites, p => p.Subject == from.Name);
    }

    [SkippableFact]
    public async Task A_new_subject_can_be_created_in_a_free_cell()
    {
        var vm = Editor();
        var (block, column, row) = FreeCell(vm);
        vm.SelectCell(block, column, row);
        Assert.True(vm.HasCellSelection);
        Assert.NotNull(vm.NewSubjectTemplate);
        string name = vm.NewSubjectName;
        Assert.Null(Core.Editing.EntityOperations.ValidateNewName(_workspace!, name));
        var template = _workspace!.Index.Entities[vm.NewSubjectTemplate!.Name];

        var plan = ResearchCreation.PlanNewSubject(_workspace, _workspace.Index.Entities[Race], template, name, block.Field, block.Index, column, row);
        plan.Apply(_workspace);
        var after = await ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, [], "English"),
            cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));

        Assert.True(after.Index.Entities[name].InManifest);
        var tree = ResearchTree.Build(after, after.Index.Entities[Race]);
        var subject = Assert.Single(tree.Subjects, s => s.Name == name);
        Assert.Equal((block.Field, block.Index, column, row, column), (subject.Field, subject.Block, subject.Column, subject.Row, subject.Tier));
        Assert.Empty(subject.Prerequisites);
        Assert.NotNull(tree.CellOf(subject));
        // Its strings are its own copies, with the template's text.
        var names = after.Index.OutgoingReferences(after.Index.Entities[name]).Where(r => r.Kind == Core.Index.ReferenceKind.String).ToList();
        Assert.NotEmpty(names);
        Assert.All(names, r => Assert.True(r.Resolved));
        Assert.DoesNotContain(names, r => after.Index.OutgoingReferences(template).Any(t => t.Target == r.Target));
    }
}
