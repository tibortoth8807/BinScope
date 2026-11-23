using System.Text.Json;
using BinScope.Cli;
using BinScope.Core.Plugins;

namespace BinScope.Tests;

public class CliTests
{
    private readonly StringWriter _out = new();
    private readonly Dictionary<string, byte[]> _written = new();

    private int Run(params string[] args) =>
        CliApp.Run(args, new CliIo(_out, p => _written.TryGetValue(p, out var b) ? b : Samples.Read(p), (p, b) => _written[p] = b));

    [Fact]
    public void ListsThePluginStack()
    {
        Assert.Equal(0, Run("plugins"));
        Assert.Contains("1. plcbin-v3", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InfoShowsTheTree()
    {
        Assert.Equal(0, Run("info", "conveyor_v3.plcbin"));
        var text = _out.ToString();
        Assert.Contains("read by PLC Studio binary v3 (format v3)", text, StringComparison.Ordinal);
        Assert.Contains("Field_IO", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareExitCodesWorkLikeDiff()
    {
        Assert.Equal(0, Run("compare", "conveyor_v3.plcbin", "conveyor_v3_rebuild.plcbin"));
        Assert.Equal(1, Run("compare", "conveyor_v3.plcbin", "conveyor_v3_rebuild.plcbin", "--no-filters"));
        Assert.Equal(2, Run("compare", "conveyor_v3.plcbin", "conveyor_v3_changed.plcbin", "--filters", "nope"));
        Assert.Equal(2, Run("compare", "only-one.plcbin"));
    }

    [Fact]
    public void CompareWritesJson()
    {
        Assert.Equal(1, Run("compare", "conveyor_v3.plcbin", "conveyor_v3_changed.plcbin", "--json"));
        using var doc = JsonDocument.Parse(_out.ToString());
        Assert.Equal(8, doc.RootElement.GetProperty("stats").GetProperty("changed").GetInt32());
        Assert.Equal(9, doc.RootElement.GetProperty("differences").GetArrayLength());
        Assert.Equal("devices/Line_PLC/cycle", doc.RootElement.GetProperty("differences")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void SetChangesAValueAndWritesANewFile()
    {
        Assert.Equal(0, Run("set", "conveyor_v3.plcbin", "devices/Line_PLC/cycle", "25", "--out", "x.plcbin"));
        var f = new PluginRegistry().Load(_written["x.plcbin"], "x.plcbin");
        Assert.Equal(25, f.Result.Model!.Devices[0].CycleMs);
        Assert.Empty(f.Result.Warnings);

        Assert.Equal(2, Run("set", "conveyor_v3.plcbin", "devices/Line_PLC/cycle", "0", "--out", "y.plcbin"));
        Assert.Equal(2, Run("set", "conveyor_v3.plcbin", "header/magic", "XXXX", "--out", "y.plcbin"));
        Assert.False(_written.ContainsKey("y.plcbin"));
    }
}
