using System.Globalization;
using System.Text;
using BinScope.Core.Formats;
using BinScope.Core.Model;

namespace BinScope.Core.Plugins;

/// <summary>What a plugin returns for a file.</summary>
/// <param name="Tree">Tree view of the content; the compare engine works on it.</param>
/// <param name="Warnings">Problems found while reading (the file was still read).</param>
/// <param name="Version">Format version, used to decide which compare filters apply; null when unknown.</param>
/// <param name="Model">Decoded content, for plugins that can write the file back after edits.</param>
public sealed record ReadResult(TreeNode Tree, IReadOnlyList<string> Warnings, int? Version, PlcBinModel? Model = null);

/// <summary>
/// A reader for one binary format. Plugins sit in a stack (<see cref="PluginRegistry"/>): the first enabled plugin
/// that says it can read a file gets it. Several plugins may understand the same file - the higher one wins.
/// </summary>
public interface IBinaryPlugin
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    /// <summary>Default position in the stack: higher = asked first.</summary>
    int Priority { get; }
    bool CanRead(byte[] bytes, string fileName);
    ReadResult Read(byte[] bytes, string fileName);
}

/// <summary>A plugin that can serialise an edited model back to bytes.</summary>
public interface IWritablePlugin : IBinaryPlugin
{
    byte[] Write(PlcBinModel model);
}

/// <summary>Full reader/writer for one .plcbin format version.</summary>
public sealed class PlcBinVersionPlugin(int version) : IWritablePlugin
{
    public string Id => $"plcbin-v{version}";
    public string Name => $"PLC Studio binary v{version}";
    public string Description => $"Full reader/writer for format version {version}: disassembly, symbols, editing{(version == 3 ? ", checksum validation" : "")}.";
    public int Priority => 100 * version;

    public bool CanRead(byte[] bytes, string fileName) => PlcBinFormat.PeekVersion(bytes) == version;

    public ReadResult Read(byte[] bytes, string fileName)
    {
        var r = PlcBinFormat.Parse(bytes, new ParseOptions(Detailed: true, Editable: true));
        return new ReadResult(r.Tree, r.Warnings, r.Model.Version, r.Model);
    }

    public byte[] Write(PlcBinModel model)
    {
        model.Version = version;
        return PlcBinFormat.Write(model);
    }
}

/// <summary>An older generic reader: understands every version, shows a summary only and cannot write.</summary>
public sealed class PlcBinLegacyPlugin : IBinaryPlugin
{
    public string Id => "plcbin-legacy";
    public string Name => "PLC Studio binary (legacy viewer)";
    public string Description => "Reads all versions, summary only: no disassembly, no symbols, read-only. Kept for compatibility.";
    public int Priority => 50;

    public bool CanRead(byte[] bytes, string fileName) => PlcBinFormat.PeekVersion(bytes) is >= 1 and <= 3;

    public ReadResult Read(byte[] bytes, string fileName)
    {
        var r = PlcBinFormat.Parse(bytes, new ParseOptions(Detailed: false, Editable: false));
        return new ReadResult(r.Tree, r.Warnings, r.Model.Version);
    }
}

/// <summary>Fallback for any file: a hex dump in 16-byte rows.</summary>
public sealed class HexDumpPlugin : IBinaryPlugin
{
    private const int Limit = 64 * 1024;

    public string Id => "hex";
    public string Name => "Hex dump";
    public string Description => "Reads any file as raw bytes, 16 per row. Last resort when no format plugin matches.";
    public int Priority => 0;

    public bool CanRead(byte[] bytes, string fileName) => true;

