using SinsEditor.Core.Documents;
using SinsEditor.Core.Index;
using SinsEditor.Core.Validation;

namespace SinsEditor.Core.Research;

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static bool TryParse(string raw, out PixelRect rect)
    {
        rect = default;
        if (!SinsValue.TryGetVector(raw, out var v) || v.Length != 4) return false;
        rect = new PixelRect((int)v[0], (int)v[1], (int)v[2], (int)v[3]);
        return true;
    }
}

/// <summary>How a research field lays out its buttons (from <c>Window/ResearchScreen.window</c>).</summary>
public sealed record ResearchFieldLayout(string Field, PixelPoint ButtonOffset, PixelPoint ButtonSize, PixelPoint ButtonSpacing, string? ButtonBackdrop)
{
    public int StrideX => ButtonSize.X + ButtonSpacing.X;
    public int StrideY => ButtonSize.Y + ButtonSpacing.Y;
}

/// <summary>A block of a field in a race's research screen (from <c>Player.researchScreenData</c>).</summary>
public sealed record ResearchBlock(string Field, int Index, PixelRect Area, string? Backdrop, string? TierBackdrop, string? TitleStringId, string? Picture)
{
    /// <summary>How many columns and rows of buttons fit into the block.</summary>
    public (int Columns, int Rows) GridSize(ResearchFieldLayout layout) =>
        (Math.Max(0, (Area.Width - layout.ButtonOffset.X + layout.ButtonSpacing.X) / layout.StrideX),
         Math.Max(0, (Area.Height - layout.ButtonOffset.Y + layout.ButtonSpacing.Y) / layout.StrideY));

    /// <summary>Top-left pixel of a cell in the research window's content area.</summary>
    public PixelPoint CellOrigin(ResearchFieldLayout layout, int column, int row) =>
        new(Area.X + layout.ButtonOffset.X + column * layout.StrideX, Area.Y + layout.ButtonOffset.Y + row * layout.StrideY);
}

public sealed record ResearchPrerequisite(string Subject, int Level, int LineIndex);

/// <summary>A research subject as placed on the screen. Line indices refer to the subject's current document.</summary>
public sealed record ResearchSubject(
    EntityRecord Entity,
    SinsDocument Document,
    string? Field,
    int? Block,
    int? Column,
    int? Row,
    int? Tier,
    int MaxLevels,
    string? HudIcon,
    IReadOnlyList<ResearchPrerequisite> Prerequisites)
{
    public string Name => Entity.Name;
    public bool HasPosition => Field != null && Block != null && Column != null && Row != null;
}

/// <summary>The arrow images the game has (<c>arrowBrushes</c>); a prerequisite needs one of these shapes to be drawn.</summary>
public enum ArrowShape
{
    Right1, Right2, Right3, Right4, Right1Down1, Right1BlockDown1, Down1, Up2, Up1Right1, Up1Right2, BlockDown1, BlockUp1, OtherTab,
    /// <summary>No arrow image fits; the game won't show the link properly.</summary>
    Unsupported,
}

public sealed record ResearchLink(ResearchSubject From, ResearchSubject To, int Level, ArrowShape Shape);

public sealed record ResearchIssue(ProblemSeverity Severity, string Message, string? Subject);

/// <summary>A race's research screen: field layouts, blocks, subjects, links and the problems found.</summary>
public sealed class ResearchTree
{
    public static readonly IReadOnlyList<string> Fields = ["Combat", "Defense", "NonCombat", "Diplomacy", "Fleet", "Artifact"];

    /// <summary>Names the game's research window shows for the fields.</summary>
    public static string FieldTitle(string field) => field switch
    {
        "Combat" => "Military",
        "NonCombat" => "Civilian",
        "Fleet" => "Fleet Logistics",
        "Artifact" => "Artifacts",
        _ => field,
    };

    private ResearchTree(EntityRecord race) => Race = race;

