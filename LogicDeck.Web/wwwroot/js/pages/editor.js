import { $, toast, initials } from '../core/dom.js';
import { createHub } from '../core/hub.js';
import '../canvas/shapes.js';
import { createBoard } from '../canvas/board.js';
import { TYPES, HOTKEYS, LABELS, SIZES, iconSvg, createNode, registerIcDefinition, getIcDefinition, getIcSize } from '../canvas/shapes.js';

const roomId = decodeURIComponent(location.pathname.split('/').pop() ?? '');
const hub = createHub();

const ui = {
  connState: $('#connState'),
  memberStack: $('#memberStack'),
  titleInput: $('#titleInput'),
  runToggle: $('#runToggle'),
  runLabel: $('#runLabel'),
  gridSel: $('#gridSel'),
  copyLinkBtn: $('#copyLinkBtn'),
  backBtn: $('#backBtn'),
  palette: $('#palette'),
  wrap: $('#canvasWrap'),
  paperEl: $('#paper'),
  cursorLayer: $('#cursorLayer'),
  hint: $('#canvasHint'),
  zoomVal: $('#zoomVal'),
  zoomIn: $('#zoomInBtn'),
  zoomOut: $('#zoomOutBtn'),
  fitBtn: $('#fitBtn'),
  duplicateBtn: $('#duplicateBtn'),
  undoBtn: $('#undoBtn'),
  redoBtn: $('#redoBtn'),
  packageBtn: $('#packageBtn'),
  truthBtn: $('#truthBtn'),
  truthDlg: $('#truthDlg'),
  truthWrap: $('#truthWrap'),
  truthSummary: $('#truthSummary'),
  truthCopy: $('#truthCopy'),
  truthDownload: $('#truthDownload'),
  truthClose: $('#truthClose'),
  tidyBtn: $('#tidyBtn'),
  clearBtn: $('#clearBtn'),
  exportBtn: $('#exportBtn'),
  importBtn: $('#importBtn'),
  importFile: $('#importFile'),
  packageDlg: $('#packageDlg'),
  packageForm: $('#packageForm'),
  packageName: $('#icNameInput'),
  packagePins: $('#packagePins'),
  packageCancel: $('#packageCancel'),
  sbX: $('#sbX'), sbY: $('#sbY'), sbZoom: $('#sbZoom'),
  sbGrid: $('#sbGrid'), sbParts: $('#sbParts'),
  sbMembers: $('#sbMembers'),
  offBanner: $('#offBanner'),
  gateDlg: $('#nameGateDlg'),
  gateForm: $('#gateForm'),
  gateInput: $('#gateNameInput'),
};

const state = {
  running: false,
  members: [],
  armed: null,
  selection: new Map(),
  applyingRemote: false,
  spacePan: false,
  armedDefinitionId: null,
  customIcs: new Map(),
  packageDraft: null,
  truthTable: null,
};

let board = null;

const SELECTOR_BY_TYPE = {
  And: 'body', Or: 'body', Xor: 'body', Nand: 'body',
  Nor: 'body', Not: 'body', Input: 'case', Output: 'ring', CLK: 'case', Button: 'case', Ic: 'case', Seg7: 'case', FlopD: 'case',
};

const SEG7_MASKS = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F, 0x77, 0x7C, 0x39, 0x5E, 0x79, 0x71];
const SEG7_CHARS = '0123456789AbCdEF';
const SEG7_KEYS = ['segA', 'segB', 'segC', 'segD', 'segE', 'segF', 'segG'];

function setConnStatus(status) {
  const map = {
    online: ['', 'LINK OK'],
    reconnecting: ['warn', 'RECONNECTING'],
    offline: ['off', 'OFFLINE'],
  };
  const [cls, label] = map[status] ?? map.offline;
  ui.connState.className = `dot-status ${cls}`;
  ui.connState.lastElementChild.textContent = label;
}

function throttle(fn, ms) {
  let last = 0;
  let timer = null;
  let ctxArgs = null;
  const invoke = () => { last = Date.now(); timer = null; fn(...ctxArgs); ctxArgs = null; };
  return (...args) => {
    ctxArgs = args;
    if (timer) return;
    const rest = ms - (Date.now() - last);
    timer = setTimeout(invoke, Math.max(0, rest));
  };
}

function buildPalette() {
  ui.palette.replaceChildren();
  const groups = [
    ['IO', ['Input', 'Output', 'CLK', 'Button', 'Seg7']],
    ['GATES', ['And', 'Or', 'Not', 'Xor', 'Xnor', 'Nand', 'Nor']],
    ['SEQ', ['FlopD']],
  ];
  if (state.customIcs.size > 0) {
    groups.push(['CUSTOM ICs', [...state.customIcs.values()].map((definition) => ({ type: 'Ic', definition }))]);
  }
  for (const [label, types] of groups) {
    ui.palette.append(Object.assign(document.createElement('div'), { className: 'micro', textContent: label }));
    for (const item of types) {
      const definition = typeof item === 'string' ? null : item.definition;
      const t = typeof item === 'string' ? item : item.type;
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'pal-btn';
      b.dataset.type = t;
      if (definition) b.dataset.definitionId = definition.id;
      b.title = definition ? definition.name : `${LABELS[t]}  [${HOTKEYS[t]}]`;
      b.innerHTML = `${iconSvg(t)}${definition ? '<span class="hk">IC</span>' : `<span class="hk">${HOTKEYS[t]}</span>`}`;
      b.addEventListener('click', () => {
        const same = state.armed === t && state.armedDefinitionId === (definition?.id ?? null);
        armTool(same ? null : t, definition?.id ?? null);
      });
      ui.palette.append(b);
    }
  }
}

function updateHint() {
  if (!board) return;
  const show = board.graph.getElements().length === 0 && !state.armed;
  ui.hint.classList.toggle('hidden', !show);
}

