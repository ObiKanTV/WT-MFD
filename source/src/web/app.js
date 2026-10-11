"use strict";
// WT-MFD front end. Draws War Thunder's own local data (localhost:8111) as an MFD.
// It only shows what the game already publishes for its built-in map and instruments.
const PAGE_NAMES = { flight: "FLT", engine: "ENG", map: "MAP", systems: "SYS" };
const ZOOMS = [1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32];
let ui = null;
let linkStatus = "offline";
let tele = null;
let mapInfo = null;
let mapVersion = -1;
let mapImg = null;
let mapObjs = [];
let mission = null;
let events = [];
let cycle = null;
let cycleAt = 0; // performance.now() when it arrived, so the countdown runs smoothly between messages
let pings = [];
let callout = null;
const CALLOUT_MS = 8000, PING_MS = 45000;
// "A3", "D 7", "D-7", "(H4)". Rejects aircraft names like F-5E or A-5C (letters right after the number).
const GRID_REF = /(?:^|[^A-Za-z0-9-])([A-Z])[ -]?(\d{1,2})(?![0-9A-Za-z-])/g;
// Chat can carry formatting tags (colours etc.) the game itself renders. Strip them for display and parsing.
function stripTags(m) {
    return m
        .replace(/<\/?[A-Za-z][^<>]{0,60}>/g, "")
        .replace(/[\uE000-\uF8FF]/g, "") // the game's own icon font (unit/country symbols)
        .replace(/weapons\/([A-Za-z0-9_]+)(?:\/[A-Za-z_]+)?/g, (_, w) => w.replace(/_/g, " ").toUpperCase()) // untranslated weapon keys
        .replace(/\s+/g, " ").trim();
}
function teamCall(e) {
    return e.kind === "chat" && !e.enemy && !!e.sender && !e.own;
}
function gridSize() {
    const ext = mapExtents();
    if (!ext || !(ext.gsX > 0) || !(ext.gsY > 0))
        return null;
    let cols = 0, rows = 0;
    for (let gx = ext.gzX; gx + ext.gsX <= ext.maxX + 1; gx += ext.gsX)
        cols++;
    for (let gy = ext.gzY; gy - ext.gsY >= ext.minY - 1; gy -= ext.gsY)
        rows++;
    return { ext, cols, rows };
}
// Quick-chat calls carry the sender's position, as the game sends it:
//   "Follow me!<color=#FF96966E> [D6, alt. 1300 m]</color>"
// -> { text: "Follow me!", square: "D6", alt: "1300 m" }
const CALL_POS = /\[\s*([A-Z])[ -]?(\d{1,2})\s*(?:,\s*alt\.?\s*([\d\s.,]+?)\s*(m|ft)?\s*)?\]/i;
function parseCall(msg) {
    const plain = stripTags(msg);
    const m = CALL_POS.exec(plain);
    if (!m)
        return null;
    const alt = m[3] ? `${m[3].replace(/\s/g, "")} ${(m[4] || "m").toLowerCase()}` : "";
    return { text: plain.slice(0, m.index).trim(), square: m[1].toUpperCase() + m[2], row: m[1].toUpperCase().charCodeAt(0) - 65, col: +m[2] - 1, alt };
}
// how a chat line reads on the MFD: tags gone, position written out plainly
function chatText(msg) {
    const c = parseCall(msg);
    return c ? `${c.text} · ${c.square}${c.alt ? " · " + c.alt.toUpperCase() : ""}` : stripTags(msg);
}
function takeCalls(items) {
    const now = Date.now();
    for (const e of items) {
        if (!teamCall(e) || e.backlog)
            continue;
        const age = now - e.t;
        if (age < CALLOUT_MS)
            callout = { who: e.sender, text: chatText(e.msg), until: e.t + CALLOUT_MS };
        if (age > PING_MS)
            continue;
        const g = gridSize();
        const call = parseCall(e.msg);
        // a quick-chat position marks where that teammate is; a square typed in chat marks that square
        const spots = call ? [[call.row, call.col, call.alt]] : gridRefs(stripTags(e.msg)).map(([r, c]) => [r, c, ""]);
        for (const [row, col, alt] of spots) {
            if (!g || row < 0 || row >= g.rows || col < 0 || col >= g.cols)
                continue;
            pings = pings.filter(p => p.who !== e.sender || (!call && (p.row !== row || p.col !== col)));
            pings.push({ col, row, who: e.sender, alt, until: e.t + PING_MS });
        }
    }
    renderCallout();
    mapDirty = true;
}
function renderCallout() {
    const el = $("callout");
    const on = !!callout && callout.until > Date.now();
    el.classList.toggle("on", on);
    if (on)
        el.innerHTML = `<b>${esc(callout.who)}</b> ${esc(callout.text)}`;
}
let trail = [];
let trailAircraft = "";
let lastTrailAt = 0;
let appLink = true;
const $ = (id) => document.getElementById(id);
// ------------------------------------------------------------------ units
const U = {
    spd: (kmh) => ui?.imperial ? kmh / 1.852 : kmh,
    spdU: () => ui?.imperial ? "KT" : "KM/H",
    alt: (m) => ui?.imperial ? m * 3.28084 : m,
    altU: () => ui?.imperial ? "FT" : "M",
    vs: (ms) => ui?.imperial ? ms * 196.85 : ms,
    vsU: () => ui?.imperial ? "FT/MIN" : "M/S",
    mass: (kg) => ui?.imperial ? kg * 2.20462 : kg,
    massU: () => ui?.imperial ? "LB" : "KG",
    dist: (m) => ui?.imperial ? m / 1852 : m / 1000,
    distU: () => ui?.imperial ? "NM" : "KM",
};
const num = (v) => (typeof v === "number" && isFinite(v) ? v : null);
const fmt = (v, digits = 0, width = 0) => {
    if (v === null)
        return "-".repeat(Math.max(width, 3));
    const s = v.toFixed(digits);
    return width ? s.padStart(width, " ") : s;
};
const mmss = (s) => {
    if (s === null || s === undefined || !isFinite(s))
        return "--:--";
    const m = Math.floor(s / 60), r = Math.floor(s % 60);
    return m > 999 ? "999+" : `${m}:${String(r).padStart(2, "0")}`;
};
const pick = (o, ...keys) => {
    if (!o)
        return null;
    for (const k of keys) {
        const v = num(o[k]);
        if (v !== null)
            return v;
    }
    return null;
};
const prettyAircraft = (t) => (t || "").replace(/_/g, " ").toUpperCase() || "NO AIRCRAFT";
// ------------------------------------------------------------------ commands (bezel buttons are clickable too)
function cmd(c) { fetch("/api/cmd/" + c, { method: "POST" }).catch(() => { }); }
function renderOsb() {
    if (!ui)
        return;
    const row = (id, labels, prefix, active) => {
        const el = $(id);
        el.innerHTML = "";
        labels.forEach((l, i) => {
            const b = document.createElement("div");
            b.className = "osb" + (l ? "" : " empty") + (active(i) ? " on" : "");
            b.textContent = l || "·";
            b.onclick = () => { b.classList.add("flash"); setTimeout(() => b.classList.remove("flash"), 150); cmd(`osb/${prefix}${i + 1}`); };
            el.appendChild(b);
        });
    };
    const pages = ["", "flight", "engine", "map", "systems"];
    row("osb-top", ui.osb.top, "t", i => pages[i] === ui.page);
    row("osb-bot", ui.osb.bottom, "b", () => false);
    row("osb-glob", ui.osb.global, "g", i => ui.osb.global[i] === "DAY"); // lit while night mode is on
}
// ------------------------------------------------------------------ page switching and header
function applyUi() {
    if (!ui)
        return;
    const frame = $("frame");
    frame.className = "pal-" + ui.palette;
    const b = (ui.brightness / 100) * (ui.night ? 0.5 : 1);
    frame.style.filter = `brightness(${b})`;
    document.querySelectorAll(".page").forEach(p => p.classList.toggle("on", p.id === "p-" + ui.page));
    $("h-page").textContent = PAGE_NAMES[ui.page] || ui.page.toUpperCase();
    renderOsb();
    renderAll();
    renderMapMission();
    mapDirty = true;
}
function renderHeader() {
    const s = $("h-status");
    s.textContent = !appLink ? "APP LINK LOST" : linkStatus === "flying" ? "LINK" : linkStatus === "hangar" ? "HANGAR" : "NO LINK";
    s.className = linkStatus === "flying" && appLink ? "" : "warnc";
    $("h-ac").textContent = prettyAircraft(tele?.derived?.aircraft || "");
    const warn = $("h-warn");
    const fuel = num(tele?.derived?.fuelKg);
    if (linkStatus === "flying" && ui && fuel !== null && fuel <= ui.bingoKg) {
        warn.textContent = "BINGO";
        warn.className = "blink";
    }
    else {
        warn.textContent = "";
        warn.className = "";
    }
    // flight and engine pages need live flight data; map and systems show whatever is there
    const nd = $("nodata");
    const needsFlight = ui && (ui.page === "flight" || ui.page === "engine");
    const show = !appLink || (needsFlight && linkStatus !== "flying");
    nd.classList.toggle("on", !!show);
    $("nodata-t").textContent = !appLink ? "APP LINK LOST" : linkStatus === "hangar" ? "IN HANGAR" : "NO LINK";
    $("nodata-s").textContent = !appLink ? "IS WT-MFD.EXE RUNNING?" : linkStatus === "hangar" ? "WAITING FOR TAKEOFF" : "START WAR THUNDER";
}
// ------------------------------------------------------------------ reward cycle (header, every page)
// C2 11:42 K1 = second 15-minute cycle of this life, 11:42 until it pays out, one kill in it.
// LAND = a cycle has paid out: landing now collects the last 20%. DOWN = shot down, waiting for the next spawn.
function renderCycle() {
    const el = $("h-cyc");
    const c = cycle;
    let text = "CYC --:--", cls = "dim";
    if (c && c.state === "down") {
        text = "CYC DOWN";
        cls = "alertc";
    }
    else if (c && c.state === "spawning") {
        text = "CYC READY";
    }
    else if (c && c.state === "running" && c.leftS !== null) {
        const since = (performance.now() - cycleAt) / 1000;
        const left = Math.max(0, c.leftS - since);
        const m = Math.floor(Math.ceil(left) / 60), r = Math.ceil(left) % 60;
        text = `C${c.n} ${m}:${String(r).padStart(2, "0")}` + (c.kills ? ` K${c.kills}` : "");
        cls = "";
        if (c.bonus === "due") {
            text += " LAND";
            cls = "warnc" + ((c.bonusDueS ?? 99) + since < 20 ? " blink" : "");
        }
        else if (c.bonus === "taken")
            text += " BONUS";
        else if (left < 60)
            cls = "warnc";
    }
    if (el.textContent !== text)
        el.textContent = text;
    const full = "cyc " + cls;
    if (el.className !== full)
        el.className = full;
}
// ------------------------------------------------------------------ flight page
function renderFlight() {
    if (!tele || !ui)
        return;
    const s = tele.state, ind = tele.ind, d = tele.derived;
    const spd = ui.speedMode === "TAS" ? pick(s, "TAS, km/h") : pick(s, "IAS, km/h");
    $("f-spd-l").textContent = ui.speedMode;
    const hl = holdLine();
    $("f-nav").innerHTML = [navLine(), hl].filter(Boolean).map(l => `<div>${esc(l)}</div>`).join("");
    $("f-spd").textContent = fmt(spd === null ? null : U.spd(spd));
    $("f-spd-u").textContent = U.spdU();
    $("f-mach").textContent = fmt(pick(s, "M"), 2);
    const alt = pick(s, "H, m");
    $("f-alt").textContent = fmt(alt === null ? null : U.alt(alt));
    $("f-alt-u").textContent = U.altU();
    const vs = pick(s, "Vy, m/s");
    $("f-vs").textContent = vs === null ? "---" : (vs > 0 ? "+" : "") + fmt(U.vs(vs), ui.imperial ? 0 : 1);
    $("f-vs-u").textContent = U.vsU();
    const aoa = pick(s, "AoA, deg");
    $("f-aoa").textContent = fmt(aoa, 1);
    const g = pick(s, "Ny");
    const gEl = $("f-g");
    gEl.textContent = fmt(g, 1);
    gEl.className = "val big" + (g !== null && (g > 8 || g < -2) ? " alertc" : g !== null && g > 6 ? " warnc" : "");
    $("f-gmm").textContent = `MIN ${fmt(num(d.gMin), 1)}  MAX ${fmt(num(d.gMax), 1)}`;
    const hdg = pick(ind, "compass");
    $("f-hdg").textContent = hdg === null ? "---" : String(Math.round(((hdg % 360) + 360) % 360)).padStart(3, "0") + "°";
    $("f-thr").textContent = fmt(pick(s, "throttle 1, %"));
    drawAdi();
}
function fitCanvas(c) {
    const r = c.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    const w = Math.max(1, Math.round(r.width * dpr)), h = Math.max(1, Math.round(r.height * dpr));
    if (c.width !== w || c.height !== h) {
        c.width = w;
        c.height = h;
    }
    const ctx = c.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { ctx, w: r.width, h: r.height };
}
const cssVar = (n) => getComputedStyle($("frame")).getPropertyValue(n).trim() || "#46ff7e";
function drawAdi() {
    const c = $("adi");
    const { ctx, w, h } = fitCanvas(c);
    const fg = cssVar("--fg"), dim = cssVar("--dim"), faint = cssVar("--faint");
    ctx.clearRect(0, 0, w, h);
    const ind = tele?.ind || {};
    let pitch = num(ind.aviahorizon_pitch), roll = num(ind.aviahorizon_roll);
    const cx = w / 2, cy = h / 2, R = Math.min(w, h * 0.85) * 0.47;
    ctx.lineWidth = 1.5;
    ctx.strokeStyle = faint;
    ctx.beginPath();
    ctx.arc(cx, cy, R, 0, Math.PI * 2);
    ctx.stroke();
    ctx.font = `${Math.round(R * 0.07)}px Consolas, monospace`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    if (pitch === null || roll === null) {
        ctx.fillStyle = dim;
        ctx.fillText("NO ATTITUDE DATA", cx, cy);
        return;
    }
    if (ui?.invertAdiPitch)
        pitch = -pitch;
    if (ui?.invertAdiRoll)
        roll = -roll;
    const pxPerDeg = R / 30;
    ctx.save();
    ctx.beginPath();
    ctx.arc(cx, cy, R - 2, 0, Math.PI * 2);
    ctx.clip();
    ctx.translate(cx, cy);
    ctx.rotate((-roll * Math.PI) / 180);
    ctx.translate(0, pitch * pxPerDeg);
    // ground shading below the horizon
    ctx.fillStyle = faint;
    ctx.globalAlpha = 0.55;
    ctx.fillRect(-R * 2, 0, R * 4, R * 4);
    ctx.globalAlpha = 1;
    // horizon and pitch ladder
    ctx.strokeStyle = fg;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(-R * 2, 0);
    ctx.lineTo(R * 2, 0);
    ctx.stroke();
    ctx.lineWidth = 1.2;
    ctx.fillStyle = dim;
    for (let p = -90; p <= 90; p += 5) {
        if (p === 0)
            continue;
        const y = -p * pxPerDeg, half = p % 10 === 0 ? R * 0.28 : R * 0.13;
        ctx.strokeStyle = p > 0 ? fg : dim;
        ctx.setLineDash(p < 0 ? [6, 5] : []);
        ctx.beginPath();
        ctx.moveTo(-half, y);
        ctx.lineTo(half, y);
        ctx.stroke();
        if (p % 10 === 0) {
            ctx.fillText(String(Math.abs(p)), -half - R * 0.09, y);
            ctx.fillText(String(Math.abs(p)), half + R * 0.09, y);
        }
    }
    ctx.setLineDash([]);
    // the pitch to hold for the selected weapon (FLT hold-over estimate): amber brackets on the ladder;
    // when they sit on the aircraft symbol, the nose is where it should be
    const hs = holdSolution();
    if (hs && !hs.out) {
        const y = -hs.pitch * pxPerDeg, warn = cssVar("--warn");
        ctx.strokeStyle = warn;
        ctx.lineWidth = 3;
        ctx.beginPath();
        ctx.moveTo(-R * 0.55, y - R * 0.05);
        ctx.lineTo(-R * 0.48, y);
        ctx.lineTo(-R * 0.55, y + R * 0.05);
        ctx.moveTo(R * 0.55, y - R * 0.05);
        ctx.lineTo(R * 0.48, y);
        ctx.lineTo(R * 0.55, y + R * 0.05);
        ctx.stroke();
    }
    ctx.restore();
    // roll scale and pointer
    ctx.save();
    ctx.translate(cx, cy);
    ctx.strokeStyle = dim;
    ctx.lineWidth = 1.5;
    for (const a of [-60, -45, -30, -20, -10, 0, 10, 20, 30, 45, 60]) {
        const r = ((a - 90) * Math.PI) / 180, l = a % 30 === 0 ? 0.1 : 0.05;
        ctx.beginPath();
        ctx.moveTo(Math.cos(r) * R, Math.sin(r) * R);
        ctx.lineTo(Math.cos(r) * R * (1 - l), Math.sin(r) * R * (1 - l));
        ctx.stroke();
    }
    ctx.rotate((-roll * Math.PI) / 180);
    ctx.fillStyle = fg;
    ctx.beginPath();
    ctx.moveTo(0, -R * 0.86);
    ctx.lineTo(-R * 0.04, -R * 0.78);
    ctx.lineTo(R * 0.04, -R * 0.78);
    ctx.closePath();
    ctx.fill();
    ctx.restore();
    // fixed aircraft symbol
    ctx.strokeStyle = fg;
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.moveTo(cx - R * 0.42, cy);
    ctx.lineTo(cx - R * 0.14, cy);
    ctx.lineTo(cx - R * 0.07, cy + R * 0.07);
    ctx.moveTo(cx + R * 0.42, cy);
    ctx.lineTo(cx + R * 0.14, cy);
    ctx.lineTo(cx + R * 0.07, cy + R * 0.07);
    ctx.stroke();
    ctx.beginPath();
    ctx.arc(cx, cy, 3, 0, Math.PI * 2);
    ctx.fillStyle = fg;
    ctx.fill();
    ctx.fillStyle = dim;
    ctx.fillText(`PITCH ${pitch.toFixed(0)}°   ROLL ${roll.toFixed(0)}°`, cx, Math.min(h - R * 0.06, cy + R + R * 0.12));
}
// ------------------------------------------------------------------ engine and fuel page
function renderEngine() {
    if (!tele || !ui)
        return;
    const s = tele.state, d = tele.derived;
    const kg = num(d.fuelKg), max = num(d.fuelMaxKg), burn = num(d.burnKgMin);
    const pct = kg !== null && max ? Math.max(0, Math.min(1, kg / max)) : null;
    $("e-fill").style.height = pct === null ? "0" : `calc(${pct * 100}% - 4px)`;
    const bingoPct = max ? Math.max(0, Math.min(1, ui.bingoKg / max)) : 0;
    $("e-bingo").style.bottom = `${bingoPct * 100}%`;
    $("e-pct").textContent = pct === null ? "--%" : `${Math.round(pct * 100)}%`;
    const low = kg !== null && kg <= ui.bingoKg;
    $("e-fill").style.background = low ? "var(--alert)" : "var(--dim)";
    $("e-fuel").textContent = fmt(kg === null ? null : U.mass(kg));
    $("e-fuel").className = "val" + (low ? " alertc" : "");
    $("e-fuel-u").textContent = U.massU();
    $("e-burn").textContent = fmt(burn === null ? null : U.mass(burn), 1);
    $("e-burn-u").textContent = U.massU() + "/MIN";
    $("e-end").textContent = mmss(d.enduranceS);
    $("e-bingo-v").textContent = fmt(U.mass(ui.bingoKg));
    $("e-bingo-u").textContent = U.massU();
    $("e-tobingo").textContent = low ? "BINGO" : mmss(d.toBingoS);
    $("e-tobingo").className = "val" + (low ? " alertc blink" : "");
    // one column per engine; rows only for values this aircraft actually reports
    const engines = [];
    for (let i = 1; i <= 8; i++)
        if (Object.keys(s).some(k => new RegExp(`^[a-zA-Z ]+ ${i}(,|$)`).test(k)))
            engines.push(i);
    const rows = [
        ["THR", i => `throttle ${i}, %`, "%"],
        ["RPM", i => `RPM ${i}`, ""],
        ["THRUST", i => `thrust ${i}, kgs`, ui.imperial ? "LBF" : "KGF"],
        ["POWER", i => `power ${i}, hp`, "HP"],
        ["OIL", i => `oil temp ${i}, C`, "°C"],
        ["EFF", i => `efficiency ${i}, %`, "%"],
    ];
    let html = `<tr><th></th>${engines.map(i => `<th>ENG ${i}</th>`).join("")}</tr>`;
    for (const [label, key, unit] of rows) {
        if (!engines.some(i => num(s[key(i)]) !== null))
            continue;
        html += `<tr><td>${label}${unit ? " " + unit : ""}</td>${engines.map(i => {
            let v = num(s[key(i)]);
            if (v !== null && label === "THRUST" && ui.imperial)
                v *= 2.20462;
            return `<td>${fmt(v)}</td>`;
        }).join("")}</tr>`;
    }
    $("e-eng").innerHTML = engines.length ? html : "";
}
// ------------------------------------------------------------------ map page
function mapExtents() {
    if (!mapInfo)
        return null;
    const p = (a) => [parseFloat(a?.[0]), parseFloat(a?.[1])];
    const [minX, minY] = p(mapInfo.map_min), [maxX, maxY] = p(mapInfo.map_max);
    const [gzX, gzY] = p(mapInfo.grid_zero), [gsX, gsY] = p(mapInfo.grid_steps);
    if (![minX, minY, maxX, maxY].every(isFinite))
        return null;
    return { minX, minY, maxX, maxY, W: maxX - minX, H: maxY - minY, gzX, gzY, gsX, gsY };
}
function player() {
    return mapObjs.find(o => o.icon === "Player");
}
function niceStep(m) {
    const steps = [500, 1000, 2000, 5000, 10000, 20000, 50000, 100000];
    return steps.find(s => s >= m) || 100000;
}
// the map picture, greyed and dimmed once per map instead of on every redraw
let mapBaseCanvas = null;
let mapBaseFor = null;
function mapBase() {
    if (!mapImg || !mapImg.complete || !mapImg.naturalWidth)
        return null;
    if (mapBaseFor !== mapImg) {
        const c = document.createElement("canvas");
        const size = Math.min(2048, mapImg.naturalWidth);
        c.width = c.height = size;
        const x = c.getContext("2d");
        x.filter = "grayscale(100%) contrast(110%)";
        x.globalAlpha = 0.5;
        x.drawImage(mapImg, 0, 0, size, size);
        mapBaseCanvas = c;
        mapBaseFor = mapImg;
    }
    return mapBaseCanvas;
}
function drawMap() {
    const c = $("map");
    const { ctx, w, h } = fitCanvas(c);
    const fg = cssVar("--fg"), dim = cssVar("--dim"), faint = cssVar("--faint");
    ctx.clearRect(0, 0, w, h);
    const ext = mapExtents();
    const hud = $("map-hud");
    ctx.font = `${Math.round(Math.min(w, h) * 0.022)}px Consolas, monospace`;
    if (!ext || !ui) {
        ctx.fillStyle = dim;
        ctx.textAlign = "center";
        ctx.fillText("NO MAP DATA", w / 2, h / 2);
        hud.textContent = "";
        return;
    }
    // view: normalised map coordinates (0..1, y down) -> canvas pixels
    const zoom = ZOOMS[Math.min(ui.mapZoom, ZOOMS.length - 1)];
    const me = player();
    let cxn = 0.5, cyn = 0.5;
    if (ui.mapFollow && me && me.x !== undefined && me.y !== undefined) {
        cxn = me.x;
        cyn = me.y;
    }
    // mouse panning (middle button) shifts the view from there; FOLLOW / FULL or a middle click puts it back
    if (pan.follow !== ui.mapFollow) {
        pan = { x: 0, y: 0, follow: ui.mapFollow };
    }
    cxn += pan.x;
    cyn += pan.y;
    const side = Math.min(w, h);
    const scale = side * zoom; // pixels per normalised unit
    const toPx = (x, y) => [w / 2 + (x - cxn) * scale, h / 2 + (y - cyn) * scale];
    mapView = { cxn, cyn, scale, w, h };
    const mPerNorm = ext.W;
    // the game's own map picture, dimmed so symbols stay readable
    const base = mapBase();
    if (base) {
        const [x0, y0] = toPx(0, 0);
        ctx.drawImage(base, x0, y0, scale, scale);
    }
    // grid: columns numbered, rows lettered, from the game's own grid definition (kept inside the map square)
    if (ui.mapGrid && isFinite(ext.gsX) && ext.gsX > 0 && isFinite(ext.gsY) && ext.gsY > 0) {
        const [mx0, my0] = toPx(0, 0), [mx1, my1] = toPx(1, 1);
        const top = Math.max(0, my0), bottom = Math.min(h, my1), left = Math.max(0, mx0), right = Math.min(w, mx1);
        ctx.save();
        ctx.beginPath();
        ctx.rect(left, top, right - left, bottom - top);
        ctx.clip();
        ctx.strokeStyle = faint;
        ctx.lineWidth = 1;
        ctx.fillStyle = dim;
        ctx.textAlign = "center";
        ctx.textBaseline = "top";
        for (let k = 0, gx = ext.gzX; gx <= ext.maxX + 1; k++, gx += ext.gsX) {
            const [px] = toPx((gx - ext.minX) / ext.W, 0);
            ctx.beginPath();
            ctx.moveTo(px, top);
            ctx.lineTo(px, bottom);
            ctx.stroke();
            const [pxNext] = toPx((gx + ext.gsX - ext.minX) / ext.W, 0);
            if (gx + ext.gsX <= ext.maxX + 1)
                ctx.fillText(String(k + 1), (px + pxNext) / 2, top + 4);
        }
        ctx.textAlign = "left";
        ctx.textBaseline = "middle";
        for (let k = 0, gy = ext.gzY; gy >= ext.minY - 1; k++, gy -= ext.gsY) {
            const [, py] = toPx(0, (ext.maxY - gy) / ext.H);
            ctx.beginPath();
            ctx.moveTo(left, py);
            ctx.lineTo(right, py);
            ctx.stroke();
            const [, pyNext] = toPx(0, (ext.maxY - (gy - ext.gsY)) / ext.H);
            if (gy - ext.gsY >= ext.minY - 1)
                ctx.fillText(String.fromCharCode(65 + (k % 26)), left + 5, (py + pyNext) / 2);
        }
        ctx.restore();
    }
    // objective squares (MSN on): dashed outline on every grid square our objectives name
    if (ui.mapMission && isFinite(ext.gsX) && ext.gsX > 0) {
        ctx.save();
        ctx.setLineDash([6, 4]);
        ctx.lineWidth = 2;
        ctx.textAlign = "right";
        ctx.textBaseline = "bottom";
        for (const o of ourObjectives()) {
            const done = o.status !== "in_progress";
            ctx.strokeStyle = done ? dim : fg;
            ctx.fillStyle = done ? dim : fg;
            for (const [row, col] of gridRefs(String(o.text || ""))) {
                const x0 = ext.gzX + col * ext.gsX, y0 = ext.gzY - row * ext.gsY;
                const [ax, ay] = toPx((x0 - ext.minX) / ext.W, (ext.maxY - y0) / ext.H);
                const [bx, by] = toPx((x0 + ext.gsX - ext.minX) / ext.W, (ext.maxY - (y0 - ext.gsY)) / ext.H);
                ctx.strokeRect(ax + 2, ay + 2, bx - ax - 4, by - ay - 4);
                ctx.fillText("OBJ", bx - 5, by - 4);
            }
        }
        ctx.restore();
    }
    // teammates' calls: the square they called from (quick-chat) or typed, blinking until it expires,
    // with who called and the altitude they gave
    pings = pings.filter(p => p.until > Date.now());
    if (pings.length && isFinite(ext.gsX) && ext.gsX > 0) {
        const on = Math.floor(performance.now() / 500) % 2 === 0;
        const cells = new Map();
        for (const p of pings) {
            const k = `${p.row},${p.col}`;
            cells.set(k, [...(cells.get(k) || []), p]);
        }
        const fs = Math.max(11, Math.round(Math.min(w, h) * 0.02));
        ctx.save();
        ctx.lineWidth = 3;
        ctx.strokeStyle = fg;
        ctx.fillStyle = fg;
        ctx.textAlign = "left";
        ctx.textBaseline = "top";
        for (const group of cells.values()) {
            const { row, col } = group[0];
            const x0 = ext.gzX + col * ext.gsX, y0 = ext.gzY - row * ext.gsY;
            const [ax, ay] = toPx((x0 - ext.minX) / ext.W, (ext.maxY - y0) / ext.H);
            const [bx, by] = toPx((x0 + ext.gsX - ext.minX) / ext.W, (ext.maxY - (y0 - ext.gsY)) / ext.H);
            ctx.globalAlpha = on ? 0.25 : 0.1;
            ctx.fillRect(ax, ay, bx - ax, by - ay);
            ctx.globalAlpha = 1;
            ctx.strokeRect(ax, ay, bx - ax, by - ay);
            ctx.font = `bold ${fs}px Consolas, monospace`;
            ctx.fillText(`${String.fromCharCode(65 + row)}${col + 1}`, ax + 4, ay + 4);
            ctx.font = `${fs}px Consolas, monospace`;
            group.slice(-4).forEach((p, i) => {
                const name = p.who.length > 11 ? p.who.slice(0, 10) + "…" : p.who;
                const line = p.alt ? `${name} ${p.alt.toUpperCase()}` : name;
                // dark backing so the text stays readable on any map picture
                const tw = ctx.measureText(line).width, ty = ay + 6 + (i + 1) * fs * 1.15;
                ctx.globalAlpha = 0.7;
                ctx.fillStyle = "#000";
                ctx.fillRect(ax + 2, ty - 1, tw + 6, fs * 1.1);
                ctx.globalAlpha = 1;
                ctx.fillStyle = fg;
                ctx.fillText(line, ax + 5, ty);
            });
        }
        ctx.restore();
    }
    // own track
    if (ui.mapTrail && trail.length > 1) {
        ctx.strokeStyle = dim;
        ctx.lineWidth = 1.5;
        ctx.setLineDash([3, 4]);
        ctx.beginPath();
        trail.forEach(([x, y], i) => { const [px, py] = toPx(x, y); i ? ctx.lineTo(px, py) : ctx.moveTo(px, py); });
        ctx.stroke();
        ctx.setLineDash([]);
    }
    // range rings around me
    if (me && me.x !== undefined && me.y !== undefined) {
        const ring = niceStep((side / scale) * mPerNorm / 4);
        const [mx, my] = toPx(me.x, me.y);
        ctx.strokeStyle = faint;
        ctx.lineWidth = 1;
        ctx.fillStyle = dim;
        ctx.textAlign = "left";
        ctx.textBaseline = "bottom";
        for (let r = 1; r <= 2; r++) {
            const rp = (ring * r / mPerNorm) * scale;
            ctx.beginPath();
            ctx.arc(mx, my, rp, 0, Math.PI * 2);
            ctx.stroke();
            ctx.fillText(`${+(U.dist(ring * r)).toFixed(1)}`, mx + rp * 0.72 + 3, my - rp * 0.72);
        }
    }
    // map objects exactly as the game reports them
    const blinkOff = Math.floor(performance.now() / 500) % 2 === 1;
    for (const o of mapObjs) {
        if (o.blink && blinkOff)
            continue;
        const col = o.color || fg;
        if (o.type === "airfield" && o.sx !== undefined && o.sy !== undefined && o.ex !== undefined && o.ey !== undefined) {
            const [ax, ay] = toPx(o.sx, o.sy), [bx, by] = toPx(o.ex, o.ey);
            ctx.strokeStyle = col;
            ctx.lineWidth = 5;
            ctx.lineCap = "round";
            ctx.beginPath();
            ctx.moveTo(ax, ay);
            ctx.lineTo(bx, by);
            ctx.stroke();
            ctx.lineCap = "butt";
            continue;
        }
        if (o.x === undefined || o.y === undefined)
            continue;
        const [px, py] = toPx(o.x, o.y);
        if (px < -20 || py < -20 || px > w + 20 || py > h + 20)
            continue;
        if (o.type === "aircraft") {
            const isMe = o.icon === "Player";
            const ang = Math.atan2(o.dy ?? -1, o.dx ?? 0);
            drawAircraft(ctx, px, py, ang, isMe ? 13 : 9, isMe ? fg : col, isMe);
        }
        else if (o.type === "ground_model") {
            ctx.fillStyle = col;
            ctx.fillRect(px - 4, py - 4, 8, 8);
        }
        else if (o.type === "bombing_point" || o.type === "defending_point") {
            ctx.strokeStyle = col;
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.arc(px, py, 9, 0, Math.PI * 2);
            ctx.moveTo(px - 13, py);
            ctx.lineTo(px + 13, py);
            ctx.moveTo(px, py - 13);
            ctx.lineTo(px, py + 13);
            ctx.stroke();
        }
        else if (o.type === "capture_zone") {
            // capture zones carry their letter (A, B, ... and I for air-supremacy zones); the colour fades as it is captured
            ctx.strokeStyle = col;
            ctx.fillStyle = col;
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.arc(px, py, 12, 0, Math.PI * 2);
            ctx.stroke();
            ctx.save();
            ctx.font = `bold 14px Consolas, monospace`;
            ctx.textAlign = "center";
            ctx.textBaseline = "middle";
            ctx.fillText(o.zone_label || "", px, py + 1);
            ctx.restore();
        }
        else if (o.type && o.type.startsWith("respawn_base")) {
            ctx.strokeStyle = col;
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.moveTo(px, py - 7);
            ctx.lineTo(px + 7, py);
            ctx.lineTo(px, py + 7);
            ctx.lineTo(px - 7, py);
            ctx.closePath();
            ctx.stroke();
        }
        else {
            ctx.strokeStyle = col;
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.arc(px, py, 7, 0, Math.PI * 2);
            ctx.stroke();
        }
    }
    drawWaypoints(ctx, toPx, ext, me, fg, dim);
    const visibleKm = U.dist((side / scale) * mPerNorm);
    hud.textContent = `${ui.mapFollow && me ? "FOLLOW" : "FULL"}${pan.x || pan.y ? " + PAN" : ""} · ×${zoom} · VIEW ${visibleKm.toFixed(0)} ${U.distU()} · RINGS ${U.distU()}${me ? "" : " · OWN POSITION NOT ON MAP"}${mapObjs.length ? "" : " · GAME SENDS NO MAP OBJECTS"}`;
}
// ------------------------------------------------------------------ waypoints
// Click the map: a waypoint there (snapped onto a map object if you clicked one). Click a waypoint: remove it.
// Right-click: remove all. The MFD shows distance, bearing and time to the active waypoint and the whole route.
// Waypoints stay until you remove them. They live in the app, so the Stream Deck and every open MFD screen share them.
let mapView = null;
const metres = (ext, ax, ay, bx, by) => Math.hypot((bx - ax) * ext.W, (by - ay) * ext.H);
// 0 = north (up on the map), clockwise
const bearing = (ext, ax, ay, bx, by) => ((Math.atan2((bx - ax) * ext.W, -(by - ay) * ext.H) * 180 / Math.PI) + 360) % 360;
function squareOf(ext, x, y) {
    if (!(ext.gsX > 0) || !(ext.gsY > 0))
        return "";
    const col = Math.floor((ext.minX + x * ext.W - ext.gzX) / ext.gsX) + 1;
    const row = Math.floor((ext.gzY - (ext.maxY - y * ext.H)) / ext.gsY);
    return row >= 0 && row < 26 && col >= 1 ? String.fromCharCode(65 + row) + col : "";
}
function clock(s) {
    s = Math.round(s);
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), ss = s % 60;
    return h ? `${h}:${String(m).padStart(2, "0")}:${String(ss).padStart(2, "0")}` : `${m}:${String(ss).padStart(2, "0")}`;
}
// The selected target (NEXT TGT) follows its object if it moves (ships, convoys): the closest map object of the same
// kind near where it was selected.
function targetPos() {
    const t = ui?.target;
    if (!t)
        return null;
    let best = 0.03, pos = { x: t.x, y: t.y, icon: t.icon };
    for (const o of mapObjs) {
        let x = o.x, y = o.y;
        if (o.type === "airfield" && x === undefined && o.sx !== undefined && o.ex !== undefined && o.sy !== undefined && o.ey !== undefined) {
            x = (o.sx + o.ex) / 2;
            y = (o.sy + o.ey) / 2;
        }
        if (x === undefined || y === undefined || ((o.icon || "").toLowerCase() !== t.icon.toLowerCase() && !(t.icon === "airfield" && o.type === "airfield")))
            continue;
        const d = Math.hypot(x - t.x, y - t.y);
        if (d < best) {
            best = d;
            pos = { x, y, icon: t.icon };
        }
    }
    return pos;
}
const fmtDist = (m) => `${U.dist(m).toFixed(U.dist(m) < 100 ? 1 : 0)} ${U.distU()}`;
function etaTo(m) {
    const tas = num(tele?.state?.["TAS, km/h"]);
    return tas && tas > 50 ? clock(m / (tas / 3.6)) : "--:--";
}
// "23.1 KM · BRG 145° · ETA 1:34" from me to a point
function legText(ext, me, x, y) {
    const m = metres(ext, me.x, me.y, x, y);
    const b = Math.round(bearing(ext, me.x, me.y, x, y)) % 360;
    return `${fmtDist(m)} · BRG ${String(b).padStart(3, "0")}° · ETA ${etaTo(m)}`;
}
function drawWaypoints(ctx, toPx, ext, me, fg, dim) {
    const wps = ui?.waypoints || [];
    const tgt = targetPos();
    const box = $("map-wp");
    if (!wps.length && !tgt) {
        box.classList.remove("on");
        return;
    }
    const hasMe = !!me && me.x !== undefined && me.y !== undefined;
    const lines = [];
    ctx.save();
    if (wps.length) {
        // route: me -> 1 -> 2 -> ... (the active leg bright, the rest dim)
        ctx.lineWidth = 2;
        let px = hasMe ? me.x : wps[0].x, py = hasMe ? me.y : wps[0].y;
        wps.forEach((p, i) => {
            const [ax, ay] = toPx(px, py), [bx, by] = toPx(p.x, p.y);
            ctx.strokeStyle = i === 0 ? fg : dim;
            ctx.setLineDash(i === 0 ? [10, 5] : [4, 6]);
            ctx.beginPath();
            ctx.moveTo(ax, ay);
            ctx.lineTo(bx, by);
            ctx.stroke();
            px = p.x;
            py = p.y;
        });
        ctx.setLineDash([]);
        // markers: numbered circles
        ctx.font = "bold 12px Consolas, monospace";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        wps.forEach((p, i) => {
            const [x, y] = toPx(p.x, p.y);
            ctx.fillStyle = "rgba(0,0,0,.75)";
            ctx.beginPath();
            ctx.arc(x, y, 10, 0, Math.PI * 2);
            ctx.fill();
            ctx.strokeStyle = i === 0 ? fg : dim;
            ctx.lineWidth = 2;
            ctx.stroke();
            ctx.fillStyle = i === 0 ? fg : dim;
            ctx.fillText(String(i + 1), x, y + 1);
        });
    }
    if (tgt) {
        // the target: a bracket box around it and a line from me, in the warning colour so it stands apart from the route
        const warn = cssVar("--warn");
        const [x, y] = toPx(tgt.x, tgt.y);
        if (hasMe) {
            const [ax, ay] = toPx(me.x, me.y);
            ctx.strokeStyle = warn;
            ctx.lineWidth = 1.5;
            ctx.setLineDash([3, 4]);
            ctx.beginPath();
            ctx.moveTo(ax, ay);
            ctx.lineTo(x, y);
            ctx.stroke();
            ctx.setLineDash([]);
        }
        ctx.strokeStyle = warn;
        ctx.lineWidth = 2;
        const r = 13, k = 6;
        ctx.beginPath();
        for (const [sx, sy] of [[-1, -1], [1, -1], [1, 1], [-1, 1]]) {
            ctx.moveTo(x + sx * r, y + sy * (r - k));
            ctx.lineTo(x + sx * r, y + sy * r);
            ctx.lineTo(x + sx * (r - k), y + sy * r);
        }
        ctx.stroke();
        ctx.font = "bold 11px Consolas, monospace";
        ctx.textAlign = "left";
        ctx.textBaseline = "bottom";
        ctx.fillStyle = warn;
        ctx.fillText("TGT", x + r + 3, y - r + 10);
        const sq = squareOf(ext, tgt.x, tgt.y);
        lines.push(`<b class="tgt">TGT${sq ? " · " + sq : ""}</b>`, hasMe ? legText(ext, me, tgt.x, tgt.y) : "OWN POSITION NOT ON MAP");
    }
    ctx.restore();
    if (wps.length) {
        const w1 = wps[0], sq = squareOf(ext, w1.x, w1.y);
        lines.push(`<b>WP1${sq ? " · " + sq : ""}</b>`);
        if (hasMe) {
            lines.push(legText(ext, me, w1.x, w1.y));
            if (wps.length > 1) {
                let total = metres(ext, me.x, me.y, w1.x, w1.y);
                for (let i = 1; i < wps.length; i++)
                    total += metres(ext, wps[i - 1].x, wps[i - 1].y, wps[i].x, wps[i].y);
                lines.push(`ROUTE ${wps.length} WP · ${fmtDist(total)} · ETA ${etaTo(total)}`);
            }
        }
        else
            lines.push("OWN POSITION NOT ON MAP");
    }
    box.innerHTML = lines.map(l => `<div>${l}</div>`).join("");
    box.classList.add("on");
}
// The point the FLT page works for: the selected target, else the active waypoint.
function aimPoint() {
    const ext = mapExtents();
    if (!ext)
        return null;
    const t = targetPos();
    if (t)
        return { label: `TGT ${squareOf(ext, t.x, t.y)}`, x: t.x, y: t.y };
    const w = ui?.waypoints?.[0];
    return w ? { label: `WP1 ${squareOf(ext, w.x, w.y)}`, x: w.x, y: w.y } : null;
}
// one line for the FLT page: distance, bearing and ETA to the target / active waypoint
function navLine() {
    const ext = mapExtents(), me = player(), p = aimPoint();
    if (!ext || !p || !me || me.x === undefined || me.y === undefined)
        return "";
    return `${p.label} · ${legText(ext, me, p.x, p.y)}`;
}
// Hold-over estimate for unguided rockets/guns (the game has no CCIP for them on some aircraft).
// Simple ballistics: time of flight t = range / average speed, gravity drop g·t²/2, so aim
// atan(drop / range) above the line of sight. The line of sight itself points down at the target by
// atan(height / range), height from the radar altimeter (assumes the target sits on the ground below that).
// Result: the pitch to hold. Average speed and an airframe offset come from settings.json, tuned by test flying.
function holdSolution() {
    const w = ui?.weapon, ext = mapExtents(), me = player(), p = aimPoint();
    if (!w || !ext || !p || !me || me.x === undefined || me.y === undefined || !(w.avgSpeed > 0))
        return null;
    const range = metres(ext, me.x, me.y, p.x, p.y);
    if (range < 50)
        return null;
    const tof = range / w.avgSpeed;
    const drop = 0.5 * 9.81 * tof * tof;
    const hold = Math.atan(drop / range) * 180 / Math.PI + (w.pitchOffset || 0);
    const ind = tele?.ind || {}, st = tele?.state || {};
    const height = num(ind.radio_altitude) ?? num(st["H, m"]) ?? 0;
    const los = -Math.atan(Math.max(0, height) / range) * 180 / Math.PI;
    let now = num(ind.aviahorizon_pitch);
    if (now !== null && ui?.invertAdiPitch)
        now = -now;
    return { name: w.name, hold, pitch: los + hold, now, tof, range, out: range > (w.maxRange || 6000) };
}
function holdLine() {
    const h = holdSolution();
    if (!h)
        return "";
    if (h.out)
        return `${h.name} · OUT OF RANGE (${fmtDist(ui?.weapon?.maxRange || 6000)})`;
    const sgn = (v) => (v >= 0 ? "+" : "") + v.toFixed(1) + "°";
    // ▲ raise the nose, ▼ lower it, ● on the hold (within half a degree)
    const delta = h.now === null ? "" : Math.abs(h.now - h.pitch) <= 0.5 ? " ●" : h.now < h.pitch ? " ▲" : " ▼";
    return `${h.name} · HOLD ${sgn(h.hold)} · PITCH ${sgn(h.pitch)}${delta} · ${h.tof.toFixed(1)} S`;
}
function mapClick(ev, clear) {
    if (clear) {
        ev.preventDefault();
        if (ui?.waypoints?.length)
            cmd("wp/clear");
        return;
    }
    const v = mapView, ext = mapExtents();
    if (!v || !ext)
        return;
    const r = $("map").getBoundingClientRect();
    const sx = ev.clientX - r.left, sy = ev.clientY - r.top;
    const toPx = (x, y) => [v.w / 2 + (x - v.cxn) * v.scale, v.h / 2 + (y - v.cyn) * v.scale];
    // on a waypoint: remove it
    const wps = ui?.waypoints || [];
    // Ctrl+click: put the target here (on a map object: that object, and it follows it; elsewhere: a fixed point)
    if (ev.ctrlKey) {
        let tx = v.cxn + (sx - v.w / 2) / v.scale, ty = v.cyn + (sy - v.h / 2) / v.scale, icon = "", near = 12;
        for (const o of mapObjs) {
            if (o.x === undefined || o.y === undefined || o.icon === "Player" || o.type === "capture_zone")
                continue;
            const [ox, oy] = toPx(o.x, o.y), dd = Math.hypot(ox - sx, oy - sy);
            if (dd < near) {
                near = dd;
                tx = o.x;
                ty = o.y;
                icon = o.icon || o.type || "";
            }
        }
        if (tx < 0 || tx > 1 || ty < 0 || ty > 1)
            return;
        cmd(`tgt/set/${tx.toFixed(5)}/${ty.toFixed(5)}` + (icon ? `/${encodeURIComponent(icon)}` : ""));
        return;
    }
    for (let i = wps.length - 1; i >= 0; i--) {
        const [x, y] = toPx(wps[i].x, wps[i].y);
        if (Math.hypot(x - sx, y - sy) <= 14) {
            cmd(`wp/del/${i + 1}`);
            return;
        }
    }
    // on a map object (target, zone, ship...): use its exact position
    let x = v.cxn + (sx - v.w / 2) / v.scale, y = v.cyn + (sy - v.h / 2) / v.scale, best = 12;
    for (const o of mapObjs) {
        if (o.x === undefined || o.y === undefined || o.icon === "Player")
            continue;
        const [ox, oy] = toPx(o.x, o.y), dd = Math.hypot(ox - sx, oy - sy);
        if (dd < best) {
            best = dd;
            x = o.x;
            y = o.y;
        }
    }
    if (x < 0 || x > 1 || y < 0 || y > 1)
        return; // off the map
    if (wps.length >= 9)
        return;
    cmd(`wp/add/${x.toFixed(5)}/${y.toFixed(5)}`);
}
function drawAircraft(ctx, x, y, ang, s, col, me) {
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(ang + Math.PI / 2);
    ctx.fillStyle = col;
    ctx.strokeStyle = "#000";
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    if (me) {
        ctx.moveTo(0, -s);
        ctx.lineTo(s * 0.75, s * 0.8);
        ctx.lineTo(0, s * 0.35);
        ctx.lineTo(-s * 0.75, s * 0.8);
    }
    else {
        ctx.moveTo(0, -s);
        ctx.lineTo(s * 0.6, s * 0.7);
        ctx.lineTo(-s * 0.6, s * 0.7);
    }
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
    ctx.restore();
}
function updateTrail() {
    const me = player();
    const ac = tele?.derived?.aircraft || "";
    if (ac !== trailAircraft) {
        trail = [];
        trailAircraft = ac;
    }
    if (!me || me.x === undefined || me.y === undefined)
        return;
    const now = performance.now();
    if (now - lastTrailAt < 1000)
        return;
    lastTrailAt = now;
    const last = trail[trail.length - 1];
    if (last && Math.hypot(last[0] - me.x, last[1] - me.y) > 0.08)
        trail = []; // respawn: jumped across the map
    trail.push([me.x, me.y]);
    if (trail.length > 900)
        trail.shift();
}
// ------------------------------------------------------------------ systems page
function lamp(name, value, onText, offText, invert = false) {
    if (value === null)
        return `<div class="lamp off"><div class="lbl">${name}</div><div class="lv">N/A</div></div>`;
    const v = invert ? 100 - value : value;
    const cls = v >= 99 ? "lit" : v <= 1 ? "off" : "mid";
    const text = v >= 99 ? onText : v <= 1 ? offText : `${Math.round(value)}%`;
    return `<div class="lamp ${cls}"><div class="lbl">${name}</div><div class="lv">${text}</div></div>`;
}
// The game's list holds both teams' objectives. Only in_progress / completed / failed are ours
// ("undefined" = the other team's, "delayed" = not active yet), which matches what the in-game panel shows.
// Duplicates removed, open ones first.
function ourObjectives() {
    const seen = new Set();
    const OURS = { in_progress: 0, failed: 1, completed: 2 };
    const rank = (o) => OURS[String(o.status)] ?? 3;
    return (mission?.objectives || [])
        .filter(o => String(o.status) in OURS)
        .filter(o => { const k = String(o.text || ""); if (seen.has(k))
        return false; seen.add(k); return true; })
        .sort((a, b) => rank(a) - rank(b));
}
// Grid squares named in a text ("Protect the convoy at C4"), as [row, col] inside the current map's grid.
function gridRefs(text) {
    const g = gridSize();
    if (!g)
        return [];
    const out = [];
    for (const m of text.matchAll(GRID_REF)) {
        const row = m[1].charCodeAt(0) - 65, col = +m[2] - 1;
        if (row >= 0 && row < g.rows && col >= 0 && col < g.cols)
            out.push([row, col]);
    }
    return out;
}
// Mission list over the map (MSN button), same objectives as the SYS page
function renderMapMission() {
    const el = $("map-msn");
    const obj = ui?.mapMission ? ourObjectives() : [];
    el.classList.toggle("on", obj.length > 0);
    el.innerHTML = obj.slice(0, 8).map(o => `<div class="${o.status === "completed" ? "done" : o.status === "failed" ? "failed" : ""}">${o.primary ? "■" : "□"} ${esc(o.text || "")}</div>`).join("")
        + (obj.length > 8 ? `<div class="more">+${obj.length - 8} MORE</div>` : "");
}
function renderSystems() {
    if (!ui)
        return;
    const s = tele?.state || {}, ind = tele?.ind || {};
    const flying = linkStatus === "flying";
    const gear = flying ? (pick(s, "gear, %") ?? (num(ind.gears) !== null ? num(ind.gears) * 100 : null)) : null;
    const flaps = flying ? (pick(s, "flaps, %") ?? (num(ind.flaps) !== null ? num(ind.flaps) * 100 : null)) : null;
    const brake = flying ? (pick(s, "airbrake, %") ?? (num(ind.airbrake_indicator) !== null ? num(ind.airbrake_indicator) * 100 : null)) : null;
    const sweep = flying ? pick(s, "wing sweep, %") ?? null : null;
    $("s-lamps").innerHTML =
        lamp("GEAR", gear, "DOWN", "UP") + lamp("FLAPS", flaps, "FULL", "UP") + lamp("AIRBRAKE", brake, "OUT", "IN") +
            (sweep !== null ? lamp("SWEEP", sweep, "AFT", "FWD") : `<div class="lamp off"><div class="lbl">LINK</div><div class="lv">${linkStatus === "flying" ? "OK" : linkStatus.toUpperCase()}</div></div>`);
    const obj = ourObjectives();
    const MAX = 10;
    const shown = obj.length > MAX ? obj.slice(0, MAX - 1) : obj;
    $("s-mission").innerHTML = obj.length
        ? shown.map(o => `<div class="${o.status === "completed" ? "done" : o.status === "failed" ? "failed" : ""}">${o.primary ? "■" : "□"} ${esc(o.text || "")}</div>`).join("")
            + (obj.length > MAX ? `<div class="more">+${obj.length - shown.length} MORE</div>` : "")
        // the game sends "objectives": null for the first minute or so of a match, before it announces them
        : `<div class="unit">${mission?.status === "running" && !mission?.objectives ? "WAITING FOR THE GAME TO SEND OBJECTIVES" : "NO OBJECTIVES"}</div>`;
    $("s-log-l").textContent = `EVENTS · ${ui.eventFilter.toUpperCase()}`;
    // War Thunder sometimes sends untranslated text keys ("...td! kd?NET_PLAYER_DISCONNECT_FROM_GAME"); tidy them up
    const clean = (m) => stripTags(m)
        .replace(/td! kd\?NET_PLAYER_DISCONNECT_FROM_GAME/g, " has disconnected from the game.")
        .replace(/td! kd\?([A-Z0-9_]+)/g, (_, k) => " " + k.toLowerCase().replace(/_/g, " "))
        .replace(/\s+/g, " ").trim();
    const isSys = (m, e) => /left the game|disconnected from the game/i.test(m) || (!!e && e.kind === "chat" && !e.sender);
    const f = ui.eventFilter;
    // the game often reports a disconnect twice (readable text plus an untranslated key), and sometimes only the key:
    // turn the key into the readable text, then keep one line per player
    const shownLeft = new Set();
    const list = events
        .map(e => ({ ...e, msg: clean(e.msg) }))
        .filter(e => { if (!/has disconnected from the game/.test(e.msg))
        return true; if (shownLeft.has(e.msg))
        return false; shownLeft.add(e.msg); return true; })
        .filter(e => f === "all" || (f === "chat" && e.kind === "chat") || (f === "damage" && e.kind === "damage" && !isSys(e.msg)))
        .slice(-60);
    $("s-log").innerHTML = list.map(e => {
        const t = new Date(e.t).toTimeString().slice(0, 5);
        const cls = [e.kind === "chat" ? "chat" : "", e.enemy ? "enemy" : "", e.me ? "me" : "", isSys(e.msg, e) ? "sys" : ""].join(" ");
        const body = e.kind === "chat" && !e.sender ? esc(e.msg) : e.kind === "chat" ? `<b>${esc(e.sender)}</b>${e.mode ? ` [${esc(e.mode)}]` : ""}: ${esc(chatText(e.msg))}` : esc(e.msg);
        return `<div class="${cls}"><span class="t">${t}</span>${body}</div>`;
    }).join("") || `<div class="unit">NO EVENTS YET</div>`;
}
const esc = (s) => s.replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
// ------------------------------------------------------------------ render loop
function renderAll() {
    renderHeader();
    if (!ui)
        return;
    if (ui.page === "flight")
        renderFlight();
    else if (ui.page === "engine")
        renderEngine();
    else if (ui.page === "systems")
        renderSystems();
}
let mapDirty = true;
let lastMapDraw = 0;
let lastCycleDraw = 0;
function frame(now) {
    if (callout && callout.until <= Date.now()) {
        callout = null;
        renderCallout();
    }
    if (now - lastCycleDraw > 200) {
        renderCycle();
        lastCycleDraw = now;
    }
    if (ui?.page === "map" && (mapDirty || now - lastMapDraw > 500)) {
        drawMap();
        mapDirty = false;
        lastMapDraw = now;
    }
    requestAnimationFrame(frame);
}
// ------------------------------------------------------------------ live link to WT-MFD.exe
function connect() {
    const es = new EventSource("/api/stream");
    es.onopen = () => { appLink = true; renderHeader(); };
    es.onerror = () => { appLink = false; renderHeader(); };
    es.onmessage = ev => {
        const m = JSON.parse(ev.data);
        switch (m.type) {
            case "ui":
                ui = m.data;
                applyUi();
                break;
            case "status":
                linkStatus = m.data.status;
                if (m.data.cycle) {
                    cycle = m.data.cycle;
                    cycleAt = performance.now();
                    renderCycle();
                }
                renderHeader();
                if (ui?.page === "systems")
                    renderSystems();
                break;
            case "tele":
                tele = m.data;
                linkStatus = "flying";
                renderAll();
                break;
            case "mapinfo":
                mapInfo = m.data.info;
                if (m.data.version !== mapVersion) {
                    mapVersion = m.data.version;
                    trail = [];
                    pan.x = pan.y = 0;
                    if (m.data.hasImage) {
                        const img = new Image();
                        img.onload = () => { mapDirty = true; };
                        img.src = `/api/map.img?v=${mapVersion}`;
                        mapImg = img;
                    }
                    else
                        mapImg = null;
                }
                break;
            case "map":
                mapObjs = m.data || [];
                updateTrail();
                mapDirty = true;
                break;
            case "mission":
                mission = m.data;
                if (ui?.page === "systems")
                    renderSystems();
                renderMapMission();
                mapDirty = true;
                break;
            case "events":
                events = m.data.reset ? m.data.items : events.concat(m.data.items);
                takeCalls(m.data.items);
                if (events.length > 400)
                    events = events.slice(-400);
                if (ui?.page === "systems")
                    renderSystems();
                break;
        }
    };
}
// keyboard shortcuts when the MFD window itself has focus (handy for testing without a Stream Deck)
document.addEventListener("keydown", e => {
    const map = { "1": "page/flight", "2": "page/engine", "3": "page/map", "4": "page/systems",
        q: "osb/b1", w: "osb/b2", e: "osb/b3", r: "osb/b4", t: "osb/b5", a: "osb/g1", s: "osb/g2", d: "osb/g3", f: "osb/g4", g: "osb/g5" };
    const c = map[e.key.toLowerCase()];
    if (c)
        cmd(c);
});
window.addEventListener("resize", () => { renderAll(); mapDirty = true; });
// ------------------------------------------------------------------ mouse: scroll = zoom (around the cursor), middle-drag = pan
// Zoom uses the same steps as the ZOOM buttons (shared with the Stream Deck and other screens); the pan is this screen's own.
let pan = { x: 0, y: 0, follow: true };
$("map").addEventListener("wheel", e => {
    e.preventDefault();
    const ev = e, v = mapView;
    if (!ui || !v || ev.deltaY === 0)
        return;
    const step = ev.deltaY < 0 ? 1 : -1;
    const z0 = Math.min(ui.mapZoom, ZOOMS.length - 1), z1 = Math.max(0, Math.min(ZOOMS.length - 1, z0 + step));
    if (z1 === z0)
        return;
    // keep the map point under the cursor where it is
    const r = $("map").getBoundingClientRect();
    const sx = ev.clientX - r.left - v.w / 2, sy = ev.clientY - r.top - v.h / 2;
    const s0 = v.scale, s1 = v.scale * ZOOMS[z1] / ZOOMS[z0];
    pan.x += sx / s0 - sx / s1;
    pan.y += sy / s0 - sy / s1;
    ui.mapZoom = z1;
    mapDirty = true; // draw at once; the app confirms a moment later
    cmd(step > 0 ? "map/zoomin" : "map/zoomout");
}, { passive: false });
let panDrag = null;
$("map").addEventListener("mousedown", e => {
    const ev = e;
    if (ev.button !== 1)
        return;
    ev.preventDefault(); // no browser auto-scroll
    panDrag = { x: ev.clientX, y: ev.clientY, moved: false };
});
window.addEventListener("mousemove", e => {
    if (!panDrag || !mapView)
        return;
    const dx = e.clientX - panDrag.x, dy = e.clientY - panDrag.y;
    if (!panDrag.moved && Math.hypot(dx, dy) < 3)
        return;
    panDrag.moved = true;
    pan.x -= dx / mapView.scale;
    pan.y -= dy / mapView.scale;
    panDrag.x = e.clientX;
    panDrag.y = e.clientY;
    mapDirty = true;
});
window.addEventListener("mouseup", e => {
    if (!panDrag || e.button !== 1)
        return;
    if (!panDrag.moved) {
        pan.x = pan.y = 0;
        mapDirty = true;
    } // a middle click without dragging: back to centre
    panDrag = null;
});
$("map").addEventListener("auxclick", e => { if (e.button === 1)
    e.preventDefault(); });
