using System.Globalization;
using BinScope.Core.Binary;
using BinScope.Core.Model;

namespace BinScope.Core.Formats;

public sealed record ParseOptions(bool Detailed, bool Editable);

public sealed record CrcCheck(uint Stored, uint Computed)
{
    public bool Valid => Stored == Computed;
}

public sealed record ParseResult(PlcBinModel Model, TreeNode Tree, IReadOnlyList<string> Warnings, CrcCheck? Crc);

/// <summary>Reading and writing .plcbin files, format versions 1-3 (see FORMAT.md).</summary>
public static class PlcBinFormat
{
    public const string Magic = "PLCB";

    /// <summary>Number formatting that matches JavaScript's String(number) for the values in these files.</summary>
    public static string Num(double v) => v == 0 ? "0" : v.ToString(CultureInfo.InvariantCulture);

    private static string Int(long v) => v.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ writing

    public static byte[] Write(PlcBinModel m)
    {
        var v = m.Version;
        var sections = new List<(int Type, Action<ByteWriter> Write)>
        {
            (Names.SectionProject, w =>
            {
                w.Str(m.Project.Name);
                if (v >= 2) w.Str(m.Project.Producer);
                if (v >= 3)
                {
                    w.U32(m.Project.Timestamp);
                    var id = new byte[16];
                    m.Project.BuildId.AsSpan(0, Math.Min(16, m.Project.BuildId.Length)).CopyTo(id);
                    w.Bytes(id);
                }
            }),
        };
        foreach (var d in m.Devices) sections.Add((Names.SectionDevice, w => WriteDevice(w, d, v)));
        foreach (var p in m.Programs) sections.Add((Names.SectionProgram, w => WriteProgram(w, p)));
        foreach (var b in m.Blocks) sections.Add((Names.SectionBlock, w => w.Str(b.Plc).Str(b.Name).U8(b.Kind).U16(b.Number).LongStr(b.Source)));
        foreach (var t in m.Tags)
        {
            sections.Add((Names.SectionTags, w =>
            {
                w.Str(t.Plc).U16(t.Tags.Count);
                foreach (var tag in t.Tags) w.Str(tag.Name).U8(tag.Type).Str(tag.Address).Str(tag.Comment);
            }));
        }
        if (v >= 2)
        {
            foreach (var h in m.Hmis)
            {
                sections.Add((Names.SectionHmi, w =>
                {
                    w.Str(h.Name).Str(h.Controller).U8(h.Elements.Count);
                    foreach (var e in h.Elements) w.U8(e.Kind).Str(e.Label).Str(e.Tag);
                }));
            }
        }

        var writer = new ByteWriter();
        foreach (var c in Magic) writer.U8(c);
        writer.U16(v).U16(sections.Count);
        var table = writer.Length;
        foreach (var s in sections) writer.U16(s.Type).U32(0).U32(0);
        for (var i = 0; i < sections.Count; i++)
        {
            var start = writer.Length;
            sections[i].Write(writer);
            writer.PatchU32(table + i * 10 + 2, (uint)start);
            writer.PatchU32(table + i * 10 + 6, (uint)(writer.Length - start));
        }
        if (v >= 3) writer.U32(Crc32.Compute(writer.ToArray()));
        return writer.ToArray();
    }

    private static void WriteDevice(ByteWriter w, DeviceRec d, int v)
    {
        w.Str(d.Name).Str(d.TypeId);
        if (v == 1) w.Str(d.Ip).Str(d.Mask);
        else w.Bytes(IpToBytes(d.Ip)).Bytes(IpToBytes(d.Mask));
        w.Str(d.Controller).U16(d.CycleMs).U8(d.Modules.Count);
        foreach (var mod in d.Modules) w.Str(mod.TypeId).U8(mod.Slot).U16(mod.Address);
    }

    private static void WriteProgram(ByteWriter w, ProgramRec p)
    {
        w.Str(p.Plc).U32((uint)p.Code.Count);
        foreach (var c in p.Code) w.I32(c);
        w.U16(p.Consts.Count);
        foreach (var c in p.Consts) w.F64(c);
        w.U16(p.SlotInit.Count);
        foreach (var c in p.SlotInit) w.F64(c);
        w.U16(p.Symbols.Count);
        foreach (var s in p.Symbols)
        {
            w.Str(s.Name).U8(s.Type);
            if (s.Slot is int slot) w.U8(0).U16(slot);
            else
            {
                var a = s.Address ?? throw new InvalidOperationException($"Symbol {s.Name} has neither a slot nor an address");
                w.U8(1).U8(a.Area).U16(a.Byte).U8(a.Bit).U8(a.Word ? 1 : 0);
            }
        }
        w.U32(p.Main).I32(p.Startup);
    }

