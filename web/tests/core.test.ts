import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test } from 'node:test';
import { run } from '../src/cli';
import { FILTERS, applicableFilters, compareTrees, differences, lineDiff, stripComments } from '../src/core/diff';
import { TreeNode, walk } from '../src/core/model';
import { parsePlcBin, writePlcBin } from '../src/core/plcbin';
import { PluginRegistry, load } from '../src/core/plugins';
import { crc32 } from '../src/core/bytes';

const SAMPLES = join(import.meta.dirname, '..', '..', 'samples');
const read = (name: string) => new Uint8Array(readFileSync(join(SAMPLES, name)));
const open = (name: string, registry = new PluginRegistry()) => load(registry, read(name), name);
const defaults = (versions: (number | null)[]) => applicableFilters(versions).filter((f) => f.byDefault);
const find = (tree: TreeNode, path: string[]): TreeNode | undefined => {
  let found: TreeNode | undefined;
  walk(tree, (n, p) => {
    if (p.slice(1).join('/') === path.join('/')) found = n;
  });
  return found;
};

test('CRC-32 matches the standard check value', () => {
  assert.equal(crc32(new TextEncoder().encode('123456789')), 0xcbf43926);
});

test('the plugin stack picks the right reader for every sample', () => {
  const expect: Record<string, [string, number | null]> = {
    'conveyor_v3.plcbin': ['plcbin-v3', 3],
    'conveyor_v2.plcbin': ['plcbin-v2', 2],
    'conveyor_v1.plcbin': ['plcbin-v1', 1],
    'unknown.bin': ['hex', null],
  };
  for (const [name, [plugin, version]] of Object.entries(expect)) {
    const f = open(name);
    assert.equal(f.plugin.id, plugin, name);
    assert.equal(f.result.version, version, name);
  }
});

test('reordering and disabling plugins changes who reads the file', () => {
  const registry = new PluginRegistry();
  for (let i = 0; i < 3; i++) registry.move('plcbin-legacy', -1);
  assert.equal(registry.stack[0].plugin.id, 'plcbin-legacy');
  const legacy = open('conveyor_v3.plcbin', registry);
  assert.equal(legacy.plugin.id, 'plcbin-legacy');
  assert.equal(find(legacy.result.tree, ['programs', 'Line_PLC', 'symbols']), undefined, 'legacy viewer shows no symbols');
  assert.equal(legacy.plugin.write, undefined, 'legacy viewer is read-only');
  for (const id of ['plcbin-v3', 'plcbin-v2', 'plcbin-v1', 'plcbin-legacy']) registry.setEnabled(id, false);
  assert.equal(open('conveyor_v3.plcbin', registry).plugin.id, 'hex');
});

test('reading and writing again gives identical bytes in every version', () => {
  for (const name of ['conveyor_v1.plcbin', 'conveyor_v2.plcbin', 'conveyor_v3.plcbin', 'conveyor_v3_changed.plcbin']) {
    const bytes = read(name);
    const { model } = parsePlcBin(bytes, { detailed: true, editable: false });
    assert.deepEqual(writePlcBin(model), bytes, name);
  }
});

test('content is decoded: devices, modules, programs, disassembly, HMI', () => {
  const f = open('conveyor_v3.plcbin');
  const m = f.result.model!;
  assert.deepEqual(m.devices.map((d) => `${d.name} ${d.ip}`), ['Line_PLC 192.168.0.10', 'Field_IO 192.168.0.20', 'Operator_Panel 192.168.0.30']);
  assert.equal(m.devices[0].modules.length, 3);
  assert.deepEqual(m.blocks.map((b) => b.name), ['Main', 'Startup', 'Scale', 'Production']);
  assert.equal(m.hmis[0].elements.length, 7);
  const code = find(f.result.tree, ['programs', 'Line_PLC', 'code'])!;
  assert.ok(code.children!.length > 50);
  assert.ok(code.children!.some((c) => c.value === 'LDBIT %I0.0'));
  assert.ok(code.children!.some((c) => /^STORE Main\.running$/.test(c.value ?? '')));
});

test('the damaged file is still read, with a checksum warning', () => {
  const f = open('conveyor_v3_corrupt.plcbin');
  assert.equal(f.plugin.id, 'plcbin-v3');
  assert.match(f.result.warnings.join(), /CRC mismatch/);
  assert.match(find(f.result.tree, ['trailer', 'valid'])!.value!, /INVALID/);
});

test('a rebuild without changes differs only in build noise, which the default filters hide', () => {
  const a = open('conveyor_v3.plcbin');
  const b = open('conveyor_v3_rebuild.plcbin');
  const raw = compareTrees(a.result.tree, b.result.tree, []);
  assert.deepEqual(differences(raw.root).map((d) => d.path.slice(1).join('/')), ['project/timestamp', 'project/buildId', 'trailer/crc', 'trailer/valid'].filter((p) => p !== 'trailer/valid'));
  const filtered = compareTrees(a.result.tree, b.result.tree, defaults([3, 3]));
  assert.deepEqual(filtered.stats, { changed: 0, added: 0, removed: 0, ignored: 3 });
});

