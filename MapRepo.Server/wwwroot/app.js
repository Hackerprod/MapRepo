/* MapRepo 3D Code Atlas — dependency-free 3D force-directed graph on canvas. */
'use strict';

const $ = id => document.getElementById(id);
const canvas = $('space');
const ctx = canvas.getContext('2d');

/* ── Colors per symbol kind ─────────────────────────────── */
const KIND_COLORS = {
  namedtype: '#4fc3f7', class: '#4fc3f7', interface: '#69f0ae', enum: '#ffd54f',
  method: '#64b5f6', function: '#64b5f6', constructor: '#9575cd', localfunction: '#64b5f6',
  property: '#b388ff', field: '#4dd0e1', event: '#f48fb1', variable: '#4dd0e1',
  module: '#ffb74d', namespace: '#ffb74d', type: '#69f0ae', delegate: '#ce93d8',
  'textual-evidence': '#546e7a'
};
const EDGE_COLORS = {
  calls: '#4fc3f7', constructs: '#ffb74d', references: '#8e7cc3',
  contains: '#5c9c6e', inherits: '#ffd54f', implements: '#69f0ae', imports: '#f48fb1'
};
const kindColor = kind => KIND_COLORS[(kind || '').toLowerCase()] || '#90a4ae';
const edgeColor = kind => EDGE_COLORS[kind] || '#607d8b';

/* ── App state ──────────────────────────────────────────── */
const state = {
  repoId: null,
  repos: [],
  kind: '',
  selection: null,     // node id
  rootId: null,
  detail: null,
  spin: true, physics: true, labels: true,
  depth: 2, limit: 120
};

/* ── 3D engine ──────────────────────────────────────────── */
const cam = { yaw: 0.6, pitch: 0.25, dist: 900, fov: 700, targetDist: 900, tx: 0, ty: 0, tz: 0, ox: 0, oy: 0 };
const CAM_HOME = { yaw: 0.6, pitch: 0.25, dist: 900, tx: 0, ty: 0, tz: 0, ox: 0, oy: 0 };
let nodes = [];              // {id, x,y,z, vx,vy,vz, rec, degree, r, color, sx, sy, ss, sz}
let edges = [];              // {source, target, kind, color, pulse}
let nodeById = new Map();
let hovered = null;
let lastInteraction = 0;

const stars = Array.from({ length: 220 }, () => ({
  a: Math.random() * Math.PI * 2, b: (Math.random() - 0.5) * Math.PI,
  d: 2400 + Math.random() * 2600, s: Math.random() * 1.4 + .3, tw: Math.random() * Math.PI * 2
}));

function setGraph(graph, rootId) {
  const prev = nodeById;
  nodeById = new Map();
  const degree = new Map();
  for (const e of graph.edges) {
    degree.set(e.sourceId, (degree.get(e.sourceId) || 0) + 1);
    degree.set(e.targetId, (degree.get(e.targetId) || 0) + 1);
  }
  // BFS shells from root for initial radial placement.
  const adj = new Map();
  for (const e of graph.edges) {
    (adj.get(e.sourceId) || adj.set(e.sourceId, []).get(e.sourceId)).push(e.targetId);
    (adj.get(e.targetId) || adj.set(e.targetId, []).get(e.targetId)).push(e.sourceId);
  }
  const depthOf = new Map([[rootId, 0]]);
  const queue = [rootId];
  while (queue.length) {
    const id = queue.shift();
    for (const n of adj.get(id) || []) if (!depthOf.has(n)) { depthOf.set(n, depthOf.get(id) + 1); queue.push(n); }
  }
  nodes = graph.nodes.map(rec => {
    const old = prev.get(rec.id);
    const shell = depthOf.has(rec.id) ? depthOf.get(rec.id) : 3;
    const R = shell === 0 ? 0 : 150 * shell + 40;
    const theta = Math.random() * Math.PI * 2, phi = Math.acos(2 * Math.random() - 1);
    const deg = degree.get(rec.id) || 0;
    const node = {
      id: rec.id, rec, degree: deg,
      r: Math.min(16, 5 + Math.sqrt(deg) * 1.6) * (rec.id === rootId ? 1.5 : 1),
      color: kindColor(rec.kind),
      x: old ? old.x : R * Math.sin(phi) * Math.cos(theta),
      y: old ? old.y : R * Math.sin(phi) * Math.sin(theta),
      z: old ? old.z : R * Math.cos(phi),
      vx: 0, vy: 0, vz: 0, sx: 0, sy: 0, ss: 1, sz: 0
    };
    nodeById.set(rec.id, node);
    return node;
  });
  edges = graph.edges
    .filter(e => nodeById.has(e.sourceId) && nodeById.has(e.targetId))
    .map(e => ({
      source: nodeById.get(e.sourceId), target: nodeById.get(e.targetId),
      kind: e.kind, color: edgeColor(e.kind), pulse: Math.random()
    }));
  state.rootId = rootId;
  followTarget = nodeById.get(rootId) || null;
  $('emptyHint').classList.add('hidden');
  cam.tx = cam.ty = cam.tz = 0;
  cam.ox = cam.oy = 0;
  autoFit = true;
  heat = 1;
}

