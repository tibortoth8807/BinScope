using System.Text.RegularExpressions;
using BinScope.Core.Model;

namespace BinScope.Core.Diff;

public enum DiffStatus { Same, Changed, Added, Removed }

public sealed class DiffNode
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    /// <summary>Key path from the root ("root" first).</summary>
    public required IReadOnlyList<string> Path { get; init; }
    public TreeNode? Left { get; init; }
    public TreeNode? Right { get; init; }
    public DiffStatus Status { get; set; }
    /// <summary>Set when a difference exists but a filter hides it.</summary>
    public string? IgnoredBy { get; set; }
    public List<DiffNode> Children { get; } = [];

    /// <summary>Path without the root, e.g. "devices/Field_IO/ip".</summary>
    public string PathText => string.Join('/', Path.Skip(1));
}

/// <summary>
/// A compare filter hides differences you do not care about. Filters are version-dependent: each declares the
/// format versions it makes sense for.
/// </summary>
public interface IDiffFilter
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    IReadOnlyList<int> Versions { get; }
    bool ByDefault { get; }

    /// <summary>True when the difference at this node should be ignored; true for a group hides its whole subtree.</summary>
    bool Ignores(IReadOnlyList<string> path, TreeNode? left, TreeNode? right);
}

/// <summary>A filter defined by a predicate.</summary>
public sealed record DiffFilter(string Id, string Name, string Description, IReadOnlyList<int> Versions, bool ByDefault,
    Func<IReadOnlyList<string>, TreeNode?, TreeNode?, bool> Predicate) : IDiffFilter
{
    public bool Ignores(IReadOnlyList<string> path, TreeNode? left, TreeNode? right) => Predicate(path, left, right);
}

public sealed record DiffStats(int Changed, int Added, int Removed, int Ignored)
{
    public bool Identical => Changed + Added + Removed == 0;
}

public sealed record DiffResult(DiffNode Root, DiffStats Stats);

public sealed record LineOp(char Op, string Text);

public static partial class DiffEngine
{
    private static bool EndsWith(IReadOnlyList<string> path, params string[] tail) =>
        path.Count >= tail.Length && tail.Select((t, i) => path[path.Count - tail.Length + i] == t).All(x => x);

    private static string At(IReadOnlyList<string> path, int i) => i < path.Count ? path[i] : "";

    [GeneratedRegex(@"\(\*[\s\S]*?\*\)")]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Source text without comments and with normalised whitespace.</summary>
    public static string StripComments(string src) =>
        Whitespace().Replace(LineComment().Replace(BlockComment().Replace(src, " "), " "), " ").Trim();

    private static readonly string[] MachineCodeParts = ["code", "consts", "slots", "main", "startup", "symbols"];

    public static readonly IReadOnlyList<IDiffFilter> Filters =
    [
        new DiffFilter("build-time", "Ignore build time", "v3 stamps every export with the time of the build.", [3], true,
            (p, _, _) => EndsWith(p, "project", "timestamp")),
        new DiffFilter("build-id", "Ignore build ID", "v3 gives every export a random build ID.", [3], true,
            (p, _, _) => EndsWith(p, "project", "buildId")),
        new DiffFilter("checksum", "Ignore checksum", "The v3 CRC-32 trailer changes whenever any byte changes.", [3], true,
            (p, _, _) => p.Contains("trailer")),
        new DiffFilter("producer", "Ignore producer", "Version string of the tool that wrote the file (v2+).", [2, 3], true,
            (p, _, _) => EndsWith(p, "project", "producer")),
        new DiffFilter("layout", "Ignore section layout", "Section offsets and sizes shift whenever content before them grows or shrinks.", [1, 2, 3], true,
            (p, _, _) => At(p, 1) == "header" && p.Count > 2 && p[2] != "version"),
        new DiffFilter("comments", "Ignore comments & whitespace", "Source changes that only touch comments or formatting, and tag comments.", [1, 2, 3], false,
            (p, l, r) => (At(p, 1) == "tags" && p[p.Count - 1] == "comment")
                || (l is { IsText: true } && r is { IsText: true } && StripComments(l.Value ?? "") == StripComments(r.Value ?? ""))),
        new DiffFilter("machine-code", "Ignore machine code", "Hide bytecode, constants and data layout - compare sources and configuration only.", [1, 2, 3], false,
            (p, _, _) => At(p, 1) == "programs" && p.Count >= 4 && MachineCodeParts.Contains(p[3])),
        new DiffFilter("addressing", "Ignore IP addressing", "IP addresses and subnet masks (e.g. a test bench versus the plant network).", [1, 2, 3], false,
            (p, _, _) => At(p, 1) == "devices" && (EndsWith(p, "ip") || EndsWith(p, "mask"))),
    ];

    /// <summary>Filters that apply to a comparison of files with these versions.</summary>
    public static IReadOnlyList<IDiffFilter> ApplicableFilters(params int?[] versions)
    {
        var vs = versions.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return Filters.Where(f => vs.Any(f.Versions.Contains)).ToList();
    }