function armTool(type, definitionId = null) {
  disarmTool();
  state.armed = type;
  state.armedDefinitionId = definitionId;
  if (!type) return;
  ui.wrap.classList.add('placing');
  updateHint();
  const definition = definitionId ? getIcDefinition(definitionId) : null;
  const selector = definitionId
    ? `.pal-btn[data-type="${type}"][data-definition-id="${definitionId}"]`
    : `.pal-btn[data-type="${type}"]`;
  $(selector)?.classList.add('armed');
  ghost = Object.assign(document.createElement('div'), {
    className: 'place-ghost',
    innerHTML: iconSvg(type),
  });
  const size = definition ? getIcSize(definition) : SIZES[type];
  Object.assign(ghost.style, {
    position: 'absolute', pointerEvents: 'none', zIndex: 45,
    width: `${size.w * 0.55}px`, opacity: 0.75, display: 'none',
    filter: 'drop-shadow(0 4px 10px rgba(69,214,164,.35))', color: 'var(--acc)',
  });
  ui.wrap.append(ghost);
}

let ghost = null;
let marquee = null;
let blankDownAt = null;
let groupDrag = null;
let pressedButtonId = null;
let buttonRequest = 0;

function disarmTool() {
  const wasArmed = state.armed;
  state.armed = null;
  state.armedDefinitionId = null;
  ui.wrap.classList.remove('placing');
  document.querySelectorAll('.pal-btn.armed').forEach((b) => b.classList.remove('armed'));
  ghost?.remove();
  ghost = null;
  if (wasArmed) updateHint();
}

ui.wrap.addEventListener('mousemove', (e) => {
  if (!state.armed || !ghost || !board) return;
  const rect = ui.wrap.getBoundingClientRect();
  ghost.style.display = 'block';
  ghost.style.left = `${e.clientX - rect.left}px`;
  ghost.style.top = `${e.clientY - rect.top}px`;
});

ui.wrap.addEventListener('mouseleave', () => { if (ghost) ghost.style.display = 'none'; });

function drawMarquee() {
  if (!marquee) return;
  const { x0, y0, x1, y1, el } = marquee;
  el.style.display = 'block';
  el.style.left = `${Math.min(x0, x1)}px`;
  el.style.top = `${Math.min(y0, y1)}px`;
  el.style.width = `${Math.abs(x1 - x0)}px`;
  el.style.height = `${Math.abs(y1 - y0)}px`;
}

window.addEventListener('pointermove', (e) => {
  if (!marquee) return;
  const rect = ui.wrap.getBoundingClientRect();
  marquee.x1 = e.clientX - rect.left;
  marquee.y1 = e.clientY - rect.top;
  drawMarquee();
});

window.addEventListener('pointerup', (e) => {
  if (!marquee || !board) return;
  const m = marquee;
  marquee = null;
  m.el.remove();
  if (Math.hypot(m.x1 - m.x0, m.y1 - m.y0) < 5) return;
  const rect = ui.wrap.getBoundingClientRect();
  const a = board.paper.clientToLocalPoint(rect.left + Math.min(m.x0, m.x1), rect.top + Math.min(m.y0, m.y1));
  const b = board.paper.clientToLocalPoint(rect.left + Math.max(m.x0, m.x1), rect.top + Math.max(m.y0, m.y1));
  const hits = [];
  for (const el of board.graph.getElements()) {
    const bb = el.getBBox();
    if (bb.x < b.x && bb.x + bb.width > a.x && bb.y < b.y && bb.y + bb.height > a.y) {
      const view = el.findView(board.paper);
      if (view) hits.push({ view, isLink: false });
    }
  }
  for (const l of board.graph.getLinks()) {
    if (l.get('draft')) continue;
    const view = l.findView(board.paper);
    const conn = view?.el.querySelector('.connection');
    if (!view || !conn) continue;
    const cr = conn.getBoundingClientRect();
    const c1 = board.paper.clientToLocalPoint(cr.left, cr.top);
    const c2 = board.paper.clientToLocalPoint(cr.right, cr.bottom);
    if (c1.x < b.x && c2.x > a.x && c1.y < b.y && c2.y > a.y) hits.push({ view, isLink: true });
  }
  if (!e.shiftKey) clearSelection();
  for (const h of hits) {
    if (state.selection.has(h.view.model.id)) continue;
    state.selection.set(h.view.model.id, { hl: highlightCell(h.view, h.isLink), model: h.view.model, isLink: h.isLink });
  }
}, true);

ui.wrap.addEventListener('click', async (e) => {
  if (!state.armed || !board) return;
  if (e.target.closest('.zoom-pill')) return;
  const p = board.paper.clientToLocalPoint(e.clientX, e.clientY);
  const g = board.gridSize;
  const definition = state.armed === 'Ic' ? getIcDefinition(state.armedDefinitionId) : null;
  const s = definition ? getIcSize(definition) : SIZES[state.armed];
  const x = Math.round((p.x - s.w / 2) / g) * g;
  const y = Math.round((p.y - s.h / 2) / g) * g;
  const type = state.armed;
  try {
    const dto = type === 'Ic'
      ? await hub.invoke('AddIcNode', type, x, y, state.armedDefinitionId)
      : await hub.invoke('AddNode', type, x, y);
    addNodeFromDto(dto, /* announce */ false);
    if (!e.shiftKey) disarmTool();
  } catch (err) {
    toast(err.message, 'error');
  }
});

function addNodeFromDto(dto, remote) {
  const prev = state.applyingRemote;
  state.applyingRemote = true;
  const node = createNode(dto.type, dto.x, dto.y, dto.definitionId);
  node.set('id', dto.id);
  node.prop('state', dto.state);
  board.graph.addCell(node, { remote });
  if (dto.type === 'Input') paintInput(node, dto.state);
  if (dto.type === 'CLK') paintClock(node, dto.state);
  if (dto.type === 'Button') paintButton(node, dto.state);
  if (dto.type === 'Seg7') paintSeg7(node, 0);
  if (dto.type === 'FlopD') paintFlop(node, dto.state);
  state.applyingRemote = prev;
  refreshCounts();
  return node;
}

function addWireFromDto(dto) {
  const prev = state.applyingRemote;
  state.applyingRemote = true;
  const link = board.addWireFromDto(dto);
  state.applyingRemote = prev;
  refreshCounts();
  return link;
}

function paintInput(node, on) {
  node.attr({
    knob: { cx: on ? 63 : 47, fill: on ? 'var(--acc)' : '#55606c' },
    track: { fill: on ? 'rgba(69,214,164,.22)' : '#232c37', stroke: on ? 'rgba(69,214,164,.6)' : '#3a4756' },
    digit: { text: on ? '1' : '0', fill: on ? 'var(--acc)' : '#61707f' },
  });
}