    public EntityRecord Race { get; }
    public IReadOnlyDictionary<string, ResearchFieldLayout> Layouts { get; private set; } = new Dictionary<string, ResearchFieldLayout>();
    public IReadOnlyList<ResearchBlock> Blocks { get; private set; } = [];
    public IReadOnlyList<ResearchSubject> Subjects { get; private set; } = [];
    public IReadOnlyList<ResearchLink> Links { get; private set; } = [];
    public IReadOnlyList<ResearchIssue> Issues { get; private set; } = [];
    /// <summary>Names in the race's research list that have no entity.</summary>
    public IReadOnlyList<string> MissingSubjects { get; private set; } = [];
    public IReadOnlyDictionary<ArrowShape, string> ArrowBrushes { get; private set; } = new Dictionary<ArrowShape, string>();
    public PixelPoint PictureTopLeft { get; private set; } = new(17, 54);
    public string? ContentBackdrop { get; private set; }

    public IEnumerable<ResearchBlock> BlocksOf(string field) => Blocks.Where(b => b.Field == field);
    public ResearchSubject? Subject(string name) => Subjects.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Subjects that can't be shown: no position, a field or block the race doesn't have, or outside the block's grid.</summary>
    public IEnumerable<ResearchSubject> Unplaced => Subjects.Where(s => CellOf(s) is null);

    /// <summary>Where a subject is drawn, or null if it isn't visible.</summary>
    public (ResearchBlock Block, ResearchFieldLayout Layout, PixelPoint Origin)? CellOf(ResearchSubject subject)
    {
        if (!subject.HasPosition || !Layouts.TryGetValue(subject.Field!, out var layout)) return null;
        var block = Blocks.FirstOrDefault(b => b.Field == subject.Field && b.Index == subject.Block);
        if (block is null) return null;
        var (cols, rows) = block.GridSize(layout);
        if (subject.Column < 0 || subject.Row < 0 || subject.Column >= cols || subject.Row >= rows) return null;
        return (block, layout, block.CellOrigin(layout, subject.Column!.Value, subject.Row!.Value));
    }

    /// <summary>
    /// Builds the tree for a race. <paramref name="currentDocument"/> returns the document to read for an entity,
    /// so unsaved edits are shown (default: the saved document).
    /// </summary>
    public static ResearchTree Build(ModWorkspace workspace, EntityRecord race, Func<EntityRecord, SinsDocument>? currentDocument = null)
    {
        currentDocument ??= e => e.Document;
        var index = workspace.Index;
        var tree = new ResearchTree(race);
        tree.LoadWindowLayout(workspace);
        tree.LoadBlocks(currentDocument(race));

        var names = index.ItemsOf(race).Where(i => i.Section == "researchInfo").Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        tree.Subjects = names.Where(n => index.Entities.ContainsKey(n)).Select(n => ReadSubject(index.Entities[n], currentDocument(index.Entities[n]), index)).ToList();
        tree.MissingSubjects = names.Where(n => !index.Entities.ContainsKey(n)).ToList();
        tree.BuildLinks();
        tree.Validate(index);
        return tree;
    }

    public static ResearchSubject ReadSubject(EntityRecord entity, SinsDocument doc, ModIndex index)
    {
        var root = doc.BuildTree();
        string? Str(SinsNode? n) => n != null && SinsValue.TryGetString(n.Value, out var s) && s.Length > 0 ? s : null;
        int? Int(SinsNode? n) => n != null && SinsValue.TryGetInt(n.Value, out int v) ? v : null;

        var location = root.Child("researchWindowLocation");
        int? column = null, row = null;
        if (location?.Child("pos") is { } pos && SinsValue.TryGetVector(pos.Value, out var p) && p.Length == 2)
        {
            column = (int)p[0];
            row = (int)p[1];
        }
        var prerequisites = root.Child("Prerequisites")?.ChildrenNamed("ResearchPrerequisite")
            .Select(n => (Subject: Str(n.Child("Subject")), Level: Int(n.Child("Level")) ?? 1, Line: n.Child("Subject")?.LineIndex ?? n.LineIndex))
            .Where(x => x.Subject != null)
            .Select(x => new ResearchPrerequisite(x.Subject!, x.Level, x.Line))
            .ToList() ?? [];

        return new ResearchSubject(entity, doc, Str(root.Child("ResearchField")), Int(location?.Child("block")), column, row,
            Int(root.Child("Tier")), Int(root.Child("MaxNumResearchLevels")) ?? 1, Str(root.Child("hudIcon")), prerequisites);
    }

