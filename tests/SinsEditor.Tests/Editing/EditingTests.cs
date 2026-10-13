using System.Text;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;

namespace SinsEditor.Tests.Editing;

public class EditingTests
{
    private const string Player =
        "TXT\r\n" +
        "entityType \"Player\"\r\n" +
        "entities\r\n" +
        "\tfrigateInfo\r\n" +
        "\t\tPage:0\r\n" +
        "\t\t\tcount 2\r\n" +
        "\t\t\tentityDefName \"A\"\r\n" +
        "\t\t\tentityDefName \"B\"\r\n" +
        "\t\tPage:1\r\n" +
        "\t\t\tcount 0\r\n" +
        "\tresearchInfo\r\n" +
        "\t\tcount 1\r\n" +
        "\t\tentityDefName \"R\"\r\n" +
        "NumWeapons 1\r\n" +
        "Weapon\r\n" +
        "\tWeaponType \"Beam\"\r\n" +
        "\tRange 100\r\n" +
        "m_weaponIndexForRange 0\r\n" +
        "MaxNumResearchLevels 1\r\n" +
        "priority 1.000000\r\n";

    private static EntityEditSession Session(string text = Player)
    {
        var doc = SinsDocument.Parse(Encoding.UTF8.GetBytes(text));
        var file = new ResolvedFile("GameInfo/X.entity", new Layer("Mod", ".", LayerKind.EditedMod), "X.entity", []);
        return new EntityEditSession(new EntityRecord("X", "Player", file, doc, true));
    }

    private static int Line(EntityEditSession s, string text) => s.Document.Lines.FindIndex(l => l.Text.Trim() == text);

    [Fact]
    public void SetValue_undo_redo_and_dirty_flag()
    {
        var s = Session();
        int line = Line(s, "entityDefName \"A\"");

        s.SetValue(line, "\"C\"");
        Assert.True(s.IsDirty);
        Assert.Equal("\t\t\tentityDefName \"C\"", s.Document.Lines[line].Text);

        s.Undo();
        Assert.False(s.IsDirty);
        Assert.Equal(Player, s.Text);

        s.Redo();
        Assert.Equal("\"C\"", s.Document.Lines[line].Value);
    }

    [Fact]
    public void Saving_makes_the_current_state_clean_and_undo_dirty_again()
    {
        var s = Session();
        s.SetValue(1, "\"Frigate\"");
        s.MarkSaved(s.Original);

        Assert.False(s.IsDirty);
        s.Undo();
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void Finds_list_from_item_counter_and_line_inside_item()
    {
        var s = Session();
        var fromItem = CountedList.Find(s.Document, Line(s, "entityDefName \"B\""))!;
        var fromCounter = CountedList.Find(s.Document, Line(s, "count 2"))!;
        var fromWeaponChild = CountedList.Find(s.Document, Line(s, "Range 100"))!;

        Assert.Equal("entityDefName", fromItem.ItemKey);
        Assert.Equal(2, fromItem.Items.Count);
        Assert.Equal(fromItem.CounterLine, fromCounter.CounterLine);
        Assert.Equal("Weapon", fromWeaponChild.ItemKey);
        Assert.Equal(new ListItem(Line(s, "Weapon"), Line(s, "m_weaponIndexForRange 0")), fromWeaponChild.Items[0]);
    }

    [Fact]
    public void Plain_integer_properties_are_not_counters()
    {
        var s = Session();

        Assert.Null(CountedList.Find(s.Document, Line(s, "priority 1.000000")));
        Assert.Null(CountedList.Find(s.Document, Line(s, "m_weaponIndexForRange 0")));
    }

    [Fact]
    public void Empty_list_is_found_without_item_key()
    {
        var s = Session();

        var list = CountedList.Find(s.Document, Line(s, "count 0"))!;

        Assert.Empty(list.Items);
        Assert.Null(list.ItemKey);
    }

    [Fact]
    public void Duplicate_and_remove_keep_the_counter_in_sync()
    {
        var s = Session();
        var list = CountedList.Find(s.Document, Line(s, "entityDefName \"A\""))!;

        ListEdits.DuplicateItem(s, list, 0);
        Assert.Equal("3", s.Document.Lines[list.CounterLine].Value);
        Assert.Equal(["\"A\"", "\"A\"", "\"B\""], s.Document.Lines.Skip(list.CounterLine + 1).Take(3).Select(l => l.Value));

        list = CountedList.Find(s.Document, list.CounterLine)!;
        ListEdits.RemoveItem(s, list, 2);
        Assert.Equal("2", s.Document.Lines[list.CounterLine].Value);

        s.Undo();
        s.Undo();
        Assert.Equal(Player, s.Text);
    }

    [Fact]
    public void Duplicating_a_block_item_copies_its_children()
    {
        var s = Session();
        var list = CountedList.Find(s.Document, Line(s, "Weapon"))!;

        ListEdits.DuplicateItem(s, list, 0);

        Assert.Equal("2", s.Document.Lines[list.CounterLine].Value);
        Assert.Equal(2, s.Document.Lines.Count(l => l.Text == "Weapon"));
        Assert.Equal(2, s.Document.Lines.Count(l => l.Text == "\tRange 100"));
    }

    [Fact]
    public void Adding_to_an_empty_list_needs_a_key()
    {
        var s = Session();
        var list = CountedList.Find(s.Document, Line(s, "count 0"))!;

        Assert.False(ListEdits.AddItem(s, list));
        Assert.True(ListEdits.AddItem(s, list, "entityDefName"));

        Assert.Equal("1", s.Document.Lines[list.CounterLine].Value);
        Assert.Equal("\t\t\tentityDefName \"\"", s.Document.Lines[list.CounterLine + 1].Text);
        Assert.Equal("\r\n", s.Document.Lines[list.CounterLine + 1].Ending);
    }

    [Fact]
    public void ReplaceText_is_one_undo_step_and_keeps_line_endings()
    {
        var s = Session("TXT\r\na 1\r\nb 2\r\n");

        s.ReplaceText("TXT\na 1\nb 3\nc 4");

        Assert.Equal("TXT\r\na 1\r\nb 3\r\nc 4\r\n", s.Text);
        s.Undo();
        Assert.Equal("TXT\r\na 1\r\nb 2\r\n", s.Text);
    }

    [Fact]
    public void ReplaceText_with_identical_text_does_nothing()
    {
        var s = Session("TXT\r\na 1\r\n");

        s.ReplaceText("TXT\na 1\n");

        Assert.False(s.CanUndo);
    }
}