let heat = 0; // physics annealing: high right after a new graph, cools down
function physicsStep(dt) {
  if (!state.physics || nodes.length === 0) return;
  const damp = 0.86, k = heat * 0.9 + 0.1;
  // Pairwise repulsion (n ≤ 300).
  for (let i = 0; i < nodes.length; i++) {
    const a = nodes[i];
    for (let j = i + 1; j < nodes.length; j++) {
      const b = nodes[j];
      let dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
      let d2 = dx * dx + dy * dy + dz * dz + 40;
      if (d2 > 260000) continue;
      const f = 26000 * k / d2;
      const d = Math.sqrt(d2);
      dx /= d; dy /= d; dz /= d;
      a.vx += dx * f; a.vy += dy * f; a.vz += dz * f;
      b.vx -= dx * f; b.vy -= dy * f; b.vz -= dz * f;
    }
  }
  // Springs along edges.
  for (const e of edges) {
    const a = e.source, b = e.target;
    let dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
    const d = Math.sqrt(dx * dx + dy * dy + dz * dz) || 1;
    const rest = e.kind === 'contains' ? 90 : 150;
    const f = (d - rest) * 0.02 * k;
    dx /= d; dy /= d; dz /= d;
    a.vx += dx * f; a.vy += dy * f; a.vz += dz * f;
    b.vx -= dx * f; b.vy -= dy * f; b.vz -= dz * f;
  }
  for (const n of nodes) {
    n.vx -= n.x * 0.004; n.vy -= n.y * 0.004; n.vz -= n.z * 0.004;
    n.vx *= damp; n.vy *= damp; n.vz *= damp;
    n.x += n.vx * dt; n.y += n.vy * dt; n.z += n.vz * dt;
  }
  // Remove cluster drift: keep the centroid at the origin so the camera always looks at the graph.
  let cx = 0, cyc = 0, cz = 0;
  for (const n of nodes) { cx += n.x; cyc += n.y; cz += n.z; }
  cx /= nodes.length; cyc /= nodes.length; cz /= nodes.length;
  for (const n of nodes) { n.x -= cx; n.y -= cyc; n.z -= cz; }
  heat = Math.max(0, heat - dt * 0.012);
}

/* Frame the whole cluster: pick a camera distance where the bounding radius fits the viewport. */
let autoFit = true;
let followTarget = null; // node the camera orbits around (selected node, root by default)
function fitCamera() {
  if (!nodes.length) return;
  const ox = followTarget ? followTarget.x : 0, oy = followTarget ? followTarget.y : 0, oz = followTarget ? followTarget.z : 0;
  let r = 0;
  for (const n of nodes) r = Math.max(r, Math.hypot(n.x - ox, n.y - oy, n.z - oz));
  const minWH = Math.min(canvas.clientWidth, canvas.clientHeight) || 800;
  cam.targetDist = Math.max(320, Math.min(3200, (r + 140) * cam.fov / (0.42 * minWH)));
}

/* Center of the unobstructed viewport area (side panels overlay the canvas). */
function viewCenter(w, h) {
  const left = $('leftPanel').classList.contains('hidden') ? 0 : 354;
  const right = $('detailPanel').classList.contains('hidden') ? 0 : 384;
  return { x: left + (w - left - right) / 2, y: h / 2 };
}

function project(n, cx0, cy0, cy, sy, cp, sp) {
  // Translate to the camera target (pan), rotate around Y (yaw), then X (pitch), then perspective.
  const px = n.x - cam.tx, py = n.y - cam.ty, pz = n.z - cam.tz;
  const x1 = px * cy + pz * sy;
  const z1 = -px * sy + pz * cy;
  const y2 = py * cp - z1 * sp;
  const z2 = py * sp + z1 * cp;
  const zc = z2 + cam.dist;
  const s = cam.fov / Math.max(60, zc);
  n.sx = cx0 + cam.ox + x1 * s;
  n.sy = cy0 + cam.oy + y2 * s;
  n.ss = s;
  n.sz = zc;
}