    private void LoadWindowLayout(ModWorkspace workspace)
    {
        var layouts = new Dictionary<string, ResearchFieldLayout>();
        var arrows = new Dictionary<ArrowShape, string>();
        if (workspace.Stack.Resolve("Window/ResearchScreen.window") is { } file)
        {
            var root = SinsDocument.Load(file.AbsolutePath).BuildTree();
            foreach (string field in Fields)
            {
                string key = char.ToLowerInvariant(field[0]) + field[1..] + "ContentWindow";
                if (root.Child(key)?.Child("blockWindowSharedDef") is not { } def) continue;
                layouts[field] = new ResearchFieldLayout(field,
                    Point(def.Child("buttonTopLeftOffset")), Point(def.Child("buttonSize")), Point(def.Child("buttonSpacing")),
                    def.Child("buttonSpecificDef")?.Child("backdrop") is { } b && SinsValue.TryGetString(b.Value, out var s) && s.Length > 0 ? s : null);
            }
            var tree = root.Child("treeSharedDef");
            if (tree?.Child("basicTreeDef")?.Child("pictureTopLeft") is { } picture) PictureTopLeft = Point(picture);
            foreach (var arrow in tree?.Child("arrowBrushes")?.Children ?? [])
            {
                if (Enum.TryParse<ArrowShape>(arrow.Key, out var shape) && SinsValue.TryGetString(arrow.Value, out var brush)) arrows[shape] = brush;
            }
            if (root.Child("tabWindow")?.Child("contentBackdrop") is { } backdrop && SinsValue.TryGetString(backdrop.Value, out var bd)) ContentBackdrop = bd;
        }
        Layouts = layouts;
        ArrowBrushes = arrows;
    }

    private static PixelPoint Point(SinsNode? node) =>
        node != null && SinsValue.TryGetVector(node.Value, out var v) && v.Length >= 2 ? new PixelPoint((int)v[0], (int)v[1]) : default;

    private void LoadBlocks(SinsDocument raceDoc)
    {
        var blocks = new List<ResearchBlock>();
        var data = raceDoc.BuildTree().Child("researchScreenData");
        foreach (var field in data?.Children ?? [])
        {
            int i = 0;
            foreach (var block in field.ChildrenNamed("block"))
            {
                string? Str(string key) => block.Child(key) is { } n && SinsValue.TryGetString(n.Value, out var s) && s.Length > 0 ? s : null;
                PixelRect.TryParse(block.Child("area")?.Value ?? "", out var area);
                blocks.Add(new ResearchBlock(field.Key, i++, area, Str("backdrop"), Str("tierIndicatorBackdrop"), Str("title"), Str("picture")));
            }
        }
        Blocks = blocks;
    }

    private void BuildLinks()
    {
        var links = new List<ResearchLink>();
        foreach (var target in Subjects)
        {
            foreach (var pre in target.Prerequisites)
            {
                if (Subject(pre.Subject) is { } from) links.Add(new ResearchLink(from, target, pre.Level, Classify(from, target)));
            }
        }
        Links = links;
    }

    /// <summary>Which arrow image the game would use for a prerequisite, by relative position.</summary>
    public static ArrowShape Classify(ResearchSubject from, ResearchSubject to)
    {
        if (!from.HasPosition || !to.HasPosition) return ArrowShape.Unsupported;
        if (from.Field != to.Field) return ArrowShape.OtherTab;
        int dx = to.Column!.Value - from.Column!.Value, dy = to.Row!.Value - from.Row!.Value;
        if (from.Block == to.Block)
        {
            return (dx, dy) switch
            {
                (1, 0) => ArrowShape.Right1,
                (2, 0) => ArrowShape.Right2,
                (3, 0) => ArrowShape.Right3,
                (4, 0) => ArrowShape.Right4,
                (1, 1) => ArrowShape.Right1Down1,
                (0, 1) => ArrowShape.Down1,
                (0, -2) => ArrowShape.Up2,
                (1, -1) => ArrowShape.Up1Right1,
                (2, -1) => ArrowShape.Up1Right2,
                _ => ArrowShape.Unsupported,
            };
        }
        if (to.Block == from.Block + 1) return dx switch { 0 => ArrowShape.BlockDown1, 1 => ArrowShape.Right1BlockDown1, _ => ArrowShape.Unsupported };
        if (to.Block == from.Block - 1 && dx == 0) return ArrowShape.BlockUp1;
        return ArrowShape.Unsupported;
    }

