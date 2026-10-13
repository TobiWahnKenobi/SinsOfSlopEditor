using SinsEditor.App.ViewModels;
using SinsEditor.Core.Index;

namespace SinsEditor.Tests.App;

public class FacetFilterTests
{
    private static readonly Facet RoleFacet = new("frigateRoleType", "Frigate role type", _ => []);

    private static FacetFilterViewModel Create(out Func<int> changes)
    {
        int count = 0;
        changes = () => count;
        IReadOnlyList<string>[] rows = [["Scout"], ["Siege", "Lrm"], ["Siege"], []];
        return new FacetFilterViewModel(RoleFacet, rows, () => count++);
    }

    [Fact]
    public void Counts_values_including_none()
    {
        var filter = Create(out _);

        Assert.Equal(["Lrm 1", "Scout 1", "Siege 2", "(none) 1"], filter.Values.Select(v => $"{v.Value} {v.Count}"));
    }

    [Fact]
    public void Matches_everything_without_selection()
    {
        var filter = Create(out _);

        Assert.True(filter.Matches(["Scout"]));
        Assert.True(filter.Matches([]));
    }

    [Fact]
    public void Matches_rows_with_any_selected_value()
    {
        var filter = Create(out var changes);

        filter.Values.Single(v => v.Value == "Siege").IsSelected = true;

        Assert.True(filter.Matches(["Lrm", "Siege"]));
        Assert.False(filter.Matches(["Scout"]));
        Assert.False(filter.Matches([]));
        Assert.Equal("Frigate role type: Siege", filter.Summary);
        Assert.Equal(1, changes());
    }

    [Fact]
    public void None_selects_rows_without_value()
    {
        var filter = Create(out _);

        filter.Values.Single(v => v.Value == FacetValueViewModel.None).IsSelected = true;

        Assert.True(filter.Matches([]));
        Assert.False(filter.Matches(["Scout"]));
    }

    [Fact]
    public void Clear_resets_with_a_single_change_notification()
    {
        var filter = Create(out var changes);
        filter.Values[0].IsSelected = true;
        filter.Values[1].IsSelected = true;

        filter.Clear();

        Assert.False(filter.HasSelection);
        Assert.Equal(3, changes());
    }
}