function paintClock(node, on) {
  node.attr({
    wave: {
      stroke: on ? 'var(--acc)' : '#5f6c7b',
      ...(on ? { filter: 'drop-shadow(0 0 4px rgba(69,214,164,.55))' } : { filter: 'none' }),
    },
    digit: { text: on ? '1' : '0', fill: on ? 'var(--acc)' : '#61707f' },
    case: { stroke: on ? 'rgba(69,214,164,.75)' : '#a9b7c6' },
  });
}

function paintButton(node, on) {
  node.attr({
    button: { fill: on ? 'rgba(69,214,164,.85)' : '#2a3542', stroke: on ? 'var(--acc)' : '#556575' },
    digit: { text: on ? '1' : '0', fill: on ? 'var(--acc)' : '#61707f' },
    case: { stroke: on ? 'rgba(69,214,164,.75)' : '#a9b7c6' },
  });
}

function paintOutput(node, on, view) {
  node.prop('state', !!on);
  node.attr({
    core: { fill: on ? '#2ea87e' : '#232c37' },
    crossA: { stroke: on ? '#0e4634' : '#3a4756' },
    crossB: { stroke: on ? '#0e4634' : '#3a4756' },
  });
  view?.el.classList.toggle('lamp-core-on', !!on && state.running);
}

function paintSeg7(node, mask) {
  const lit = SEG7_KEYS.map((key, bit) => {
    const on = (mask & (1 << bit)) !== 0;
    return [key, on
      ? { fill: 'var(--acc)', filter: 'drop-shadow(0 0 4px rgba(69,214,164,.5))' }
      : { fill: '#1c242e', filter: 'none' }];
  });
  const index = SEG7_MASKS.indexOf(mask);
  node.attr(Object.fromEntries([
    ...lit,
    ['digit', { text: state.running && index >= 0 ? SEG7_CHARS[index] : '' }],
  ]));
}

function paintFlop(node, q) {
  const lit = !!q && state.running;
  node.prop('state', !!q);
  node.attr({
    qMark: {
      fill: lit ? 'var(--acc)' : '#5f6c7b',
      ...(lit ? { filter: 'drop-shadow(0 0 4px rgba(69,214,164,.5))' } : { filter: 'none' }),
    },
    qbMark: { fill: !!q ? '#5f6c7b' : '#2b3644' },
  });
}

function findNode(id) { return board.graph.getCell(id); }

async function toggleInput(node) {
  if (!state.running) {
    toast('Press RUN first to power the bench.', 'warn');
    return;
  }
  const next = !(node.prop('state') === true);
  paintInput(node, next);
  node.prop('state', next);
  try {
    const confirmed = await hub.invoke('ToggleInput', node.id);
    if (confirmed !== next) {
      node.prop('state', confirmed);
      paintInput(node, confirmed);
    }
  } catch (err) {
    node.prop('state', !next);
    paintInput(node, !next);
    toast(err.message, 'error');
  }
}

async function setButton(node, pressed) {
  if (pressed && !state.running) {
    toast('Press RUN first to power the bench.', 'warn');
    return;
  }
  const request = ++buttonRequest;
  if (pressed) pressedButtonId = node.id;
  else if (pressedButtonId === node.id) pressedButtonId = null;
  node.prop('state', pressed);
  paintButton(node, pressed);
  try {
    const confirmed = await hub.invoke('SetButton', node.id, pressed);
    if (request !== buttonRequest) return;
    node.prop('state', confirmed);
    paintButton(node, confirmed);
  } catch (err) {
    if (request !== buttonRequest) return;
    node.prop('state', !pressed);
    paintButton(node, !pressed);
    toast(err.message, 'error');
  }
}

function releaseButton() {
  if (!pressedButtonId) return;
  const node = findNode(pressedButtonId);
  pressedButtonId = null;
  if (node) void setButton(node, false);
}

const pendingMoves = new Map();
let moveTimer = null;

function flushMovesNow() {
  if (moveTimer) { clearTimeout(moveTimer); moveTimer = null; }
  for (const [id, m] of pendingMoves) {
    hub.invoke('MoveNode', id, m.x, m.y).catch(() => {});
  }
  pendingMoves.clear();
}

function scheduleMoveBroadcast(node) {
  const p = node.position();
  pendingMoves.set(node.id, p);
  if (moveTimer) return;
  moveTimer = setTimeout(() => { moveTimer = null; flushMovesNow(); }, 90);
}

const packageableTypes = new Set(['And', 'Or', 'Not', 'Xor', 'Xnor', 'Nand', 'Nor']);

function selectedNodes() {
  return [...state.selection.values()].filter((s) => !s.isLink).map((s) => s.model);
}

function canPackageSelection() {
  const nodes = selectedNodes();
  return nodes.length > 0 && nodes.every((node) => packageableTypes.has(String(node.get('type') ?? '').split('.')[1]));
}

function packageBoundary() {
  const nodes = selectedNodes();
  if (!canPackageSelection()) return null;
  const selectedIds = new Set(nodes.map((node) => node.id));
  const links = board.graph.getLinks().filter((link) => !link.get('draft'));
  const inputs = [];
  const outputs = [];
  for (const node of nodes) {
    const type = String(node.get('type') ?? '').split('.')[1];
    const inputCount = ['And', 'Or', 'Xor', 'Xnor', 'Nand', 'Nor'].includes(type) ? 2 : type === 'Not' ? 1 : 0;
    for (let pin = 0; pin < inputCount; pin++) {
      const port = `in${pin}`;
      const incoming = links.find((link) => link.target().id === node.id && link.target().port === port);
      if (!incoming || !selectedIds.has(incoming.source().id)) {
        inputs.push({ key: `${node.id}:${port}`, label: `IN${inputs.length + 1}` });
      }
    }
    if (type !== 'Output' && !links.some((link) => link.source().id === node.id && selectedIds.has(link.target().id))) {
      outputs.push({ key: node.id, label: `OUT${outputs.length + 1}` });
    }
  }
  return outputs.length > 0 ? { nodeIds: [...selectedIds], inputs, outputs } : null;
}

