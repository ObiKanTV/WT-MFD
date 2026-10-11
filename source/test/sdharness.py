"""Pretend to be the Stream Deck app: load the plugin, place 15 keys, press some, and save every key face."""
import asyncio, base64, json, io, os, sys, urllib.request
import websockets
from playwright.async_api import async_playwright
from PIL import Image

PORT = 28888
APP = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18120"
HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN = os.path.join(HERE, "..", "..", "streamdeck", "com.kai.wtmfd.sdPlugin", "plugin.html")
SHOTS = os.path.join(HERE, "shots")
faces = {}
sent = []

async def handler(ws):
    reg = json.loads(await ws.recv())
    sent.append(reg)
    for r in range(3):
        for c in range(5):
            await ws.send(json.dumps({"event": "willAppear", "action": "com.kai.wtmfd.button", "context": f"k{r}{c}",
                                      "payload": {"settings": {"base": APP}, "coordinates": {"column": c, "row": r}}}))
    async def reader():
        async for msg in ws:
            m = json.loads(msg); sent.append(m)
            if m.get("event") == "setImage":
                faces[m["context"]] = m["payload"]["image"]
    t = asyncio.create_task(reader())
    await asyncio.sleep(2.5)
    snap("1_flight")
    await ws.send(json.dumps({"event": "keyDown", "context": "k03", "payload": {}}))   # MAP page (top key 1 is the folder back key)
    await asyncio.sleep(1.5); snap("2_map")
    await ws.send(json.dumps({"event": "keyDown", "context": "k12", "payload": {}}))   # follow/full
    await ws.send(json.dumps({"event": "keyDown", "context": "k23", "payload": {}}))   # colour -> amber
    await asyncio.sleep(1.5); snap("3_map_amber")
    t.cancel()

def snap(name):
    grid = Image.new("RGB", (5 * 150, 3 * 150), (0, 0, 0))
    for r in range(3):
        for c in range(5):
            d = faces.get(f"k{r}{c}")
            if d:
                im = Image.open(io.BytesIO(base64.b64decode(d.split(",", 1)[1])))
                grid.paste(im, (c * 150 + 3, r * 150 + 3))
    os.makedirs(SHOTS, exist_ok=True)
    grid.save(os.path.join(SHOTS, f"sd_{name}.png"))

async def main():
    async with websockets.serve(handler, "127.0.0.1", PORT):
        async with async_playwright() as p:
            b = await p.chromium.launch(args=["--allow-file-access-from-files"])
            pg = await b.new_page()
            errs = []
            pg.on("pageerror", lambda e: errs.append(str(e)))
            pg.on("console", lambda m: errs.append("console: " + m.text) if m.type == "error" else None)
            await pg.goto("file:///" + os.path.abspath(PLUGIN).replace(os.sep, "/").lstrip("/"))
            await pg.evaluate(f"connectElgatoStreamDeckSocket({PORT}, 'plugin-uuid', 'registerPlugin', '{{}}')")
            await asyncio.sleep(7.5)
            print("errors:", errs)
            print("events from plugin:", sorted({m.get('event') for m in sent}))
            print("register:", sent[0])
            print("settings written:", [m['payload']['cmd'] for m in sent if m.get('event') == 'setSettings'][:15])
            await b.close()
    # reset colour for later tests
    urllib.request.urlopen(APP + "/api/cmd/display/color").read(); urllib.request.urlopen(APP + "/api/cmd/display/color").read()

asyncio.run(main())
