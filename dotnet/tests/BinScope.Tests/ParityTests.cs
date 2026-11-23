using System.Text.Json;
using BinScope.Core.Diff;

namespace BinScope.Tests;

/// <summary>
/// The .NET implementation must read and compare every sample exactly like the TypeScript reference (web/), whose
/// results are stored in samples/expected.json (regenerate with: cd web &amp;&amp; npm run samples:expected).
/// </summary>
public class ParityTests
{
    public static TheoryData<string> FileNames()
    {
        var data = new TheoryData<string>();
        foreach (var p in Samples.Expected.GetProperty("files").EnumerateObject()) data.Add(p.Name);
        return data;
    }

    public static TheoryData<int> ComparisonIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Samples.Expected.GetProperty("comparisons").GetArrayLength(); i++) data.Add(i);
        return data;
    }

    private static List<string> Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToList();

    [Theory]
    [MemberData(nameof(FileNames))]
    public void ReadsEverySampleLikeTheReference(string name)
    {
        var expected = Samples.Expected.GetProperty("files").GetProperty(name);
        var f = Samples.Open(name);

        Assert.Equal(expected.GetProperty("plugin").GetString(), f.Plugin.Id);
        var version = expected.GetProperty("version");
        Assert.Equal(version.ValueKind == JsonValueKind.Null ? (int?)null : version.GetInt32(), f.Result.Version);
        Assert.Equal(expected.GetProperty("nodes").GetInt32(), f.Result.Tree.Walk().Count());
        Assert.Equal(expected.GetProperty("warnings").GetInt32(), f.Result.Warnings.Count);

        if (!expected.TryGetProperty("devices", out var devices)) return;
        var m = f.Result.Model!;
        Assert.Equal(Strings(devices), m.Devices.Select(d => d.Name));
        Assert.Equal(Strings(expected.GetProperty("blocks")), m.Blocks.Select(b => b.Name));
        Assert.Equal(expected.GetProperty("codeWords").EnumerateArray().Select(x => x.GetInt32()), m.Programs.Select(p => p.Code.Count));
        Assert.Equal(expected.GetProperty("tags").GetInt32(), m.Tags.Sum(t => t.Tags.Count));
        Assert.Equal(expected.GetProperty("hmiElements").GetInt32(), m.Hmis.Sum(h => h.Elements.Count));
    }

    [Theory]
    [MemberData(nameof(ComparisonIndexes))]
    public void ComparesLikeTheReference(int index)
    {
        var c = Samples.Expected.GetProperty("comparisons")[index];
        var a = Samples.Open(c.GetProperty("left").GetString()!);
        var b = Samples.Open(c.GetProperty("right").GetString()!);
        var ids = Strings(c.GetProperty("filters"));
        var filters = DiffEngine.Filters.Where(f => ids.Contains(f.Id)).ToList();
        Assert.Equal(ids.Count, filters.Count);

        var result = DiffEngine.Compare(a.Result.Tree, b.Result.Tree, filters);

        var stats = c.GetProperty("stats");
        Assert.Equal(
            new DiffStats(stats.GetProperty("changed").GetInt32(), stats.GetProperty("added").GetInt32(), stats.GetProperty("removed").GetInt32(), stats.GetProperty("ignored").GetInt32()),
            result.Stats);
        Assert.Equal(
            Strings(c.GetProperty("differences")),
            DiffEngine.Differences(result.Root).Select(d => $"{d.Status.ToString().ToLowerInvariant()} {d.PathText}"));
    }
}
