/**
 * Writes samples/expected.json: what the reference (TypeScript) implementation reads from every
 * sample and every comparison. The .NET test suite checks its own results against this file, so
 * both implementations stay in step.  Run: npx tsx scripts/expected.ts
 */
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { applicableFilters, compareTrees, differences } from '../src/core/diff';
import { walk } from '../src/core/model';
import { PluginRegistry, load } from '../src/core/plugins';

const dir = join(import.meta.dirname, '..', '..', 'samples');
const registry = new PluginRegistry();
const open = (name: string) => load(registry, new Uint8Array(readFileSync(join(dir, name))), name);

const files: Record<string, unknown> = {};
for (const name of readdirSync(dir).filter((n) => !n.endsWith('.json')).sort()) {
  const f = open(name);
  let nodes = 0;
  walk(f.result.tree, () => nodes++);
  const m = f.result.model;
  files[name] = {
    plugin: f.plugin.id,
    version: f.result.version,
    nodes,
    warnings: f.result.warnings.length,
    ...(m && {
      devices: m.devices.map((d) => d.name),
      blocks: m.blocks.map((b) => b.name),
      codeWords: m.programs.map((p) => p.code.length),
      tags: m.tags.reduce((n, t) => n + t.tags.length, 0),
      hmiElements: m.hmis.reduce((n, h) => n + h.elements.length, 0),
    }),
  };
}

const pairs: [string, string][] = [
  ['conveyor_v3.plcbin', 'conveyor_v3_rebuild.plcbin'],
  ['conveyor_v3.plcbin', 'conveyor_v3_changed.plcbin'],
  ['conveyor_v1.plcbin', 'conveyor_v3.plcbin'],
  ['conveyor_v2.plcbin', 'conveyor_v3.plcbin'],
  ['conveyor_v3.plcbin', 'conveyor_v3_corrupt.plcbin'],
];
const comparisons = pairs.flatMap(([l, r]) => {
  const a = open(l);
  const b = open(r);
  const applicable = applicableFilters([a.result.version, b.result.version]);
  return (['none', 'default', 'all'] as const).map((mode) => {
    const filters = mode === 'none' ? [] : mode === 'all' ? applicable : applicable.filter((f) => f.byDefault);
    const { root, stats } = compareTrees(a.result.tree, b.result.tree, filters);
    return {
      left: l,
      right: r,
      filters: filters.map((f) => f.id),
      stats,
      differences: differences(root).map((d) => `${d.status} ${d.path.slice(1).join('/')}`),
    };
  });
});

writeFileSync(join(dir, 'expected.json'), JSON.stringify({ files, comparisons }, null, 2) + '\n');
console.log(`expected.json: ${Object.keys(files).length} files, ${comparisons.length} comparisons`);