function buildExportData() {
  return {
    format: 'logicdeck-circuit',
    version: 1,
    title: ui.titleInput.value.trim() || 'Untitled circuit',
    gridSize: board.gridSize,
    nodes: board.graph.getElements().map((node) => {
      const rawType = String(node.get('type') ?? '');
      const type = rawType.startsWith('ld.Ic_') ? 'Ic' : rawType.split('.')[1];
      const position = node.position();
      return {
        id: node.id,
        type,
        x: position.x,
        y: position.y,
        state: node.prop('state') === true,
        definitionId: node.get('definitionId') ?? null,
      };
    }),
    wires: board.graph.getLinks().filter((link) => !link.get('draft')).map((link) => {
      const source = link.source();
      const target = link.target();
      return {
        id: link.id,
        fromNodeId: source.id,
        toNodeId: target.id,
        toPin: Number(String(target.port).replace('in', '')),
        fromPort: source.port ?? 'out',
      };
    }),
    customIcs: [...state.customIcs.values()],
  };
}

function exportCircuit() {
  const data = buildExportData();
  const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `${data.title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'circuit'}.json`;
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 0);
  toast('Circuit exported as JSON.', 'ok');
}

async function importCircuitFile(event) {
  const file = event.target.files?.[0];
  event.target.value = '';
  if (!file) return;
  try {
    if (file.size > 2_000_000) throw new Error('That circuit file is too large.');
    const data = JSON.parse(await file.text());
    if (data?.format !== 'logicdeck-circuit' || data.version !== 1 || !Array.isArray(data.nodes) || !Array.isArray(data.wires))
      throw new Error('That file is not a supported LogicDeck circuit.');
    if (!window.confirm('Import this circuit and replace the current bench?')) return;
    const snapshot = await hub.invoke('ImportCircuit', {
      version: 1,
      title: data.title ?? 'Untitled circuit',
      gridSize: data.gridSize ?? 16,
      nodes: data.nodes,
      wires: data.wires,
      customIcs: data.customIcs ?? [],
    });
    applySnapshot(snapshot);
    ui.titleInput.dataset.saved = snapshot.title;
    toast('Circuit imported.', 'ok');
  } catch (err) {
    toast(err.message, 'error');
  }
}

function openPackageDialog() {
  const draft = packageBoundary();
  if (!draft) {
    toast('Select connected combinational gates with at least one output.', 'warn');
    return;
  }
  state.packageDraft = draft;
  ui.packageName.value = '';
  ui.packagePins.replaceChildren();
  for (const [kind, pins] of [['Input', draft.inputs], ['Output', draft.outputs]]) {
    for (const pin of pins) {
      const row = document.createElement('div');
      row.className = 'package-pin-row';
      const label = document.createElement('label');
      label.className = 'micro';
      label.textContent = `${kind} ${pin.label.replace(/^(IN|OUT)/, '')}`;
      const input = document.createElement('input');
      input.type = 'text';
      input.value = pin.label;
      input.maxLength = 24;
      input.dataset.pinKey = pin.key;
      input.dataset.pinKind = kind.toLowerCase();
      row.append(label, input);
      ui.packagePins.append(row);
    }
  }
  ui.packageDlg.showModal();
  ui.packageName.focus();
}

function updateSelectionUi() {
  ui.duplicateBtn.disabled = selectedNodes().length === 0;
  ui.packageBtn.disabled = !canPackageSelection();
  ui.tidyBtn.disabled = !board || board.graph.getElements().length < 2;
  ui.clearBtn.disabled = !board || board.graph.getElements().length === 0;
}

function setHistoryUi(canUndo, canRedo) {
  ui.undoBtn.disabled = !canUndo;
  ui.redoBtn.disabled = !canRedo;
}

async function runHistory(command) {
  try {
    const snapshot = await hub.invoke(command);
    applySnapshot(snapshot);
  } catch (err) {
    toast(err.message, 'error');
  }
}

async function tidyLayout() {
  try {
    const ids = selectedNodes().map((node) => node.id);
    const result = await hub.invoke('TidyLayout', ids);
    const prev = state.applyingRemote;
    state.applyingRemote = true;
    for (const dto of result.nodes ?? []) {
      const node = findNode(dto.id);
      if (node) node.position(dto.x, dto.y, { remote: true });
    }
    state.applyingRemote = prev;
    clearSelection();
  } catch (err) {
    toast(err.message, 'error');
  }
}

function clearSelection() {
  for (const s of state.selection.values()) s.hl.remove();
  state.selection.clear();
  updateSelectionUi();
}

function highlightCell(view, isLink) {
  const typeName = String(view.model.get('type') ?? '').split('.')[1];
  const selector = isLink ? '.connection' : typeName.startsWith('Ic_') ? 'case' : SELECTOR_BY_TYPE[typeName] ?? 'body';
  return joint.highlighters.stroke.add(board.paper, view, selector, {
    stroke: '#45d6a4', strokeWidth: 2, padding: 6,
  });
}

function selectCell(view, isLink, additive) {
  const id = view.model.id;
  if (!additive && !(state.selection.size === 1 && state.selection.has(id))) clearSelection();
  if (state.selection.has(id)) {
    if (additive) {
      state.selection.get(id).hl.remove();
      state.selection.delete(id);
      updateSelectionUi();
    }
    return;
  }
  state.selection.set(id, { hl: highlightCell(view, isLink), model: view.model, isLink });
  updateSelectionUi();
}

async function duplicateSelection() {
  const nodeIds = [...state.selection.values()]
    .filter((s) => !s.isLink)
    .map((s) => s.model.id);
  if (nodeIds.length === 0) return;
  try {
    const result = await hub.invoke('DuplicateNodes', nodeIds);
    for (const dto of result.nodes ?? []) addNodeFromDto(dto, false);
    for (const dto of result.wires ?? []) addWireFromDto(dto);
    clearSelection();
    for (const dto of result.nodes ?? []) {
      const view = board.graph.getCell(dto.id)?.findView(board.paper);
      if (view) selectCell(view, false, true);
    }
  } catch (err) {
    toast(err.message, 'error');
  }
}

async function deleteSelection() {
  if (state.selection.size === 0) return;
  const items = [...state.selection.values()];
  clearSelection();
  const nodeIds = items.filter((s) => !s.isLink).map((s) => s.model.id);
  const linkIds = items.filter((s) => s.isLink).map((s) => s.model.id);
  for (const id of linkIds) board.graph.getCell(id)?.remove();
  for (const id of nodeIds) board.graph.getCell(id)?.remove();
  refreshCounts();
  try {
    for (const id of linkIds) await hub.invoke('RemoveWire', id);
    if (nodeIds.length > 0) await hub.invoke('DeleteNodes', nodeIds);
  } catch (err) {
    toast(err.message, 'error');
  }
}

