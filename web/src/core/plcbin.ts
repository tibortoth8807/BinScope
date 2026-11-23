/** Reading and writing .plcbin files (see FORMAT.md). */
import { ByteReader, ByteWriter, FormatError, crc32, hex, ipToBytes } from './bytes';
import { disassemble } from './disasm';
import {
  AREA_NAME, BLOCK_KIND, DeviceRec, FormatVersion, HMI_KIND, PlcBinModel, ProgramRec, SECTION, SECTION_NAME, SymbolRec, TYPE_NAME,
  TreeNode, leaf, node,
} from './model';

export const MAGIC = 'PLCB';

// ------------------------------------------------------------------ writing

export function writePlcBin(m: PlcBinModel): Uint8Array {
  const v = m.version;
  const sections: [number, (w: ByteWriter) => void][] = [];
  sections.push([SECTION.Project, (w) => {
    w.str(m.project.name);
    if (v >= 2) w.str(m.project.producer);
    if (v >= 3) {
      w.u32(m.project.timestamp);
      const id = new Uint8Array(16);
      id.set(m.project.buildId.subarray(0, 16));
      w.bytes(id);
    }
  }]);
  for (const d of m.devices) sections.push([SECTION.Device, (w) => writeDevice(w, d, v)]);
  for (const p of m.programs) sections.push([SECTION.Program, (w) => writeProgram(w, p)]);
  for (const b of m.blocks) sections.push([SECTION.Block, (w) => w.str(b.plc).str(b.name).u8(b.kind).u16(b.number).longStr(b.source)]);
  for (const t of m.tags) {
    sections.push([SECTION.Tags, (w) => {
      w.str(t.plc).u16(t.tags.length);
      for (const tag of t.tags) w.str(tag.name).u8(tag.type).str(tag.address).str(tag.comment);
    }]);
  }
  if (v >= 2) {
    for (const h of m.hmis) {
      sections.push([SECTION.Hmi, (w) => {
        w.str(h.name).str(h.controller).u8(h.elements.length);
        for (const e of h.elements) w.u8(e.kind).str(e.label).str(e.tag);
      }]);
    }
  }

  const w = new ByteWriter();
  for (const c of MAGIC) w.u8(c.charCodeAt(0));
  w.u16(v).u16(sections.length);
  const table = w.length;
  for (let i = 0; i < sections.length; i++) w.u16(sections[i][0]).u32(0).u32(0);
  sections.forEach(([, write], i) => {
    const start = w.length;
    write(w);
    w.patchU32(table + i * 10 + 2, start);
    w.patchU32(table + i * 10 + 6, w.length - start);
  });
  if (v >= 3) {
    const body = w.toBytes();
    w.u32(crc32(body));
  }
  return w.toBytes();
}

function writeDevice(w: ByteWriter, d: DeviceRec, v: number): void {
  w.str(d.name).str(d.typeId);
  if (v === 1) w.str(d.ip).str(d.mask);
  else {
    for (const b of ipToBytes(d.ip)) w.u8(b);
    for (const b of ipToBytes(d.mask)) w.u8(b);
  }
  w.str(d.controller).u16(d.cycleMs).u8(d.modules.length);
  for (const m of d.modules) w.str(m.typeId).u8(m.slot).u16(m.address);
}

function writeProgram(w: ByteWriter, p: ProgramRec): void {
  w.str(p.plc).u32(p.code.length);
  for (const c of p.code) w.i32(c);
  w.u16(p.consts.length);
  for (const c of p.consts) w.f64(c);
  w.u16(p.slotInit.length);
  for (const c of p.slotInit) w.f64(c);
  w.u16(p.symbols.length);
  for (const s of p.symbols) {
    w.str(s.name).u8(s.type);
    if (s.slot !== undefined) w.u8(0).u16(s.slot);
    else w.u8(1).u8(s.address!.area).u16(s.address!.byte).u8(s.address!.bit).u8(s.address!.word ? 1 : 0);
  }
  w.u32(p.main).i32(p.startup);
}

// ------------------------------------------------------------------ reading