let lastT = performance.now();
function frame(t) {
  const dt = Math.min(2.5, (t - lastT) / 16.6); lastT = t;
  const w = canvas.clientWidth, h = canvas.clientHeight;

  if (state.spin && t - lastInteraction > 2600) cam.yaw += 0.0016 * dt;
  cam.dist += (cam.targetDist - cam.dist) * 0.12;

  physicsStep(dt);
  // Orbit center glides to the focused node so rotation happens around it.
  if (followTarget && nodeById.get(followTarget.id) === followTarget) {
    cam.tx += (followTarget.x - cam.tx) * 0.10 * dt;
    cam.ty += (followTarget.y - cam.ty) * 0.10 * dt;
    cam.tz += (followTarget.z - cam.tz) * 0.10 * dt;
  }
  if (autoFit && nodes.length) fitCamera();

  const cy = Math.cos(cam.yaw), sy = Math.sin(cam.yaw);
  const cp = Math.cos(cam.pitch), sp = Math.sin(cam.pitch);

  ctx.clearRect(0, 0, w, h);

  // Starfield with parallax from camera angles.
  ctx.save();
  for (const st of stars) {
    const x = Math.cos(st.a + cam.yaw * .35) * Math.cos(st.b) * st.d;
    const y = Math.sin(st.b + cam.pitch * .2) * st.d;
    const z = Math.sin(st.a + cam.yaw * .35) * Math.cos(st.b) * st.d;
    const zc = z + 3600;
    if (zc < 200) continue;
    const s = 900 / zc;
    const alpha = 0.25 + 0.35 * Math.abs(Math.sin(t / 900 + st.tw));
    ctx.fillStyle = `rgba(160,200,255,${alpha * Math.min(1, s * 2)})`;
    ctx.fillRect(w / 2 + x * s, h / 2 + y * s, st.s, st.s);
  }
  ctx.restore();

  if (nodes.length === 0) { requestAnimationFrame(frame); return; }

  const vc = viewCenter(w, h);
  for (const n of nodes) project(n, vc.x, vc.y, cy, sy, cp, sp);

  const selected = state.selection ? nodeById.get(state.selection) : null;
  const related = new Set();
  if (selected) for (const e of edges) {
    if (e.source === selected) related.add(e.target.id);
    if (e.target === selected) related.add(e.source.id);
  }

  // Edges (painter's order not needed; alpha by depth).
  ctx.lineWidth = 1;
  for (const e of edges) {
    const a = e.source, b = e.target;
    if (a.sz < 80 || b.sz < 80) continue;
    const depthAlpha = Math.min(1, 620 / ((a.sz + b.sz) / 2));
    const focus = !selected || a === selected || b === selected;
    const alpha = depthAlpha * (focus ? 0.55 : 0.10);
    ctx.strokeStyle = e.color;
    ctx.globalAlpha = alpha;
    ctx.beginPath(); ctx.moveTo(a.sx, a.sy); ctx.lineTo(b.sx, b.sy); ctx.stroke();
    // Traveling pulse on call/import edges.
    if ((e.kind === 'calls' || e.kind === 'imports' || e.kind === 'constructs') && focus) {
      e.pulse = (e.pulse + 0.004 * dt) % 1;
      const px = a.sx + (b.sx - a.sx) * e.pulse, py = a.sy + (b.sy - a.sy) * e.pulse;
      ctx.globalAlpha = alpha * 1.6;
      ctx.fillStyle = e.color;
      ctx.beginPath(); ctx.arc(px, py, 1.8, 0, Math.PI * 2); ctx.fill();
    }
  }
  ctx.globalAlpha = 1;

  // Nodes back-to-front.
  const sorted = [...nodes].sort((a, b) => b.sz - a.sz);
  for (const n of sorted) {
    if (n.sz < 80) continue;
    const r = Math.max(1.5, n.r * n.ss);
    const dim = selected && n !== selected && !related.has(n.id);
    const alpha = Math.min(1, 700 / n.sz) * (dim ? 0.25 : 1);
    ctx.globalAlpha = alpha;
    const isFocus = n === selected || n === hovered || n.id === state.rootId;
    ctx.shadowColor = n.color;
    ctx.shadowBlur = isFocus ? 22 : 10;
    const g = ctx.createRadialGradient(n.sx - r * .35, n.sy - r * .35, r * .1, n.sx, n.sy, r);
    g.addColorStop(0, '#ffffff');
    g.addColorStop(0.25, n.color);
    g.addColorStop(1, shade(n.color, -0.55));
    ctx.fillStyle = g;
    ctx.beginPath(); ctx.arc(n.sx, n.sy, r, 0, Math.PI * 2); ctx.fill();
    ctx.shadowBlur = 0;
    if (n === selected) {
      ctx.strokeStyle = '#ffffff';
      ctx.globalAlpha = alpha * (0.6 + 0.4 * Math.sin(t / 260));
      ctx.lineWidth = 1.6;
      ctx.beginPath(); ctx.arc(n.sx, n.sy, r + 5, 0, Math.PI * 2); ctx.stroke();
      ctx.lineWidth = 1;
    }
    // Labels: root, selection, hover and hubs when close enough.
    if (state.labels && !dim && (isFocus || (n.degree >= 4 && n.ss > 0.75) || n.ss > 1.15)) {
      ctx.globalAlpha = Math.min(1, alpha + .2);
      ctx.font = `${isFocus ? '600 ' : ''}${Math.max(10, 11 * Math.min(1.25, n.ss))}px Inter, system-ui`;
      ctx.fillStyle = isFocus ? '#eaf4ff' : 'rgba(200,220,240,.85)';
      ctx.fillText(n.rec.name, n.sx + r + 6, n.sy + 3);
      if (isFocus) {
        ctx.font = `${Math.max(9, 9.5 * Math.min(1.1, n.ss))}px ${'"Cascadia Code"'}, Consolas, monospace`;
        ctx.fillStyle = 'rgba(110,150,190,.9)';
        ctx.fillText(`${n.rec.filePath}:${n.rec.startLine}`, n.sx + r + 6, n.sy + 16);
      }
    }
  }
  ctx.globalAlpha = 1;
  requestAnimationFrame(frame);
}