async function clearCircuit() {
  if (!board) return;
  const cells = board.graph.getCells().filter((cell) => !cell.get('draft'));
  const nodeIds = cells.filter((cell) => !cell.isLink()).map((cell) => cell.id);
  if (nodeIds.length === 0) return;
  if (!window.confirm('Clear this circuit? All parts and wires will be deleted.')) return;
  clearSelection();
  for (const cell of cells) cell.remove();
  refreshCounts();
  try {
    await hub.invoke('DeleteNodes', nodeIds);
  } catch (err) {
    toast(err.message, 'error');
  }
}

function registerIc(definition) {
  registerIcDefinition(definition);
  state.customIcs.set(definition.id, definition);
  buildPalette();
}

function applyIcPackage(result) {
  const prev = state.applyingRemote;
  state.applyingRemote = true;
  for (const id of result.removedNodeIds ?? []) board.graph.getCell(id)?.remove();
  for (const id of result.removedWireIds ?? []) board.graph.getCell(id)?.remove();
  for (const wire of result.addedWires ?? []) addWireFromDto(wire);
  state.applyingRemote = prev;
  registerIc(result.definition);
  addNodeFromDto(result.node, false);
  clearSelection();
  refreshCounts();
}

function applySnapshot(s) {
  state.customIcs.clear();
  for (const definition of s.customIcs ?? []) {
    registerIcDefinition(definition);
    state.customIcs.set(definition.id, definition);
  }
  buildPalette();
  board.setGridSize(s.gridSize);
  ui.gridSel.value = String(s.gridSize);
  ui.sbGrid.textContent = String(s.gridSize);
  ui.titleInput.value = s.title;
  ui.titleInput.dataset.saved = s.title;
  setRunningUI(s.running);
  state.members = s.members ?? [];
  renderMembers();
  clearSelection();
  state.applyingRemote = true;
  board.clearAll();
  for (const n of s.nodes ?? []) {
    const node = createNode(n.type, n.x, n.y, n.definitionId);
    node.set('id', n.id);
    node.prop('state', n.state);
    board.graph.addCell(node, { remote: true });
    if (n.type === 'Input') paintInput(node, n.state);
    if (n.type === 'CLK') paintClock(node, n.state);
    if (n.type === 'Button') paintButton(node, n.state);
    if (n.type === 'Seg7') paintSeg7(node, 0);
    if (n.type === 'FlopD') paintFlop(node, n.state);
  }
  for (const w of s.wires ?? []) board.addWireFromDto(w);
  state.applyingRemote = false;
  clearSignals();
  refreshCounts();
  if (s.signals && s.running) applySignals(s.signals);
}

function setRunningUI(on) {
  state.running = on;
  ui.runToggle.classList.toggle('on', on);
  ui.runLabel.textContent = on ? 'STOP' : 'RUN';
  ui.runToggle.querySelector('i').className = on ? 'bi bi-pause-fill' : 'bi bi-power';
}

function setRunning(on) {
  state.running = on;
  if (!on) {
    releaseButton();
    clearSignals();
  }
}

function clearSignals() {
  if (!board) return;
  for (const l of board.graph.getLinks()) {
    l.findView(board.paper)?.el.classList.remove('sig-high', 'sig-low', 'sig-unstable');
  }
  for (const n of board.graph.getElements()) {
    n.findView(board.paper)?.el.classList.remove('lamp-core-on');
    const t = n.get('type');
    if (t === 'ld.Output') paintOutput(n, false, n.findView(board.paper));
    else if (t === 'ld.Seg7') paintSeg7(n, 0);
    else if (t === 'ld.FlopD') paintFlop(n, false);
  }
}

function applySignals(frame) {
  if (!board || !state.running) return;
  const unstable = new Set(frame.unstable ?? []);
  for (const l of board.graph.getLinks()) {
    if (l.get('draft')) continue;
    const cls = unstable.has(l.id) ? 'sig-unstable'
      : frame.wires[l.id] === true ? 'sig-high'
        : frame.wires[l.id] === false ? 'sig-low' : '';
    const view = l.findView(board.paper);
    view.el.classList.remove('sig-high', 'sig-low', 'sig-unstable');
    if (cls) view.el.classList.add(cls);
  }
  const sources = frame.sources ?? {};
  for (const n of board.graph.getElements()) {
    const t = n.get('type');
    if (t === 'ld.Output') {
      const on = frame.outputs?.[n.id] === true;
      paintOutput(n, on, n.findView(board.paper));
    } else if (t === 'ld.CLK' && n.id in sources) {
      paintClock(n, sources[n.id] === true);
    } else if (t === 'ld.Input' && n.id in sources) {
      paintInput(n, sources[n.id] === true);
      n.prop('state', sources[n.id] === true);
    } else if (t === 'ld.Seg7') {
      paintSeg7(n, frame.displays?.[n.id] ?? 0);
    } else if (t === 'ld.FlopD') {
      paintFlop(n, frame.flops?.[n.id] === true);
    }
  }
}

function refreshCounts() {
  if (!board) return;
  const parts = board.graph.getElements().length;
  ui.sbParts.textContent = String(parts);
  updateSelectionUi();
  updateHint();
}

function el_span(m) {
  const s = document.createElement('span');
  s.className = 'avatar';
  s.title = m.name;
  s.style.background = m.color;
  s.textContent = initials(m.name);
  return s;
}

function renderMembers() {
  const nodes = state.members.slice(0, 4).map(el_span);
  if (state.members.length > 4) {
    const more = document.createElement('span');
    more.className = 'more';
    more.textContent = `+${state.members.length - 4}`;
    nodes.push(more);
  }
  ui.memberStack.replaceChildren(...nodes);
  ui.sbMembers.textContent = `${state.members.length} ONLINE`;
}

const cursors = new Map();

