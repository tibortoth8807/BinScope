/**
 * BinScope command line - the same core the web app uses.
 *
 *   binscope plugins
 *   binscope info <file> [--plugin <id>]
 *   binscope compare <left> <right> [--filters id,id | --no-filters | --all-filters] [--json]
 *   binscope set <file> <path> <value> --out <file>      e.g. set a.plcbin devices/Field_IO/ip 10.0.0.7 --out b.plcbin
 *
 * Exit code for compare: 0 = no visible differences, 1 = differences, 2 = error (like diff).
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { basename } from 'node:path';
import { DiffFilter, FILTERS, applicableFilters, compareTrees, differences } from './core/diff';
import { TreeNode, walk } from './core/model';
import { LoadedFile, PluginRegistry, load } from './core/plugins';

export interface CliIO {
  out: (line: string) => void;
  readFile: (path: string) => Uint8Array;
  writeFile?: (path: string, bytes: Uint8Array) => void;
}

const defaultIO: CliIO = { out: (l) => console.log(l), readFile: (p) => new Uint8Array(readFileSync(p)), writeFile: (p, b) => writeFileSync(p, b) };

export function run(argv: string[], io: CliIO = defaultIO): number {
  const [cmd, ...rest] = argv;
  const flags = new Map<string, string | true>();
  const args: string[] = [];
  for (let i = 0; i < rest.length; i++) {
    if (rest[i].startsWith('--')) {
      const name = rest[i].slice(2);
      const next = rest[i + 1];
      if (['filters', 'plugin', 'out'].includes(name) && next !== undefined) {
        flags.set(name, next);
        i++;
      } else flags.set(name, true);
    } else args.push(rest[i]);
  }
  const registry = new PluginRegistry();
  const open = (path: string): LoadedFile => {
    const bytes = io.readFile(path);
    const forced = flags.get('plugin');
    if (typeof forced === 'string') {
      const p = registry.get(forced);
      if (!p) throw new Error(`Unknown plugin ${forced}`);
      return { fileName: basename(path), bytes, plugin: p, result: p.read(bytes, basename(path)) };
    }
    return load(registry, bytes, basename(path));
  };

  try {
    switch (cmd) {
      case 'plugins':
        io.out('Plugin stack (first match wins):');
        registry.stack.forEach((e, i) => io.out(`  ${i + 1}. ${e.plugin.id.padEnd(16)} ${e.plugin.name} – ${e.plugin.description}`));
        return 0;
      case 'info': {
        if (args.length !== 1) throw new Error('usage: binscope info <file>');
        const f = open(args[0]);
        io.out(`${f.fileName}: ${f.bytes.length} bytes, read by ${f.plugin.name}${f.result.version ? ` (format v${f.result.version})` : ''}`);
        for (const w of f.result.warnings) io.out(`  ⚠ ${w}`);
        walk(f.result.tree, (n, path) => {
          if (path.length > 3) return;
          io.out(`${'  '.repeat(path.length - 1)}${n.label}${n.value !== undefined && !n.text ? `: ${n.value}` : ''}`);
        });
        return 0;
      }
      case 'compare': {
        if (args.length !== 2) throw new Error('usage: binscope compare <left> <right> [--filters a,b] [--json]');
        const [a, b] = args.map(open);
        const filters = chooseFilters(flags, [a.result.version, b.result.version]);
        const { root, stats } = compareTrees(a.result.tree, b.result.tree, filters);
        const diffs = differences(root);
        if (flags.has('json')) {
          io.out(JSON.stringify({
            left: { file: a.fileName, plugin: a.plugin.id, version: a.result.version },
            right: { file: b.fileName, plugin: b.plugin.id, version: b.result.version },
            filters: filters.map((f) => f.id),
            stats,
            differences: diffs.map((d) => ({ path: d.path.slice(1).join('/'), status: d.status, left: d.left?.value, right: d.right?.value })),
          }, null, 2));
        } else {
          io.out(`${a.fileName} (${a.plugin.id}) ↔ ${b.fileName} (${b.plugin.id})`);
          io.out(`Filters: ${filters.map((f) => f.id).join(', ') || 'none'}`);
          for (const d of diffs) {
            const sign = d.status === 'added' ? '+' : d.status === 'removed' ? '-' : '~';
            const detail = d.status === 'changed' && !d.left?.text ? `: ${d.left?.value} → ${d.right?.value}` : d.left?.text ? ' (source changed)' : '';
            io.out(`  ${sign} ${d.path.slice(1).join(' / ')}${detail}`);
          }
          io.out(`${stats.changed} changed, ${stats.added} added, ${stats.removed} removed, ${stats.ignored} hidden by filters`);
        }
        return diffs.length ? 1 : 0;
      }
      case 'set': {
        const out = flags.get('out');
        if (args.length !== 3 || typeof out !== 'string') throw new Error('usage: binscope set <file> <path> <value> --out <file>');
        const f = open(args[0]);
        const path = args[1].split('/').filter(Boolean);
        let target: TreeNode | undefined = f.result.tree;
        for (const key of path) target = target?.children?.find((c) => c.key === key);
        if (!target) throw new Error(`No element ${args[1]}`);
        if (!target.edit || !f.plugin.write || !f.result.model) throw new Error(`${args[1]} is not editable with ${f.plugin.name}`);
        const problem = target.edit.apply(args[2]);
        if (problem) throw new Error(problem);
        const bytes = f.plugin.write(f.result.model);
        (io.writeFile ?? defaultIO.writeFile!)(out, bytes);
        io.out(`${args[1]} = ${args[2]} → ${out} (${bytes.length} bytes, written by ${f.plugin.name})`);
        return 0;
      }
      default:
        io.out('usage: binscope plugins | info <file> | compare <left> <right> [--filters a,b|--no-filters|--all-filters] [--json] | set <file> <path> <value> --out <file>');
        return cmd ? 2 : 0;
    }
  } catch (e) {
    io.out(`error: ${(e as Error).message}`);
    return 2;
  }
}

function chooseFilters(flags: Map<string, string | true>, versions: (number | null)[]): DiffFilter[] {
  const applicable = applicableFilters(versions);
  if (flags.has('no-filters')) return [];
  if (flags.has('all-filters')) return applicable;
  const list = flags.get('filters');
  if (typeof list === 'string') {
    const ids = list.split(',').map((s) => s.trim()).filter(Boolean);
    const unknown = ids.filter((id) => !FILTERS.some((f) => f.id === id));
    if (unknown.length) throw new Error(`Unknown filter(s): ${unknown.join(', ')}. Known: ${FILTERS.map((f) => f.id).join(', ')}`);
    return applicable.filter((f) => ids.includes(f.id));
  }
  return applicable.filter((f) => f.byDefault);
}

// run when executed directly (tsx src/cli.ts ...)
if (typeof process !== 'undefined' && process.argv[1] && /cli\.(t|j)s$/.test(process.argv[1])) {
  process.exitCode = run(process.argv.slice(2));
}