function shade(hex, amount) {
  const v = parseInt(hex.slice(1), 16);
  let r = (v >> 16) & 255, g = (v >> 8) & 255, b = v & 255;
  r = Math.round(r * (1 + amount)); g = Math.round(g * (1 + amount)); b = Math.round(b * (1 + amount));
  return `rgb(${Math.max(0, Math.min(255, r))},${Math.max(0, Math.min(255, g))},${Math.max(0, Math.min(255, b))})`;
}

/* ── Canvas interaction ─────────────────────────────────── */
let dragging = false, panning = false, moved = false, lastX = 0, lastY = 0, velYaw = 0, velPitch = 0;
canvas.addEventListener('mousedown', e => {
  dragging = true; moved = false;
  // Standard 3D-viewer controls: left drag orbits, right/middle/shift drag pans.
  panning = e.button === 1 || e.button === 2 || e.shiftKey;
  if (e.button === 1) e.preventDefault();
  lastX = e.clientX; lastY = e.clientY;
  canvas.classList.add('dragging');
  lastInteraction = performance.now();
});
canvas.addEventListener('contextmenu', e => e.preventDefault());
window.addEventListener('mouseup', () => { dragging = false; panning = false; canvas.classList.remove('dragging'); });
window.addEventListener('mousemove', e => {
  if (dragging) {
    const dx = e.clientX - lastX, dy = e.clientY - lastY;
    if (Math.abs(dx) + Math.abs(dy) > 2) moved = true;
    if (panning) {
      // Pan is a persistent screen-space offset; the orbit pivot (selected node) is unaffected,
      // so rotation keeps happening around the node exactly where you dragged it.
      cam.ox += dx;
      cam.oy += dy;
    } else {
      cam.yaw += dx * 0.0052;
      cam.pitch = Math.max(-1.35, Math.min(1.35, cam.pitch + dy * 0.0052));
      velYaw = dx * 0.0052; velPitch = dy * 0.0052;
    }
    lastX = e.clientX; lastY = e.clientY;
    lastInteraction = performance.now();
  } else {
    hovered = pick(e.clientX, e.clientY);
    updateTooltip(e.clientX, e.clientY);
  }
});
canvas.addEventListener('wheel', e => {
  e.preventDefault();
  autoFit = false;
  cam.targetDist = Math.max(220, Math.min(3200, cam.targetDist * (e.deltaY > 0 ? 1.12 : 0.89)));
  lastInteraction = performance.now();
}, { passive: false });
canvas.addEventListener('click', e => {
  if (moved) return;
  const n = pick(e.clientX, e.clientY);
  if (n) selectNode(n.id); else { state.selection = null; followTarget = null; $('detailPanel').classList.add('hidden'); }
});
canvas.addEventListener('dblclick', e => {
  const n = pick(e.clientX, e.clientY);
  if (n) loadGraph(n.id, n.rec.name);
});

function pick(mx, my) {
  let best = null, bestD = 1e9;
  for (const n of nodes) {
    if (n.sz < 80) continue;
    const r = Math.max(6, n.r * n.ss) + 4;
    const dx = mx - n.sx, dy = my - n.sy;
    const d = dx * dx + dy * dy;
    if (d < r * r && n.sz - (best ? best.sz : 1e9) < 0 || (d < r * r && !best)) { best = n; bestD = d; }
    else if (d < r * r && n.sz < best.sz) best = n;
  }
  return best;
}

function updateTooltip(mx, my) {
  const tip = $('tooltip');
  if (!hovered) { tip.classList.add('hidden'); return; }
  tip.innerHTML = `<div class="t-name">${esc(hovered.rec.name)}</div>
    <div class="t-meta">${esc(hovered.rec.kind)} · ${hovered.degree} connections</div>
    <div class="t-file">${esc(hovered.rec.filePath)}:${hovered.rec.startLine}</div>`;
  tip.style.left = Math.min(window.innerWidth - 360, mx + 16) + 'px';
  tip.style.top = (my + 14) + 'px';
  tip.classList.remove('hidden');
}

