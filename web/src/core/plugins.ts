/**
 * Plugin architecture: every binary format is read by a plugin. Plugins sit in a stack ordered by
 * priority; the first enabled plugin that says it can read a file gets it. Several plugins may
 * understand the same file - the one higher in the stack wins, and reordering the stack changes
 * how the file is presented.
 */
import { hex } from './bytes';
import { PlcBinModel, TreeNode, leaf, node } from './model';
import { parsePlcBin, peekVersion, writePlcBin } from './plcbin';

export interface ReadResult {
  tree: TreeNode;
  warnings: string[];
  /** Format version, used to decide which compare filters apply. */
  version: number | null;
  /** Decoded content, for plugins that can write the file back after edits. */
  model?: PlcBinModel;
}

export interface BinaryPlugin {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  /** Default position in the stack: higher = asked first. */
  readonly priority: number;
  canRead(bytes: Uint8Array, fileName: string): boolean;
  read(bytes: Uint8Array, fileName: string): ReadResult;
  /** Present when the plugin can serialise an edited model back to bytes. */
  write?(model: PlcBinModel): Uint8Array;
}

class PlcBinVersionPlugin implements BinaryPlugin {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly priority: number;

  constructor(private readonly version: 1 | 2 | 3) {
    this.id = `plcbin-v${version}`;
    this.name = `PLC Studio binary v${version}`;
    this.description = `Full reader/writer for format version ${version}: disassembly, symbols, editing${version === 3 ? ', checksum validation' : ''}.`;
    this.priority = 100 * version;
  }

  canRead(bytes: Uint8Array): boolean {
    return peekVersion(bytes) === this.version;
  }

  read(bytes: Uint8Array): ReadResult {
    const r = parsePlcBin(bytes, { detailed: true, editable: true });
    return { tree: r.tree, warnings: r.warnings, version: r.model.version, model: r.model };
  }

  write(model: PlcBinModel): Uint8Array {
    return writePlcBin({ ...model, version: this.version });
  }
}

/** An older, generic reader: understands every version but only shows a summary and cannot write. */
class PlcBinLegacyPlugin implements BinaryPlugin {
  readonly id = 'plcbin-legacy';
  readonly name = 'PLC Studio binary (legacy viewer)';
  readonly description = 'Reads all versions, summary only: no disassembly, no symbols, read-only. Kept for compatibility.';
  readonly priority = 50;

  canRead(bytes: Uint8Array): boolean {
    const v = peekVersion(bytes);
    return v !== null && v >= 1 && v <= 3;
  }

  read(bytes: Uint8Array): ReadResult {
    const r = parsePlcBin(bytes, { detailed: false, editable: false });
    return { tree: r.tree, warnings: r.warnings, version: r.model.version };
  }
}

/** Fallback for anything: a hex dump in 16-byte rows. */
class HexDumpPlugin implements BinaryPlugin {
  readonly id = 'hex';
  readonly name = 'Hex dump';
  readonly description = 'Reads any file as raw bytes, 16 per row. Last resort when no format plugin matches.';
  readonly priority = 0;

  canRead(): boolean {
    return true;
  }

  read(bytes: Uint8Array, fileName: string): ReadResult {
    const rows: TreeNode[] = [];
    const limit = Math.min(bytes.length, 64 * 1024);
    for (let at = 0; at < limit; at += 16) {
      const chunk = bytes.subarray(at, Math.min(at + 16, bytes.length));
      const ascii = Array.from(chunk, (b) => (b >= 32 && b < 127 ? String.fromCharCode(b) : '·')).join('');
      rows.push(leaf(`0x${at.toString(16).padStart(6, '0')}`, `0x${at.toString(16).padStart(6, '0')}`, `${hex(chunk).replace(/(..)/g, '$1 ').trim().padEnd(47)}  ${ascii}`, { offset: at, length: chunk.length }));
    }
    const warnings = bytes.length > limit ? [`Only the first ${limit} bytes are shown.`] : [];
    return { tree: node('root', fileName || 'file', rows, { value: `${bytes.length} bytes (raw)`, offset: 0, length: bytes.length }), warnings, version: null };
  }
}

export function builtinPlugins(): BinaryPlugin[] {
  return [new PlcBinVersionPlugin(3), new PlcBinVersionPlugin(2), new PlcBinVersionPlugin(1), new PlcBinLegacyPlugin(), new HexDumpPlugin()];
}

export interface StackEntry {
  plugin: BinaryPlugin;
  enabled: boolean;
}

export class PluginRegistry {
  readonly stack: StackEntry[];

  constructor(plugins: BinaryPlugin[] = builtinPlugins()) {
    this.stack = [...plugins].sort((a, b) => b.priority - a.priority).map((plugin) => ({ plugin, enabled: true }));
  }

  /** All enabled plugins that could read the file, in stack order. */
  candidates(bytes: Uint8Array, fileName = ''): BinaryPlugin[] {
    return this.stack.filter((e) => e.enabled && e.plugin.canRead(bytes, fileName)).map((e) => e.plugin);
  }

  /** The plugin that gets the file: first match from the top of the stack. */
  pick(bytes: Uint8Array, fileName = ''): BinaryPlugin | null {
    return this.candidates(bytes, fileName)[0] ?? null;
  }

  move(id: string, delta: -1 | 1): void {
    const i = this.stack.findIndex((e) => e.plugin.id === id);
    const j = i + delta;
    if (i < 0 || j < 0 || j >= this.stack.length) return;
    [this.stack[i], this.stack[j]] = [this.stack[j], this.stack[i]];
  }

  setEnabled(id: string, enabled: boolean): void {
    const e = this.stack.find((x) => x.plugin.id === id);
    if (e) e.enabled = enabled;
  }

  get(id: string): BinaryPlugin | undefined {
    return this.stack.find((e) => e.plugin.id === id)?.plugin;
  }
}

export interface LoadedFile {
  fileName: string;
  bytes: Uint8Array;
  plugin: BinaryPlugin;
  result: ReadResult;
}

/** Reads a file with the first plugin that accepts it; if that plugin fails, the next one is tried. */
export function load(registry: PluginRegistry, bytes: Uint8Array, fileName: string): LoadedFile {
  const errors: string[] = [];
  for (const plugin of registry.candidates(bytes, fileName)) {
    try {
      return { fileName, bytes, plugin, result: plugin.read(bytes, fileName) };
    } catch (e) {
      errors.push(`${plugin.name}: ${(e as Error).message}`);
    }
  }
  throw new Error(errors.length ? `No plugin could read ${fileName}.\n${errors.join('\n')}` : `No enabled plugin accepts ${fileName}.`);
}
