using System.Text.Json;
using BinScope.Core.Binary;
using BinScope.Core.Diff;
using BinScope.Core.Plugins;

namespace BinScope.Cli;

/// <summary>File and console access of the CLI - replaced by in-memory fakes in tests.</summary>
public sealed record CliIo(TextWriter Out, Func<string, byte[]> ReadFile, Action<string, byte[]> WriteFile);

/// <summary>
/// binscope plugins
/// binscope info &lt;file&gt; [--plugin id]
/// binscope compare &lt;left&gt; &lt;right&gt; [--filters a,b | --no-filters | --all-filters] [--json]
/// binscope set &lt;file&gt; &lt;path&gt; &lt;value&gt; --out &lt;file&gt;
/// Exit code for compare: 0 = no visible differences, 1 = differences, 2 = error (like diff).
/// </summary>
public static class CliApp
{
    private const string Usage =
        "usage: binscope plugins | info <file> | compare <left> <right> [--filters a,b|--no-filters|--all-filters] [--json] | set <file> <path> <value> --out <file>";

    private static readonly string[] ValueFlags = ["filters", "plugin", "out"];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string[] argv, CliIo io)
    {
        var cmd = argv.Length > 0 ? argv[0] : "";
        var flags = new Dictionary<string, string?>();
        var args = new List<string>();
        for (var i = 1; i < argv.Length; i++)
        {
            if (argv[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = argv[i][2..];
                if (ValueFlags.Contains(name) && i + 1 < argv.Length) flags[name] = argv[++i];
                else flags[name] = null;
            }
            else args.Add(argv[i]);
        }

        var registry = new PluginRegistry();
        LoadedFile Open(string path)
        {
            var bytes = io.ReadFile(path);
            var name = Path.GetFileName(path);
            if (flags.TryGetValue("plugin", out var forced) && forced is not null)
            {
                var p = registry.Get(forced) ?? throw new ArgumentException($"Unknown plugin {forced}");
                return new LoadedFile(name, bytes, p, p.Read(bytes, name));
            }
            return registry.Load(bytes, name);
        }

        try
        {
            switch (cmd)
            {
                case "plugins":
                    io.Out.WriteLine("Plugin stack (first match wins):");
                    for (var i = 0; i < registry.Stack.Count; i++)
                    {
                        var p = registry.Stack[i].Plugin;
                        io.Out.WriteLine($"  {i + 1}. {p.Id,-16} {p.Name} – {p.Description}");
                    }
                    return 0;

                case "info":
                {
                    if (args.Count != 1) throw new ArgumentException("usage: binscope info <file>");
                    var f = Open(args[0]);
                    io.Out.WriteLine($"{f.FileName}: {f.Bytes.Length} bytes, read by {f.Plugin.Name}{(f.Result.Version is int v ? $" (format v{v})" : "")}");
                    foreach (var w in f.Result.Warnings) io.Out.WriteLine($"  ⚠ {w}");
                    foreach (var (n, path) in f.Result.Tree.Walk())
                    {
                        if (path.Count > 3) continue;
                        var value = n.Value is not null && !n.IsText ? $": {n.Value}" : "";
                        io.Out.WriteLine($"{new string(' ', 2 * (path.Count - 1))}{n.Label}{value}");
                    }
                    return 0;
                }

                case "compare":
                {
                    if (args.Count != 2) throw new ArgumentException("usage: binscope compare <left> <right> [--filters a,b] [--json]");
                    var a = Open(args[0]);
                    var b = Open(args[1]);
                    var filters = ChooseFilters(flags, a.Result.Version, b.Result.Version);
                    var result = DiffEngine.Compare(a.Result.Tree, b.Result.Tree, filters);
                    var diffs = DiffEngine.Differences(result.Root);
                    if (flags.ContainsKey("json"))
                    {
                        io.Out.WriteLine(JsonSerializer.Serialize(new
                        {
                            left = new { file = a.FileName, plugin = a.Plugin.Id, version = a.Result.Version },
                            right = new { file = b.FileName, plugin = b.Plugin.Id, version = b.Result.Version },
                            filters = filters.Select(x => x.Id),
                            stats = new { result.Stats.Changed, result.Stats.Added, result.Stats.Removed, result.Stats.Ignored },
                            differences = diffs.Select(d => new
                            {
                                path = d.PathText,
                                status = d.Status.ToString().ToLowerInvariant(),
                                left = d.Left?.Value,
                                right = d.Right?.Value,
                            }),
                        }, Json));
                    }
                    else
                    {
                        io.Out.WriteLine($"{a.FileName} ({a.Plugin.Id}) ↔ {b.FileName} ({b.Plugin.Id})");
                        io.Out.WriteLine($"Filters: {(filters.Count > 0 ? string.Join(", ", filters.Select(x => x.Id)) : "none")}");
                        foreach (var d in diffs)
                        {
                            var sign = d.Status switch { DiffStatus.Added => '+', DiffStatus.Removed => '-', _ => '~' };
                            var detail = d.Left?.IsText == true ? " (source changed)"
                                : d.Status == DiffStatus.Changed ? $": {d.Left?.Value} → {d.Right?.Value}" : "";
                            io.Out.WriteLine($"  {sign} {string.Join(" / ", d.Path.Skip(1))}{detail}");
                        }
                        var s = result.Stats;
                        io.Out.WriteLine($"{s.Changed} changed, {s.Added} added, {s.Removed} removed, {s.Ignored} hidden by filters");
                    }
                    return diffs.Count > 0 ? 1 : 0;
                }

                case "set":
                {
                    if (args.Count != 3 || !flags.TryGetValue("out", out var output) || output is null)
                        throw new ArgumentException("usage: binscope set <file> <path> <value> --out <file>");
                    var f = Open(args[0]);
                    var target = f.Result.Tree.Find(args[1].Split('/', StringSplitOptions.RemoveEmptyEntries))
                        ?? throw new ArgumentException($"No element {args[1]}");
                    if (target.Edit is null || f.Plugin is not IWritablePlugin writer || f.Result.Model is null)
                        throw new ArgumentException($"{args[1]} is not editable with {f.Plugin.Name}");
                    var problem = target.Edit.Apply(args[2]);
                    if (problem is not null) throw new ArgumentException(problem);
                    var bytes = writer.Write(f.Result.Model);
                    io.WriteFile(output, bytes);
                    io.Out.WriteLine($"{args[1]} = {args[2]} → {output} ({bytes.Length} bytes, written by {f.Plugin.Name})");
                    return 0;
                }

                default:
                    io.Out.WriteLine(Usage);
                    return cmd.Length > 0 ? 2 : 0;
            }
        }
        catch (Exception e) when (e is ArgumentException or BinaryFormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            io.Out.WriteLine($"error: {e.Message}");
            return 2;
        }
    }

    private static IReadOnlyList<IDiffFilter> ChooseFilters(Dictionary<string, string?> flags, int? left, int? right)
    {
        var applicable = DiffEngine.ApplicableFilters(left, right);
        if (flags.ContainsKey("no-filters")) return [];
        if (flags.ContainsKey("all-filters")) return applicable;
        if (flags.TryGetValue("filters", out var list) && list is not null)
        {
            var ids = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var unknown = ids.Where(id => DiffEngine.Filters.All(f => f.Id != id)).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"Unknown filter(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", DiffEngine.Filters.Select(f => f.Id))}");
            return applicable.Where(f => ids.Contains(f.Id)).ToList();
        }
        return applicable.Where(f => f.ByDefault).ToList();
    }
}