/* ── API helpers ────────────────────────────────────────── */
async function json(url, options) {
  const response = await fetch(url, options);
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || response.statusText);
  return data;
}
const esc = v => String(v ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));

let toastTimer = null;
function toast(message, isError) {
  const el = $('toast');
  el.textContent = message;
  el.classList.toggle('error', !!isError);
  el.classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add('hidden'), 3200);
}

/* ── Repos ──────────────────────────────────────────────── */
async function refreshRepos(selectId) {
  try {
    state.repos = await json('/api/repos');
    const select = $('repoSelect');
    const current = selectId || state.repoId;
    select.innerHTML = state.repos.length
      ? state.repos.map(r => `<option value="${esc(r.definition.id)}">${esc(r.definition.id)} · ${r.status.symbols} sym</option>`).join('')
      : '<option value="">— no repositories —</option>';
    if (current && state.repos.some(r => r.definition.id === current)) select.value = current;
    const chosen = select.value || null;
    if (chosen !== state.repoId) { state.repoId = chosen; onRepoChanged(); }
    else state.repoId = chosen;
    updateStats();
  } catch { /* server offline; health loop reports it */ }
}

function updateStats() {
  const repo = state.repos.find(r => r.definition.id === state.repoId);
  $('repoStats').textContent = repo
    ? `${repo.status.symbols.toLocaleString()} symbols · ${repo.status.relationships.toLocaleString()} edges${repo.status.indexing ? ' · indexing…' : ''}${repo.status.watcherActive ? ' · watching' : ''}`
    : '';
}

function onRepoChanged() {
  nodes = []; edges = []; state.selection = null; state.rootId = null;
  $('detailPanel').classList.add('hidden');
  $('results').innerHTML = '';
  $('emptyHint').classList.remove('hidden');
  if (!state.repoId) return;
  loadOverview();
  loadFiles();
}

async function health() {
  try {
    await json('/health');
    $('health').classList.add('on');
    $('health').innerHTML = '<i class="dot"></i>online';
  } catch {
    $('health').classList.remove('on');
    $('health').innerHTML = '<i class="dot"></i>offline';
  }
}

/* ── Search ─────────────────────────────────────────────── */
let searchTimer = null;
async function search() {
  if (!state.repoId) return;
  const q = $('query').value.trim();
  if (!q) { $('results').innerHTML = ''; return; }
  try {
    const url = `/api/search/${encodeURIComponent(state.repoId)}?q=${encodeURIComponent(q)}&limit=40${state.kind ? `&kind=${encodeURIComponent(state.kind)}` : ''}`;
    const results = await json(url);
    $('results').innerHTML = results.length ? results.map(r => `
      <div class="result" data-id="${esc(r.symbol.id)}" data-name="${esc(r.symbol.name)}">
        <div class="result-name"><span class="kind-dot" style="background:${kindColor(r.symbol.kind)}"></span>${esc(r.symbol.name)}</div>
        <div class="result-meta">${esc(r.symbol.kind)} · ${esc(r.symbol.qualifiedName)}</div>
        <div class="result-line">${esc(r.symbol.filePath)}:${r.symbol.startLine}</div>
      </div>`).join('') : '<div class="empty">No symbols found</div>';
    document.querySelectorAll('#results .result').forEach(el => el.onclick = () => {
      document.querySelectorAll('#results .result').forEach(x => x.classList.remove('active'));
      el.classList.add('active');
      loadGraph(el.dataset.id, el.dataset.name);
    });
  } catch (error) { $('results').innerHTML = `<div class="empty">${esc(error.message)}</div>`; }
}

/* ── Graph / selection ──────────────────────────────────── */
async function loadGraph(symbolId, name) {
  if (!state.repoId) return;
  try {
    const graph = await json(`/api/graph/${encodeURIComponent(state.repoId)}/${encodeURIComponent(symbolId)}?depth=${state.depth}&limit=${state.limit}`);
    if (!graph.nodes.length) { toast('No relationships recorded for this symbol'); return; }
    setGraph(graph, symbolId);
    selectNode(symbolId);
    if (graph.truncated) toast(`Graph truncated to ${state.limit} nodes — raise the nodes slider for more`);
  } catch (error) { toast(error.message, true); }
}