    public ReadResult Read(byte[] bytes, string fileName)
    {
        var root = TreeNode.Group("root", string.IsNullOrEmpty(fileName) ? "file" : fileName, 0);
        root.Length = bytes.Length;
        root.Value = $"{bytes.Length} bytes (raw)";
        var limit = Math.Min(bytes.Length, Limit);
        for (var at = 0; at < limit; at += 16)
        {
            var chunk = bytes.AsSpan(at, Math.Min(16, bytes.Length - at));
            var hex = string.Join(' ', chunk.ToArray().Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            var ascii = new StringBuilder();
            foreach (var b in chunk) ascii.Append(b is >= 32 and < 127 ? (char)b : '·');
            var key = "0x" + at.ToString("x6", CultureInfo.InvariantCulture);
            root.Add(TreeNode.Leaf(key, key, $"{hex,-47}  {ascii}", at, chunk.Length));
        }
        string[] warnings = bytes.Length > limit ? [$"Only the first {limit} bytes are shown."] : Array.Empty<string>();
        return new ReadResult(root, warnings, null);
    }
}

public static class BuiltinPlugins
{
    public static IReadOnlyList<IBinaryPlugin> All() =>
        [new PlcBinVersionPlugin(3), new PlcBinVersionPlugin(2), new PlcBinVersionPlugin(1), new PlcBinLegacyPlugin(), new HexDumpPlugin()];
}

public sealed class StackEntry(IBinaryPlugin plugin)
{
    public IBinaryPlugin Plugin { get; } = plugin;
    public bool Enabled { get; set; } = true;
}

/// <summary>A file read by the stack: which plugin took it and what it returned.</summary>
public sealed record LoadedFile(string FileName, byte[] Bytes, IBinaryPlugin Plugin, ReadResult Result);

/// <summary>The plugin stack: ordered by priority, reorderable, each entry can be disabled.</summary>
public sealed class PluginRegistry
{
    private readonly List<StackEntry> _stack;

    public PluginRegistry(IEnumerable<IBinaryPlugin>? plugins = null)
    {
        _stack = (plugins ?? BuiltinPlugins.All()).OrderByDescending(p => p.Priority).Select(p => new StackEntry(p)).ToList();
    }

    public IReadOnlyList<StackEntry> Stack => _stack;

    /// <summary>Adds a plugin at the position its priority gives it (e.g. one loaded from an external assembly).</summary>
    public void Register(IBinaryPlugin plugin)
    {
        var i = _stack.FindIndex(e => e.Plugin.Priority < plugin.Priority);
        _stack.Insert(i < 0 ? _stack.Count : i, new StackEntry(plugin));
    }

    /// <summary>All enabled plugins that could read the file, in stack order.</summary>
    public IEnumerable<IBinaryPlugin> Candidates(byte[] bytes, string fileName = "") =>
        _stack.Where(e => e.Enabled && e.Plugin.CanRead(bytes, fileName)).Select(e => e.Plugin);

    /// <summary>The plugin that gets the file: first match from the top.</summary>
    public IBinaryPlugin? Pick(byte[] bytes, string fileName = "") => Candidates(bytes, fileName).FirstOrDefault();

    public void Move(string id, int delta)
    {
        var i = _stack.FindIndex(e => e.Plugin.Id == id);
        var j = i + Math.Sign(delta);
        if (i < 0 || j < 0 || j >= _stack.Count) return;
        (_stack[i], _stack[j]) = (_stack[j], _stack[i]);
    }

    public void SetEnabled(string id, bool enabled)
    {
        var e = _stack.Find(x => x.Plugin.Id == id);
        if (e is not null) e.Enabled = enabled;
    }

    public IBinaryPlugin? Get(string id) => _stack.Find(e => e.Plugin.Id == id)?.Plugin;

    /// <summary>Reads a file with the first plugin that accepts it; if that plugin fails, the next one is tried.</summary>
    public LoadedFile Load(byte[] bytes, string fileName)
    {
        var errors = new List<string>();
        foreach (var plugin in Candidates(bytes, fileName))
        {
            try
            {
                return new LoadedFile(fileName, bytes, plugin, plugin.Read(bytes, fileName));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a plugin that fails hands the file to the next one in the stack
                errors.Add($"{plugin.Name}: {e.Message}");
            }
        }
        throw new InvalidDataException(errors.Count > 0
            ? $"No plugin could read {fileName}.{Environment.NewLine}{string.Join(Environment.NewLine, errors)}"
            : $"No enabled plugin accepts {fileName}.");
    }

    public LoadedFile Load(string path) => Load(File.ReadAllBytes(path), Path.GetFileName(path));
}
