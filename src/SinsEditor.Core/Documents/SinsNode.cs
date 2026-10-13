namespace SinsEditor.Core.Documents;

/// <summary>A line in the indentation tree of a <see cref="SinsDocument"/>. The root node has no line.</summary>
public sealed class SinsNode
{
    internal SinsNode(SinsLine? line, int depth, SinsNode? parent)
    {
        Line = line;
        Depth = depth;
        Parent = parent;
    }

    public SinsLine? Line { get; }
    public int LineIndex { get; internal init; } = -1;
    public int Depth { get; }
    public SinsNode? Parent { get; }
    public List<SinsNode> Children { get; } = [];

    public string Key => Line?.Key ?? "";
    public string Value => Line?.Value ?? "";

    /// <summary>First child with the given key (case-sensitive, as the game is).</summary>
    public SinsNode? Child(string key) => Children.FirstOrDefault(c => c.Key == key);

    public IEnumerable<SinsNode> ChildrenNamed(string key) => Children.Where(c => c.Key == key);

    public IEnumerable<SinsNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var d in child.Descendants()) yield return d;
        }
    }

    public override string ToString() => Line?.Text.Trim() ?? "<root>";
}
