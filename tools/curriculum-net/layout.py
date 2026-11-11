"""A crossing-free layout for the drawn net (kept related edges + hard arrows).

1. networkx's planar_layout gives straight-line positions with NO crossings (guaranteed for a
   planar graph) but cramped and ugly.
2. Relax it with a spring model where a node may only move if the move keeps the drawing
   crossing-free and keeps every node clear of every edge it is not on. Crossing-freedom is
   therefore an invariant from the first step to the last.
3. Verify: count crossings at the end; refuse to write anything if it is not 0.
Writes layout.json: {id: [x, y]} in the page's 1100 x 820 box."""
import json, math, random, sys, os
import networkx as nx
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fullnet import TOPICS

W, H, PAD = 1100, 820, 70
best = {tuple(e) for e in json.load(open("best-net.json"))}
edges = set(best)
for t, (hard, _) in TOPICS.items():
    for p in hard: edges.add((p, t))
SEED = int(sys.argv[1]) if len(sys.argv) > 1 else 7
_r = random.Random(SEED * 7919)
_nodes = ["trunk", *TOPICS]; _r.shuffle(_nodes)
_edges = [tuple(e) if _r.random() < .5 else tuple(reversed(e)) for e in edges]; _r.shuffle(_edges)
G = nx.Graph(); G.add_nodes_from(_nodes); G.add_edges_from(_edges)
ok, emb = nx.check_planarity(G)
assert ok, "not planar"
E = [tuple(e) for e in G.edges()]
N = list(G.nodes())
main = {"trunk", "text", "maps", "records", "things", "methods", "kinds", "failure", "files",
        "input", "rabbits", "tasks", "channels", "books", "modules"}

def cross(p1, p2, p3, p4):
    def o(a, b, c): return (b[0]-a[0])*(c[1]-a[1]) - (b[1]-a[1])*(c[0]-a[0])
    d1, d2, d3, d4 = o(p3, p4, p1), o(p3, p4, p2), o(p1, p2, p3), o(p1, p2, p4)
    return ((d1 > 0) != (d2 > 0)) and ((d3 > 0) != (d4 > 0)) and 0 not in (d1, d2, d3, d4)

def seg_dist(p, a, b):
    vx, vy = b[0]-a[0], b[1]-a[1]; L = vx*vx + vy*vy or 1e-9
    t = max(0, min(1, ((p[0]-a[0])*vx + (p[1]-a[1])*vy) / L))
    return math.hypot(p[0]-a[0]-t*vx, p[1]-a[1]-t*vy)

def crossings(pos):
    c = 0
    for i, (a, b) in enumerate(E):
        for (u, v) in E[i+1:]:
            if len({a, b, u, v}) < 4: continue
            if cross(pos[a], pos[b], pos[u], pos[v]): c += 1
    return c

def clear(pos, n, gap):
    """n is at least `gap` from every edge it is not an end of, and its own edges cross nothing,
    and its own edges pass at least `gap` from every other node."""
    p = pos[n]
    for (a, b) in E:
        if n in (a, b): continue
        if seg_dist(p, pos[a], pos[b]) < gap: return False
    mine = [e for e in E if n in e]
    for (a, b) in mine:
        for (u, v) in E:
            if len({a, b, u, v}) < 4: continue
            if cross(pos[a], pos[b], pos[u], pos[v]): return False
        for m in N:
            if m in (a, b): continue
            if seg_dist(pos[m], pos[a], pos[b]) < gap: return False
    return True

rng = random.Random(SEED)
from networkx.algorithms.planar_drawing import combinatorial_embedding_to_pos
pos = {n: tuple(map(float, v)) for n, v in combinatorial_embedding_to_pos(emb).items()}
# scale into the box
xs, ys = [p[0] for p in pos.values()], [p[1] for p in pos.values()]
sx = (W - 2*PAD) / ((max(xs) - min(xs)) or 1); sy = (H - 2*PAD) / ((max(ys) - min(ys)) or 1)
pos = {n: (PAD + (p[0]-min(xs))*sx, PAD + (p[1]-min(ys))*sy) for n, p in pos.items()}
assert crossings(pos) == 0, "planar_layout crossed"

def length(n, m):
    return 120 if (n in main and m in main) else 80

ITER = int(os.environ.get("ITER", 2500))
REP = float(os.environ.get("REP", 30000))
for it in range(ITER):
    temp = 45 * (1 - it / ITER) + 1
    gap = 14 + 14 * min(1, it / (ITER * 0.4))
    order = N[:]; rng.shuffle(order)
    for n in order:
        x, y = pos[n]; fx = fy = 0.0
        for m in N:
            if m == n: continue
            dx, dy = x - pos[m][0], y - pos[m][1]; d = math.hypot(dx, dy) or 0.01
            rep = REP / (d * d)
            fx += dx / d * rep; fy += dy / d * rep
        for m in G[n]:
            dx, dy = pos[m][0] - x, pos[m][1] - y; d = math.hypot(dx, dy) or 0.01
            att = (d - length(n, m)) * 0.25
            fx += dx / d * att; fy += dy / d * att
        for (a, b) in E:
            if n in (a, b): continue
            ax, ay = pos[a]; bx, by = pos[b]; vx, vy = bx-ax, by-ay; L = vx*vx + vy*vy or 1e-9
            t = max(0, min(1, ((x-ax)*vx + (y-ay)*vy) / L))
            qx, qy = ax + t*vx, ay + t*vy; dx, dy = x - qx, y - qy; d = math.hypot(dx, dy) or 0.01
            if d < 60:
                push = (60 - d) * 0.6
                fx += dx / d * push; fy += dy / d * push
        fx += (W/2 - x) * 0.002; fy += (H/2 - y) * 0.003
        f = math.hypot(fx, fy) or 1
        step = min(temp, f)
        for scale in (1, 0.5, 0.25):
            nx_, ny_ = x + fx / f * step * scale, y + fy / f * step * scale
            nx_ = min(W - PAD/2, max(PAD/2, nx_)); ny_ = min(H - PAD/2, max(PAD/2, ny_))
            old = pos[n]; pos[n] = (nx_, ny_)
            if clear(pos, n, gap): break
            pos[n] = old

c = crossings(pos)
mind = min(seg_dist(pos[m], pos[a], pos[b]) for (a, b) in E for m in N if m not in (a, b))
near = min(math.hypot(pos[a][0]-pos[b][0], pos[a][1]-pos[b][1]) for i, a in enumerate(N) for b in N[i+1:])
print(f"crossings {c}  closest node-to-edge {mind:.0f}px  closest node pair {near:.0f}px")
import layout2
st = sum(layout2.stress_of(pos, n) for n in N) / 2
print(f"stress {st:.1f}")
if c:
    sys.exit("NOT WRITTEN: crossings remain")
out = sys.argv[2] if len(sys.argv) > 2 else "layout.json"
json.dump({n: [round(p[0], 1), round(p[1], 1)] for n, p in pos.items()}, open(out, "w"))
print("wrote layout.json")
