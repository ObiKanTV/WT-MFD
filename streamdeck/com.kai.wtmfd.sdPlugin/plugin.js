// WT-MFD Stream Deck plugin.
// Each key is one MFD button. The key face is drawn live from WT-MFD's own button labels,
// so the middle row changes with the MFD page exactly like the bezel on screen.

const DEFAULT_BASE = "http://127.0.0.1:8120";
const PAGES = ["", "flight", "engine", "map", "systems"]; // top button 1 is free for the folder back key
const PAGE_SHORT = { flight: "FLT", engine: "ENG", map: "MAP", systems: "SYS" };
const PALETTE = { green: "#46ff7e", amber: "#ffb52e", white: "#e9f1f5" };
const NAMED = {
  "page/flight": "FLT", "page/engine": "ENG", "page/map": "MAP", "page/systems": "SYS", "page/next": "NEXT", "page/prev": "PREV",
  "map/zoomin": "ZOOM+", "map/zoomout": "ZOOM-", "map/follow": "FOLLOW", "map/grid": "GRID", "map/trail": "TRAIL", "map/mission": "MSN",
  "fuel/bingo-up": "BINGO+", "fuel/bingo-down": "BINGO-", "fuel/reset": "BURN RST", "g/reset": "G RST", "speed/toggle": "IAS TAS",
  "events/filter": "FILTER", "events/clear": "CLEAR", "display/night": "NIGHT", "display/dim": "DIM", "display/bright": "BRT",
  "display/color": "COLOR", "display/units": "UNITS", "wp/next": "DEL WP", "wp/clear": "CLR WP", "tgt/next": "NEXT TGT", "tgt/clear": "CLR TGT", "wpn/next": "WPN",
  "cycle/reset": "CYC RST", "cycle/stop": "CYC OFF",
  "fuel/bingo-up-500": "BINGO +500", "fuel/bingo-down-500": "BINGO -500", "fuel/bingo-25": "BINGO 25%",
};

let ws = null;
const keys = {};          // context -> { cmd, base }
let ui = null;            // latest button state from WT-MFD
let cycle = null, cycleAt = 0; // Air Sim reward cycle from WT-MFD's status messages (about once a second)
let appUp = false;
let es = null, esBase = null;

// ------------------------------------------------------------------ Stream Deck connection
function connectElgatoStreamDeckSocket(port, uuid, registerEvent) {
  ws = new WebSocket("ws://127.0.0.1:" + port);
  ws.onopen = () => ws.send(JSON.stringify({ event: registerEvent, uuid }));
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    const ctx = m.context;
    const payload = m.payload || {};
    switch (m.event) {
      case "willAppear": {
        const s = Object.assign({}, payload.settings || {});
        if (!s.cmd) { s.cmd = defaultCmd(payload.coordinates); send({ event: "setSettings", context: ctx, payload: s }); }
        keys[ctx] = { cmd: s.cmd, base: (s.base || DEFAULT_BASE).replace(/\/+$/, "") };
        send({ event: "setTitle", context: ctx, payload: { title: "", target: 0 } });
        ensureStream();
        draw(ctx);
        break;
      }
      case "willDisappear":
        delete keys[ctx];
        break;
      case "didReceiveSettings": {
        const s = payload.settings || {};
        if (keys[ctx]) {
          keys[ctx].cmd = s.cmd || keys[ctx].cmd;
          keys[ctx].base = (s.base || DEFAULT_BASE).replace(/\/+$/, "");
        }
        ensureStream();
        draw(ctx);
        break;
      }
      case "keyDown":
        press(ctx);
        break;
    }
  };
}

function send(o) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(o)); }

// A fresh key picks its button from where it sits: row 1 = pages, row 2 = page buttons, row 3 = display.
function defaultCmd(c) {
  c = c || { column: 0, row: 0 };
  const row = ["t", "b", "g"][c.row];
  if (!row || c.column > 4) return "page/next";
  return "osb/" + row + (c.column + 1);
}

function press(ctx) {
  const k = keys[ctx];
  if (!k) return;
  fetch(k.base + "/api/cmd/" + k.cmd, { method: "POST", mode: "no-cors" })
    .catch(() => send({ event: "showAlert", context: ctx }));
}

// ------------------------------------------------------------------ live labels from WT-MFD
function ensureStream() {
  const first = Object.values(keys)[0];
  const base = first ? first.base : DEFAULT_BASE;
  if (es && esBase === base) return;
  if (es) es.close();
  esBase = base;
  es = new EventSource(base + "/api/stream?only=ui");
  es.onopen = () => { appUp = true; drawAll(); };
  es.onerror = () => { appUp = false; drawAll(); }; // EventSource retries on its own
  es.onmessage = ev => {
    const m = JSON.parse(ev.data);
    if (m.type === "ui") { ui = m.data; appUp = true; drawAll(); }
    else if (m.type === "status" && m.data && m.data.cycle) { cycle = m.data.cycle; cycleAt = Date.now(); appUp = true; drawCycleKeys(); }
  };
}

function keyInfo(cmd) {
  const m = /^osb\/([tbg])([1-5])$/.exec(cmd);
  if (m) {
    const i = +m[2] - 1;
    if (!ui) return { label: m[1].toUpperCase() + m[2], active: false, caption: "" };
    const row = { t: ui.osb.top, b: ui.osb.bottom, g: ui.osb.global }[m[1]] || [];
    return {
      label: row[i] || "",
      // matched by label, not position, so moving page buttons around on the MFD never mixes up the highlight
      active: (m[1] === "t" && !!row[i] && row[i] === PAGE_SHORT[ui.page]) || (m[1] === "g" && row[i] === "DAY"),
      // rows 2 and 3 change with the page (row 3 is display settings only on SYS): show which page they belong to
      caption: m[1] === "b" || (m[1] === "g" && ui.page !== "systems") ? PAGE_SHORT[ui.page] || "" : "",
    };
  }
  const page = { "page/flight": "flight", "page/engine": "engine", "page/map": "map", "page/systems": "systems" }[cmd];
  return { label: NAMED[cmd] || cmd.toUpperCase(), active: !!(ui && page && ui.page === page), caption: "" };
}

