export const TYPES = ['Input', 'Output', 'CLK', 'Button', 'Ic', 'And', 'Or', 'Not', 'Xor', 'Xnor', 'Nand', 'Nor', 'Seg7', 'FlopD'];

export const HOTKEYS = { Input: '1', Output: '2', CLK: '9', Button: 'b', And: '3', Or: '4', Not: '5', Xor: '6', Xnor: '0', Nand: '7', Nor: '8', Seg7: 'd', FlopD: 'f' };

export const LABELS = {
  Input: 'Input switch', Output: 'Output lamp', CLK: 'Clock', Button: 'Momentary push button', Ic: 'Custom IC',
  And: 'AND gate', Or: 'OR gate', Not: 'NOT gate',
  Xor: 'XOR gate', Xnor: 'XNOR gate', Nand: 'NAND gate', Nor: 'NOR gate', Seg7: '7-segment display', FlopD: 'D flip-flop',
};

const GATE_STROKE = '#a9b7c6';
const BODY_FILL = '#151b23';
const LABEL_FILL = '#5f6c7b';

const dualBody = {
  And: { body: 'M22 8 H44 A24 24 0 0 1 44 56 H22 Z', stub: 'M68 32 H96', inX0: 22, inX1: 22 },
  Nand: {
    body: 'M22 8 H40 A24 24 0 0 1 40 56 H22 Z', stub: 'M74 32 H96', inX0: 22, inX1: 22,
    bubble: { cx: 69, cy: 32 },
  },
  Or: { body: 'M20 8 Q58 10 78 32 Q58 54 20 56 Q33 32 20 8 Z', stub: 'M78 32 H96', inX0: 25.8, inX1: 25.8 },
  Nor: {
    body: 'M20 8 Q54 10 72 32 Q54 54 20 56 Q33 32 20 8 Z', stub: 'M82 32 H96', inX0: 25.8, inX1: 25.8,
    bubble: { cx: 77.5, cy: 32 },
  },
  Xor: {
    body: 'M26 8 Q62 10 80 32 Q62 54 26 56 Q39 32 26 8 Z', stub: 'M80 32 H96', inX0: 21.1, inX1: 21.1,
    extra: 'M18 8 Q32 32 18 56',
  },
  Xnor: {
    body: 'M26 8 Q62 10 80 32 Q62 54 26 56 Q39 32 26 8 Z', stub: 'M90 32 H96', inX0: 21.1, inX1: 21.1,
    extra: 'M18 8 Q32 32 18 56',
    bubble: { cx: 85, cy: 32 },
  },
};

const DUAL_W = 96;
const DUAL_H = 76;
const DUAL_IN_Y = [24, 40];

function jsonMarkup(spec) {
  return spec.map(([tagName, selector, attrs]) => ({ tagName, selector, attrs }));
}

const icDefinitions = new Map();

function icPinY(index, count, height) {
  if (count <= 1) return height / 2;
  return 16 + ((index + 0.5) * (height - 32)) / count;
}

export function getIcDefinition(id) {
  return icDefinitions.get(id);
}

export function getIcSize(definition) {
  const inputs = definition?.inputs?.length ?? 0;
  const outputs = definition?.outputs?.length ?? 0;
  const height = Math.max(64, Math.max(inputs, outputs) * 24 + 24);
  const width = Math.max(96, Math.min(180, (definition?.name?.length ?? 8) * 7 + 32));
  return { w: width, h: height };
}

