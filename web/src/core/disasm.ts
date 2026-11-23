/** Disassembler for PLC Studio bytecode (instruction set of the PLC Studio compiler). */
import { AREA_NAME, ProgramRec } from './model';

/** Opcode names and operand counts, in opcode order. */
export const OPCODES: [string, number][] = [
  ['RET', 0], ['PUSH', 1], ['LOAD', 1], ['STORE', 1], ['LDBIT', 3], ['STBIT', 3], ['LDW', 2], ['STW', 2],
  ['IADD', 0], ['ISUB', 0], ['IMUL', 0], ['IDIV', 0], ['IMOD', 0], ['FADD', 0], ['FSUB', 0], ['FMUL', 0], ['FDIV', 0],
  ['INEG', 0], ['FNEG', 0], ['NOT', 0], ['AND', 0], ['OR', 0], ['XOR', 0], ['EQ', 0], ['NE', 0], ['LT', 0], ['LE', 0], ['GT', 0], ['GE', 0],
  ['JMP', 1], ['JZ', 1], ['CALL', 1], ['POP', 0], ['I2F', 0], ['F2I', 0], ['TON', 1], ['RTRIG', 1], ['FTRIG', 1],
  ['IABS', 0], ['FABS', 0], ['MIN', 0], ['MAX', 0], ['LIMIT', 0], ['CVT', 1],
];

export interface Instruction {
  pc: number;
  /** Words used by the instruction (opcode + operands). */
  size: number;
  text: string;
}

export function disassemble(p: ProgramRec): Instruction[] {
  const slotName = new Map<number, string>();
  for (const s of p.symbols) if (s.slot !== undefined) slotName.set(s.slot, s.name);
  const out: Instruction[] = [];
  let pc = 0;
  while (pc < p.code.length) {
    const op = p.code[pc];
    const def = OPCODES[op];
    if (!def) {
      out.push({ pc, size: 1, text: `??? ${op}` });
      pc++;
      continue;
    }
    const [name, arity] = def;
    const a = p.code.slice(pc + 1, pc + 1 + arity);
    let args = '';
    switch (name) {
      case 'PUSH':
        args = `${p.consts[a[0]] ?? `c${a[0]}`}`;
        break;
      case 'LOAD':
      case 'STORE':
        args = slotName.get(a[0]) ?? `#${a[0]}`;
        break;
      case 'LDBIT':
      case 'STBIT':
        args = `%${AREA_NAME[a[0]] ?? '?'}${a[1]}.${a[2]}`;
        break;
      case 'LDW':
      case 'STW':
        args = `%${AREA_NAME[a[0]] ?? '?'}W${a[1]}`;
        break;
      case 'JMP':
      case 'JZ':
      case 'CALL':
        args = `→ ${String(a[0]).padStart(4, '0')}`;
        break;
      case 'TON':
      case 'RTRIG':
      case 'FTRIG':
        args = `#${a[0]}`;
        break;
      case 'CVT':
        args = `depth ${a[0]}`;
        break;
    }
    out.push({ pc, size: 1 + arity, text: `${name}${args ? ` ${args}` : ''}` });
    pc += 1 + arity;
  }
  return out;
}