function onCursorMoved(sid, name, color, x, y) {
  let c = cursors.get(sid);
  if (!c) {
    const root = document.createElement('div');
    root.className = 'r-cursor';
    root.innerHTML = `
      <svg width="17" height="20" viewBox="0 0 17 20"><path d="M1 1 L6.2 18 L8.8 11.4 L16 9 Z"
        fill="${color}" stroke="#0b0f14" stroke-width="1"/></svg>
      <span class="tag" style="background:${color}">${name}</span>`;
    ui.cursorLayer.append(root);
    c = { root, tx: x, ty: y, cx: x, cy: y, idleTimer: null };
    cursors.set(sid, c);
  }
  c.tx = x; c.ty = y;
  clearTimeout(c.idleTimer);
  c.root.classList.remove('idle');
  c.idleTimer = setTimeout(() => c.root.classList.add('idle'), 2200);
}

(function cursorLoop() {
  for (const c of cursors.values()) {
    c.cx += (c.tx - c.cx) * 0.28;
    c.cy += (c.ty - c.cy) * 0.28;
    c.root.style.transform = `translate(${c.cx}px, ${c.cy}px)`;
  }
  requestAnimationFrame(cursorLoop);
})();

const sendCursor = throttle((x, y) => {
  hub.invoke('SendCursor', x, y).catch(() => {});
}, 70);

function bindBoardEvents() {
  const { graph, paper } = board;

  graph.on('change:position', (node, pos, opt) => {
    if (state.applyingRemote || opt?.remote) return;
    if (groupDrag && !opt?.groupMove && node.id === groupDrag.id && state.selection.size > 1) {
      const dx = pos.x - groupDrag.last.x;
      const dy = pos.y - groupDrag.last.y;
      groupDrag.last = { x: pos.x, y: pos.y };
      if (dx !== 0 || dy !== 0) {
        for (const s of state.selection.values()) {
          if (s.model.id !== node.id && !s.model.isLink()) {
            const p = s.model.position();
            s.model.position(p.x + dx, p.y + dy, { groupMove: true });
          }
        }
      }
    }
    scheduleMoveBroadcast(node);
  });

  graph.on('remove', (cell) => {
    if (state.applyingRemote || cell.get('draft')) return;
    refreshCounts();
  });
  graph.on('add', () => refreshCounts());

  paper.on('element:pointerup', () => { groupDrag = null; flushMovesNow(); });
  window.addEventListener('pointerup', releaseButton);
  window.addEventListener('pointercancel', releaseButton);
  window.addEventListener('blur', releaseButton);

  paper.on('element:pointerdown', (view, evt) => {
    if (view.model.get('type') === 'ld.Button' && !evt?.shiftKey) {
      void setButton(view.model, true);
      return;
    }
    groupDrag = { id: view.model.id, last: { ...view.model.position() } };
  });

  paper.on('element:pointerclick', (view, evt) => {
    if (state.armed) return;
    const t = view.model.get('type');
    if (t === 'ld.Input' && !evt?.shiftKey) {
      toggleInput(view.model);
      return;
    }
    selectCell(view, false, !!evt?.shiftKey);
  });

  paper.on('link:pointerclick', (view, evt) => {
    if (state.armed) return;
    if (view.model.get('draft')) return;
    selectCell(view, true, !!evt?.shiftKey);
  });

  paper.on('blank:pointerdown', (evt) => {
    if (evt.button === 0) blankDownAt = { x: evt.clientX, y: evt.clientY };
    if (state.armed || state.spacePan || evt.button !== 0) return;
    const rect = ui.wrap.getBoundingClientRect();
    const el = document.createElement('div');
    el.className = 'rubber-band';
    ui.wrap.append(el);
    marquee = {
      x0: evt.clientX - rect.left, y0: evt.clientY - rect.top,
      x1: evt.clientX - rect.left, y1: evt.clientY - rect.top, el,
    };
  });

  paper.on('blank:pointerclick', (evt) => {
    if (state.armed) return;
    if (blankDownAt && evt && Math.hypot(evt.clientX - blankDownAt.x, evt.clientY - blankDownAt.y) > 5) return;
    clearSelection();
  });

  paper.on('link:pointerup', async (linkView) => {
    const link = linkView.model;
    if (!link.get('draft')) return;
    const src = link.source();
    const tgt = link.target();
    if (!src.id || !tgt.id || !src.port || !tgt.port) return;
    const forward = /^out\d*$/.test(src.port) && /^in\d+$/.test(tgt.port);
    const reverse = /^in\d+$/.test(src.port) && /^out\d*$/.test(tgt.port);
    if (!forward && !reverse) return;
    const inputPort = forward ? tgt.port : src.port;
    const outputPort = forward ? src.port : tgt.port;
    const pin = Number(String(inputPort).replace('in', ''));
    const fromPin = outputPort === 'out' ? 0 : Number(String(outputPort).replace('out', ''));
    if (Number.isNaN(pin) || Number.isNaN(fromPin)) return;
    const fromNodeId = forward ? src.id : tgt.id;
    const toNodeId = forward ? tgt.id : src.id;
    link.remove();
    try {
      const dto = fromPin === 0
        ? await hub.invoke('AddWire', fromNodeId, toNodeId, pin)
        : await hub.invoke('AddIcWire', fromNodeId, toNodeId, pin, fromPin);
      addWireFromDto(dto);
    } catch (err) {
      toast(err.message, 'error');
      refreshCounts();
    }
  });

  ui.wrap.addEventListener('pointermove', (e) => {
    if (!board) return;
    const p = board.paper.clientToLocalPoint(e.clientX, e.clientY);
    ui.sbX.textContent = String(Math.round(p.x));
    ui.sbY.textContent = String(Math.round(p.y));
    sendCursor(p.x, p.y);
  });

  ui.wrap.addEventListener('contextmenu', (e) => {
    if (state.armed) {
      e.preventDefault();
      disarmTool();
    }
  });
}

function updateZoomLabel(s) {
  ui.zoomVal.textContent = `${Math.round(s * 100)}%`;
  ui.sbZoom.textContent = `${Math.round(s * 100)}%`;
}

function truthTableCsv(dto) {
  const header = [...dto.inputs, ...dto.outputs].map((v) => v.label);
  const lines = [header.join(',')];
  for (const row of dto.rows) {
    lines.push([...row.inputs, ...row.outputs].map((v) => (v ? 1 : 0)).join(','));
  }
  return lines.join('\n');
}