export function registerIcDefinition(definition) {
  if (icDefinitions.has(definition.id)) return;
  icDefinitions.set(definition.id, definition);
  const size = getIcSize(definition);
  const markup = [
    ['rect', 'case', {}],
    ...(definition.inputs ?? []).map((_, index) => ['text', `inLabel${index}`, {}]),
    ...(definition.outputs ?? []).map((_, index) => ['text', `outLabel${index}`, {}]),
    ['text', 'label', {}],
  ];
  const attrs = {
    case: { x: 2, y: 8, width: size.w - 4, height: size.h - 16, rx: 10, fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2 },
    label: {
      text: definition.name, x: size.w / 2, y: size.h - 10, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 8, letterSpacing: 0.6,
      fill: LABEL_FILL, fontWeight: 700,
    },
  };
  (definition.inputs ?? []).forEach((pin, index) => {
    attrs[`inLabel${index}`] = {
      text: pin.label, x: 12, y: icPinY(index, definition.inputs.length, size.h) + 3,
      textAnchor: 'start', fontFamily: '"JetBrains Mono", monospace', fontSize: 8,
      fill: '#93a1b0', fontWeight: 600,
    };
  });
  (definition.outputs ?? []).forEach((pin, index) => {
    attrs[`outLabel${index}`] = {
      text: pin.label, x: size.w - 12, y: icPinY(index, definition.outputs.length, size.h) + 3,
      textAnchor: 'end', fontFamily: '"JetBrains Mono", monospace', fontSize: 8,
      fill: '#93a1b0', fontWeight: 600,
    };
  });
  joint.dia.Element.define(`ld.Ic_${definition.id}`, {
    size,
    markup: jsonMarkup(markup),
    attrs,
  });
}

function defineDual(name, def) {
  joint.dia.Element.define(`ld.${name}`, {
    size: { width: DUAL_W, height: DUAL_H },
    markup: jsonMarkup([
      ['path', 'inLead0', {}],
      ['path', 'inLead1', {}],
      ['path', 'body', {}],
      ...(def.extra ? [['path', 'extra', {}]] : []),
      ...(def.bubble ? [['circle', 'bubble', {}]] : []),
      ['path', 'stub', {}],
      ['text', 'label', {}],
    ]),
    attrs: {
      inLead0: { d: `M4 ${DUAL_IN_Y[0]} H${def.inX0}`, fill: 'none', stroke: GATE_STROKE, strokeWidth: 2, strokeLinecap: 'round' },
      inLead1: { d: `M4 ${DUAL_IN_Y[1]} H${def.inX1}`, fill: 'none', stroke: GATE_STROKE, strokeWidth: 2, strokeLinecap: 'round' },
      body: { d: def.body, fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2, strokeLinejoin: 'round' },
      ...(def.extra ? { extra: { d: def.extra, fill: 'none', stroke: GATE_STROKE, strokeWidth: 2, strokeLinecap: 'round' } } : {}),
      ...(def.bubble ? { bubble: { cx: def.bubble.cx, cy: def.bubble.cy, r: 5, fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2 } } : {}),
      stub: { d: def.stub, fill: 'none', stroke: GATE_STROKE, strokeWidth: 2 },
      label: {
        text: def.title, x: DUAL_W / 2, y: DUAL_H - 8,
        textAnchor: 'middle', fontFamily: '"JetBrains Mono", monospace',
        fontSize: 9, letterSpacing: 2, fill: LABEL_FILL, fontWeight: 600,
      },
    },
  });
}

joint.dia.Element.define('ld.Not', {
  size: { width: 80, height: DUAL_H },
  markup: jsonMarkup([
    ['path', 'inLead', {}],
    ['path', 'body', {}],
    ['circle', 'bubble', {}],
    ['path', 'stub', {}],
    ['text', 'label', {}],
  ]),
  attrs: {
    inLead: { d: 'M4 32 H16', fill: 'none', stroke: GATE_STROKE, strokeWidth: 2, strokeLinecap: 'round' },
    body: { d: 'M16 12 L48 32 L16 52 Z', fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2, strokeLinejoin: 'round' },
    bubble: { cx: 53.5, cy: 32, r: 5.5, fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2 },
    stub: { d: 'M59 32 H80', fill: 'none', stroke: GATE_STROKE, strokeWidth: 2 },
    label: {
      text: 'NOT', x: 40, y: DUAL_H - 8, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 9, letterSpacing: 2,
      fill: LABEL_FILL, fontWeight: 600,
    },
  },
});