async function selectNode(symbolId) {
  state.selection = symbolId;
  const node = nodeById.get(symbolId);
  if (!node) return;
  followTarget = node; // clicking a node makes it the orbit center
  $('detailPanel').classList.remove('hidden');
  $('detailName').textContent = node.rec.name;
  $('detailKind').textContent = `${node.rec.kind} · ${node.rec.language}`;
  $('detailKind').style.color = kindColor(node.rec.kind);
  $('detailFile').textContent = `${node.rec.filePath}:${node.rec.startLine}:${node.rec.startColumn}`;
  $('detailSignature').textContent = node.rec.signature;
  $('sourceView').classList.add('hidden');
  try {
    const detail = await json(`/api/symbol/${encodeURIComponent(state.repoId)}/${encodeURIComponent(symbolId)}?limit=60`);
    state.detail = detail;
    renderEdgeList('edgesOut', detail.outgoing, detail.neighbors, e => e.targetId, symbolId);
    renderEdgeList('edgesIn', detail.incoming, detail.neighbors, e => e.sourceId, symbolId);
  } catch { state.detail = null; }
}

function renderEdgeList(containerId, edgeRecords, neighbors, otherId, selfId) {
  const byId = new Map(neighbors.map(n => [n.id, n]));
  const container = $(containerId).querySelector('.edge-list');
  const items = edgeRecords
    .filter(e => otherId(e) !== selfId)
    .slice(0, 40)
    .map(e => {
      const other = byId.get(otherId(e));
      const label = other ? other.name : otherId(e).slice(0, 10) + '…';
      const color = edgeColor(e.kind);
      return `<div class="edge-item" data-id="${esc(otherId(e))}" data-name="${esc(label)}">
        <span class="edge-kind" style="color:${color};border-color:${color}55">${esc(e.kind)}</span>
        <span class="edge-target">${esc(label)}</span>
      </div>`;
    });
  container.innerHTML = items.length ? items.join('') : '<div class="empty">none</div>';
  container.querySelectorAll('.edge-item').forEach(el => el.onclick = () => {
    if (nodeById.has(el.dataset.id)) selectNode(el.dataset.id);
    else loadGraph(el.dataset.id, el.dataset.name);
  });
}

async function viewSource() {
  const node = state.selection ? nodeById.get(state.selection) : null;
  if (!node) return;
  const rec = node.rec;
  const start = Math.max(1, rec.startLine - 3);
  const end = Math.min(rec.endLine + 3, rec.startLine + 120);
  try {
    const slice = await json(`/api/repos/${encodeURIComponent(state.repoId)}/source?path=${encodeURIComponent(rec.filePath)}&start=${start}&end=${end}`);
    const lines = slice.content.split('\n');
    $('sourceView').innerHTML = lines.map((line, i) => {
      const ln = slice.startLine + i;
      const active = ln >= rec.startLine && ln <= rec.endLine;
      return `<span class="${active ? 'hl' : ''}"><span class="ln">${ln}</span>${esc(line)}</span>`;
    }).join('\n');
    $('sourceView').classList.remove('hidden');
  } catch (error) { toast(error.message, true); }
}

/* ── Files & overview ───────────────────────────────────── */
async function loadFiles() {
  if (!state.repoId) return;
  const contains = $('fileFilter').value.trim();
  try {
    const files = await json(`/api/repos/${encodeURIComponent(state.repoId)}/files?limit=400${contains ? `&contains=${encodeURIComponent(contains)}` : ''}`);
    $('fileList').innerHTML = files.length ? files.map(f => `
      <div class="result" data-path="${esc(f.filePath)}">
        <div class="result-name"><span class="kind-dot" style="background:${kindColor(f.language)}"></span>${esc(f.filePath.split('/').pop())}</div>
        <div class="result-meta">${esc(f.filePath)}</div>
        <div class="result-line">${f.symbols} declarations · ${esc(f.language)}</div>
      </div>`).join('') : '<div class="empty">No files indexed yet</div>';
    document.querySelectorAll('#fileList .result').forEach(el => el.onclick = () => openOutline(el.dataset.path));
  } catch (error) { $('fileList').innerHTML = `<div class="empty">${esc(error.message)}</div>`; }
}

async function openOutline(path) {
  try {
    const outline = await json(`/api/repos/${encodeURIComponent(state.repoId)}/outline?path=${encodeURIComponent(path)}`);
    if (!outline.symbols.length) { toast('No declarations in this file'); return; }
    // Show outline entries in the search tab and jump the graph to the file's main symbol.
    switchTab('search');
    $('query').value = '';
    $('results').innerHTML = outline.symbols.map(s => `
      <div class="result" data-id="${esc(s.id)}" data-name="${esc(s.name)}">
        <div class="result-name"><span class="kind-dot" style="background:${kindColor(s.kind)}"></span>${esc(s.name)}</div>
        <div class="result-meta">${esc(s.kind)} · line ${s.startLine}</div>
        <div class="result-line">${esc(s.filePath)}:${s.startLine}</div>
      </div>`).join('');
    document.querySelectorAll('#results .result').forEach(el => el.onclick = () => loadGraph(el.dataset.id, el.dataset.name));
    const main = outline.symbols.reduce((a, b) => (b.endLine - b.startLine) > (a.endLine - a.startLine) ? b : a);
    loadGraph(main.id, main.name);
  } catch (error) { toast(error.message, true); }
}