$("map").addEventListener("click", e => mapClick(e, false));
$("h-cyc").addEventListener("click", () => cmd("cycle/reset")); // same as CYC RST
$("map").addEventListener("contextmenu", e => mapClick(e, true));
// ------------------------------------------------------------------ running as the WT-MFD app (own window)
// WT-MFD.exe shows this page in its own borderless window (WebView2). There is no title bar, so the page draws
// window buttons in a slim strip on top and tells the window what to do.
const host = window.chrome?.webview;
if (host) {
    document.body.classList.add("app");
    const title = document.createElement("div");
    title.id = "wintitle";
    title.textContent = "WT-MFD";
    const bar = document.createElement("div");
    bar.id = "winctl";
    bar.innerHTML = `<button data-w="minimize" title="Minimize">&#x2013;</button>` +
        `<button data-w="maximize" title="Fill the screen (F11)">&#x25A1;</button>` +
        `<button data-w="close" class="x" title="Close WT-MFD">&#x2715;</button>`;
    document.body.append(title, bar);
    bar.addEventListener("click", e => { const b = e.target.closest("button"); if (b)
        host.postMessage(b.dataset.w); });
    document.addEventListener("keydown", e => { if (e.key === "F11") {
        e.preventDefault();
        host.postMessage("maximize");
    } });
    // Normally the window engine moves the window itself (CSS app-region). If it can't, these mouse events arrive
    // here instead, and the window is asked to start the drag.
    document.addEventListener("mousedown", e => {
        if (e.button !== 0 || e.target.closest(".osb, #winctl, #map, #h-cyc"))
            return;
        host.postMessage(e.detail === 2 ? "maximize" : "drag");
    });
}
connect();
requestAnimationFrame(frame);