// ------------------------------------------------------------------ key faces
function drawAll() { Object.keys(keys).forEach(draw); }
function drawCycleKeys() { Object.keys(keys).forEach(ctx => { if (keys[ctx].cmd === "cycle/reset") draw(ctx); }); }

// The cycle key: a live countdown to the next payout; pressing it restarts the cycle (CYC RST).
//   C2 / 11:42 / K1   second cycle, 11:42 to go, one kill in it
//   LAND              a cycle paid out: landing now collects the last 20%
//   DOWN              shot down, waiting for the next spawn
function drawCycle(g, S, fg) {
  const c = cycle;
  const amber = PALETTE.amber, red = "#ff4a3d", dim = "#5a6066";
  let top = "CYCLE", big = "--:--", bottom = "RST", col = dim, bottomCol = dim;
  if (c && c.state === "down") { big = "DOWN"; col = red; }
  else if (c && c.state === "spawning") { big = "READY"; }
  else if (c && c.state === "running" && c.leftS != null) {
    const left = Math.max(0, c.leftS - (Date.now() - cycleAt) / 1000);
    const sec = Math.ceil(left);
    top = "C" + c.n; big = Math.floor(sec / 60) + ":" + String(sec % 60).padStart(2, "0"); col = left < 60 ? amber : fg;
    bottom = c.kills ? "K" + c.kills : "";
    bottomCol = fg;
    if (c.bonus === "due") { bottom = "LAND"; bottomCol = amber; col = amber; }
    else if (c.bonus === "taken") bottom = "BONUS";
  }
  g.globalAlpha = 0.6; g.fillStyle = col === dim ? dim : fg; g.font = "bold 22px Consolas, monospace"; g.fillText(top, S / 2, 26); g.globalAlpha = 1;
  let size = 44;
  g.font = `bold ${size}px Consolas, monospace`;
  while (size > 20 && g.measureText(big).width > S - 16) { size -= 2; g.font = `bold ${size}px Consolas, monospace`; }
  g.fillStyle = col; g.fillText(big, S / 2, S / 2 + 4);
  if (bottom) { g.fillStyle = bottomCol; g.font = "bold 24px Consolas, monospace"; g.fillText(bottom, S / 2, S - 26); }
}

function draw(ctx) {
  const k = keys[ctx];
  if (!k) return;
  const S = 144;
  const c = document.createElement("canvas");
  c.width = c.height = S;
  const g = c.getContext("2d");
  const fg = PALETTE[(ui && ui.palette) || "green"] || PALETTE.green;

  // bezel button
  const grad = g.createLinearGradient(0, 0, 0, S);
  grad.addColorStop(0, "#24282c");
  grad.addColorStop(1, "#0e1012");
  g.fillStyle = grad;
  g.fillRect(0, 0, S, S);
  g.textAlign = "center";
  g.textBaseline = "middle";

  if (!appUp) {
    g.fillStyle = "#5a6066";
    g.font = "bold 30px Consolas, monospace";
    g.fillText("MFD", S / 2, S / 2 - 16);
    g.font = "bold 22px Consolas, monospace";
    g.fillText("OFF", S / 2, S / 2 + 18);
    return setImage(ctx, c);
  }

  if (k.cmd === "cycle/reset") { drawCycle(g, S, fg); return setImage(ctx, c); }

  const info = keyInfo(k.cmd);
  if (info.active) {
    g.strokeStyle = fg;
    g.lineWidth = 6;
    g.strokeRect(5, 5, S - 10, S - 10);
  }
  if (info.caption) {
    g.globalAlpha = 0.5;
    g.fillStyle = fg;
    g.font = "18px Consolas, monospace";
    g.fillText(info.caption, S / 2, 24);
    g.globalAlpha = 1;
  }
  if (!info.label) {
    g.fillStyle = "#3a3f44";
    g.beginPath(); g.arc(S / 2, S / 2, 5, 0, Math.PI * 2); g.fill();
    return setImage(ctx, c);
  }

  // one or two lines, as big as fits
  const words = info.label.split(" ");
  const lines = words.length > 1 && info.label.length > 6 ? [words.slice(0, Math.ceil(words.length / 2)).join(" "), words.slice(Math.ceil(words.length / 2)).join(" ")] : [info.label];
  let size = lines.length > 1 ? 36 : 44;
  g.font = `bold ${size}px Consolas, monospace`;
  while (size > 16 && Math.max(...lines.map(l => g.measureText(l).width)) > S - 22) {
    size -= 2;
    g.font = `bold ${size}px Consolas, monospace`;
  }
  g.fillStyle = fg;
  g.globalAlpha = info.active ? 1 : 0.85;
  if (info.active) { g.shadowColor = fg; g.shadowBlur = 12; }
  const lh = size * 1.1;
  const y0 = S / 2 + (info.caption ? (lines.length > 1 ? 14 : 8) : 0) - ((lines.length - 1) * lh) / 2;
  lines.forEach((l, i) => g.fillText(l, S / 2, y0 + i * lh));
  setImage(ctx, c);
}

function setImage(ctx, canvas) {
  send({ event: "setImage", context: ctx, payload: { image: canvas.toDataURL("image/png"), target: 0 } });
}