joint.dia.Element.define('ld.Input', {
  size: { width: 88, height: 56 },
  markup: jsonMarkup([
    ['rect', 'case', {}],
    ['rect', 'track', {}],
    ['circle', 'knob', {}],
    ['text', 'tag', {}],
    ['text', 'digit', {}],
  ]),
  attrs: {
    case: { x: 2, y: 8, width: 84, height: 48, rx: 11, fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2 },
    track: { x: 40, y: 25, width: 30, height: 14, rx: 7, fill: '#232c37', stroke: '#3a4756', strokeWidth: 1.25 },
    knob: { cx: 47, cy: 32, r: 5.5, fill: '#55606c', stroke: 'none' },
    tag: {
      text: 'IN', x: 20, y: 37, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 11, letterSpacing: 1,
      fill: LABEL_FILL, fontWeight: 700,
    },
    digit: {
      text: '0', x: 78, y: 36, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 12, fill: LABEL_FILL, fontWeight: 700,
    },
  },
});

joint.dia.Element.define('ld.CLK', {
  size: { width: 88, height: 56 },
  markup: jsonMarkup([
    ['rect', 'case', {}],
    ['path', 'wave', {}],
    ['text', 'tag', {}],
    ['text', 'digit', {}],
  ]),
  attrs: {
    case: { x: 2, y: 8, width: 84, height: 48, rx: 11, fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2 },
    wave: {
      d: 'M30 40 H38 V24 H52 V40 H58', fill: 'none', stroke: LABEL_FILL,
      strokeWidth: 2, strokeLinejoin: 'round', strokeLinecap: 'round',
    },
    tag: {
      text: 'CLK', x: 16, y: 36, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 8, letterSpacing: 0.5,
      fill: LABEL_FILL, fontWeight: 700,
    },
    digit: {
      text: '0', x: 74, y: 36, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 12, fill: LABEL_FILL, fontWeight: 700,
    },
  },
});

joint.dia.Element.define('ld.Button', {
  size: { width: 88, height: 56 },
  markup: jsonMarkup([
    ['rect', 'case', {}],
    ['rect', 'button', {}],
    ['text', 'tag', {}],
    ['text', 'digit', {}],
  ]),
  attrs: {
    case: { x: 2, y: 8, width: 84, height: 48, rx: 11, fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2 },
    button: { x: 38, y: 23, width: 30, height: 18, rx: 5, fill: '#2a3542', stroke: '#556575', strokeWidth: 1.25 },
    tag: {
      text: 'PB', x: 18, y: 36, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 9, letterSpacing: 1,
      fill: LABEL_FILL, fontWeight: 700,
    },
    digit: {
      text: '0', x: 76, y: 36, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 12, fill: LABEL_FILL, fontWeight: 700,
    },
  },
});

joint.dia.Element.define('ld.Output', {
  size: { width: 88, height: 56 },
  markup: jsonMarkup([
    ['path', 'lead', {}],
    ['circle', 'ring', {}],
    ['circle', 'core', {}],
    ['path', 'crossA', {}],
    ['path', 'crossB', {}],
    ['text', 'label', {}],
  ]),
  attrs: {
    lead: { d: 'M0 32 H15', fill: 'none', stroke: GATE_STROKE, strokeWidth: 2 },
    ring: { cx: 34, cy: 32, r: 19, fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2 },
    core: { cx: 34, cy: 32, r: 13, fill: '#232c37', stroke: 'none' },
    crossA: { d: 'M24.8 22.8 L43.2 41.2', stroke: '#3a4756', strokeWidth: 1.5, strokeLinecap: 'round' },
    crossB: { d: 'M43.2 22.8 L24.8 41.2', stroke: '#3a4756', strokeWidth: 1.5, strokeLinecap: 'round' },
    label: {
      text: 'OUT', x: 60, y: 36, textAnchor: 'start',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 9, letterSpacing: 2,
      fill: LABEL_FILL, fontWeight: 600,
    },
  },
});

