import { createWire } from './shapes.js';

const MIN_ZOOM = 0.3;
const MAX_ZOOM = 2.6;

function resolvePortFromMagnet(view, magnet) {
  let el = magnet instanceof Element ? magnet : null;
  while (el && el.getAttribute && !el.getAttribute('port')) {
    el = el.parentElement;
    if (el && el.classList && el.classList.contains('joint-paper')) return null;
  }
  const id = el?.getAttribute?.('port');
  return id ? view.model.getPort(id) : null;
}

export function createBoard(container, { gridSize = 16, onZoom = () => {}, onMove = null, isSpacePan = () => false, isArmed = () => false } = {}) {
  const graph = new joint.dia.Graph({}, { cellNamespace: joint.shapes });

  const paper = new joint.dia.Paper({
    el: container,
    model: graph,
    width: '100%',
    height: '100%',
    gridSize,
    drawGrid: { name: 'dot', args: { color: '#1d2531', thickness: 1.3 } },
    background: { color: '#0b0f14' },
    async: true,
    sorting: joint.dia.Paper.sorting.APPROX,
    defaultLink: () => new joint.shapes.ld.Wire({ draft: true }),
    validateMagnet: (view, magnet) => {
      const port = resolvePortFromMagnet(view, magnet);
      if (!port || (port.group !== 'in' && port.group !== 'out')) return false;
      if (port.group !== 'in') return true;
      return !graph.getConnectedLinks(view.model).some((l) =>
        !l.get('draft') && l.target().id === view.model.id && l.target().port === port.id);
    },
    validateConnection: (srcView, sourceMagnet, tgtView, targetMagnet) => {
      if (!srcView || !tgtView || tgtView.model.isLink() || !targetMagnet) return false;
      const sourcePort = resolvePortFromMagnet(srcView, sourceMagnet);
      const targetPort = resolvePortFromMagnet(tgtView, targetMagnet);
      if (!sourcePort || !targetPort || sourcePort.group === targetPort.group) return false;
      if (srcView.model.id === tgtView.model.id) return false;
      const inputView = sourcePort.group === 'in' ? srcView : tgtView;
      const inputPort = sourcePort.group === 'in' ? sourcePort : targetPort;
      const taken = graph.getConnectedLinks(inputView.model).some((l) =>
        !l.get('draft') && l.target().id === inputView.model.id && l.target().port === inputPort.id);
      return !taken;
    },
    interactive: (cellView) => {
      if (isArmed()) return false;
      if (cellView.model.isLink()) {
        return { vertexAdd: false, labelMove: false };
      }
      return true;
    },
    snapLinks: { radius: 26 },
    linkPinning: false,
    highlighting: {
      magnetAdsorber: {
        name: 'addClass',
        options: { className: 'ld-port-target' },
      },
    },
    preventDefaultBlankAction: false,
  });

  let currentGrid = gridSize;
  const snap = (v) => Math.round(v / currentGrid) * currentGrid;

  let localDrag = false;
  paper.on('element:pointerdown', () => { localDrag = true; });
  paper.on('element:pointerup pointercancel', () => {
    localDrag = false;
    if (onMove) onMove.flush?.();
  });

  graph.on('change:position', (node, pos, opt) => {
    if (!localDrag || opt?.remote || opt?.snapped || opt?.groupMove) return;
    const sx = snap(pos.x);
    const sy = snap(pos.y);
    if (sx !== pos.x || sy !== pos.y) node.position(sx, sy, { snapped: true });
  });

  const translate = () => paper.translate();
  const scale = () => paper.scale().sx;

  function setTransform(tx, ty, s) {
    if (!Number.isFinite(tx) || !Number.isFinite(ty) || !Number.isFinite(s)) return;
    paper.translate(tx, ty);
    paper.scale(s);
    onZoom(s);
  }

  function zoomTo(nextScale, cx, cy) {
    const s = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, nextScale));
    const t = translate();
    const nx = cx - ((cx - t.tx) * s) / scale();
    const ny = cy - ((cy - t.ty) * s) / scale();
    setTransform(nx, ny, s);
  }

  container.addEventListener('wheel', (e) => {
    e.preventDefault();
    const rect = container.getBoundingClientRect();
    if (e.ctrlKey || e.metaKey) {
      const factor = Math.exp(-e.deltaY / 400);
      zoomTo(scale() * factor, e.clientX - rect.left, e.clientY - rect.top);
    } else {
      const t = translate();
      setTransform(t.tx - e.deltaX, t.ty - e.deltaY, scale());
    }
  }, { passive: false });

  let panCtx = null;
  container.addEventListener('pointerdown', (e) => {
    if (e.button === 1) e.preventDefault();
  });
  paper.on('blank:pointerdown', (evt) => {
    if (evt.button === 1 || evt.button === undefined || (evt.button === 0 && isSpacePan())) {
      panCtx = { x: evt.clientX, y: evt.clientY, tx: translate().tx, ty: translate().ty };
      container.classList.add('panning');
      evt.data ??= {};
      evt.data.panning = true;
    }
  });
  paper.on('blank:pointermove', (evt) => {
    if (!panCtx) return;
    setTransform(panCtx.tx + evt.clientX - panCtx.x, panCtx.ty + evt.clientY - panCtx.y, scale());
  });
  window.addEventListener('pointerup', () => {
    panCtx = null;
    container.classList.remove('panning');
  });

  function zoomStep(factor) {
    const rect = container.getBoundingClientRect();
    zoomTo(scale() * factor, rect.width / 2, rect.height / 2);
  }

  function fit(padding = 60) {
    const cells = graph.getElements();
    const rect = container.getBoundingClientRect();
    if (cells.length === 0) { setTransform(0, 0, 1); return; }
    const bb = cells[0].getBBox();
    for (const c of cells.slice(1)) bb.union(c.getBBox());
    const w = bb.width + padding * 2;
    const h = bb.height + padding * 2;
    const s = Math.min(1.25, Math.max(MIN_ZOOM, Math.min(rect.width / w, rect.height / h)));
    setTransform(
      -bb.x * s + (rect.width - bb.width * s) / 2,
      -bb.y * s + (rect.height - bb.height * s) / 2,
      s,
    );
  }

  return {
    graph,
    paper,
    scale,
    zoomTo,
    zoomIn: () => zoomStep(1.2),
    zoomOut: () => zoomStep(1 / 1.2),
    zoomReset: () => { const r = container.getBoundingClientRect(); zoomTo(1, r.width / 2, r.height / 2); },
    fit: () => fit(),
    setGridSize(g) {
      currentGrid = g;
      paper.setGridSize(g);
      paper.drawGrid();
    },
    get gridSize() { return currentGrid; },
    addWireFromDto(dto) {
      const link = createWire(dto);
      graph.addCell(link);
      return link;
    },
    clearAll() { graph.clear(); },
  };
}
