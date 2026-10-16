const puppeteer = require(process.env.PUPPETEER_PATH);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
(async () => {
  const browser = await puppeteer.launch({ executablePath: 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', headless: true });
  const ctx = await browser.createBrowserContext();
  const p = await ctx.newPage();
  const errors = [];
  p.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  p.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await p.setViewport({ width: 1280, height: 800 });
  await p.goto('http://127.0.0.1:5050/', { waitUntil: 'networkidle2' });
  await sleep(500);
  await p.screenshot({ path: 'docs/screenshots/audit-01-gate.png' });
  await p.setViewport({ width: 360, height: 800 });
  await sleep(500);
  const lobbyNarrow = await p.evaluate(() => ({
    body: document.body.scrollWidth > window.innerWidth + 1,
    card: document.querySelector('.gate-card')?.getBoundingClientRect(),
  }));
  await p.screenshot({ path: 'docs/screenshots/audit-01b-gate-narrow.png' });
  await p.setViewport({ width: 1280, height: 800 });
  await sleep(300);
  await p.type('#nameInput', 'Auditor');
  await p.click('#joinBtn');
  await p.waitForSelector('#dash:not([hidden])');
  await sleep(700);
  await p.screenshot({ path: 'docs/screenshots/audit-02-dash-empty.png' });
  await p.click('#newRoomBtn');
  await p.waitForSelector('#newRoomDlg[open]');
  await sleep(300);
  await p.screenshot({ path: 'docs/screenshots/audit-03-dialog.png' });
  await p.type('#dlgTitle', 'UI audit circuit');
  await p.click('#dlgCreate');
  await p.waitForFunction(() => location.pathname.startsWith('/circuit/'), { timeout: 10000 });
  await sleep(1200);
  await p.screenshot({ path: 'docs/screenshots/audit-04-editor-empty.png' });
  const wrap = await p.$('#canvasWrap');
  const bb = await wrap.boundingBox();
  await p.keyboard.press('3'); await sleep(150);
  await p.mouse.move(bb.x + 420, bb.y + 320); await sleep(300);
  const ghost = await p.evaluate(() => {
    const ghostEl = document.querySelector('.place-ghost');
    const svg = ghostEl?.querySelector('svg');
    const gb = ghostEl?.getBoundingClientRect();
    const sb = svg?.getBoundingClientRect();
    const hintHidden = document.querySelector('#canvasHint')?.classList.contains('hidden');
    const armed = !!document.querySelector('.pal-btn.armed');
    return { ghost: { w: gb?.width, h: gb?.height }, svg: { w: sb?.width, h: sb?.height }, hintHidden, armed };
  });
  await p.screenshot({ path: 'docs/screenshots/audit-04b-ghost.png' });
  await p.keyboard.press('Escape'); await sleep(150);
  // --- pan / zoom / space-pan verification (no cells yet) ---
  const panBefore = await p.evaluate(() => ({ ...__ld.board.paper.translate() }));
  await p.mouse.move(bb.x + 500, bb.y + 500); await p.mouse.down(); await sleep(120);
  await p.mouse.move(bb.x + 620, bb.y + 500, { steps: 10 }); await sleep(200);
  await p.mouse.up(); await sleep(200);
  const panMiddle = await p.evaluate(() => ({ ...__ld.board.paper.translate() }));
  await p.keyboard.down('Space'); await sleep(150);
  const spaceClassOn = await p.evaluate(() => document.querySelector('#canvasWrap').classList.contains('space-pan'));
  await p.mouse.move(bb.x + 500, bb.y + 500); await p.mouse.down(); await sleep(120);
  await p.mouse.move(bb.x + 620, bb.y + 500, { steps: 10 }); await sleep(200);
  await p.mouse.up(); await p.keyboard.up('Space'); await sleep(200);
  const panAfter = await p.evaluate(() => ({ ...__ld.board.paper.translate() }));
  const spaceClassOff = await p.evaluate(() => !document.querySelector('#canvasWrap').classList.contains('space-pan'));
  const pan = {
    blankDragDeltaX: Math.round((panMiddle.tx ?? 0) - (panBefore.tx ?? 0)),
    spaceDeltaX: Math.round((panAfter.tx ?? 0) - (panMiddle.tx ?? 0)),
    spaceClassOn, spaceClassOff,
  };
  await p.click('#zoomInBtn'); await sleep(300);
  const zoom = await p.evaluate(() => ({ scale: __ld.board.scale() }));
  await p.evaluate(() => __ld.board.fit()); await sleep(300);
  // --- place Input + AND + Output with all three on the same port row ---
  await p.keyboard.press('1'); await sleep(150);
  await p.mouse.click(bb.x + 220, bb.y + 284); await sleep(350);
  await p.keyboard.press('3'); await sleep(150);
  await p.mouse.click(bb.x + 420, bb.y + 294); await sleep(350);
  await p.keyboard.press('2'); await sleep(150);
  await p.mouse.click(bb.x + 620, bb.y + 284); await sleep(350);
  await p.keyboard.press('Escape'); await sleep(200);
  const ports = await p.evaluate(() => ({
    translate: { ...__ld.board.paper.translate() },
    scale: __ld.board.scale(),
    els: __ld.board.graph.getElements().map((el) => {
      const v = el.findView(__ld.board.paper);
      const ps = Array.from(v.el.querySelectorAll('[port]')).map((n) => {
        const r = n.getBoundingClientRect();
        return { id: n.getAttribute('port'), x: r.x + r.width / 2, y: r.y + r.height / 2 };
      });
      return { type: el.get('type'), pos: el.position(), ports: ps };
    }),
  }));
  async function dragWire(from, to) {
    await p.mouse.move(from.x, from.y); await p.mouse.down(); await sleep(150);
    await p.mouse.move(to.x, to.y, { steps: 15 }); await sleep(300);
    await p.mouse.up();
  }
  const inOut = ports.els.find((t) => t.type === 'ld.Input').ports.find((q) => q.id === 'out');
  const andIn0 = ports.els.find((t) => t.type === 'ld.And').ports.find((q) => q.id === 'in0');
  const andIn1 = ports.els.find((t) => t.type === 'ld.And').ports.find((q) => q.id === 'in1');
  const andOut = ports.els.find((t) => t.type === 'ld.And').ports.find((q) => q.id === 'out');
  const outIn = ports.els.find((t) => t.type === 'ld.Output').ports.find((q) => q.id === 'in0');
  await dragWire(inOut, andIn0);
  await p.waitForFunction(() => __ld.board.graph.getLinks().filter((l) => !l.get('draft')).length === 1, { timeout: 10000 });
  await dragWire(andOut, outIn);
  await p.waitForFunction(() => __ld.board.graph.getLinks().filter((l) => !l.get('draft')).length === 2, { timeout: 10000 });
  await sleep(600);
  const wire = await p.evaluate(() => {
    const ds = __ld.board.graph.getLinks().filter((l) => !l.get('draft')).map((link) => {
      const d = link.findView(__ld.board.paper).el.querySelector('.connection').getAttribute('d');
      const nums = d.match(/-?\d+(\.\d+)?/g).map(Number);
      const ys = nums.filter((_, i) => i % 2 === 1);
      return { d, straight: Math.max(...ys) - Math.min(...ys) < 0.6 };
    });
    return { wires: ds, allStraight: ds.every((w) => w.straight) };
  });
  wire.centeredInputs = Math.abs(andIn0.y - (andOut.y - 8)) < 0.6 && Math.abs(andIn1.y - (andOut.y + 8)) < 0.6;
  await p.screenshot({ path: 'docs/screenshots/audit-05-parts.png' });
  await p.click('#runToggle'); await sleep(900);
  await p.screenshot({ path: 'docs/screenshots/audit-06-running.png' });
  // --- marquee multi-select (draw.io style) ---
  await p.mouse.move(bb.x + 150, bb.y + 180); await p.mouse.down(); await sleep(120);
  await p.mouse.move(bb.x + 720, bb.y + 420, { steps: 15 }); await sleep(250);
  await p.screenshot({ path: 'docs/screenshots/audit-08-marquee.png' });
  await p.mouse.up(); await sleep(400);
  const selCount = await p.evaluate(() => window.__ld.selection());
  // --- group move: drag AND body +64px, all selected elements must follow ---
  const dragPt = await p.evaluate(() => {
    const pt = __ld.board.paper.localToPagePoint({ x: 416, y: 294 });
    const els = __ld.board.graph.getElements().map((e) => ({ id: e.id, ...e.position() }));
    return { ...pt, els };
  });
  await p.mouse.move(dragPt.x, dragPt.y); await p.mouse.down(); await sleep(150);
  await p.mouse.move(dragPt.x + 64, dragPt.y, { steps: 12 }); await sleep(250);
  await p.mouse.up(); await sleep(500);
  const moved = await p.evaluate(() => __ld.board.graph.getElements().map((e) => ({ id: e.id, ...e.position() })));
  const groupMoved = moved.every((m) => {
    const b = dragPt.els.find((x) => x.id === m.id);
    return m.x - b.x === 64 && m.y === b.y;
  });
  // --- group delete ---
  await p.keyboard.press('Delete'); await sleep(300);
  await p.waitForFunction(() => __ld.board.graph.getCells().length === 0 && window.__ld.selection() === 0, { timeout: 10000 });
  await sleep(400);
  const cleared = await p.evaluate(() => ({
    cells: __ld.board.graph.getCells().length,
    selection: window.__ld.selection(),
  }));
  await p.screenshot({ path: 'docs/screenshots/audit-09-cleared.png' });
  const multi = { selCount, groupMoved, cleared };
  await p.keyboard.press('1'); await sleep(150);
  await p.mouse.click(bb.x + 220, bb.y + 284); await sleep(350);
  await p.keyboard.down('Shift');
  await p.mouse.click(bb.x + 220, bb.y + 284);
  await p.keyboard.up('Shift'); await sleep(150);
  const duplicateBefore = await p.evaluate(() => ({
    selection: window.__ld.selection(),
    buttonEnabled: !document.querySelector('#duplicateBtn').disabled,
  }));
  await p.keyboard.down('Control');
  await p.keyboard.press('d');
  await p.keyboard.up('Control');
  await p.waitForFunction(() => __ld.board.graph.getElements().length === 2 && window.__ld.selection() === 1, { timeout: 10000 });
  const duplicate = await p.evaluate(() => ({
    elements: __ld.board.graph.getElements().length,
    selection: window.__ld.selection(),
    buttonEnabled: !document.querySelector('#duplicateBtn').disabled,
  }));
  await p.mouse.click(bb.x + 650, bb.y + 500); await sleep(150);
  await p.keyboard.press('4'); await sleep(150);
  await p.mouse.click(bb.x + 460, bb.y + 420); await sleep(350);
  await p.waitForFunction(() => __ld.board.graph.getElements().some((e) => e.get('type') === 'ld.Or'), { timeout: 10000 });
  const inputLeadAlignment = await p.evaluate(() => {
    const gate = __ld.board.graph.getElements().find((e) => e.get('type') === 'ld.Or');
    const view = gate.findView(__ld.board.paper);
    const nums = (selector) => (view.el.querySelector(`[joint-selector="${selector}"]`)?.getAttribute('d') ?? '').match(/-?\d+(?:\.\d+)?/g)?.map(Number) ?? [];
    return { top: nums('inLead0'), bottom: nums('inLead1') };
  });
  const reverseWire = await p.evaluate(() => {
    const gate = __ld.board.graph.getElements().find((e) => e.get('type') === 'ld.Or');
    const source = __ld.board.graph.getElements().find((e) => e.get('type') === 'ld.Input');
    const center = (cell, port) => {
      const rect = cell.findView(__ld.board.paper).el.querySelector(`[port="${port}"]`).getBoundingClientRect();
      return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
    };
    return { from: center(gate, 'in1'), to: center(source, 'out'), gateId: gate.id, sourceId: source.id };
  });
  await dragWire(reverseWire.from, reverseWire.to);
  await p.waitForFunction(() => __ld.board.graph.getLinks().filter((l) => !l.get('draft')).length === 1, { timeout: 10000 });
  const reverseWireConnected = await p.evaluate(({ gateId, sourceId }) => __ld.board.graph.getLinks().some((l) => {
    const src = l.source();
    const tgt = l.target();
    return !l.get('draft') && src.id === sourceId && src.port === 'out' && tgt.id === gateId && tgt.port === 'in1';
  }), reverseWire);
  await p.keyboard.press('0'); await sleep(150);
  await p.mouse.click(bb.x + 700, bb.y + 420); await sleep(350);
  await p.waitForFunction(() => __ld.board.graph.getElements().some((e) => e.get('type') === 'ld.Xnor'), { timeout: 10000 });
  const xnorRendered = await p.evaluate(() => {
    const gate = __ld.board.graph.getElements().find((e) => e.get('type') === 'ld.Xnor');
    const view = gate.findView(__ld.board.paper);
    return {
      palette: !!document.querySelector('.pal-btn[data-type="Xnor"]'),
      bubble: view.el.querySelector('[joint-selector="bubble"]')?.getAttribute('cx'),
    };
  });
  await p.mouse.click(bb.x + 460, bb.y + 420); await sleep(150);
  await p.click('#packageBtn');
  await p.waitForSelector('#packageDlg[open]');
  await p.type('#icNameInput', 'OR IC');
  await p.click('#packageCreate');
  await p.waitForFunction(() => __ld.board.graph.getElements().some((e) => String(e.get('type')).startsWith('ld.Ic_')), { timeout: 10000 });
  const packageIcUi = await p.evaluate(() => ({
    customButtons: document.querySelectorAll('.pal-btn[data-definition-id]').length,
    hasIc: __ld.board.graph.getElements().some((e) => String(e.get('type')).startsWith('ld.Ic_')),
  }));
  await p.keyboard.press('b'); await sleep(150);
  await p.mouse.click(bb.x + 700, bb.y + 560); await sleep(350);
  await p.waitForFunction(() => __ld.board.graph.getElements().some((e) => e.get('type') === 'ld.Button'), { timeout: 10000 });
  const buttonPoint = await p.evaluate(() => {
    const button = __ld.board.graph.getElements().find((e) => e.get('type') === 'ld.Button');
    const rect = button.findView(__ld.board.paper).el.getBoundingClientRect();
    return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
  });
  await p.mouse.move(buttonPoint.x, buttonPoint.y); await p.mouse.down(); await sleep(200);
  const buttonHeld = await p.evaluate(() => __ld.board.graph.getElements().find((e) => e.get('type') === 'ld.Button')?.prop('state') === true);
  await p.mouse.up(); await sleep(300);
  const buttonReleased = await p.evaluate(() => __ld.board.graph.getElements().find((e) => e.get('type') === 'ld.Button')?.prop('state') === false);
  const exportData = await p.evaluate(() => __ld.exportData());
  const importUi = await p.evaluate(() => !!document.querySelector('#importBtn'));
  await p.click('#tidyBtn'); await sleep(400);
  const tidyUi = await p.evaluate(() => ({
    enabled: !document.querySelector('#tidyBtn').disabled,
    columns: new Set(__ld.board.graph.getElements().map((e) => e.position().x)).size,
  }));
  const clearButton = await p.$('#clearBtn');
  p.once('dialog', (dialog) => dialog.accept());
  if (clearButton) await clearButton.click();
  if (clearButton) await p.waitForFunction(() => __ld.board.graph.getCells().length === 0 && window.__ld.selection() === 0, { timeout: 10000 });
  const clearCircuit = await p.evaluate(() => ({
    button: !!document.querySelector('#clearBtn') && !document.querySelector('#clearBtn').disabled,
    cells: __ld.board.graph.getCells().length,
    selection: window.__ld.selection(),
  }));
  // narrow viewport overflow checks
  const narrow = [];
  for (const width of [620, 360]) {
    await p.setViewport({ width, height: 800 });
    await sleep(600);
    const result = await p.evaluate((w) => {
      const tb = document.querySelector('.topbar');
      const sb = document.querySelector('.statusbar');
      return {
        width: w,
        topbar: tb.scrollWidth > tb.clientWidth + 1,
        body: document.body.scrollWidth > window.innerWidth + 1,
        inviteVisible: getComputedStyle(document.querySelector('#copyLinkBtn')).display !== 'none',
        inviteLabelVisible: getComputedStyle(document.querySelector('#copyLinkBtn span')).display !== 'none',
        gridVisible: getComputedStyle(document.querySelector('#gridSel')).display !== 'none',
        statusVisible: getComputedStyle(sb).display !== 'none',
      };
    }, width);
    await p.screenshot({ path: `docs/screenshots/audit-07-narrow-${width}.png` });
    narrow.push(result);
  }
  await p.goto('http://127.0.0.1:5050/', { waitUntil: 'networkidle2' });
  await sleep(600);
  const deleteButton = await p.$('.room-card .room-delete');
  p.once('dialog', (dialog) => dialog.accept());
  if (deleteButton) await deleteButton.click();
  if (deleteButton) await p.waitForFunction(() => document.querySelectorAll('.room-card').length === 0, { timeout: 10000 });
  const deleteUi = { hadButton: !!deleteButton, remainingCards: await p.evaluate(() => document.querySelectorAll('.room-card').length) };
  const pass = {
    noErrors: errors.length === 0,
    noOverflow: narrow.every((x) => !x.topbar && !x.body) && !lobbyNarrow.body,
    blankDoesNotPan: pan.blankDragDeltaX === 0,
    spacePans: pan.spaceDeltaX !== 0 && pan.spaceClassOn && pan.spaceClassOff,
    hintHiddenWhileArmed: ghost.armed === true && ghost.hintHidden === true,
    ghostSized: ghost.ghost.w > 0 && ghost.svg.w > 0,
    zoomed: Math.abs(zoom.scale - 1.2) < 0.01,
    centeredDualInputs: wire.centeredInputs,
    multiSelect: multi.selCount === 5 && multi.groupMoved === true,
    cleared: multi.cleared.cells === 0 && multi.cleared.selection === 0,
    duplicatesSelection: duplicateBefore.selection === 1 && duplicateBefore.buttonEnabled && duplicate.elements === 2 && duplicate.selection === 1 && duplicate.buttonEnabled,
    inputLeadsMeetBody: inputLeadAlignment.top[1] === 24 && inputLeadAlignment.top[2] === 25.8 && inputLeadAlignment.bottom[1] === 40 && inputLeadAlignment.bottom[2] === 25.8,
    reverseWireConnects: reverseWireConnected,
    xnorRendered: xnorRendered.palette && xnorRendered.bubble === '85',
    packageIcWorks: packageIcUi.customButtons > 0 && packageIcUi.hasIc,
    buttonMomentary: buttonHeld && buttonReleased,
    deleteCircuit: deleteUi.hadButton && deleteUi.remainingCards === 0,
    exportData: exportData?.format === 'logicdeck-circuit' && exportData.version === 1 && Array.isArray(exportData.nodes) && Array.isArray(exportData.wires),
    importUi,
    tidyLayout: tidyUi.enabled && tidyUi.columns > 1,
    clearCircuit: clearCircuit.button === false && clearCircuit.cells === 0 && clearCircuit.selection === 0,
  };
  const ok = Object.values(pass).every(Boolean);
  console.log(JSON.stringify({ ok, pass, errors, overflow: narrow.filter((x) => x.topbar || x.body), narrow, lobbyNarrow, ghost, pan, zoom, wire, ports, multi, duplicateBefore, duplicate, inputLeadAlignment, reverseWireConnected, xnorRendered, packageIcUi, buttonHeld, buttonReleased, deleteUi, importUi, tidyUi, clearCircuit, exportData: { format: exportData?.format, version: exportData?.version, nodes: exportData?.nodes?.length, wires: exportData?.wires?.length, customIcs: exportData?.customIcs?.length } }, null, 1));
  await browser.close();
  if (!ok) process.exit(1);
})();