function renderTruthTable(dto) {
  ui.truthSummary.textContent =
    `${dto.inputs.length} input${dto.inputs.length === 1 ? '' : 's'} · `
    + `${dto.outputs.length} output${dto.outputs.length === 1 ? '' : 's'} · `
    + `${dto.rows.length} row${dto.rows.length === 1 ? '' : 's'}`;

  const cell = (value, isOutput) => {
    const td = document.createElement('td');
    td.className = isOutput ? 'truth-v out' : 'truth-v';
    td.textContent = value ? '1' : '0';
    if (value) td.classList.add('hi');
    return td;
  };

  const table = document.createElement('table');
  table.className = 'truth-table';
  const thead = document.createElement('thead');
  const headRow = document.createElement('tr');
  for (const v of dto.inputs) {
    const th = document.createElement('th');
    th.className = 'truth-in';
    th.textContent = v.label;
    headRow.append(th);
  }
  for (const v of dto.outputs) {
    const th = document.createElement('th');
    th.className = 'truth-out';
    th.textContent = v.label;
    headRow.append(th);
  }
  thead.append(headRow);
  const tbody = document.createElement('tbody');
  for (const row of dto.rows) {
    const tr = document.createElement('tr');
    for (const value of row.inputs) tr.append(cell(value, false));
    for (const value of row.outputs) tr.append(cell(value, true));
    tbody.append(tr);
  }
  table.append(thead, tbody);
  ui.truthWrap.replaceChildren(table);
}

async function openTruthTable() {
  try {
    const dto = await hub.invoke('ComputeTruthTable');
    state.truthTable = dto;
    renderTruthTable(dto);
    ui.truthDlg.showModal();
  } catch (err) {
    toast(err.message, 'error');
  }
}

