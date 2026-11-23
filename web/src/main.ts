import './styles.css';
import { hex } from './core/bytes';
import { DiffFilter, DiffNode, FILTERS, applicableFilters, compareTrees, differences, lineDiff } from './core/diff';
import { TreeNode } from './core/model';
import { LoadedFile, PluginRegistry, load } from './core/plugins';
import { SAMPLES } from './samples';

type Side = 'left' | 'right';

const registry = new PluginRegistry();
const files: Record<Side, LoadedFile | null> = { left: null, right: null };
const edited: Record<Side, boolean> = { left: false, right: false };
const originals: Record<Side, LoadedFile | null> = { left: null, right: null };
const enabledFilters = new Set(FILTERS.filter((f) => f.byDefault).map((f) => f.id));
const expanded = new Set<string>(['root']);
let selected: string | null = null;
let onlyDiffs = false;
let lastDiff: DiffNode | null = null;
let message = '';

const $ = (id: string) => document.getElementById(id)!;

function el<K extends keyof HTMLElementTagNameMap>(tag: K, attrs: Record<string, unknown> = {}, ...children: (Node | string | null | false | undefined)[]): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith('on') && typeof v === 'function') e.addEventListener(k.slice(2), v as EventListener);
    else if (k === 'class') e.className = String(v);
    else if (v === true) e.setAttribute(k, '');
    else e.setAttribute(k, String(v));
  }
  for (const c of children) if (c !== null && c !== undefined && c !== false) e.append(c);
  return e;
}

const fromBase64 = (b64: string) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));

// ------------------------------------------------------------------ loading

function open(side: Side, bytes: Uint8Array, name: string): void {
  try {
    files[side] = load(registry, bytes, name);
    originals[side] = files[side];
    edited[side] = false;
    message = '';
  } catch (e) {
    files[side] = null;
    message = (e as Error).message;
  }
  autoExpand();
  render();
}

/** Re-read both files after the plugin stack changed. */
function reload(): void {
  for (const side of ['left', 'right'] as Side[]) {
    const f = files[side];
    if (f) {
      try {
        files[side] = load(registry, f.bytes, f.fileName);
      } catch (e) {
        files[side] = null;
        message = (e as Error).message;
      }
    }
  }
  autoExpand();
  render();
}

function openSamplePair(left: string, right: string | null): void {
  open('left', fromBase64(SAMPLES[left]), left);
  if (right) open('right', fromBase64(SAMPLES[right]), right);
  else {
    files.right = null;
    render();
  }
}

function autoExpand(): void {
  expanded.clear();
  expanded.add('root');
  const d = currentDiff();
  if (!d) return;
  const visit = (n: DiffNode) => {
    if (n.status === 'changed' && n.children.length) {
      expanded.add(n.path.join('/'));
      n.children.forEach(visit);
    }
  };
  visit(d.root);
  if (!files.right) for (const c of d.root.children) if (['project', 'devices', 'blocks'].includes(c.key)) expanded.add(c.path.join('/'));
}

// ------------------------------------------------------------------ diff

function activeFilters(): DiffFilter[] {
  return applicableFilters([files.left?.result.version ?? null, files.right?.result.version ?? null]).filter((f) => enabledFilters.has(f.id));
}

function currentDiff() {
  const l = files.left;
  const r = files.right;
  if (!l) return null;
  return compareTrees(l.result.tree, (r ?? l).result.tree, r ? activeFilters() : []);
}

// ------------------------------------------------------------------ rendering

function render(): void {
  renderFiles();
  renderPlugins();
  renderFilters();
  renderTree();
  renderDetail();
}