    private void Validate(ModIndex index)
    {
        var issues = new List<ResearchIssue>();
        foreach (string missing in MissingSubjects)
            issues.Add(new(ProblemSeverity.Error, $"The race's research list names '{missing}', which doesn't exist.", null));

        foreach (var subject in Subjects)
        {
            string name = index.DisplayName(subject.Entity) ?? subject.Name;
            if (!subject.HasPosition)
                issues.Add(new(ProblemSeverity.Warning, $"{name} has no research window position.", subject.Name));
            else if (!Layouts.ContainsKey(subject.Field!) || !Blocks.Any(b => b.Field == subject.Field && b.Index == subject.Block))
                issues.Add(new(ProblemSeverity.Error, $"{name} is in {subject.Field} block {subject.Block}, which this race's research screen doesn't have.", subject.Name));
            else if (CellOf(subject) is null)
                issues.Add(new(ProblemSeverity.Error, $"{name} is at column {subject.Column}, row {subject.Row}, outside its block.", subject.Name));
            else if (subject.Tier is { } tier && tier != subject.Column)
                issues.Add(new(ProblemSeverity.Info, $"{name} is in column {subject.Column} but has tier {tier}.", subject.Name));

            foreach (var pre in subject.Prerequisites)
            {
                if (Subject(pre.Subject) is { } from)
                {
                    if (pre.Level > from.MaxLevels)
                        issues.Add(new(ProblemSeverity.Warning, $"{name} requires level {pre.Level} of {pre.Subject}, which has only {from.MaxLevels}.", subject.Name));
                }
                else if (index.Exists(ReferenceKind.Entity, pre.Subject))
                    issues.Add(new(ProblemSeverity.Warning, $"{name} requires {pre.Subject}, which isn't in this race's research list.", subject.Name));
                else
                    issues.Add(new(ProblemSeverity.Error, $"{name} requires {pre.Subject}, which doesn't exist.", subject.Name));
            }
        }

        foreach (var group in Subjects.Where(s => CellOf(s) != null).GroupBy(s => (s.Field, s.Block, s.Column, s.Row)).Where(g => g.Count() > 1))
            issues.Add(new(ProblemSeverity.Error,
                $"{string.Join(", ", group.Select(s => s.Name))} share the cell column {group.Key.Column}, row {group.Key.Row} in {group.Key.Field} block {group.Key.Block}.",
                group.First().Name));

        foreach (var link in Links.Where(l => l.Shape == ArrowShape.Unsupported))
            issues.Add(new(ProblemSeverity.Warning,
                $"No arrow shape fits {link.From.Name} → {link.To.Name}; the game can't draw this link.", link.To.Name));

        foreach (string cycle in FindCycles())
            issues.Add(new(ProblemSeverity.Error, $"Prerequisites form a cycle: {cycle}.", null));

        Issues = issues;
    }

    private IEnumerable<string> FindCycles()
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        var found = new List<string>();

        void Visit(ResearchSubject s)
        {
            state[s.Name] = 1;
            stack.Add(s.Name);
            foreach (var pre in s.Prerequisites)
            {
                if (Subject(pre.Subject) is not { } next) continue;
                int st = state.GetValueOrDefault(next.Name);
                if (st == 1) found.Add(string.Join(" → ", stack.SkipWhile(n => !n.Equals(next.Name, StringComparison.OrdinalIgnoreCase)).Append(next.Name)));
                else if (st == 0) Visit(next);
            }
            stack.RemoveAt(stack.Count - 1);
            state[s.Name] = 2;
        }

        foreach (var subject in Subjects)
        {
            if (state.GetValueOrDefault(subject.Name) == 0) Visit(subject);
        }
        return found;
    }
}
