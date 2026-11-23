namespace BinScope.Core.Model;

/// <summary>Decoded content of a .plcbin file (see FORMAT.md in the repository root).</summary>
public sealed class PlcBinModel
{
    public int Version { get; set; } = 3;
    public ProjectRec Project { get; set; } = new();
    public List<DeviceRec> Devices { get; } = [];
    public List<ProgramRec> Programs { get; } = [];
    public List<BlockRec> Blocks { get; } = [];
    public List<TagTableRec> Tags { get; } = [];
    public List<HmiRec> Hmis { get; } = [];
}

public sealed class ProjectRec
{
    public string Name { get; set; } = "";
    public string Producer { get; set; } = "";
    /// <summary>Unix seconds (v3).</summary>
    public uint Timestamp { get; set; }
    /// <summary>16 bytes (v3).</summary>
    public byte[] BuildId { get; set; } = new byte[16];
}

public sealed class DeviceRec
{
    public string Name { get; set; } = "";
    public string TypeId { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Mask { get; set; } = "";
    public string Controller { get; set; } = "";
    public int CycleMs { get; set; }
    public List<ModuleRec> Modules { get; } = [];
}

public sealed class ModuleRec
{
    public string TypeId { get; set; } = "";
    public int Slot { get; set; }
    public int Address { get; set; }
}

public sealed record IoAddress(int Area, int Byte, int Bit, bool Word);

public sealed class SymbolRec
{
    public string Name { get; set; } = "";
    /// <summary>0 BOOL, 1 INT, 2 REAL.</summary>
    public int Type { get; set; }
    /// <summary>Data slot, or null when the symbol is an I/O address.</summary>
    public int? Slot { get; set; }
    public IoAddress? Address { get; set; }
}

public sealed class ProgramRec
{
    public string Plc { get; set; } = "";
    public List<int> Code { get; } = [];
    public List<double> Consts { get; } = [];
    public List<double> SlotInit { get; } = [];
    public List<SymbolRec> Symbols { get; } = [];
    public uint Main { get; set; }
    public int Startup { get; set; } = -1;
}

public sealed class BlockRec
{
    public string Plc { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>0 OB, 1 FC, 2 DB.</summary>
    public int Kind { get; set; }
    public int Number { get; set; }
    public string Source { get; set; } = "";
}

public sealed class TagRec
{
    public string Name { get; set; } = "";
    public int Type { get; set; }
    public string Address { get; set; } = "";
    public string Comment { get; set; } = "";
}

public sealed class TagTableRec
{
    public string Plc { get; set; } = "";
    public List<TagRec> Tags { get; } = [];
}

public sealed class HmiElementRec
{
    /// <summary>0 button, 1 switch, 2 lamp, 3 value, 4 slider.</summary>
    public int Kind { get; set; }
    public string Label { get; set; } = "";
    public string Tag { get; set; } = "";
}

public sealed class HmiRec
{
    public string Name { get; set; } = "";
    public string Controller { get; set; } = "";
    public List<HmiElementRec> Elements { get; } = [];
}

public static class Names
{
    public const int SectionProject = 1, SectionDevice = 2, SectionProgram = 3, SectionBlock = 4, SectionTags = 5, SectionHmi = 6;

    public static readonly IReadOnlyDictionary<int, string> Section = new Dictionary<int, string>
    {
        [1] = "Project", [2] = "Device", [3] = "Program", [4] = "Block", [5] = "Tags", [6] = "HMI",
    };

    public static readonly string[] Type = ["BOOL", "INT", "REAL"];
    public static readonly string[] BlockKind = ["OB", "FC", "DB"];
    public static readonly string[] HmiKind = ["button", "switch", "lamp", "value", "slider"];
    public static readonly string[] Area = ["I", "Q", "M"];

    public static string At(string[] names, int index) => index >= 0 && index < names.Length ? names[index] : $"?{index}";
}