function fileCard(side: Side): HTMLElement {
  const f = files[side];
  const input = el('input', { type: 'file', class: 'hidden', 'data-side': side }) as HTMLInputElement;
  input.addEventListener('change', async () => {
    const file = input.files?.[0];
    if (file) open(side, new Uint8Array(await file.arrayBuffer()), file.name);
    input.value = '';
  });
  const card = el(
    'div',
    { class: `file-card ${side}${f ? '' : ' empty'}` },
    el('div', { class: 'fc-side' }, side === 'left' ? 'A · left' : 'B · right'),
    f
      ? el('div', { class: 'fc-body' },
          el('b', {}, `${f.fileName}${edited[side] ? ' (edited)' : ''}`),
          el('span', {}, `${f.bytes.length} bytes · ${f.plugin.name}${f.result.version ? ` · v${f.result.version}` : ''}`),
          ...f.result.warnings.map((w) => el('span', { class: 'warn' }, `⚠ ${w}`)),
        )
      : el('div', { class: 'fc-body muted' }, side === 'left' ? 'Drop a .plcbin file here, or pick a sample' : 'Drop a second file to compare'),
    el('div', { class: 'fc-actions' },
      el('button', { type: 'button', class: 'btn', onclick: () => input.click() }, '📂 Open'),
      f && edited[side] && f.plugin.write ? el('button', { type: 'button', class: 'btn', onclick: () => saveFile(side) }, '💾 Save') : null,
      f && edited[side] && side === 'left' && !files.right && originals.left ? el('button', { type: 'button', class: 'btn', title: 'Compare the edited file with the file as it was opened', onclick: compareWithOriginal }, '⇄ vs original') : null,
      f?.result.model ? convertSelect(side) : null,
      f && side === 'right' ? el('button', { type: 'button', class: 'btn', onclick: () => { files.right = null; autoExpand(); render(); } }, '✕') : null,
    ),
    input,
  );
  card.addEventListener('dragover', (e) => {
    e.preventDefault();
    card.classList.add('over');
  });
  card.addEventListener('dragleave', () => card.classList.remove('over'));
  card.addEventListener('drop', async (e) => {
    e.preventDefault();
    card.classList.remove('over');
    const file = e.dataTransfer?.files[0];
    if (file) open(side, new Uint8Array(await file.arrayBuffer()), file.name);
  });
  return card;
}

function renderFiles(): void {
  const box = $('files');
  box.replaceChildren(fileCard('left'), el('div', { class: 'vs' }, '⇄'), fileCard('right'));
  const d = currentDiff();
  const stats = $('stats');
  if (d && files.right) {
    const s = d.stats;
    stats.replaceChildren(
      el('span', { class: 'chip changed' }, `${s.changed} changed`),
      el('span', { class: 'chip added' }, `${s.added} added`),
      el('span', { class: 'chip removed' }, `${s.removed} removed`),
      el('span', { class: 'chip ignored' }, `${s.ignored} hidden by filters`),
      ...(s.changed + s.added + s.removed === 0 ? [el('span', { class: 'chip same' }, '✔ identical (with the active filters)')] : []),
    );
  } else stats.replaceChildren(files.left ? el('span', { class: 'chip' }, 'Viewing one file - open a second one to compare') : el('span'));
  $('message').textContent = message;
  $('message').classList.toggle('hidden', !message);
}

function renderPlugins(): void {
  const list = $('plugins');
  list.replaceChildren(
    ...registry.stack.map((entry, i) => {
      const reads = (['left', 'right'] as Side[]).filter((s) => files[s]?.plugin.id === entry.plugin.id).map((s) => (s === 'left' ? 'A' : 'B'));
      const can = (['left', 'right'] as Side[]).some((s) => files[s] && entry.plugin.canRead(files[s]!.bytes, files[s]!.fileName));
      const check = el('input', { type: 'checkbox', checked: entry.enabled, title: 'Enable / disable' }) as HTMLInputElement;
      check.addEventListener('change', () => {
        registry.setEnabled(entry.plugin.id, check.checked);
        reload();
      });
      return el(
        'li',
        { class: `plugin${entry.enabled ? '' : ' disabled'}${reads.length ? ' active' : ''}`, title: entry.plugin.description },
        el('span', { class: 'pos' }, String(i + 1)),
        check,
        el('div', { class: 'pl-body' },
          el('b', {}, entry.plugin.name),
          el('small', {}, `${entry.plugin.id}${entry.plugin.write ? ' · read/write' : ' · read-only'}${can && !reads.length && entry.enabled ? ' · could read' : ''}`),
        ),
        reads.length ? el('span', { class: 'reads' }, reads.join(' ')) : null,
        el('div', { class: 'updown' },
          el('button', { type: 'button', title: 'Move up', disabled: i === 0, onclick: () => { registry.move(entry.plugin.id, -1); reload(); } }, '▲'),
          el('button', { type: 'button', title: 'Move down', disabled: i === registry.stack.length - 1, onclick: () => { registry.move(entry.plugin.id, 1); reload(); } }, '▼'),
        ),
      );
    }),
  );
}

