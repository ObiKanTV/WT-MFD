"""Fake War Thunder localhost:8111 for testing WT-MFD without the game."""
import io, json, math, sys, time, random
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8111
T0 = time.time()
MODE = {'v': 'flying'}  # flying | hangar
# /mode?ias=0&rpm=0&fuel=4100 pins those values (spawned on the runway, engine off); /mode?ias=&rpm=&fuel= frees them again.
# /feed?msg=... adds a kill-feed line, e.g. "Hawk_77 (MiG-21SMT) shot down ObiKan (AJS37)" (reward cycle testing)
PIN = {}
EXTRA = []

def map_png():
    from PIL import Image, ImageDraw
    im = Image.new('RGB', (1024, 1024), (70, 92, 70))
    d = ImageDraw.Draw(im)
    d.ellipse((600, -200, 1400, 500), fill=(40, 70, 110))       # sea
    d.polygon([(100, 700), (400, 650), (500, 900), (150, 1000)], fill=(110, 105, 80))
    for i in range(30):
        x, y = random.Random(i).randint(0, 1000), random.Random(i * 7).randint(0, 1000)
        d.ellipse((x, y, x + 60, y + 40), fill=(60, 80, 60))
    b = io.BytesIO(); im.save(b, 'PNG'); return b.getvalue()

PNG = map_png()

def me():
    t = time.time() - T0
    a = t * 0.05
    return 0.5 + 0.18 * math.cos(a), 0.5 + 0.18 * math.sin(a), -math.sin(a), math.cos(a), t

def state():
    x, y, dx, dy, t = me()
    fuel = max(0.0, 2400 - t * 6)
    st = {"valid": True, "aileron, %": 3, "elevator, %": -2, "rudder, %": 0, "flaps, %": 0, "gear, %": 0, "airbrake, %": 0,
            "H, m": 4200 + 300 * math.sin(t / 7), "TAS, km/h": 980 + 30 * math.sin(t / 5), "IAS, km/h": 760 + 25 * math.sin(t / 5),
            "M": 0.86, "AoA, deg": 4.2 + math.sin(t), "AoS, deg": 0.3, "Ny": 1.0 + 2.5 * max(0, math.sin(t / 3)),
            "Vy, m/s": 12 * math.cos(t / 7), "Wx, deg/s": 2, "Mfuel, kg": fuel, "Mfuel0, kg": 3600,
            "throttle 1, %": 100, "RPM 1": 8200, "thrust 1, kgs": 6300, "efficiency 1, %": 0, "oil temp 1, C": 92}
    for k, key in (('ias', 'IAS, km/h'), ('rpm', 'RPM 1'), ('fuel', 'Mfuel, kg')):
        if k in PIN: st[key] = PIN[k]
    return st

AC = ['saab_ajs37']  # /mode?ac=mi_28a_sweden switches the aircraft

def indicators():
    x, y, dx, dy, t = me()
    return {"valid": True, "type": AC[0], "radio_altitude": 150.0, "speed": 211, "compass": (math.degrees(math.atan2(dx, -dy)) % 360),
            "aviahorizon_roll": 25 * math.sin(t / 3), "aviahorizon_pitch": 6 * math.sin(t / 4), "gears": 0, "flaps": 0,
            "airbrake_indicator": 0, "throttle": 1, "mach": 0.86, "g_meter": 1.2}

def map_obj():
    x, y, dx, dy, t = me()
    objs = [
        {"type": "aircraft", "color": "#faC81E", "blink": 0, "icon": "Player", "icon_bg": "none", "x": x, "y": y, "dx": dx, "dy": dy},
        {"type": "aircraft", "color": "#185AFF", "blink": 0, "icon": "Fighter", "icon_bg": "none", "x": x - 0.05, "y": y + 0.03, "dx": dx, "dy": dy},
        {"type": "aircraft", "color": "#f40C00", "blink": 1, "icon": "Fighter", "icon_bg": "none", "x": 0.75, "y": 0.3, "dx": -1, "dy": 0.2},
        {"type": "airfield", "color": "#185AFF", "sx": 0.2, "sy": 0.8, "ex": 0.27, "ey": 0.78},
        {"type": "airfield", "color": "#f40C00", "sx": 0.8, "sy": 0.15, "ex": 0.86, "ey": 0.18},
        {"type": "bombing_point", "color": "#f40C00", "blink": 0, "icon": "bombing_point", "x": 0.62, "y": 0.42},
        {"type": "defending_point", "color": "#185AFF", "blink": 0, "icon": "defending_point", "x": 0.35, "y": 0.6},
        # capture zones as seen in a real match: a letter, and a colour that fades while it is being captured
        {"type": "capture_zone", "color": "#fa0C00", "blink": 0, "icon": "capture_zone", "x": 0.705, "y": 0.383, "zone_id": 8, "zone_label": "A", "zone_label_code": 65},
        {"type": "capture_zone", "color": "#174DFF", "blink": 0, "icon": "capture_zone", "x": 0.163, "y": 0.502, "zone_id": 10, "zone_label": "B", "zone_label_code": 66},
        {"type": "capture_zone", "color": "#feE0DA", "blink": 1, "icon": "capture_zone", "x": 0.312, "y": 0.438, "zone_id": 84, "zone_label": "I", "zone_label_code": 73},
        {"type": "ground_model", "color": "#f40C00", "blink": 0, "icon": "Tracked", "x": 0.58, "y": 0.47},
        {"type": "respawn_base_fighter", "color": "#185AFF", "blink": 0, "icon": "respawn_base_fighter", "x": 0.22, "y": 0.82},
    ]
    return objs