    public static byte[] IpToBytes(string ip)
    {
        var parts = ip.Split('.');
        var result = new byte[4];
        if (parts.Length != 4) return result;
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 255) return new byte[4];
            result[i] = (byte)n;
        }
        return result;
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Format version from the header, or null when the file is not a .plcbin.</summary>
    public static int? PeekVersion(byte[] bytes)
    {
        if (bytes.Length < 8) return null;
        for (var i = 0; i < 4; i++) if (bytes[i] != Magic[i]) return null;
        return bytes[4] | (bytes[5] << 8);
    }

    public static ParseResult Parse(byte[] bytes, ParseOptions opts) => new Parser(bytes, opts).Run();

    private sealed class Parser(byte[] bytes, ParseOptions opts)
    {
        private readonly PlcBinModel _model = new();
        private readonly List<string> _warnings = [];
        private int _version;

        private readonly TreeNode _project = TreeNode.Group("project", "Project");
        private readonly TreeNode _devices = TreeNode.Group("devices", "Devices");
        private readonly TreeNode _programs = TreeNode.Group("programs", "Programs");
        private readonly TreeNode _blocks = TreeNode.Group("blocks", "Blocks");
        private readonly TreeNode _tags = TreeNode.Group("tags", "Tags");
        private readonly TreeNode _hmi = TreeNode.Group("hmi", "HMI panels");

        private NodeEdit? Editable(string hint, Func<string, string?> apply) => opts.Editable ? new NodeEdit(hint, apply) : null;

        /// <summary>Reads one value and records it as a leaf with its byte range.</summary>
        private static T Field<T>(ByteReader r, TreeNode parent, string key, string label, Func<T> read, Func<T, string>? show = null, NodeEdit? edit = null, bool text = false)
        {
            var offset = r.Pos;
            var v = read();
            var leaf = TreeNode.Leaf(key, label, show is null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : show(v), offset, r.Pos - offset);
            leaf.Edit = edit;
            leaf.IsText = text;
            parent.Add(leaf);
            return v;
        }

        public ParseResult Run()
        {
            var r = new ByteReader(bytes);
            var header = TreeNode.Group("header", "Header", 0);
            var magic = Field(r, header, "magic", "Magic", () => new string(r.Take(4).Select(b => (char)b).ToArray()));
            if (magic != Magic) throw new BinaryFormatException($"Not a .plcbin file (magic \"{magic}\")", 0);
            _version = Field(r, header, "version", "Format version", r.U16);
            if (_version is < 1 or > 3) throw new BinaryFormatException($"Unknown format version {_version}", 4);
            _model.Version = _version;
            var count = Field(r, header, "sections", "Section count", r.U16);
            var tableNode = TreeNode.Group("table", "Section table", r.Pos);
            tableNode.Length = count * 10;
            var table = new List<(int Type, uint Offset, uint Length)>();
            for (var i = 0; i < count; i++)
            {
                var at = r.Pos;
                var entry = (Type: r.U16(), Offset: r.U32(), Length: r.U32());
                table.Add(entry);
                var name = Names.Section.TryGetValue(entry.Type, out var n) ? n : $"type {entry.Type}";
                tableNode.Add(TreeNode.Leaf($"#{i}", $"#{i}", $"{name} @ {entry.Offset}, {entry.Length} bytes", at, 10));
            }
            header.Add(tableNode);
            header.Length = r.Pos;

            var bodyEnd = _version >= 3 ? bytes.Length - 4 : bytes.Length;
            for (var i = 0; i < table.Count; i++)
            {
                var s = table[i];
                if (s.Offset < 8 + count * 10 || (long)s.Offset + s.Length > bodyEnd)
                    throw new BinaryFormatException($"Section #{i} points outside the file", 8 + i * 10);
                var sectionEnd = (int)(s.Offset + s.Length);
                var sub = new ByteReader(bytes, sectionEnd) { Pos = (int)s.Offset };
                ReadSection(s.Type, sub, i);
                if (sub.Pos != sectionEnd)
                {
                    var name = Names.Section.TryGetValue(s.Type, out var n) ? n : Int(s.Type);
                    _warnings.Add($"Section #{i} ({name}) has {sectionEnd - sub.Pos} unread byte(s).");
                }
            }

            var children = new List<TreeNode> { header, _project, _devices, _programs, _blocks, _tags };
            if (_version >= 2) children.Add(_hmi);
            CrcCheck? crc = null;
            if (_version >= 3)
            {
                if (bytes.Length < 4) throw new BinaryFormatException("Missing CRC trailer", bytes.Length);
                var stored = BitConverter.ToUInt32(bytes, bytes.Length - 4);
                if (!BitConverter.IsLittleEndian) stored = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(stored);
                var computed = Crc32.Compute(bytes.AsSpan(0, bytes.Length - 4));
                crc = new CrcCheck(stored, computed);
                if (!crc.Valid)
                    _warnings.Add($"CRC mismatch: stored {stored:x}, computed {computed:x} - the file was modified or damaged.");
                var trailer = TreeNode.Group("trailer", "Trailer", bytes.Length - 4);
                trailer.Length = 4;
                trailer.Add(TreeNode.Leaf("crc", "CRC-32", Hex.U32(stored), bytes.Length - 4, 4));
                trailer.Add(TreeNode.Leaf("valid", "Checksum", crc.Valid ? "valid ✔" : $"INVALID ✘ (expected {Hex.U32(computed)})"));
                children.Add(trailer);
            }
            foreach (var c in children)
                if (c.Children is not null && c.Value is null && c != header)
                    c.Value = c.Key == "project" ? _model.Project.Name : Int(c.Children.Count);

            var root = new TreeNode("root", $".plcbin v{_version}") { Children = children, Offset = 0, Length = bytes.Length, Value = $"{bytes.Length} bytes" };
            return new ParseResult(_model, root, _warnings, crc);
        }

        private void ReadSection(int type, ByteReader r, int index)
        {
            var start = r.Pos;
            switch (type)
            {
                case Names.SectionProject: ReadProject(r, start); break;
                case Names.SectionDevice: ReadDevice(r, start); break;
                case Names.SectionProgram: ReadProgram(r, start); break;
                case Names.SectionBlock: ReadBlock(r, start); break;
                case Names.SectionTags: ReadTags(r, start); break;
                case Names.SectionHmi: ReadHmi(r, start); break;
                default:
                    _warnings.Add($"Section #{index} has unknown type {type} - skipped.");
                    r.Pos = r.End;
                    break;
            }
        }

        private void ReadProject(ByteReader r, int start)
        {
            var p = _model.Project;
            _project.Offset = start;
            p.Name = Field(r, _project, "name", "Name", r.Str, edit: Editable("text", v => { p.Name = v; return null; }));
            if (_version >= 2) p.Producer = Field(r, _project, "producer", "Producer", r.Str);
            if (_version >= 3)
            {
                p.Timestamp = Field(r, _project, "timestamp", "Build time", r.U32,
                    t => DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
                p.BuildId = Field(r, _project, "buildId", "Build ID", () => r.Take(16), b => Hex.Of(b));
            }
            _project.Length = r.Pos - start;
        }

        private void ReadDevice(ByteReader r, int start)
        {
            var d = new DeviceRec();
            var n = TreeNode.Group("?", "?", start);
            d.Name = Field(r, n, "name", "Name", r.Str);
            n.Key = n.Label = d.Name;
            d.TypeId = Field(r, n, "type", "Type", r.Str);
            Func<string> readIp = _version == 1 ? () => r.Str() : () => string.Join('.', r.Take(4));
            d.Ip = Field(r, n, "ip", "IP address", readIp, edit: Editable("ip", v => SetIp(v, x => d.Ip = x)));
            d.Mask = Field(r, n, "mask", "Subnet mask", readIp, edit: Editable("ip", v => SetIp(v, x => d.Mask = x)));
            d.Controller = Field(r, n, "controller", "Controller", r.Str, c => c.Length > 0 ? c : "–");
            d.CycleMs = Field(r, n, "cycle", "Cycle time (ms)", r.U16, edit: Editable("number", v => SetInt(v, 1, 65535, x => d.CycleMs = x)));
            var mods = TreeNode.Group("modules", "Modules", r.Pos);
            var count = r.U8();
            for (var i = 0; i < count; i++)
            {
                var mStart = r.Pos;
                var mn = TreeNode.Group("?", "?", mStart);
                var mod = new ModuleRec();
                mod.TypeId = Field(r, mn, "type", "Type", r.Str);
                mod.Slot = Field(r, mn, "slot", "Slot", r.U8);
                mod.Address = Field(r, mn, "address", "Start byte", r.U16, edit: Editable("number", v => SetInt(v, 0, 250, x => mod.Address = x)));
                mn.Key = $"slot{mod.Slot}";
                mn.Label = $"Slot {mod.Slot}";
                mn.Value = mod.TypeId;
                mn.Length = r.Pos - mStart;
                mods.Add(mn);
                d.Modules.Add(mod);
            }
            mods.Length = r.Pos - mods.Offset!.Value;
            n.Add(mods);
            n.Length = r.Pos - start;
            _devices.Add(n);
            _model.Devices.Add(d);
        }

        private void ReadProgram(ByteReader r, int start)
        {
            var p = new ProgramRec();
            var n = TreeNode.Group("?", "?", start);
            p.Plc = Field(r, n, "plc", "Controller", r.Str);
            n.Key = n.Label = p.Plc;

            var codeAt = r.Pos;
            var len = r.U32();
            for (var i = 0; i < len; i++) p.Code.Add(r.I32());
            var codeCrc = Crc32.Compute(bytes.AsSpan(codeAt + 4, r.Pos - codeAt - 4));
            var codeNode = TreeNode.Group("code", "Code", codeAt);
            codeNode.Length = r.Pos - codeAt;
            codeNode.Value = $"{len} words, CRC {Hex.U32(codeCrc)}";
            n.Add(codeNode);

            var constAt = r.Pos;
            var cc = r.U16();
            for (var i = 0; i < cc; i++) p.Consts.Add(r.F64());
            var constNode = TreeNode.Group("consts", "Constants", constAt);
            constNode.Length = r.Pos - constAt;
            constNode.Value = Int(cc);

            var initAt = r.Pos;
            var ic = r.U16();
            for (var i = 0; i < ic; i++) p.SlotInit.Add(r.F64());
            n.Add(TreeNode.Leaf("slots", "Data slots", Int(ic), initAt, r.Pos - initAt));

            var symAt = r.Pos;
            var sc = r.U16();
            var symNode = TreeNode.Group("symbols", "Symbols", symAt);
            symNode.Value = Int(sc);
            for (var i = 0; i < sc; i++)
            {
                var sStart = r.Pos;
                var s = new SymbolRec { Name = r.Str(), Type = r.U8() };
                if (r.U8() == 0) s.Slot = r.U16();
                else s.Address = new IoAddress(r.U8(), r.U16(), r.U8(), r.U8() == 1);
                p.Symbols.Add(s);
                var where = s.Slot is int slot
                    ? $"slot #{slot}"
                    : $"%{Names.At(Names.Area, s.Address!.Area)}{(s.Address.Word ? $"W{s.Address.Byte}" : $"{s.Address.Byte}.{s.Address.Bit}")}";
                symNode.Add(TreeNode.Leaf(s.Name, s.Name, $"{Names.At(Names.Type, s.Type)} · {where}", sStart, r.Pos - sStart));
            }
            symNode.Length = r.Pos - symAt;
            p.Main = Field(r, n, "main", "Entry: Main", r.U32);
            p.Startup = Field(r, n, "startup", "Entry: Startup", r.I32, x => x < 0 ? "–" : Int(x));

            if (opts.Detailed)
            {
                foreach (var ins in Disassembler.Disassemble(p))
                    codeNode.Add(TreeNode.Leaf($"pc{ins.Pc}", ins.Pc.ToString("D4", CultureInfo.InvariantCulture), ins.Text, codeAt + 4 + ins.Pc * 4, ins.Size * 4));
                for (var i = 0; i < p.Consts.Count; i++)
                    constNode.Add(TreeNode.Leaf($"c{i}", $"c{i}", Num(p.Consts[i]), constAt + 2 + i * 8, 8));
                n.Add(constNode);
                n.Add(symNode);
            }
            else
            {
                n.Add(constNode);
            }
            n.Length = r.Pos - start;
            _programs.Add(n);
            _model.Programs.Add(p);
        }

        private void ReadBlock(ByteReader r, int start)
        {
            var b = new BlockRec();
            var n = TreeNode.Group("?", "?", start);
            b.Plc = Field(r, n, "plc", "Controller", r.Str);
            b.Name = Field(r, n, "name", "Name", r.Str);
            b.Kind = Field(r, n, "kind", "Kind", r.U8, k => Names.At(Names.BlockKind, k));
            b.Number = Field(r, n, "number", "Number", r.U16, edit: Editable("number", v => SetInt(v, 1, 65535, x => b.Number = x)));
            b.Source = Field(r, n, "source", "Source", r.LongStr, s => s, Editable("source", v => { b.Source = v; return null; }), text: true);
            n.Key = $"{b.Plc}.{b.Name}";
            n.Label = $"{b.Name} [{(b.Kind >= 0 && b.Kind < Names.BlockKind.Length ? Names.BlockKind[b.Kind] : "?")}{b.Number}]";
            n.Value = b.Plc;
            n.Length = r.Pos - start;
            _blocks.Add(n);
            _model.Blocks.Add(b);
        }

        private void ReadTags(ByteReader r, int start)
        {
            var t = new TagTableRec();
            var n = TreeNode.Group("?", "?", start);
            t.Plc = r.Str();
            n.Key = n.Label = t.Plc;
            var count = r.U16();
            for (var i = 0; i < count; i++)
            {
                var tStart = r.Pos;
                var tag = new TagRec();
                var tn = TreeNode.Group("?", "?", tStart);
                tag.Name = Field(r, tn, "name", "Name", r.Str);
                tag.Type = Field(r, tn, "type", "Type", r.U8, x => Names.At(Names.Type, x));
                tag.Address = Field(r, tn, "address", "Address", r.Str, edit: Editable("text", v => { tag.Address = v.ToUpperInvariant(); return null; }));
                tag.Comment = Field(r, tn, "comment", "Comment", r.Str, edit: Editable("text", v => { tag.Comment = v; return null; }));
                tn.Key = tn.Label = tag.Name;
                tn.Value = tag.Address;
                tn.Length = r.Pos - tStart;
                n.Add(tn);
                t.Tags.Add(tag);
            }
            n.Length = r.Pos - start;
            _tags.Add(n);
            _model.Tags.Add(t);
        }

        private void ReadHmi(ByteReader r, int start)
        {
            var h = new HmiRec();
            var n = TreeNode.Group("?", "?", start);
            h.Name = Field(r, n, "name", "Name", r.Str);
            n.Key = n.Label = h.Name;
            h.Controller = Field(r, n, "controller", "Controller", r.Str);
            var count = r.U8();
            for (var i = 0; i < count; i++)
            {
                var eStart = r.Pos;
                var el = new HmiElementRec();
                var en = TreeNode.Group($"#{i}", $"Element {i + 1}", eStart);
                el.Kind = Field(r, en, "kind", "Kind", r.U8, k => Names.At(Names.HmiKind, k));
                el.Label = Field(r, en, "label", "Label", r.Str, edit: Editable("text", v => { el.Label = v; return null; }));
                el.Tag = Field(r, en, "tag", "Tag", r.Str, edit: Editable("text", v => { el.Tag = v; return null; }));
                en.Value = $"{Names.At(Names.HmiKind, el.Kind)} \"{el.Label}\" → {el.Tag}";
                en.Length = r.Pos - eStart;
                n.Add(en);
                h.Elements.Add(el);
            }
            n.Length = r.Pos - start;
            _hmi.Add(n);
            _model.Hmis.Add(h);
        }
    }

    private static string? SetIp(string value, Action<string> set)
    {
        var parts = value.Trim().Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit) || int.Parse(p, CultureInfo.InvariantCulture) > 255))
            return $"\"{value}\" is not an IPv4 address";
        set(string.Join('.', parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture))));
        return null;
    }

    private static string? SetInt(string value, int min, int max, Action<int> set)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
            return $"Enter a whole number between {min} and {max}";
        set(n);
        return null;
    }
}