function renderFilters(): void {
  const versions = [files.left?.result.version ?? null, files.right?.result.version ?? null];
  const applicable = new Set(applicableFilters(versions).map((f) => f.id));
  const d = currentDiff();
  const hidden = new Map<string, number>();
  if (d && files.right) {
    const visit = (n: DiffNode, parent: boolean) => {
      if (n.ignoredBy && !parent) hidden.set(n.ignoredBy, (hidden.get(n.ignoredBy) ?? 0) + 1);
      n.children.forEach((c) => visit(c, parent || !!n.ignoredBy));
    };
    visit(d.root, false);
  }
  $('filters').classList.toggle('idle', !files.right);
  $('filters').replaceChildren(
    ...(files.left && !files.right ? [el('div', { class: 'filters-note' }, 'Filters apply when two files are compared.')] : []),
    ...FILTERS.map((f) => {
      const ok = applicable.has(f.id) || versions.every((v) => v === null);
      const check = el('input', { type: 'checkbox', checked: enabledFilters.has(f.id), disabled: !ok }) as HTMLInputElement;
      check.addEventListener('change', () => {
        if (check.checked) enabledFilters.add(f.id);
        else enabledFilters.delete(f.id);
        autoExpand();
        render();
      });
      return el(
        'label',
        { class: `filter${ok ? '' : ' na'}`, title: f.description },
        check,
        el('div', {},
          el('b', {}, f.name),
          el('small', {}, `${ok ? f.description : `Only for format ${f.versions.map((v) => `v${v}`).join('/')}`}`),
        ),
        hidden.get(f.id) ? el('span', { class: 'hid' }, String(hidden.get(f.id))) : null,
      );
    }),
  );
}

function renderTree(): void {
  const box = $('tree');
  const d = currentDiff();
  lastDiff = d?.root ?? null;
  if (!d) {
    box.replaceChildren(
      el('div', { class: 'welcome' },
        el('h2', {}, 'Open two binaries and see what really changed'),
        el('p', {}, 'BinScope reads files through a stack of plugins, shows them as a tree, and compares them - with filters for the differences you do not care about.'),
        el('div', { class: 'sample-buttons' }, ...sampleButtons()),
      ),
    );
    return;
  }
  const compare = !!files.right;
  const rows: HTMLElement[] = [
    el('div', { class: 'row head' }, el('div', { class: 'c-name' }, 'Element'), el('div', { class: 'c-val' }, compare ? `A · ${files.left!.fileName}` : 'Value'), compare ? el('div', { class: 'c-val' }, `B · ${files.right!.fileName}`) : null),
  ];
  const addRows = (n: DiffNode, depth: number) => {
    if (onlyDiffs && compare && n.status === 'same' && !n.ignoredBy && depth > 0) return;
    const key = n.path.join('/');
    const hasKids = n.children.length > 0;
    const open = expanded.has(key);
    const l = n.left;
    const r = n.right;
    const row = el(
      'div',
      { class: `row ${compare ? n.status : 'same'}${n.ignoredBy ? ' ignored' : ''}${selected === key ? ' selected' : ''}`, 'data-path': key },
      el('div', { class: 'c-name', style: `padding-left:${8 + depth * 16}px` },
        el('span', { class: 'tw' }, hasKids ? (open ? '▾' : '▸') : ''),
        el('span', {}, n.label),
        n.ignoredBy ? el('span', { class: 'tag' }, n.ignoredBy) : null,
        hasKids && compare && n.status !== 'same' ? el('span', { class: 'dot' }) : null,
      ),
      el('div', { class: 'c-val' }, valueText(l)),
      compare ? el('div', { class: 'c-val' }, valueText(r)) : null,
    );
    row.addEventListener('click', () => {
      if (hasKids) {
        if (open) expanded.delete(key);
        else expanded.add(key);
      }
      selected = key;
      renderTree();
      renderDetail();
    });
    rows.push(row);
    if (hasKids && open) n.children.forEach((c) => addRows(c, depth + 1));
  };
  addRows(d.root, 0);
  box.replaceChildren(...rows);
}

function valueText(n?: TreeNode): string {
  if (!n) return '—';
  if (n.value === undefined) return '';
  if (n.text) {
    const lines = n.value.split('\n').length;
    return `${lines} lines of source`;
  }
  return n.value;
}

function sampleButtons(): HTMLElement[] {
  const pairs: [string, string, string | null, string][] = [
    ['🔁 Rebuild, no changes', 'conveyor_v3.plcbin', 'conveyor_v3_rebuild.plcbin', 'Only build noise differs - the default filters hide it'],
    ['✏️ Real change set', 'conveyor_v3.plcbin', 'conveyor_v3_changed.plcbin', 'Logic, comments, IP, a new module, an HMI label'],
    ['🗂 Format v1 vs v3', 'conveyor_v1.plcbin', 'conveyor_v3.plcbin', 'Same project, different format versions'],
    ['💥 Damaged file', 'conveyor_v3.plcbin', 'conveyor_v3_corrupt.plcbin', 'One flipped byte - checksum invalid'],
    ['👁 Just view a file', 'conveyor_v3.plcbin', null, 'Tree with disassembly, symbols, editing'],
    ['❓ Unknown file', 'unknown.bin', null, 'No format plugin matches - the hex plugin takes it'],
  ];
  return pairs.map(([label, a, b, hint]) => el('button', { type: 'button', class: 'sample', onclick: () => openSamplePair(a, b) }, el('b', {}, label), el('small', {}, hint)));
}