joint.dia.Element.define('ld.Seg7', {
  size: { width: 96, height: 84 },
  markup: jsonMarkup([
    ['rect', 'case', {}],
    ['path', 'segA', {}],
    ['path', 'segB', {}],
    ['path', 'segC', {}],
    ['path', 'segD', {}],
    ['path', 'segE', {}],
    ['path', 'segF', {}],
    ['path', 'segG', {}],
    ['text', 'digit', {}],
  ]),
  attrs: {
    case: { x: 2, y: 6, width: 92, height: 76, rx: 10, fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2 },
    segA: { d: 'M35 12 H61 L65 16 L61 20 H35 L31 16 Z', fill: '#1c242e', stroke: 'none' },
    segB: { d: 'M66 16 L70 20 V34 L66 38 L62 34 V20 Z', fill: '#1c242e', stroke: 'none' },
    segC: { d: 'M66 46 L70 50 V64 L66 68 L62 64 V50 Z', fill: '#1c242e', stroke: 'none' },
    segD: { d: 'M35 66 H61 L65 70 L61 74 H35 L31 70 Z', fill: '#1c242e', stroke: 'none' },
    segE: { d: 'M30 46 L34 50 V64 L30 68 L26 64 V50 Z', fill: '#1c242e', stroke: 'none' },
    segF: { d: 'M30 16 L34 20 V34 L30 38 L26 34 V20 Z', fill: '#1c242e', stroke: 'none' },
    segG: { d: 'M35 40 H61 L65 44 L61 48 H35 L31 44 Z', fill: '#1c242e', stroke: 'none' },
    digit: {
      text: '', x: 48, y: 80, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 9, letterSpacing: 1.5,
      fill: LABEL_FILL, fontWeight: 700,
    },
  },
});

joint.dia.Element.define('ld.FlopD', {
  size: { width: 92, height: 64 },
  markup: jsonMarkup([
    ['rect', 'case', {}],
    ['path', 'wedge', {}],
    ['text', 'dMark', {}],
    ['text', 'qMark', {}],
    ['text', 'qbMark', {}],
    ['text', 'label', {}],
  ]),
  attrs: {
    case: { x: 2, y: 4, width: 88, height: 56, rx: 10, fill: BODY_FILL, stroke: GATE_STROKE, strokeWidth: 2 },
    wedge: { d: 'M36 36 L36 46 L44 41 Z', fill: 'none', stroke: LABEL_FILL, strokeWidth: 1.5, strokeLinejoin: 'round' },
    dMark: {
      text: 'D', x: 22, y: 28, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 10,
      fill: LABEL_FILL, fontWeight: 700,
    },
    qMark: {
      text: 'Q', x: 70, y: 28, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 10,
      fill: LABEL_FILL, fontWeight: 700,
    },
    qbMark: {
      text: 'Q\u0304', x: 70, y: 46, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 10,
      fill: LABEL_FILL, fontWeight: 700,
    },
    label: {
      text: 'D-FF', x: 45, y: 58, textAnchor: 'middle',
      fontFamily: '"JetBrains Mono", monospace', fontSize: 7, letterSpacing: 1.5,
      fill: LABEL_FILL, fontWeight: 600,
    },
  },
});

for (const [name, def] of Object.entries(dualBody)) {
  defineDual(name, { ...def, title: name.toUpperCase() });
}

export const SIZES = {
  Input: { w: 88, h: 56 }, Output: { w: 88, h: 56 }, CLK: { w: 88, h: 56 }, Button: { w: 88, h: 56 },
  And: { w: 96, h: 76 }, Or: { w: 96, h: 76 }, Xor: { w: 96, h: 76 }, Xnor: { w: 96, h: 76 },
  Nand: { w: 96, h: 76 }, Nor: { w: 96, h: 76 }, Not: { w: 80, h: 76 }, Ic: { w: 120, h: 84 },
  Seg7: { w: 96, h: 84 }, FlopD: { w: 92, h: 64 },
};