async function loadOverview() {
  if (!state.repoId) return;
  try {
    const ov = await json(`/api/repos/${encodeURIComponent(state.repoId)}/overview`);
    const bars = (entries, colorFor, clickKind) => {
      const max = Math.max(1, ...entries.map(e => e.count));
      return entries.map(e => `
        <div class="ov-bar${clickKind ? ' clickable' : ''}" ${clickKind ? `data-kind="${esc(e.key)}"` : ''} title="${esc(e.key)}">
          <span class="ov-key">${esc(e.key)}</span>
          <span class="ov-track"><span class="ov-fill" style="width:${Math.round(e.count / max * 100)}%;${colorFor ? `background:${colorFor(e.key)}` : ''}"></span></span>
          <span class="ov-count">${e.count.toLocaleString()}</span>
        </div>`).join('');
    };
    $('overview').innerHTML = `
      <div class="ov-block"><h4>Totals</h4>
        <div class="ov-bar"><span class="ov-key">symbols</span><span class="ov-count">${ov.symbols.toLocaleString()}</span></div>
        <div class="ov-bar"><span class="ov-key">relationships</span><span class="ov-count">${ov.relationships.toLocaleString()}</span></div>
      </div>
      <div class="ov-block"><h4>Hubs — most connected</h4>${ov.hubs.map(h => `
        <div class="ov-bar clickable" data-hub="${esc(h.symbol.id)}" data-name="${esc(h.symbol.name)}" title="${esc(h.symbol.filePath)}">
          <span class="ov-key" style="color:${kindColor(h.symbol.kind)}">${esc(h.symbol.name)}</span>
          <span class="ov-count">${h.degree}</span>
        </div>`).join('')}</div>
      <div class="ov-block"><h4>Symbol kinds</h4>${bars(ov.kinds, kindColor)}</div>
      <div class="ov-block"><h4>Edge kinds</h4>${bars(ov.edgeKinds, edgeColor)}</div>
      <div class="ov-block"><h4>Languages</h4>${bars(ov.languages, kindColor)}</div>
      <div class="ov-block"><h4>Top files</h4>${ov.topFiles.map(f => `
        <div class="ov-bar clickable" data-file="${esc(f.key)}" title="${esc(f.key)}">
          <span class="ov-key">${esc(f.key.split('/').pop())}</span>
          <span class="ov-count">${f.count}</span>
        </div>`).join('')}</div>`;
    $('overview').querySelectorAll('[data-hub]').forEach(el => el.onclick = () => loadGraph(el.dataset.hub, el.dataset.name));
    $('overview').querySelectorAll('[data-file]').forEach(el => el.onclick = () => openOutline(el.dataset.file));
  } catch (error) { $('overview').innerHTML = `<div class="empty">${esc(error.message)}</div>`; }
}

/* ── UI wiring ──────────────────────────────────────────── */
function switchTab(name) {
  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === name));
  ['search', 'files', 'overview'].forEach(id => $(`tab-${id}`).classList.toggle('hidden', id !== name));
}
document.querySelectorAll('.tab').forEach(t => t.onclick = () => switchTab(t.dataset.tab));

$('collapseLeft').onclick = () => { $('leftPanel').classList.add('hidden'); $('restoreLeft').classList.remove('hidden'); };
$('restoreLeft').onclick = () => { $('leftPanel').classList.remove('hidden'); $('restoreLeft').classList.add('hidden'); };
$('closeDetail').onclick = () => { $('detailPanel').classList.add('hidden'); state.selection = null; };
$('centerHere').onclick = () => { const n = nodeById.get(state.selection); if (n) loadGraph(n.id, n.rec.name); };
$('loadSource').onclick = viewSource;

$('query').addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(search, 260); });
$('query').addEventListener('keydown', e => { if (e.key === 'Enter') search(); });
$('fileFilter').addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(loadFiles, 300); });

$('kindChips').querySelectorAll('.chip').forEach(chip => chip.onclick = () => {
  $('kindChips').querySelectorAll('.chip').forEach(c => c.classList.remove('active'));
  chip.classList.add('active');
  state.kind = chip.dataset.kind;
  search();
});

