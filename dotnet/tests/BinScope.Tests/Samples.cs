using System.Text.Json;
using BinScope.Core.Plugins;

namespace BinScope.Tests;

/// <summary>Access to the shared sample files (repository root /samples) and the reference results in expected.json.</summary>
internal static class Samples
{
    public static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "samples");

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Dir, name));

    public static LoadedFile Open(string name, PluginRegistry? registry = null) => (registry ?? new PluginRegistry()).Load(Read(name), name);

    private static readonly Lazy<JsonElement> ExpectedJson = new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "expected.json"))).RootElement);

    public static JsonElement Expected => ExpectedJson.Value;
}
