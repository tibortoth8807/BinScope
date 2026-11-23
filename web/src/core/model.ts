/** The decoded content of a .plcbin file, and the tree shown in the UI. */

export type FormatVersion = 1 | 2 | 3;

export interface ModuleRec {
  typeId: string;
  slot: number;
  address: number;
}

export interface DeviceRec {
  name: string;
  typeId: string;
  ip: string;
  mask: string;
  controller: string;
  cycleMs: number;
  modules: ModuleRec[];
}

export interface SymbolRec {
  name: string;
  type: number; // 0 BOOL, 1 INT, 2 REAL
  slot?: number;
  address?: { area: number; byte: number; bit: number; word: boolean };
}

export interface ProgramRec {
  plc: string;
  code: number[];
  consts: number[];
  slotInit: number[];
  symbols: SymbolRec[];
  main: number;
  startup: number;
}

export interface BlockRec {
  plc: string;
  name: string;
  kind: number; // 0 OB, 1 FC, 2 DB
  number: number;
  source: string;
}

export interface TagRec {
  name: string;
  type: number;
  address: string;
  comment: string;
}

export interface TagTableRec {
  plc: string;
  tags: TagRec[];
}

export interface HmiElementRec {
  kind: number; // 0 button, 1 switch, 2 lamp, 3 value, 4 slider
  label: string;
  tag: string;
}

export interface HmiRec {
  name: string;
  controller: string;
  elements: HmiElementRec[];
}

export interface PlcBinModel {
  version: FormatVersion;
  project: { name: string; producer: string; timestamp: number; buildId: Uint8Array };
  devices: DeviceRec[];
  programs: ProgramRec[];
  blocks: BlockRec[];
  tags: TagTableRec[];
  hmis: HmiRec[];
}

export const SECTION = { Project: 1, Device: 2, Program: 3, Block: 4, Tags: 5, Hmi: 6 } as const;
export const SECTION_NAME: Record<number, string> = { 1: 'Project', 2: 'Device', 3: 'Program', 4: 'Block', 5: 'Tags', 6: 'HMI' };
export const TYPE_NAME = ['BOOL', 'INT', 'REAL'];
export const BLOCK_KIND = ['OB', 'FC', 'DB'];
export const HMI_KIND = ['button', 'switch', 'lamp', 'value', 'slider'];
export const AREA_NAME = ['I', 'Q', 'M'];

/** One node of the tree a plugin builds from a file. */
export interface TreeNode {
  /** Stable key among its siblings - the compare engine matches nodes by key path. */
  key: string;
  label: string;
  /** Display value of a leaf. */
  value?: string;
  /** Byte range in the file this node came from. */
  offset?: number;
  length?: number;
  /** Multi-line text (sources) gets a line diff in the detail panel. */
  text?: boolean;
  /** Editable leaf: applies a new value to the model, returns an error message or null. */
  edit?: { hint: string; apply: (value: string) => string | null };
  children?: TreeNode[];
}

export function node(key: string, label: string, children: TreeNode[] = [], extra: Partial<TreeNode> = {}): TreeNode {
  return { key, label, children, ...extra };
}

export function leaf(key: string, label: string, value: string, extra: Partial<TreeNode> = {}): TreeNode {
  return { key, label, value, ...extra };
}

/** Depth-first walk with the key path. */
export function walk(n: TreeNode, fn: (n: TreeNode, path: string[]) => void, path: string[] = []): void {
  const p = [...path, n.key];
  fn(n, p);
  for (const c of n.children ?? []) walk(c, fn, p);
}