export function createNode(type, x, y, definitionId = null) {
  const definition = definitionId ? getIcDefinition(definitionId) : null;
  const Shape = definitionId ? joint.shapes.ld[`Ic_${definitionId}`] : joint.shapes.ld[type];
  const size = definition ? getIcSize(definition) : SIZES[type];
  const inputCount = definition
    ? definition.inputs.length
    : type === 'And' || type === 'Or' || type === 'Xor' || type === 'Xnor' || type === 'Nand' || type === 'Nor'
      ? 2
      : (type === 'Not' || type === 'Output' ? 1 : type === 'Seg7' ? 4 : type === 'FlopD' ? 2 : 0);

  const ports = [];
  if (definition) {
    definition.inputs.forEach((_, index) => ports.push({
      id: `in${index}`, group: 'in', args: { x: 0, y: icPinY(index, definition.inputs.length, size.h) },
    }));
    definition.outputs.forEach((_, index) => ports.push({
      id: `out${index}`, group: 'out', args: { x: size.w, y: icPinY(index, definition.outputs.length, size.h) },
    }));
  } else if (type === 'FlopD') {
    ports.push({ id: 'in0', group: 'in', args: { x: 0, y: DUAL_IN_Y[0] } });
    ports.push({ id: 'in1', group: 'in', args: { x: 0, y: DUAL_IN_Y[1] } });
    ports.push({ id: 'out0', group: 'out', args: { x: size.w, y: DUAL_IN_Y[0] } });
    ports.push({ id: 'out1', group: 'out', args: { x: size.w, y: DUAL_IN_Y[1] } });
  } else {
    if (inputCount > 0) {
      for (let i = 0; i < inputCount; i++) {
        const y = inputCount === 2 ? DUAL_IN_Y[i]
          : inputCount === 1 ? 32
            : (size.h * (i + 0.5)) / inputCount;
        ports.push({ id: `in${i}`, group: 'in', args: { x: 0, y } });
      }
    }
    if (type !== 'Output' && type !== 'Seg7') {
      ports.push({ id: 'out', group: 'out', args: { x: size.w, y: 32 } });
    }
  }

  return new Shape({
    position: { x, y },
    size,
    state: false,
    definitionId,
    ports: {
      groups: {
        in: {
          position: { name: 'absolute' },
          markup: '<g class="ld-port"><circle class="port-ring" r="5.5"/><circle class="port-dot" r="2"/></g>',
          attrs: {
            '.port-ring': { magnet: true, fill: '#10151c', stroke: '#4a5866', strokeWidth: 1.5 },
            '.port-dot': { fill: '#4a5866', stroke: 'none', pointerEvents: 'none' },
          },
        },
        out: {
          position: { name: 'absolute' },
          markup: '<g class="ld-port"><circle class="port-ring" r="5.5"/><circle class="port-dot" r="2"/></g>',
          attrs: {
            '.port-ring': { magnet: true, fill: '#10151c', stroke: '#45d6a4', strokeWidth: 1.5 },
            '.port-dot': { fill: '#45d6a4', stroke: 'none', pointerEvents: 'none' },
          },
        },
      },
      items: ports,
    },
  });
}

joint.dia.Link.define('ld.Wire', {
  attrs: {
    '.connection': {
      stroke: '#b7c3d0', strokeWidth: 2, targetMarker: null, sourceMarker: null,
      strokeLinecap: 'round', fill: 'none',
    },
  },
  router: { name: 'orthogonal', args: { step: 16, padding: 12 } },
  connector: { name: 'rounded', args: { radius: 10 } },
});

export function createWire(wireDto) {
  return new joint.shapes.ld.Wire({
    id: wireDto.id,
    z: -1,
    source: { id: wireDto.fromNodeId, port: wireDto.fromPort ?? 'out' },
    target: { id: wireDto.toNodeId, port: `in${wireDto.toPin}` },
  });
}

const ICON_VIEWBOX = '0 0 100 64';

