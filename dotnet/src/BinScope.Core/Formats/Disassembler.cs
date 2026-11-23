using System.Globalization;
using BinScope.Core.Model;

namespace BinScope.Core.Formats;

/// <param name="Size">Words used by the instruction (opcode + operands).</param>
public sealed record Instruction(int Pc, int Size, string Text);

/// <summary>Disassembler for PLC Studio bytecode.</summary>
public static class Disassembler
{
    /// <summary>Opcode names and operand counts, in opcode order.</summary>
    public static readonly (string Name, int Arity)[] Opcodes =
    [
        ("RET", 0), ("PUSH", 1), ("LOAD", 1), ("STORE", 1), ("LDBIT", 3), ("STBIT", 3), ("LDW", 2), ("STW", 2),
        ("IADD", 0), ("ISUB", 0), ("IMUL", 0), ("IDIV", 0), ("IMOD", 0), ("FADD", 0), ("FSUB", 0), ("FMUL", 0), ("FDIV", 0),
        ("INEG", 0), ("FNEG", 0), ("NOT", 0), ("AND", 0), ("OR", 0), ("XOR", 0), ("EQ", 0), ("NE", 0), ("LT", 0), ("LE", 0), ("GT", 0), ("GE", 0),
        ("JMP", 1), ("JZ", 1), ("CALL", 1), ("POP", 0), ("I2F", 0), ("F2I", 0), ("TON", 1), ("RTRIG", 1), ("FTRIG", 1),
        ("IABS", 0), ("FABS", 0), ("MIN", 0), ("MAX", 0), ("LIMIT", 0), ("CVT", 1),
    ];

    public static List<Instruction> Disassemble(ProgramRec p)
    {
        var slotName = new Dictionary<int, string>();
        foreach (var s in p.Symbols)
            if (s.Slot is int slot) slotName[slot] = s.Name;

        var output = new List<Instruction>();
        var pc = 0;
        while (pc < p.Code.Count)
        {
            var op = p.Code[pc];
            if (op < 0 || op >= Opcodes.Length)
            {
                output.Add(new Instruction(pc, 1, $"??? {op}"));
                pc++;
                continue;
            }
            var (name, arity) = Opcodes[op];
            int Arg(int i) => pc + 1 + i < p.Code.Count ? p.Code[pc + 1 + i] : 0;
            var args = name switch
            {
                "PUSH" => Arg(0) >= 0 && Arg(0) < p.Consts.Count ? PlcBinFormat.Num(p.Consts[Arg(0)]) : $"c{Arg(0)}",
                "LOAD" or "STORE" => slotName.TryGetValue(Arg(0), out var n) ? n : $"#{Arg(0)}",
                "LDBIT" or "STBIT" => $"%{Area(Arg(0))}{Arg(1)}.{Arg(2)}",
                "LDW" or "STW" => $"%{Area(Arg(0))}W{Arg(1)}",
                "JMP" or "JZ" or "CALL" => $"→ {Arg(0).ToString("D4", CultureInfo.InvariantCulture)}",
                "TON" or "RTRIG" or "FTRIG" => $"#{Arg(0)}",
                "CVT" => $"depth {Arg(0)}",
                _ => "",
            };
            output.Add(new Instruction(pc, 1 + arity, args.Length > 0 ? $"{name} {args}" : name));
            pc += 1 + arity;
        }
        return output;
    }

    private static string Area(int a) => a >= 0 && a < Names.Area.Length ? Names.Area[a] : "?";
}