    /// <summary>The applicable filters that are on by default.</summary>
    public static IReadOnlyList<IDiffFilter> DefaultFilters(params int?[] versions) =>
        ApplicableFilters(versions).Where(f => f.ByDefault).ToList();

    public static DiffResult Compare(TreeNode left, TreeNode right, IReadOnlyList<IDiffFilter> filters)
    {
        var root = Visit(left, right, Array.Empty<string>(), filters);
        int changed = 0, added = 0, removed = 0, ignored = 0;
        foreach (var d in Differences(root))
        {
            switch (d.Status)
            {
                case DiffStatus.Changed: changed++; break;
                case DiffStatus.Added: added++; break;
                case DiffStatus.Removed: removed++; break;
            }
        }
        void CountIgnored(DiffNode d, bool parentIgnored)
        {
            if (d.IgnoredBy is not null && !parentIgnored) ignored++;
            foreach (var c in d.Children) CountIgnored(c, parentIgnored || d.IgnoredBy is not null);
        }
        CountIgnored(root, false);
        return new DiffResult(root, new DiffStats(changed, added, removed, ignored));
    }

    private static DiffNode Visit(TreeNode? l, TreeNode? r, IReadOnlyList<string> path, IReadOnlyList<IDiffFilter> filters)
    {
        var any = (l ?? r)!;
        var p = path.Append(any.Key).ToList();
        var d = new DiffNode { Key = any.Key, Label = (r ?? l)!.Label, Path = p, Left = l, Right = r };

        var lc = l?.Children ?? new List<TreeNode>();
        var rc = r?.Children ?? new List<TreeNode>();
        if (lc.Count > 0 || rc.Count > 0)
        {
            // match children by key; keep the right side's order and slot removed ones where they were
            var rKeys = new Dictionary<string, TreeNode>();
            foreach (var c in rc) rKeys[c.Key] = c;
            var lKeys = new Dictionary<string, TreeNode>();
            foreach (var c in lc) lKeys[c.Key] = c;
            var order = new List<string>();
            var seen = new HashSet<string>();
            void Push(string k)
            {
                if (seen.Add(k)) order.Add(k);
            }
            var ri = 0;
            foreach (var c in lc)
            {
                if (rKeys.ContainsKey(c.Key))
                {
                    while (ri < rc.Count && rc[ri].Key != c.Key)
                    {
                        if (!lKeys.ContainsKey(rc[ri].Key)) Push(rc[ri].Key);
                        ri++;
                    }
                }
                Push(c.Key);
            }
            foreach (var c in rc) Push(c.Key);
            foreach (var k in order) d.Children.Add(Visit(lKeys.GetValueOrDefault(k), rKeys.GetValueOrDefault(k), p, filters));
        }

        DiffStatus raw;
        if (l is null) raw = DiffStatus.Added;
        else if (r is null) raw = DiffStatus.Removed;
        else if (d.Children.Count > 0) raw = d.Children.Any(c => c.Status != DiffStatus.Same) ? DiffStatus.Changed : DiffStatus.Same;
        else raw = l.Value == r.Value ? DiffStatus.Same : DiffStatus.Changed;

        if (raw != DiffStatus.Same)
        {
            var filter = filters.FirstOrDefault(f => f.Ignores(p, l, r));
            if (filter is not null)
            {
                d.IgnoredBy = filter.Id;
                MarkIgnored(d, filter.Id);
                return d;
            }
        }
        d.Status = raw;
        return d;
    }

    private static void MarkIgnored(DiffNode d, string id)
    {
        foreach (var c in d.Children)
        {
            if (c.Status != DiffStatus.Same)
            {
                c.IgnoredBy = id;
                c.Status = DiffStatus.Same;
            }
            MarkIgnored(c, id);
        }
    }

    /// <summary>Flat list of the visible differences (leaves, plus added/removed groups).</summary>
    public static List<DiffNode> Differences(DiffNode root)
    {
        var output = new List<DiffNode>();
        void Visit(DiffNode d)
        {
            if (d.Status == DiffStatus.Same) return;
            if (d.Status != DiffStatus.Changed || d.Children.Count == 0)
            {
                output.Add(d);
                return;
            }
            foreach (var c in d.Children) Visit(c);
        }
        Visit(root);
        return output;
    }

    /// <summary>Line diff (longest common subsequence) for block sources.</summary>
    public static List<LineOp> LineDiff(string a, string b)
    {
        var x = a.Split('\n');
        var y = b.Split('\n');
        int n = x.Length, m = y.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = x[i] == y[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var output = new List<LineOp>();
        int ii = 0, jj = 0;
        while (ii < n && jj < m)
        {
            if (x[ii] == y[jj])
            {
                output.Add(new LineOp(' ', x[ii]));
                ii++;
                jj++;
            }
            else if (dp[ii + 1, jj] >= dp[ii, jj + 1]) output.Add(new LineOp('-', x[ii++]));
            else output.Add(new LineOp('+', y[jj++]));
        }
        while (ii < n) output.Add(new LineOp('-', x[ii++]));
        while (jj < m) output.Add(new LineOp('+', y[jj++]));
        return output;
    }
}
