"""Render layout.json the way the page draws it, to preview.png, so the layout can be LOOKED at."""
import json, sys, os
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fullnet import TOPICS
from gen import LABEL, MAIN

pos = json.load(open("layout.json"))
best = {tuple(e) for e in json.load(open("best-net.json"))}
hard = [(p, t) for t, (h, _) in TOPICS.items() for p in h]
img = Image.new("RGB", (1100, 820), (31, 36, 34)); d = ImageDraw.Draw(img)
try: font = ImageFont.truetype("arial.ttf", 11)
except OSError: font = ImageFont.load_default()
for a, b in best:
    d.line([tuple(pos[a]), tuple(pos[b])], fill=(120, 128, 124), width=2)
for a, b in hard:
    d.line([tuple(pos[a]), tuple(pos[b])], fill=(222, 140, 80), width=2)
for n, (x, y) in pos.items():
    r = 22 if n == "trunk" else 12 if n in MAIN else 6
    col = (235, 238, 236) if n == "trunk" else (90, 190, 130) if n in MAIN else (150, 150, 220)
    d.ellipse([x - r, y - r, x + r, y + r], fill=col)
    lab = LABEL[n]; w = d.textlength(lab, font=font)
    d.text((x - w / 2, y + r + 2), lab, fill=(235, 238, 236), font=font)
img.save("preview.png")
print("ok")
