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
}
