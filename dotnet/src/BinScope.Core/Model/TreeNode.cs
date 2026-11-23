namespace BinScope.Core.Model;

/// <summary>Edit handler of a leaf: applies a new value to the model, returns an error message or null.</summary>
public sealed record NodeEdit(string Hint, Func<string, string?> Apply);

/// <summary>One node of the tree a plugin builds from a file. The compare engine matches nodes by their key path.</summary>
public sealed class TreeNode(string key, string label)
{
    /// <summary>Stable key among its siblings.</summary>
    public string Key { get; set; } = key;
    public string Label { get; set; } = label;
    /// <summary>Display value; null for pure groups.</summary>
    public string? Value { get; set; }
    /// <summary>Byte range in the file this node came from.</summary>
    public int? Offset { get; set; }
    public int? Length { get; set; }
    /// <summary>Multi-line text (block sources).</summary>
    public bool IsText { get; set; }
    public NodeEdit? Edit { get; set; }
    /// <summary>Null for leaves; groups always have a list (possibly empty).</summary>
    public List<TreeNode>? Children { get; set; }

    public static TreeNode Group(string key, string label, int? offset = null) => new(key, label) { Children = [], Offset = offset };

    public static TreeNode Leaf(string key, string label, string value, int? offset = null, int? length = null) =>
        new(key, label) { Value = value, Offset = offset, Length = length };

    public TreeNode Add(TreeNode child)
    {
        (Children ??= new List<TreeNode>()).Add(child);
        return this;
    }

    /// <summary>Depth-first walk with the key path (the root key first).</summary>
    public IEnumerable<(TreeNode Node, IReadOnlyList<string> Path)> Walk()
    {
        var stack = new Stack<(TreeNode, List<string>)>();
        stack.Push((this, new List<string> { Key }));
        while (stack.Count > 0)
        {
            var (n, path) = stack.Pop();
            yield return (n, path);
            if (n.Children is null) continue;
            for (var i = n.Children.Count - 1; i >= 0; i--)
            {
                var c = n.Children[i];
                stack.Push((c, new List<string>(path) { c.Key }));
            }
        }
    }

    /// <summary>Finds a node by key path below this node, e.g. Find("devices", "Field_IO", "ip").</summary>
    public TreeNode? Find(params string[] path)
    {
        var n = this;
        foreach (var key in path)
        {
            n = n.Children?.FirstOrDefault(c => c.Key == key);
            if (n is null) return null;
        }
        return n;
    }
}