MAPINFO = {"grid_steps": ["8192.0", "8192.0"], "grid_zero": ["-32768.0", "32768.0"], "map_generation": "3",
           "map_max": ["32768.0", "32768.0"], "map_min": ["-32768.0", "-32768.0"]}

DMG = [
    "ObiKan (AJS37) shot down Hawk_77 (MiG-21SMT)",
    "Tomcat_99 (F-5E) destroyed T-55AM",
    "*Iron_Ivan (Su-17M2) has crashed.",
    "ObiKan (AJS37) severely damaged Bomber_X (Tu-16)",
    "nobody_99td! kd?NET_PLAYER_DISCONNECT_FROM_GAME",
    "测试飞行员 (Su-17M4) shot down -TESTcn- (A-5C)",
    "\ue03aItO 90M shot down weapons/su_fab_1500m_46/short",
    "Pilot_42 has disconnected from the game",
]
# Message shapes as captured from a real match: quick-chat arrives as plain text with no position,
# the game's own notices have an empty sender.
CHAT = [("Wingman_1", False, "Team", "Move after me!<color=#FF96966E> [D6, alt. 1300 m]</color>"), ("Wingman_2", False, "Team", "Follow me!<color=#FF96966E> [D6, alt. 100 m]</color>"), ("ΠΙΛΟΤΟΣ_3", False, "Team", "Guide on me!<color=#FF96966E> [E4, alt. 2300 m]</color>"), ("", True, "All", "Chat Spamming"),
        ("Tomcat_99", False, "Team", "Attention to the designated grid square!"), ("Hawk_77", True, "All", "gg"),
        ("Viggen_Vera", False, "Team", "bandit north of C4, F-5E low"), ("Tomcat_99", False, "Team", "Returning to the base."), ("Tomcat_99", False, "Team", "<color=#ffcc00>Attention</color> to <b>D3</b>!")]

def hud(q):
    ld = int(q.get('lastDmg', ['0'])[0])
    n = int((time.time() - T0) // 4) + 45
    items = [{"id": i + 1, "msg": DMG[i % len(DMG)], "sender": "", "enemy": False, "mode": ""} for i in range(min(n, 60))]
    items += [{"id": 1000 + i, "msg": m, "sender": "", "enemy": False, "mode": ""} for i, m in enumerate(EXTRA)]
    return {"events": [], "damage": [i for i in items if i["id"] > ld]}

def chat(q):
    li = int(q.get('lastId', ['0'])[0])
    n = int((time.time() - T0) // 3)
    items = [{"id": i + 1, "msg": CHAT[i % len(CHAT)][3], "sender": CHAT[i % len(CHAT)][0], "enemy": CHAT[i % len(CHAT)][1],
              "mode": CHAT[i % len(CHAT)][2], "time": i * 3} for i in range(min(n, 30))]
    return [i for i in items if i["id"] > li]

# Real /mission.json captured from a match: the list holds both teams' objectives.
# "undefined" = not ours, "delayed" = not active yet; the game only shows the others.
_OBJ = [("Cover the bombers at H4", "undefined"), ("Protect the convoy at C4", "in_progress"), ("Destroy the convoy at C4", "undefined"),
        ("Destroy the bombers at H4", "delayed"), ("Protect the convoy at G4", "undefined"), ("Destroy the convoy at G4", "in_progress"),
        ("Protect the convoy at B5", "in_progress"), ("Destroy the convoy at B5", "undefined"), ("Win air supremacy at D7", "in_progress"),
        ("Win air supremacy at D7", "undefined"), ("Protect the convoy at H3", "undefined"), ("Destroy the convoy at H3", "in_progress"),
        ("Cover the surveillance aircraft at A2", "completed")]
MISSION = {"objectives": [{"primary": True, "status": st, "text": t} for t, st in _OBJ], "status": "running"}

def mission():
    # moving targets: the bomber objective walks east one square every 10 s, like a bomber group crossing the map
    col = 4 + int((time.time() - T0) // 10) % 4
    objs = [dict(o) for o in MISSION["objectives"]]
    objs.append({"primary": True, "status": "in_progress", "text": f"Destroy the bombers at E{col}"})
    return {"objectives": objs, "status": "running"}

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def send(self, body, ctype='application/json'):
        if not isinstance(body, (bytes, bytearray)): body = json.dumps(body).encode()
        self.send_response(200); self.send_header('Content-Type', ctype); self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body)
    def do_GET(self):
        u = urlparse(self.path); q = parse_qs(u.query, keep_blank_values=True); p = u.path
        if p == '/mode':
            MODE['v'] = q.get('v', ['flying'])[0]
            if 'ac' in q: AC[0] = q['ac'][0]
            for k in ('ias', 'rpm', 'fuel'):
                if k in q:
                    if q[k][0] == '': PIN.pop(k, None)
                    else: PIN[k] = float(q[k][0])
            return self.send({"mode": MODE['v'], "aircraft": AC[0], "pinned": PIN})
        if p == '/feed':
            EXTRA.append(q.get('msg', [''])[0])
            return self.send({"feed": len(EXTRA)})
        flying = MODE['v'] == 'flying'
        if p == '/state': return self.send(state() if flying else {"valid": False})
        if p == '/indicators': return self.send(indicators() if flying else {"valid": False})
        if p == '/map_obj.json': return self.send(map_obj() if flying else [])
        if p == '/map_info.json': return self.send(MAPINFO)
        if p == '/map.img': return self.send(PNG, 'image/png')
        if p == '/hudmsg': return self.send(hud(q))
        if p == '/gamechat': return self.send(chat(q))
        if p == '/mission.json': return self.send(mission())
        self.send_response(404); self.end_headers()

ThreadingHTTPServer(('127.0.0.1', PORT), H).serve_forever()
