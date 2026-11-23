/** Tree compare with version-dependent filters, and a line diff for sources. */
import { TreeNode } from './model';

export type DiffStatus = 'same' | 'changed' | 'added' | 'removed';

export interface DiffNode {
  key: string;
  label: string;
  path: string[];
  left?: TreeNode;
  right?: TreeNode;
  status: DiffStatus;
  /** Set when a difference exists but a filter hides it. */
  ignoredBy?: string;
  children: DiffNode[];
}

export interface DiffFilter {
  id: string;
  name: string;
  description: string;
  /** Format versions the filter makes sense for. */
  versions: number[];
  /** Enabled by default? */
  byDefault: boolean;
  /**
   * Return true when the difference at this node should be ignored. Called for every node that
   * differs; returning true for a group hides its whole subtree.
   */
  ignores(path: string[], left?: TreeNode, right?: TreeNode): boolean;
}

const endsWith = (path: string[], ...tail: string[]) => tail.every((t, i) => path[path.length - tail.length + i] === t);

/** Source text without comments and with normalised whitespace. */
export function stripComments(src: string): string {
  return src
    .replace(/\(\*[\s\S]*?\*\)/g, ' ')
    .replace(/\/\/[^\n]*/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

export const FILTERS: DiffFilter[] = [
  {
    id: 'build-time', name: 'Ignore build time', versions: [3], byDefault: true,
    description: 'v3 stamps every export with the time of the build.',
    ignores: (p) => endsWith(p, 'project', 'timestamp'),
  },
  {
    id: 'build-id', name: 'Ignore build ID', versions: [3], byDefault: true,
    description: 'v3 gives every export a random build ID.',
    ignores: (p) => endsWith(p, 'project', 'buildId'),
  },
  {
    id: 'checksum', name: 'Ignore checksum', versions: [3], byDefault: true,
    description: 'The v3 CRC-32 trailer changes whenever any byte changes.',
    ignores: (p) => p.includes('trailer'),
  },
  {
    id: 'producer', name: 'Ignore producer', versions: [2, 3], byDefault: true,
    description: 'Version string of the tool that wrote the file (v2+).',
    ignores: (p) => endsWith(p, 'project', 'producer'),
  },
  {
    id: 'layout', name: 'Ignore section layout', versions: [1, 2, 3], byDefault: true,
    description: 'Section offsets and sizes shift whenever content before them grows or shrinks.',
    ignores: (p) => p[1] === 'header' && p.length > 2 && p[2] !== 'version',
  },
  {
    id: 'comments', name: 'Ignore comments & whitespace', versions: [1, 2, 3], byDefault: false,
    description: 'Source changes that only touch comments or formatting, and tag comments.',
    ignores: (p, l, r) =>
      (p[1] === 'tags' && p[p.length - 1] === 'comment') ||
      (!!l?.text && !!r?.text && stripComments(l.value ?? '') === stripComments(r.value ?? '')),
  },
  {
    id: 'machine-code', name: 'Ignore machine code', versions: [1, 2, 3], byDefault: false,
    description: 'Hide bytecode, constants and data layout - compare sources and configuration only.',
    ignores: (p) => p[1] === 'programs' && p.length >= 4 && ['code', 'consts', 'slots', 'main', 'startup', 'symbols'].includes(p[3]),
  },
  {
    id: 'addressing', name: 'Ignore IP addressing', versions: [1, 2, 3], byDefault: false,
    description: 'IP addresses and subnet masks (e.g. a test bench versus the plant network).',
    ignores: (p) => p[1] === 'devices' && (endsWith(p, 'ip') || endsWith(p, 'mask')),
  },
];

/** Filters that apply to a comparison of files with these versions. */
export function applicableFilters(versions: (number | null)[]): DiffFilter[] {
  const vs = versions.filter((v): v is number => v !== null);
  return FILTERS.filter((f) => vs.some((v) => f.versions.includes(v)));
}

export interface DiffResult {
  root: DiffNode;
  stats: { changed: number; added: number; removed: number; ignored: number };
}

export function compareTrees(left: TreeNode, right: TreeNode, filters: DiffFilter[]): DiffResult {
  const stats = { changed: 0, added: 0, removed: 0, ignored: 0 };

  const visit = (l: TreeNode | undefined, r: TreeNode | undefined, path: string[]): DiffNode => {
    const key = (l ?? r)!.key;
    const p = [...path, key];
    const d: DiffNode = { key, label: (r ?? l)!.label, path: p, left: l, right: r, status: 'same', children: [] };

    const lc = l?.children ?? [];
    const rc = r?.children ?? [];
    if (lc.length || rc.length) {
      // match children by key, keep the order of the right side and slot removed ones where they were
      const rKeys = new Map(rc.map((c) => [c.key, c]));
      const lKeys = new Map(lc.map((c) => [c.key, c]));
      const order: string[] = [];
      let ri = 0;
      for (const c of lc) {
        if (rKeys.has(c.key)) {
          while (ri < rc.length && rc[ri].key !== c.key) {
            if (!lKeys.has(rc[ri].key) && !order.includes(rc[ri].key)) order.push(rc[ri].key);
            ri++;
          }
        }
        if (!order.includes(c.key)) order.push(c.key);
      }
      for (const c of rc) if (!order.includes(c.key)) order.push(c.key);
      d.children = order.map((k) => visit(lKeys.get(k), rKeys.get(k), p));
    }

    let raw: DiffStatus;
    if (!l) raw = 'added';
    else if (!r) raw = 'removed';
    else if (d.children.length) raw = d.children.some((c) => c.status !== 'same') ? 'changed' : 'same';
    else raw = l.value === r.value ? 'same' : 'changed';

    if (raw !== 'same') {
      const filter = filters.find((f) => f.ignores(p, l, r));
      if (filter) {
        d.ignoredBy = filter.id;
        markIgnored(d, filter.id);
        return d;
      }
    }
    d.status = raw;
    return d;
  };

  const root = visit(left, right, []);
  for (const d of differences(root)) stats[d.status as 'changed' | 'added' | 'removed']++;
  const countIgnored = (d: DiffNode, parentIgnored: boolean) => {
    if (d.ignoredBy && !parentIgnored) stats.ignored++;
    d.children.forEach((c) => countIgnored(c, parentIgnored || !!d.ignoredBy));
  };
  countIgnored(root, false);
  return { root, stats };
}

function markIgnored(d: DiffNode, id: string): void {
  for (const c of d.children) {
    if (c.status !== 'same') {
      c.ignoredBy = id;
      c.status = 'same';
    }
    markIgnored(c, id);
  }
}

/** Flat list of the visible differences (leaves, plus added/removed groups). */
export function differences(root: DiffNode): DiffNode[] {
  const out: DiffNode[] = [];
  const visit = (d: DiffNode) => {
    if (d.status === 'same') return;
    if (d.status !== 'changed' || d.children.length === 0) {
      out.push(d);
      return;
    }
    d.children.forEach(visit);
  };
  visit(root);
  return out;
}

export type LineOp = { op: ' ' | '-' | '+'; text: string };

/** Line diff (LCS) for block sources. */
export function lineDiff(a: string, b: string): LineOp[] {
  const x = a.split('\n');
  const y = b.split('\n');
  const n = x.length;
  const m = y.length;
  const dp: Uint16Array[] = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) dp[i][j] = x[i] === y[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
  const out: LineOp[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (x[i] === y[j]) {
      out.push({ op: ' ', text: x[i] });
      i++;
      j++;
    } else if (dp[i + 1][j] >= dp[i][j + 1]) out.push({ op: '-', text: x[i++] });
    else out.push({ op: '+', text: y[j++] });
  }
  while (i < n) out.push({ op: '-', text: x[i++] });
  while (j < m) out.push({ op: '+', text: y[j++] });
  return out;
}