test('real changes are found and named', () => {
  const a = open('conveyor_v3.plcbin');
  const b = open('conveyor_v3_changed.plcbin');
  const { root, stats } = compareTrees(a.result.tree, b.result.tree, defaults([3, 3]));
  const paths = differences(root).map((d) => `${d.status} ${d.path.slice(1).join('/')}`);
  assert.deepEqual(paths, [
    'changed devices/Line_PLC/cycle',
    'changed devices/Field_IO/ip',
    'added devices/Field_IO/modules/slot3',
    'changed programs/Line_PLC/code/pc136',
    'changed programs/Line_PLC/consts/c4',
    'changed blocks/Line_PLC.Main/source',
    'changed blocks/Line_PLC.Scale/source',
    'changed tags/Line_PLC/Horn/comment',
    'changed hmi/Operator_Panel/#3/label',
  ]);
  assert.deepEqual(stats, { changed: 8, added: 1, removed: 0, ignored: 12 });
});

test('optional filters: comments, machine code, addressing', () => {
  const a = open('conveyor_v3.plcbin');
  const b = open('conveyor_v3_changed.plcbin');
  const pick = (...ids: string[]) => FILTERS.filter((f) => f.byDefault || ids.includes(f.id));
  const visible = (ids: string[]) => differences(compareTrees(a.result.tree, b.result.tree, pick(...ids)).root).map((d) => d.path.slice(1).join('/'));
  const comments = visible(['comments']);
  assert.ok(!comments.includes('blocks/Line_PLC.Scale/source'), 'comment-only source change hidden');
  assert.ok(comments.includes('blocks/Line_PLC.Main/source'), 'real code change kept');
  assert.ok(!comments.includes('tags/Line_PLC/Horn/comment'));
  assert.ok(!visible(['machine-code']).some((p) => p.startsWith('programs/')));
  assert.ok(!visible(['addressing']).includes('devices/Field_IO/ip'));
});

test('filters depend on the format version', () => {
  assert.deepEqual(applicableFilters([1, 1]).map((f) => f.id), ['layout', 'comments', 'machine-code', 'addressing']);
  assert.ok(applicableFilters([2, 2]).some((f) => f.id === 'producer'));
  assert.ok(!applicableFilters([2, 2]).some((f) => f.id === 'build-id'));
  assert.ok(applicableFilters([1, 3]).some((f) => f.id === 'checksum'));
});

test('comparing format versions: v1 against v3 of the same project', () => {
  const a = open('conveyor_v1.plcbin');
  const b = open('conveyor_v3.plcbin');
  const paths = differences(compareTrees(a.result.tree, b.result.tree, defaults([1, 3])).root).map((d) => `${d.status} ${d.path.slice(1).join('/')}`);
  assert.deepEqual(paths, ['changed header/version', 'added hmi']); // the trailer is new too, but hidden by the checksum filter
});

test('editing through the plugin: change an IP address and save a valid file', () => {
  const f = open('conveyor_v3.plcbin');
  const ip = find(f.result.tree, ['devices', 'Field_IO', 'ip'])!;
  assert.match(ip.edit!.apply('300.1.1.1') ?? '', /not an IPv4/);
  assert.equal(ip.edit!.apply('10.0.0.7'), null);
  const saved = f.plugin.write!(f.result.model!);
  const again = load(new PluginRegistry(), saved, 'edited.plcbin');
  assert.equal(again.result.model!.devices[1].ip, '10.0.0.7');
  assert.equal(again.result.warnings.length, 0, 'checksum recomputed');
});

test('line diff and comment stripping', () => {
  assert.deepEqual(lineDiff('a\nb\nc', 'a\nx\nc').map((l) => l.op + l.text), [' a', '-b', '+x', ' c']);
  assert.equal(stripComments('x := 1; (* one *)\n// two\ny := 2;'), 'x := 1; y := 2;');
});

test('CLI: compare with JSON output and exit codes', () => {
  const lines: string[] = [];
  const io = { out: (l: string) => lines.push(l), readFile: (p: string) => read(p) };
  assert.equal(run(['compare', 'conveyor_v3.plcbin', 'conveyor_v3_rebuild.plcbin'], io), 0);
  lines.length = 0;
  assert.equal(run(['compare', 'conveyor_v3.plcbin', 'conveyor_v3_changed.plcbin', '--json'], io), 1);
  const json = JSON.parse(lines.join('\n'));
  assert.equal(json.stats.changed, 8);
  assert.equal(run(['compare', 'conveyor_v3.plcbin', 'conveyor_v3_changed.plcbin', '--filters', 'nope'], io), 2);
});

test('CLI: set a value and write a new file', () => {
  const written = new Map<string, Uint8Array>();
  const io = { out: () => {}, readFile: (p: string) => written.get(p) ?? read(p), writeFile: (p: string, b: Uint8Array) => void written.set(p, b) };
  assert.equal(run(['set', 'conveyor_v3.plcbin', 'devices/Line_PLC/cycle', '25', '--out', 'x.plcbin'], io), 0);
  const f = load(new PluginRegistry(), written.get('x.plcbin')!, 'x.plcbin');
  assert.equal(f.result.model!.devices[0].cycleMs, 25);
  assert.equal(f.result.warnings.length, 0);
  assert.equal(run(['set', 'conveyor_v3.plcbin', 'devices/Line_PLC/cycle', '0', '--out', 'y.plcbin'], io), 2);
  assert.equal(run(['set', 'conveyor_v3.plcbin', 'header/magic', 'XXXX', '--out', 'y.plcbin'], io), 2);
});

test('converting the format version: v3 content written by the v2 and v1 plugins equals the v2/v1 builds', () => {
  const registry = new PluginRegistry();
  const v3 = open('conveyor_v3.plcbin');
  assert.deepEqual(registry.get('plcbin-v2')!.write!(v3.result.model!), read('conveyor_v2.plcbin'));
  assert.deepEqual(registry.get('plcbin-v1')!.write!(v3.result.model!), read('conveyor_v1.plcbin'));
});