function bindUiEvents() {
  ui.zoomIn.addEventListener('click', () => board.zoomIn());
  ui.zoomOut.addEventListener('click', () => board.zoomOut());
  ui.fitBtn.addEventListener('click', () => board.fit());
  ui.duplicateBtn.addEventListener('click', duplicateSelection);
  ui.undoBtn.addEventListener('click', () => runHistory('Undo'));
  ui.redoBtn.addEventListener('click', () => runHistory('Redo'));
  ui.tidyBtn.addEventListener('click', tidyLayout);
  ui.clearBtn.addEventListener('click', clearCircuit);
  ui.exportBtn.addEventListener('click', exportCircuit);
  ui.importBtn.addEventListener('click', () => ui.importFile.click());
  ui.importFile.addEventListener('change', importCircuitFile);
  ui.packageBtn.addEventListener('click', openPackageDialog);
  ui.packageCancel.addEventListener('click', () => ui.packageDlg.close());
  ui.packageForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (!state.packageDraft) return;
    const name = ui.packageName.value.trim() || 'Custom IC';
    const inputLabels = {};
    const outputLabels = {};
    for (const input of ui.packagePins.querySelectorAll('input')) {
      const value = input.value.trim() || input.defaultValue;
      if (input.dataset.pinKind === 'input') inputLabels[input.dataset.pinKey] = value;
      else outputLabels[input.dataset.pinKey] = value;
    }
    try {
      const result = await hub.invoke('PackageIc', name, state.packageDraft.nodeIds, inputLabels, outputLabels);
      applyIcPackage(result);
      state.packageDraft = null;
      ui.packageDlg.close();
      toast(`${result.definition.name} is ready in the custom IC palette.`, 'ok');
    } catch (err) {
      toast(err.message, 'error');
    }
  });
  ui.truthBtn.addEventListener('click', openTruthTable);
  ui.truthClose.addEventListener('click', () => ui.truthDlg.close());
  ui.truthCopy.addEventListener('click', async () => {
    if (!state.truthTable) return;
    try {
      await navigator.clipboard.writeText(truthTableCsv(state.truthTable));
      toast('Truth table copied as CSV.', 'ok');
    } catch {
      toast('Could not access the clipboard — use Download instead.', 'warn');
    }
  });
  ui.truthDownload.addEventListener('click', () => {
    if (!state.truthTable) return;
    const name = (ui.titleInput.value || 'circuit').replace(/[^\w.-]+/g, '_');
    const blob = new Blob([truthTableCsv(state.truthTable)], { type: 'text/csv' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${name}-truth-table.csv`;
    a.click();
    URL.revokeObjectURL(url);
  });

  ui.backBtn.addEventListener('click', async () => {
    try { await hub.invoke('LeaveRoom'); } catch {}
    location.href = '/';
  });

  ui.runToggle.addEventListener('click', async () => {
    const next = !state.running;
    setRunningUI(next);
    try {
      await hub.invoke('SetRunning', next);
      setRunning(next);
    } catch (err) {
      setRunningUI(!next);
      toast(err.message, 'error');
    }
  });

  ui.gridSel.addEventListener('change', async () => {
    const val = Number(ui.gridSel.value);
    const old = board.gridSize;
    board.setGridSize(val);
    ui.sbGrid.textContent = String(val);
    try {
      await hub.invoke('SetGridSize', val);
    } catch (err) {
      board.setGridSize(old);
      ui.gridSel.value = String(old);
      ui.sbGrid.textContent = String(old);
      toast(err.message, 'error');
    }
  });

  const commitTitle = async () => {
    const val = ui.titleInput.value.trim() || 'Untitled circuit';
    if (val === ui.titleInput.dataset.saved) return;
    ui.titleInput.dataset.saved = val;
    try {
      const summary = await hub.invoke('RenameRoom', val);
      ui.titleInput.value = summary.title;
    } catch (err) {
      toast(err.message, 'error');
    }
  };
  ui.titleInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); ui.titleInput.blur(); }
    if (e.key === 'Escape') { ui.titleInput.value = ui.titleInput.dataset.saved ?? ''; ui.titleInput.blur(); }
  });
  ui.titleInput.addEventListener('blur', commitTitle);

  ui.copyLinkBtn.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(location.href);
      toast('Invite link copied — send it to your teammate.', 'ok');
    } catch {
      toast(location.href, 'info', 6000);
    }
  });

  document.addEventListener('keydown', (e) => {
    if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;
    if (e.code === 'Space') {
      const t = e.target;
      const interactive = t instanceof HTMLSelectElement
        || t instanceof HTMLButtonElement
        || t instanceof HTMLAnchorElement
        || (t instanceof Element && t.closest('button, a, select, [role="button"]'));
      if (interactive) return;
      e.preventDefault();
      if (!state.spacePan) {
        state.spacePan = true;
        ui.wrap.classList.add('space-pan');
      }
      return;
    }
    if (e.key === 'Escape') {
      if (marquee) {
        marquee.el.remove();
        marquee = null;
        return;
      }
      disarmTool();
      clearSelection();
      return;
    }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'd') {
      e.preventDefault();
      duplicateSelection();
      return;
    }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z') {
      e.preventDefault();
      runHistory(e.shiftKey ? 'Redo' : 'Undo');
      return;
    }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'y') {
      e.preventDefault();
      runHistory('Redo');
      return;
    }
    if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); deleteSelection(); return; }
    const byHotkey = Object.entries(HOTKEYS).find(([, k]) => k === e.key);
    if (byHotkey) armTool(state.armed === byHotkey[0] ? null : byHotkey[0]);
  });

  window.addEventListener('beforeunload', () => {
    try { hub.conn.invoke('LeaveRoom'); } catch {}
  });

  document.addEventListener('keyup', (e) => {
    if (e.code === 'Space' && state.spacePan) {
      state.spacePan = false;
      ui.wrap.classList.remove('space-pan');
    }
  });
  window.addEventListener('blur', () => {
    state.spacePan = false;
    ui.wrap.classList.remove('space-pan');
  });
}

function bindHubEvents() {
  hub.on('RoomsChanged', () => {});
  hub.on('MemberJoined', (m) => {
    if (!state.members.some((x) => x.sessionId === m.sessionId)) state.members.push(m);
    renderMembers();
    toast(`${m.name} joined the circuit.`, 'ok');
  });
  hub.on('MemberLeft', (m) => {
    state.members = state.members.filter((x) => x.sessionId !== m.sessionId);
    renderMembers();
    toast(`${m.name} left.`, 'info');
  });
  hub.on('NodeAdded', (dto) => addNodeFromDto(dto, true));
  hub.on('NodeMoved', (id, x, y, sid) => {
    const node = findNode(id);
    if (!node) return;
    state.applyingRemote = true;
    node.position(x, y, { remote: true });
    state.applyingRemote = false;
    void sid;
  });
  hub.on('NodesDeleted', (ids) => {
    state.applyingRemote = true;
    for (const id of ids) board.graph.getCell(id)?.remove();
    state.applyingRemote = false;
    clearSelection();
    refreshCounts();
  });
  hub.on('IcPackaged', (definition, node, removedNodeIds, removedWireIds, addedWires) => {
    applyIcPackage({ definition, node, removedNodeIds, removedWireIds, addedWires });
  });
  hub.on('CircuitReplaced', (snapshot) => applySnapshot(snapshot));
  hub.on('HistoryChanged', (canUndo, canRedo) => setHistoryUi(canUndo, canRedo));
  hub.on('WireAdded', (dto) => addWireFromDto(dto));
  hub.on('WireRemoved', (id) => {
    state.applyingRemote = true;
    board.graph.getCell(id)?.remove();
    state.applyingRemote = false;
    clearSelection();
    refreshCounts();
  });
  hub.on('TitleChanged', (t) => { ui.titleInput.value = t; ui.titleInput.dataset.saved = t; });
  hub.on('GridSizeChanged', (g) => {
    board.setGridSize(g);
    ui.gridSel.value = String(g);
    ui.sbGrid.textContent = String(g);
  });
  hub.on('InputToggled', (id, st) => {
    const node = findNode(id);
    if (node) { node.prop('state', st); paintInput(node, st); }
  });
  hub.on('ButtonChanged', (id, st) => {
    const node = findNode(id);
    if (node) { node.prop('state', st); paintButton(node, st); }
  });
  hub.on('RunningChanged', (running) => setRunning(running));
  hub.on('Signals', (frame) => applySignals(frame));
  hub.on('CursorMoved', (sid, name, color, x, y) => onCursorMoved(sid, name, color, x, y));

  hub.onReconnecting = () => {
    setConnStatus('reconnecting');
    ui.offBanner.hidden = false;
    disarmTool();
  };
  hub.onReconnected = async () => {
    setConnStatus('online');
    try {
      await hub.joinLobby(hub.state.name);
      const s = await hub.invoke('EnterRoom', roomId);
      applySnapshot(s);
    } catch (err) {
      toast(err.message, 'error');
    }
    ui.offBanner.hidden = true;
  };
  hub.onClose = () => {
    setConnStatus('offline');
    ui.offBanner.hidden = false;
  };
}

(async function boot() {
  buildPalette();
  board = createBoard(ui.paperEl, {
    gridSize: 16,
    onZoom: updateZoomLabel,
    isSpacePan: () => state.spacePan,
    isArmed: () => !!state.armed,
  });
  window.__ld = { board, selection: () => state.selection.size, exportData: buildExportData, state };
  bindBoardEvents();
  bindUiEvents();
  bindHubEvents();

  setConnStatus((await hub.start()) ? 'online' : 'offline');

  try {
    await ensureIdentity();
    const snapshot = await hub.invoke('EnterRoom', roomId);
    applySnapshot(snapshot);
    ui.titleInput.dataset.saved = snapshot.title;
    setTimeout(() => board.fit(), 30);
  } catch (err) {
    toast(err.message ?? 'Failed to open this circuit.', 'error');
    toast('Returning to the lobby…', 'info');
    setTimeout(() => { location.href = '/'; }, 1400);
  }
})();

function ensureIdentity() {
  if (hub.state.name) return hub.joinLobby(hub.state.name);
  return new Promise((resolve, reject) => {
    ui.gateDlg.showModal();
    ui.gateDlg.addEventListener('cancel', (e) => e.preventDefault());
    ui.gateForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const name = ui.gateInput.value.trim();
      if (!name || ui.gateForm.dataset.busy) return;
      ui.gateForm.dataset.busy = '1';
      try {
        await hub.joinLobby(name);
        ui.gateDlg.close();
        toast(`Welcome aboard, ${hub.state.name}.`, 'ok');
        resolve();
      } catch (err) {
        toast(err.message, 'error');
      } finally {
        delete ui.gateForm.dataset.busy;
      }
    });
  });
}