function findDiff(path: string): DiffNode | null {
  if (!lastDiff) return null;
  let found: DiffNode | null = null;
  const visit = (n: DiffNode) => {
    if (found) return;
    if (n.path.join('/') === path) found = n;
    else n.children.forEach(visit);
  };
  visit(lastDiff);
  return found;
}

function renderDetail(): void {
  const box = $('detail');
  const d = selected ? findDiff(selected) : null;
  if (!d) {
    box.replaceChildren(el('div', { class: 'muted pad' }, 'Select an element to see its bytes, the source diff, or to edit it.'));
    return;
  }
  const compare = !!files.right;
  const parts: (HTMLElement | null)[] = [
    el('h3', {}, d.label),
    el('div', { class: 'path' }, d.path.slice(1).join(' / ') || 'file'),
    compare ? el('div', { class: `status ${d.status}` }, d.ignoredBy ? `different, hidden by filter "${d.ignoredBy}"` : d.status) : null,
  ];
  // sources: line diff in compare mode, or the text when viewing one file
  const l = d.left;
  const r = d.right;
  if ((l?.text || r?.text) && compare && l && r) {
    const ops = lineDiff(l.value ?? '', r.value ?? '');
    parts.push(el('h4', {}, 'Source diff'));
    parts.push(el('pre', { class: 'srcdiff' }, ...ops.map((o) => el('div', { class: o.op === '+' ? 'add' : o.op === '-' ? 'del' : '' }, `${o.op} ${o.text}`))));
  } else if (l?.text && !compare) {
    parts.push(el('h4', {}, 'Source'));
    if (l.edit && files.left?.plugin.write) parts.push(sourceEditor('left', l));
    else parts.push(el('pre', { class: 'src' }, l.value ?? ''));
  }
  const sides: Side[] = compare ? ['left', 'right'] : ['left'];
  for (const side of sides) {
    const n = side === 'left' ? d.left : d.right;
    const f = files[side];
    if (!n || !f) continue;
    parts.push(el('h4', {}, compare ? (side === 'left' ? 'A' : 'B') : 'Bytes'));
    if (n.offset !== undefined) {
      const len = Math.min(n.length ?? 16, 160);
      const chunk = f.bytes.subarray(n.offset, n.offset + len);
      // in compare mode, mark the bytes that differ from the other side at the same position
      const otherSide: Side = side === 'left' ? 'right' : 'left';
      const o = otherSide === 'left' ? d.left : d.right;
      const other = compare && o?.offset !== undefined ? files[otherSide]!.bytes.subarray(o.offset, o.offset + len) : null;
      const pre = el('pre', { class: 'hex' });
      for (let i = 0; i < chunk.length; i += 16) {
        pre.append(`${(n.offset + i).toString(16).padStart(6, '0')} `);
        for (let k = i; k < Math.min(i + 16, chunk.length); k++) {
          const text = ` ${hex(chunk.subarray(k, k + 1))}`;
          pre.append(other && other[k] !== chunk[k] ? el('span', { class: 'hx' }, text) : text);
        }
        pre.append('\n');
      }
      if ((n.length ?? 0) > len) pre.append('…');
      parts.push(el('div', { class: 'kv' }, `offset ${n.offset} (0x${n.offset.toString(16)}), ${n.length ?? '?'} bytes`));
      parts.push(pre);
    }
    if (n.edit && f.plugin.write && !n.text) parts.push(editBox(side, n));
  }
  box.replaceChildren(...parts.filter((p): p is HTMLElement => !!p));
  const pre = box.querySelector<HTMLElement>('pre.srcdiff');
  const first = pre?.querySelector<HTMLElement>('.add, .del');
  if (pre && first) pre.scrollTop = Math.max(0, first.offsetTop - pre.offsetTop - pre.clientHeight / 3);
}

function editBox(side: Side, n: TreeNode): HTMLElement {
  const input = el('input', { value: n.value ?? '' }) as HTMLInputElement;
  const err = el('span', { class: 'err' });
  const apply = () => {
    const problem = n.edit!.apply(input.value.trim());
    if (problem) {
      err.textContent = problem;
      return;
    }
    rewrite(side);
  };
  input.addEventListener('keydown', (e) => e.key === 'Enter' && apply());
  return el('div', { class: 'edit' }, el('label', {}, `Edit (${n.edit!.hint})`), input, el('button', { type: 'button', class: 'btn', onclick: apply }, 'Apply'), err);
}