export function iconSvg(type) {
  const s = `fill="none" stroke="currentColor" stroke-width="5" stroke-linejoin="round" stroke-linecap="round"`;
  let inner = '';
  switch (type) {
    case 'And':
      inner = `<path d="M14 8 H36 A24 24 0 0 1 36 56 H14 Z" ${s}/><path d="M60 32 H92" ${s}/>`;
      break;
    case 'Nand':
      inner = `<path d="M14 8 H34 A24 24 0 0 1 34 56 H14 Z" ${s}/><circle cx="65" cy="32" r="5" ${s}/><path d="M70 32 H90" ${s}/>`;
      break;
    case 'Or':
      inner = `<path d="M14 8 Q50 10 68 32 Q50 54 14 56 Q27 32 14 8 Z" ${s}/><path d="M68 32 H92" ${s}/>`;
      break;
    case 'Nor':
      inner = `<path d="M14 8 Q46 10 60 32 Q46 54 14 56 Q27 32 14 8 Z" ${s}/><circle cx="67" cy="32" r="5" ${s}/><path d="M72 32 H88" ${s}/>`;
      break;
    case 'Xor':
      inner = `<path d="M24 8 Q58 10 74 32 Q58 54 24 56 Q37 32 24 8 Z" ${s}/><path d="M16 8 Q29 32 16 56" ${s}/><path d="M74 32 H94" ${s}/>`;
      break;
    case 'Xnor':
      inner = `<path d="M24 8 Q58 10 74 32 Q58 54 24 56 Q37 32 24 8 Z" ${s}/><path d="M16 8 Q29 32 16 56" ${s}/><circle cx="79" cy="32" r="5" ${s}/><path d="M84 32 H94" ${s}/>`;
      break;
    case 'Not':
      inner = `<path d="M14 10 L52 32 L14 54 Z" ${s}/><circle cx="60" cy="32" r="6" ${s}/><path d="M66 32 H88" ${s}/>`;
      break;
    case 'Input':
      inner = `<rect x="8" y="16" width="52" height="32" rx="9" ${s}/><path d="M22 32 H38 M38 32 A8 8 0 1 0 54 32 A8 8 0 0 0 38 32" ${s.replace('stroke-linejoin="round"', '')}/><path d="M66 32 H90" ${s}/>`;
      break;
    case 'CLK':
      inner = `<rect x="8" y="16" width="52" height="32" rx="9" ${s}/><path d="M18 34 H26 V24 H40 V40 H52 V34 H54" ${s}/><path d="M66 32 H90" ${s}/>`;
      break;
    case 'Button':
      inner = `<rect x="8" y="16" width="52" height="32" rx="9" ${s}/><rect x="34" y="25" width="20" height="14" rx="4" ${s}/><path d="M66 32 H90" ${s}/>`;
      break;
    case 'Ic':
      inner = `<rect x="12" y="8" width="76" height="48" rx="8" ${s}/><path d="M12 24 H4 M12 40 H4 M88 24 H96 M88 40 H96" ${s}/><path d="M26 20 H50 V32 H66 V44" ${s}/>`;
      break;
    case 'Seg7':
      inner = `<rect x="8" y="10" width="84" height="52" rx="9" ${s}/>
        <path d="M26 16 H46 L50 20 H26 L22 20 Z" ${s}/>
        <path d="M50 22 V32 M50 42 V52" ${s}/>
        <path d="M26 34 H46 L50 38 H26 L22 38 Z" ${s}/>
        <path d="M22 22 V32 M22 42 V52" ${s}/>
        <path d="M26 50 H46 L50 54 H26 L22 54 Z" ${s}/>`;
      break;
    case 'FlopD':
      inner = `<rect x="14" y="12" width="72" height="40" rx="8" ${s}/>
        <text x="30" y="30" font-family="monospace" font-size="12" fill="currentColor" font-weight="700">D</text>
        <path d="M26 40 L26 50 L34 45 Z" fill="currentColor"/>
        <text x="64" y="30" font-family="monospace" font-size="12" fill="currentColor" font-weight="700">Q</text>
        <text x="64" y="46" font-family="monospace" font-size="12" fill="currentColor" font-weight="700">Q&#772;</text>
        <path d="M8 24 H14 M8 44 H14 M86 24 H92 M86 44 H92" ${s}/>`;
      break;
    case 'Output':
      inner = `<circle cx="42" cy="32" r="17" ${s}/><path d="M31 21 L53 43 M53 21 L31 43" stroke="currentColor" stroke-width="4" stroke-linecap="round" opacity=".55"/><path d="M8 32 H25 M59 32 H82" ${s}/>`;
      break;
  }
  return `<svg viewBox="${ICON_VIEWBOX}" aria-hidden="true">${inner}</svg>`;
}
