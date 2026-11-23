using System.Text;
using BinScope.Core.Binary;
using BinScope.Core.Diff;
using BinScope.Core.Formats;
using BinScope.Core.Model;
using BinScope.Core.Plugins;

namespace BinScope.Tests;

public class CoreTests
{
    private static IReadOnlyList<string> Visible(LoadedFile a, LoadedFile b, IReadOnlyList<IDiffFilter> filters) =>
        DiffEngine.Differences(DiffEngine.Compare(a.Result.Tree, b.Result.Tree, filters).Root).Select(d => d.PathText).ToList();

    [Fact]
    public void Crc32MatchesTheStandardCheckValue() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));

    [Theory]
    [InlineData("conveyor_v1.plcbin")]
    [InlineData("conveyor_v2.plcbin")]
    [InlineData("conveyor_v3.plcbin")]
    [InlineData("conveyor_v3_changed.plcbin")]
    public void ReadingAndWritingGivesIdenticalBytes(string name)
    {
        var bytes = Samples.Read(name);
        var parsed = PlcBinFormat.Parse(bytes, new ParseOptions(Detailed: true, Editable: false));
        Assert.Equal(bytes, PlcBinFormat.Write(parsed.Model));
    }

    [Fact]
    public void DisassemblyShowsIoAddressesAndSymbolNames()
    {
        var code = Samples.Open("conveyor_v3.plcbin").Result.Tree.Find("programs", "Line_PLC", "code")!;
        Assert.Contains(code.Children!, c => c.Value == "LDBIT %I0.0");
        Assert.Contains(code.Children!, c => c.Value == "STORE Main.running");
    }

    [Fact]
    public void ReorderingAndDisablingPluginsChangesWhoReadsTheFile()
    {
        var registry = new PluginRegistry();
        for (var i = 0; i < 3; i++) registry.Move("plcbin-legacy", -1);
        Assert.Equal("plcbin-legacy", registry.Stack[0].Plugin.Id);

        var legacy = Samples.Open("conveyor_v3.plcbin", registry);
        Assert.Equal("plcbin-legacy", legacy.Plugin.Id);
        Assert.Null(legacy.Result.Tree.Find("programs", "Line_PLC", "symbols"));
        Assert.IsNotAssignableFrom<IWritablePlugin>(legacy.Plugin);

        foreach (var id in new[] { "plcbin-v3", "plcbin-v2", "plcbin-v1", "plcbin-legacy" }) registry.SetEnabled(id, false);
        Assert.Equal("hex", Samples.Open("conveyor_v3.plcbin", registry).Plugin.Id);
    }

    [Fact]
    public void AFailingPluginHandsTheFileToTheNextOne()
    {
        var registry = new PluginRegistry(new IBinaryPlugin[] { new BrokenPlugin(), new HexDumpPlugin() });
        Assert.Equal("hex", registry.Load(new byte[] { 1, 2, 3 }, "x.bin").Plugin.Id);
    }

    [Fact]
    public void ExternalPluginsAreSortedInByPriority()
    {
        var registry = new PluginRegistry();
        registry.Register(new BrokenPlugin());
        Assert.Equal(new[] { "plcbin-v3", "plcbin-v2", "broken", "plcbin-v1", "plcbin-legacy", "hex" }, registry.Stack.Select(e => e.Plugin.Id));
    }

    [Fact]
    public void TheDamagedFileIsStillReadWithAChecksumWarning()
    {
        var f = Samples.Open("conveyor_v3_corrupt.plcbin");
        Assert.Equal("plcbin-v3", f.Plugin.Id);
        Assert.Contains(f.Result.Warnings, w => w.Contains("CRC mismatch", StringComparison.Ordinal));
        Assert.StartsWith("INVALID", f.Result.Tree.Find("trailer", "valid")!.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedFileIsRejectedWithTheOffset()
    {
        var bytes = Samples.Read("conveyor_v2.plcbin")[..100];
        var e = Assert.Throws<BinaryFormatException>(() => PlcBinFormat.Parse(bytes, new ParseOptions(true, false)));
        Assert.True(e.Offset >= 8);
    }

    [Fact]
    public void ARebuildDiffersOnlyInBuildNoise()
    {
        var a = Samples.Open("conveyor_v3.plcbin");
        var b = Samples.Open("conveyor_v3_rebuild.plcbin");
        Assert.Equal(new[] { "project/timestamp", "project/buildId", "trailer/crc" }, Visible(a, b, Array.Empty<IDiffFilter>()));
        var filtered = DiffEngine.Compare(a.Result.Tree, b.Result.Tree, DiffEngine.DefaultFilters(3, 3));
        Assert.Equal(new DiffStats(0, 0, 0, 3), filtered.Stats);
        Assert.True(filtered.Stats.Identical);
    }

    [Fact]
    public void OptionalFiltersHideCommentsMachineCodeAndAddressing()
    {
        var a = Samples.Open("conveyor_v3.plcbin");
        var b = Samples.Open("conveyor_v3_changed.plcbin");
        IReadOnlyList<IDiffFilter> Pick(string id) => DiffEngine.Filters.Where(f => f.ByDefault || f.Id == id).ToList();

        var comments = Visible(a, b, Pick("comments"));
        Assert.DoesNotContain("blocks/Line_PLC.Scale/source", comments);
        Assert.Contains("blocks/Line_PLC.Main/source", comments);
        Assert.DoesNotContain("tags/Line_PLC/Horn/comment", comments);
        Assert.DoesNotContain(Visible(a, b, Pick("machine-code")), p => p.StartsWith("programs/", StringComparison.Ordinal));
        Assert.DoesNotContain("devices/Field_IO/ip", Visible(a, b, Pick("addressing")));
    }

    [Fact]
    public void FiltersDependOnTheFormatVersion()
    {
        Assert.Equal(new[] { "layout", "comments", "machine-code", "addressing" }, DiffEngine.ApplicableFilters(1, 1).Select(f => f.Id));
        Assert.Contains(DiffEngine.ApplicableFilters(2, 2), f => f.Id == "producer");
        Assert.DoesNotContain(DiffEngine.ApplicableFilters(2, 2), f => f.Id == "build-id");
        Assert.Contains(DiffEngine.ApplicableFilters(1, 3), f => f.Id == "checksum");
        Assert.Empty(DiffEngine.ApplicableFilters(null, null));
    }

    [Fact]
    public void EditingAnIpAddressWritesAValidFile()
    {
        var f = Samples.Open("conveyor_v3.plcbin");
        var ip = f.Result.Tree.Find("devices", "Field_IO", "ip")!;
        Assert.Contains("not an IPv4", ip.Edit!.Apply("300.1.1.1"), StringComparison.Ordinal);
        Assert.Null(ip.Edit.Apply("10.0.0.7"));

        var saved = ((IWritablePlugin)f.Plugin).Write(f.Result.Model!);
        var again = new PluginRegistry().Load(saved, "edited.plcbin");
        Assert.Equal("10.0.0.7", again.Result.Model!.Devices[1].Ip);
        Assert.Empty(again.Result.Warnings);
    }

    [Fact]
    public void ConvertingTheFormatVersionThroughThePluginStack()
    {
        var v3 = Samples.Open("conveyor_v3.plcbin");
        var registry = new PluginRegistry();
        var v2Writer = (IWritablePlugin)registry.Get("plcbin-v2")!;
        Assert.Equal(Samples.Read("conveyor_v2.plcbin"), v2Writer.Write(v3.Result.Model!));
    }

    [Fact]
    public void LineDiffAndCommentStripping()
    {
        Assert.Equal(new[] { " a", "-b", "+x", " c" }, DiffEngine.LineDiff("a\nb\nc", "a\nx\nc").Select(l => $"{l.Op}{l.Text}"));
        Assert.Equal("x := 1; y := 2;", DiffEngine.StripComments("x := 1; (* one *)\n// two\ny := 2;"));
    }

    private sealed class BrokenPlugin : IBinaryPlugin
    {
        public string Id => "broken";
        public string Name => "Broken";
        public string Description => "Always throws";
        public int Priority => 150;
        public bool CanRead(byte[] bytes, string fileName) => true;
        public ReadResult Read(byte[] bytes, string fileName) => throw new BinaryFormatException("broken", 0);
    }
}