function sourceEditor(side: Side, n: TreeNode): HTMLElement {
  const ta = el('textarea', { class: 'src', spellcheck: 'false', rows: 16 }) as HTMLTextAreaElement;
  ta.value = n.value ?? '';
  return el('div', { class: 'edit' }, ta, el('button', { type: 'button', class: 'btn', onclick: () => { n.edit!.apply(ta.value); rewrite(side); } }, 'Apply source change'));
}

/** Serialise the edited model with the reading plugin and load the new bytes. */
function rewrite(side: Side): void {
  const f = files[side]!;
  const bytes = f.plugin.write!(f.result.model!);
  files[side] = { ...f, bytes, result: f.plugin.read(bytes, f.fileName) };
  edited[side] = true;
  message = `Edited ${f.fileName} - written again by ${f.plugin.name}${f.result.version === 3 ? ', checksum recalculated' : ''}. Save it with 💾.`;
  render();
}

function compareWithOriginal(): void {
  const edit = files.left!;
  files.right = { ...edit, fileName: edit.fileName.replace(/(\.\w+)?$/, '_edited$1') };
  edited.right = true;
  files.left = originals.left;
  edited.left = false;
  message = '';
  autoExpand();
  render();
}

/** Save the file in another format version, written by that version's plugin. */
function convertSelect(side: Side): HTMLElement {
  const sel = el('select', { class: 'btn', title: 'Save in another format version' },
    el('option', { value: '' }, '⇩ Save as…'),
    ...[3, 2, 1].map((v) => el('option', { value: String(v) }, `format v${v}`)),
  ) as HTMLSelectElement;
  sel.addEventListener('change', () => {
    const v = Number(sel.value);
    sel.value = '';
    const f = files[side];
    const plugin = registry.get(`plcbin-v${v}`);
    if (!f?.result.model || !plugin?.write) return;
    const bytes = plugin.write(f.result.model);
    download(bytes, f.fileName.replace(/(_v\d)?(\.\w+)?$/, `_v${v}.plcbin`));
    message = `Saved ${f.fileName} as format v${v} (${bytes.length} bytes, written by ${plugin.name}).${v < (f.result.version ?? 0) ? ' Older formats drop what they cannot store (v1: no HMI, no producer; v2: no build stamp, no checksum).' : ''}`;
    renderFiles();
  });
  return sel;
}

function download(bytes: Uint8Array, name: string): void {
  const a = el('a', { href: URL.createObjectURL(new Blob([bytes as BlobPart])), download: name });
  document.body.append(a);
  a.click();
  a.remove();
}

function saveFile(side: Side): void {
  const f = files[side]!;
  download(f.bytes, f.fileName.replace(/(\.\w+)?$/, '_edited$1'));
}

// ------------------------------------------------------------------ wiring

$('only-diffs').addEventListener('change', (e) => {
  onlyDiffs = (e.target as HTMLInputElement).checked;
  renderTree();
});
$('expand-all').addEventListener('click', () => {
  const d = currentDiff();
  if (!d) return;
  const visit = (n: DiffNode) => {
    if (n.children.length && !n.path.includes('code')) expanded.add(n.path.join('/'));
    n.children.forEach(visit);
  };
  visit(d.root);
  renderTree();
});
$('collapse-all').addEventListener('click', () => {
  expanded.clear();
  expanded.add('root');
  renderTree();
});
$('next-diff').addEventListener('click', () => {
  const d = currentDiff();
  if (!d || !files.right) return;
  const list = differences(d.root);
  if (!list.length) return;
  const keys = list.map((x) => x.path.join('/'));
  const i = selected ? keys.indexOf(selected) : -1;
  const next = list[(i + 1) % list.length];
  for (let k = 1; k < next.path.length; k++) expanded.add(next.path.slice(0, k).join('/'));
  selected = next.path.join('/');
  renderTree();
  renderDetail();
  document.querySelector(`[data-path="${CSS.escape(selected)}"]`)?.scrollIntoView({ block: 'center' });
});
$('samples-btn').addEventListener('click', () => {
  files.left = null;
  files.right = null;
  selected = null;
  render();
});
document.addEventListener('dragover', (e) => e.preventDefault());
document.addEventListener('drop', (e) => e.preventDefault());

render();

// read-only hook for automated browser tests
(window as unknown as { __binscope: unknown }).__binscope = { files, registry, diff: () => currentDiff() };