$('repoSelect').onchange = () => { state.repoId = $('repoSelect').value || null; onRepoChanged(); updateStats(); };
$('addRepo').onclick = () => { $('modalBackdrop').classList.remove('hidden'); $('mError').textContent = ''; $('mRoot').focus(); };
$('mCancel').onclick = () => $('modalBackdrop').classList.add('hidden');
$('modalBackdrop').addEventListener('click', e => { if (e.target === $('modalBackdrop')) $('modalBackdrop').classList.add('hidden'); });
$('mOpen').onclick = async () => {
  const rootPath = $('mRoot').value.trim();
  if (!rootPath) { $('mError').textContent = 'Root path is required'; return; }
  const id = $('mId').value.trim() || rootPath.split(/[\\/]/).filter(Boolean).pop();
  try {
    $('mOpen').disabled = true;
    await json('/api/repos/open', {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ id, rootPath, solutionPath: $('mSolution').value.trim() || null })
    });
    $('modalBackdrop').classList.add('hidden');
    toast(`Repository “${id}” registered — indexing runs in the background`);
    await refreshRepos(id);
  } catch (error) { $('mError').textContent = error.message; }
  finally { $('mOpen').disabled = false; }
};
$('reindexRepo').onclick = async () => {
  if (!state.repoId) return;
  try { await json(`/api/repos/${encodeURIComponent(state.repoId)}/reindex`, { method: 'POST' }); toast('Full reindex started'); }
  catch (error) { toast(error.message, true); }
};

$('depth').oninput = () => { state.depth = +$('depth').value; $('depthValue').textContent = state.depth; };
$('depth').onchange = () => { if (state.rootId) loadGraph(state.rootId); };
$('limitRange').oninput = () => { state.limit = +$('limitRange').value; $('limitValue').textContent = state.limit; };
$('limitRange').onchange = () => { if (state.rootId) loadGraph(state.rootId); };
$('toggleSpin').onclick = () => { state.spin = !state.spin; $('toggleSpin').classList.toggle('active', state.spin); };
$('togglePhysics').onclick = () => { state.physics = !state.physics; $('togglePhysics').classList.toggle('active', state.physics); if (state.physics) heat = Math.max(heat, .4); };
$('toggleLabels').onclick = () => { state.labels = !state.labels; $('toggleLabels').classList.toggle('active', state.labels); };
$('resetCamera').onclick = () => {
  cam.yaw = CAM_HOME.yaw; cam.pitch = CAM_HOME.pitch;
  followTarget = (state.selection && nodeById.get(state.selection)) || (state.rootId && nodeById.get(state.rootId)) || null;
  if (!followTarget) { cam.tx = CAM_HOME.tx; cam.ty = CAM_HOME.ty; cam.tz = CAM_HOME.tz; }
  cam.ox = CAM_HOME.ox; cam.oy = CAM_HOME.oy;
  autoFit = true;
  if (nodes.length) fitCamera(); else cam.targetDist = CAM_HOME.dist;
};

// Keyboard: arrows pan, +/- zoom, 0 resets — skipped while typing in inputs.
window.addEventListener('keydown', e => {
  if (/^(INPUT|SELECT|TEXTAREA)$/.test(document.activeElement?.tagName || '')) return;
  const step = 36;
  const pan = (dx, dy) => {
    cam.ox -= dx;
    cam.oy -= dy;
    lastInteraction = performance.now();
  };
  switch (e.key) {
    case 'ArrowLeft': pan(-step, 0); break;
    case 'ArrowRight': pan(step, 0); break;
    case 'ArrowUp': pan(0, -step); break;
    case 'ArrowDown': pan(0, step); break;
    case '+': case '=': autoFit = false; cam.targetDist = Math.max(220, cam.targetDist * 0.89); break;
    case '-': autoFit = false; cam.targetDist = Math.min(3200, cam.targetDist * 1.12); break;
    case '0': $('resetCamera').onclick(); break;
    default: return;
  }
  e.preventDefault();
});

$('legend').innerHTML = Object.entries(EDGE_COLORS)
  .map(([kind, color]) => `<span class="leg"><i style="background:${color};color:${color}"></i>${kind}</span>`).join('');

function resize() {
  const ratio = window.devicePixelRatio || 1;
  canvas.width = window.innerWidth * ratio;
  canvas.height = window.innerHeight * ratio;
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
}
window.addEventListener('resize', resize);

/* Deep link: #repo=<id>&q=<query> auto-searches and renders the first match. */
async function applyDeepLink() {
  const params = new URLSearchParams(location.hash.slice(1));
  const q = params.get('q');
  if (!q) return;
  const repo = params.get('repo');
  if (repo && state.repos.some(r => r.definition.id === repo)) {
    state.repoId = repo;
    $('repoSelect').value = repo;
    onRepoChanged();
  }
  if (!state.repoId) return;
  $('query').value = q;
  await search();
  const first = document.querySelector('#results .result');
  if (first) loadGraph(first.dataset.id, first.dataset.name);
}

/* ── Boot ───────────────────────────────────────────────── */
resize();
health();
refreshRepos().then(applyDeepLink);
setInterval(health, 5000);
setInterval(() => refreshRepos(), 6000);
requestAnimationFrame(frame);