export interface ParseOptions {
  /** Add disassembly, symbol details and constants. */
  detailed: boolean;
  /** Attach edit handlers to editable leaves. */
  editable: boolean;
}

export interface ParseResult {
  model: PlcBinModel;
  tree: TreeNode;
  warnings: string[];
  crc?: { stored: number; computed: number };
}

export function peekVersion(bytes: Uint8Array): number | null {
  if (bytes.length < 8) return null;
  for (let i = 0; i < 4; i++) if (bytes[i] !== MAGIC.charCodeAt(i)) return null;
  return bytes[4] | (bytes[5] << 8);
}

export function parsePlcBin(bytes: Uint8Array, opts: ParseOptions): ParseResult {
  const r = new ByteReader(bytes);
  const warnings: string[] = [];
  const model: PlcBinModel = {
    version: 1,
    project: { name: '', producer: '', timestamp: 0, buildId: new Uint8Array(16) },
    devices: [], programs: [], blocks: [], tags: [], hmis: [],
  };

  /** Reads one value and records it as a leaf with its byte range. */
  const field = <T,>(parent: TreeNode, key: string, label: string, read: () => T, show: (v: T) => string = String, extra: Partial<TreeNode> = {}): T => {
    const offset = r.pos;
    const v = read();
    parent.children!.push(leaf(key, label, show(v), { offset, length: r.pos - offset, ...extra }));
    return v;
  };
  const edit = (hint: string, apply: (value: string) => string | null) => (opts.editable ? { edit: { hint, apply } } : {});

  // header
  const header = node('header', 'Header', [], { offset: 0 });
  const magic = field(header, 'magic', 'Magic', () => String.fromCharCode(...r.take(4)));
  if (magic !== MAGIC) throw new FormatError(`Not a .plcbin file (magic "${magic}")`, 0);
  const version = field(header, 'version', 'Format version', () => r.u16());
  if (version < 1 || version > 3) throw new FormatError(`Unknown format version ${version}`, 4);
  model.version = version as FormatVersion;
  const count = field(header, 'sections', 'Section count', () => r.u16());
  const tableNode = node('table', 'Section table', [], { offset: r.pos, length: count * 10 });
  const table: { type: number; offset: number; length: number }[] = [];
  for (let i = 0; i < count; i++) {
    const at = r.pos;
    const entry = { type: r.u16(), offset: r.u32(), length: r.u32() };
    table.push(entry);
    tableNode.children!.push(leaf(`#${i}`, `#${i}`, `${SECTION_NAME[entry.type] ?? `type ${entry.type}`} @ ${entry.offset}, ${entry.length} bytes`, { offset: at, length: 10 }));
  }
  header.children!.push(tableNode);
  header.length = r.pos;

  const projectNode = node('project', 'Project');
  const devicesNode = node('devices', 'Devices');
  const programsNode = node('programs', 'Programs');
  const blocksNode = node('blocks', 'Blocks');
  const tagsNode = node('tags', 'Tags');
  const hmiNode = node('hmi', 'HMI panels');

  const bodyEnd = version >= 3 ? bytes.length - 4 : bytes.length;
  for (const [i, s] of table.entries()) {
    if (s.offset < 8 + count * 10 || s.offset + s.length > bodyEnd) {
      throw new FormatError(`Section #${i} points outside the file`, 8 + i * 10);
    }
    const sectionEnd = s.offset + s.length;
    const sub = new ByteReader(bytes, sectionEnd);
    sub.pos = s.offset;
    readSection(s.type, sub, i);
    if (sub.pos !== sectionEnd) warnings.push(`Section #${i} (${SECTION_NAME[s.type] ?? s.type}) has ${sectionEnd - sub.pos} unread byte(s).`);
  }

  function readSection(type: number, rr: ByteReader, index: number): void {
    // route `field` to this section's reader
    const f = <T,>(parent: TreeNode, key: string, label: string, read: () => T, show: (v: T) => string = String, extra: Partial<TreeNode> = {}): T => {
      const offset = rr.pos;
      const v = read();
      parent.children!.push(leaf(key, label, show(v), { offset, length: rr.pos - offset, ...extra }));
      return v;
    };
    const start = rr.pos;
    switch (type) {
      case SECTION.Project: {
        const p = model.project;
        projectNode.offset = start;
        p.name = f(projectNode, 'name', 'Name', () => rr.str(), String, edit('text', (v) => ((p.name = v), null)));
        if (version >= 2) p.producer = f(projectNode, 'producer', 'Producer', () => rr.str());
        if (version >= 3) {
          p.timestamp = f(projectNode, 'timestamp', 'Build time', () => rr.u32(), (t) => new Date(t * 1000).toISOString().replace('T', ' ').replace('.000Z', ' UTC'));
          p.buildId = f(projectNode, 'buildId', 'Build ID', () => rr.take(16), hex);
        }
        projectNode.length = rr.pos - start;
        break;
      }
      case SECTION.Device: {
        const d: DeviceRec = { name: '', typeId: '', ip: '', mask: '', controller: '', cycleMs: 0, modules: [] };
        const n = node('?', '?', [], { offset: start });
        d.name = f(n, 'name', 'Name', () => rr.str());
        n.key = d.name;
        n.label = d.name;
        d.typeId = f(n, 'type', 'Type', () => rr.str());
        if (version === 1) {
          d.ip = f(n, 'ip', 'IP address', () => rr.str(), String, edit('ip', (v) => setIp(d, 'ip', v)));
          d.mask = f(n, 'mask', 'Subnet mask', () => rr.str(), String, edit('ip', (v) => setIp(d, 'mask', v)));
        } else {
          d.ip = f(n, 'ip', 'IP address', () => Array.from(rr.take(4)).join('.'), String, edit('ip', (v) => setIp(d, 'ip', v)));
          d.mask = f(n, 'mask', 'Subnet mask', () => Array.from(rr.take(4)).join('.'), String, edit('ip', (v) => setIp(d, 'mask', v)));
        }
        d.controller = f(n, 'controller', 'Controller', () => rr.str(), (c) => c || '–');
        d.cycleMs = f(n, 'cycle', 'Cycle time (ms)', () => rr.u16(), String, edit('number', (v) => setInt(v, 1, 65535, (x) => (d.cycleMs = x))));
        const mods = node('modules', 'Modules', [], { offset: rr.pos });
        const mc = rr.u8();
        for (let i = 0; i < mc; i++) {
          const mStart = rr.pos;
          const mn = node('?', '?');
          const mod = { typeId: '', slot: 0, address: 0 };
          mod.typeId = f(mn, 'type', 'Type', () => rr.str());
          mod.slot = f(mn, 'slot', 'Slot', () => rr.u8());
          mod.address = f(mn, 'address', 'Start byte', () => rr.u16(), String, edit('number', (v) => setInt(v, 0, 250, (x) => (mod.address = x))));
          mn.key = `slot${mod.slot}`;
          mn.label = `Slot ${mod.slot}`;
          mn.value = mod.typeId;
          mn.offset = mStart;
          mn.length = rr.pos - mStart;
          mods.children!.push(mn);
          d.modules.push(mod);
        }
        mods.length = rr.pos - (mods.offset ?? 0);
        n.children!.push(mods);
        n.length = rr.pos - start;
        devicesNode.children!.push(n);
        model.devices.push(d);
        break;
      }
      case SECTION.Program: {
        const p: ProgramRec = { plc: '', code: [], consts: [], slotInit: [], symbols: [], main: 0, startup: -1 };
        const n = node('?', '?', [], { offset: start });
        p.plc = f(n, 'plc', 'Controller', () => rr.str());
        n.key = p.plc;
        n.label = p.plc;
        const codeAt = rr.pos;
        const len = rr.u32();
        for (let i = 0; i < len; i++) p.code.push(rr.i32());
        const codeBytes = bytes.subarray(codeAt + 4, rr.pos);
        const codeNode = node('code', 'Code', [], { offset: codeAt, length: rr.pos - codeAt, value: `${len} words, CRC ${crc32(codeBytes).toString(16).padStart(8, '0')}` });
        n.children!.push(codeNode);
        const constAt = rr.pos;
        const cc = rr.u16();
        for (let i = 0; i < cc; i++) p.consts.push(rr.f64());
        const constNode = node('consts', 'Constants', [], { offset: constAt, length: rr.pos - constAt, value: `${cc}` });
        const initAt = rr.pos;
        const ic = rr.u16();
        for (let i = 0; i < ic; i++) p.slotInit.push(rr.f64());
        n.children!.push(leaf('slots', 'Data slots', `${ic}`, { offset: initAt, length: rr.pos - initAt }));
        const symAt = rr.pos;
        const sc = rr.u16();
        const symNode = node('symbols', 'Symbols', [], { offset: symAt, value: `${sc}` });
        for (let i = 0; i < sc; i++) {
          const sStart = rr.pos;
          const s: SymbolRec = { name: rr.str(), type: rr.u8() };
          const kind = rr.u8();
          if (kind === 0) s.slot = rr.u16();
          else s.address = { area: rr.u8(), byte: rr.u16(), bit: rr.u8(), word: rr.u8() === 1 };
          p.symbols.push(s);
          const where = s.slot !== undefined ? `slot #${s.slot}` : `%${AREA_NAME[s.address!.area]}${s.address!.word ? `W${s.address!.byte}` : `${s.address!.byte}.${s.address!.bit}`}`;
          symNode.children!.push(leaf(s.name, s.name, `${TYPE_NAME[s.type]} · ${where}`, { offset: sStart, length: rr.pos - sStart }));
        }
        symNode.length = rr.pos - symAt;
        p.main = f(n, 'main', 'Entry: Main', () => rr.u32());
        p.startup = f(n, 'startup', 'Entry: Startup', () => rr.i32(), (x) => (x < 0 ? '–' : String(x)));
        if (opts.detailed) {
          // the disassembly: one child per instruction, keyed by position
          for (const ins of disassemble(p)) codeNode.children!.push(leaf(`pc${ins.pc}`, String(ins.pc).padStart(4, '0'), ins.text, { offset: codeAt + 4 + ins.pc * 4, length: ins.size * 4 }));
          p.consts.forEach((c, i) => constNode.children!.push(leaf(`c${i}`, `c${i}`, String(c), { offset: constAt + 2 + i * 8, length: 8 })));
          n.children!.push(constNode, symNode);
        } else {
          n.children!.push(constNode);
        }
        n.length = rr.pos - start;
        programsNode.children!.push(n);
        model.programs.push(p);
        break;
      }
      case SECTION.Block: {
        const b = { plc: '', name: '', kind: 0, number: 0, source: '' };
        const n = node('?', '?', [], { offset: start });
        b.plc = f(n, 'plc', 'Controller', () => rr.str());
        b.name = f(n, 'name', 'Name', () => rr.str());
        b.kind = f(n, 'kind', 'Kind', () => rr.u8(), (k) => BLOCK_KIND[k] ?? `?${k}`);
        b.number = f(n, 'number', 'Number', () => rr.u16(), String, edit('number', (v) => setInt(v, 1, 65535, (x) => (b.number = x))));
        b.source = f(n, 'source', 'Source', () => rr.longStr(), (s) => s, { text: true, ...edit('source', (v) => ((b.source = v), null)) });
        n.key = `${b.plc}.${b.name}`;
        n.label = `${b.name} [${BLOCK_KIND[b.kind] ?? '?'}${b.number}]`;
        n.value = b.plc;
        n.length = rr.pos - start;
        blocksNode.children!.push(n);
        model.blocks.push(b);
        break;
      }
      case SECTION.Tags: {
        const t = { plc: '', tags: [] as PlcBinModel['tags'][number]['tags'] };
        const n = node('?', '?', [], { offset: start });
        t.plc = rr.str();
        n.key = t.plc;
        n.label = t.plc;
        const c = rr.u16();
        for (let i = 0; i < c; i++) {
          const tStart = rr.pos;
          const tag = { name: '', type: 0, address: '', comment: '' };
          const tn = node('?', '?');
          tag.name = f(tn, 'name', 'Name', () => rr.str());
          tag.type = f(tn, 'type', 'Type', () => rr.u8(), (x) => TYPE_NAME[x] ?? `?${x}`);
          tag.address = f(tn, 'address', 'Address', () => rr.str(), String, edit('text', (v) => ((tag.address = v.toUpperCase()), null)));
          tag.comment = f(tn, 'comment', 'Comment', () => rr.str(), String, edit('text', (v) => ((tag.comment = v), null)));
          tn.key = tag.name;
          tn.label = tag.name;
          tn.value = tag.address;
          tn.offset = tStart;
          tn.length = rr.pos - tStart;
          n.children!.push(tn);
          t.tags.push(tag);
        }
        n.length = rr.pos - start;
        tagsNode.children!.push(n);
        model.tags.push(t);
        break;
      }
      case SECTION.Hmi: {
        const h = { name: '', controller: '', elements: [] as PlcBinModel['hmis'][number]['elements'] };
        const n = node('?', '?', [], { offset: start });
        h.name = f(n, 'name', 'Name', () => rr.str());
        n.key = h.name;
        n.label = h.name;
        h.controller = f(n, 'controller', 'Controller', () => rr.str());
        const c = rr.u8();
        for (let i = 0; i < c; i++) {
          const eStart = rr.pos;
          const el = { kind: 0, label: '', tag: '' };
          const en = node(`#${i}`, `Element ${i + 1}`);
          el.kind = f(en, 'kind', 'Kind', () => rr.u8(), (k) => HMI_KIND[k] ?? `?${k}`);
          el.label = f(en, 'label', 'Label', () => rr.str(), String, edit('text', (v) => ((el.label = v), null)));
          el.tag = f(en, 'tag', 'Tag', () => rr.str(), String, edit('text', (v) => ((el.tag = v), null)));
          en.value = `${HMI_KIND[el.kind]} "${el.label}" → ${el.tag}`;
          en.offset = eStart;
          en.length = rr.pos - eStart;
          n.children!.push(en);
          h.elements.push(el);
        }
        n.length = rr.pos - start;
        hmiNode.children!.push(n);
        model.hmis.push(h);
        break;
      }
      default:
        warnings.push(`Section #${index} has unknown type ${type} - skipped.`);
        rr.pos = rr.end;
    }
  }

  const children = [header, projectNode, devicesNode, programsNode, blocksNode, tagsNode];
  if (version >= 2) children.push(hmiNode);
  let crc: ParseResult['crc'];
  if (version >= 3) {
    if (bytes.length < 4) throw new FormatError('Missing CRC trailer', bytes.length);
    const stored = new DataView(bytes.buffer, bytes.byteOffset).getUint32(bytes.length - 4, true);
    const computed = crc32(bytes, 0, bytes.length - 4);
    crc = { stored, computed };
    const ok = stored === computed;
    if (!ok) warnings.push(`CRC mismatch: stored ${stored.toString(16)}, computed ${computed.toString(16)} - the file was modified or damaged.`);
    children.push(node('trailer', 'Trailer', [
      leaf('crc', 'CRC-32', stored.toString(16).padStart(8, '0'), { offset: bytes.length - 4, length: 4 }),
      leaf('valid', 'Checksum', ok ? 'valid ✔' : `INVALID ✘ (expected ${computed.toString(16).padStart(8, '0')})`),
    ], { offset: bytes.length - 4, length: 4 }));
  }
  for (const c of children) if (c.children && c.value === undefined && c !== header) c.value = c.key === 'project' ? model.project.name : `${c.children.length}`;
  const tree = node('root', `.plcbin v${version}`, children, { offset: 0, length: bytes.length, value: `${bytes.length} bytes` });
  return { model, tree, warnings, crc };
}

function setIp(d: DeviceRec, key: 'ip' | 'mask', v: string): string | null {
  const parts = v.trim().split('.');
  if (parts.length !== 4 || parts.some((p) => !/^\d{1,3}$/.test(p) || Number(p) > 255)) return `"${v}" is not an IPv4 address`;
  d[key] = parts.map(Number).join('.');
  return null;
}

function setInt(v: string, min: number, max: number, set: (x: number) => void): string | null {
  const n = Number(v);
  if (!Number.isInteger(n) || n < min || n > max) return `Enter a whole number between ${min} and ${max}`;
  set(n);
  return null;
}
