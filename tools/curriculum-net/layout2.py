"""Crossing-free layout, second approach.
1. STRESS layout: screen distance ~ graph distance (good global shape), from several random starts.
2. ANNEAL: random single-node moves, energy = stress + BIG * crossings + node-on-edge penalty,
   until crossings reach 0.
3. RELAX with crossing-freedom as an invariant (a move is kept only if it crosses nothing).
4. Verify; write layout.json only if crossings == 0. Prints the metrics of every start."""
import json, math, random, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fullnet import TOPICS

W, H, PAD = 1100, 820, 60
best = {tuple(e) for e in json.load(open("best-net.json"))}
edges = set(best)
for t, (hard, _) in TOPICS.items():
    for p in hard: edges.add((p, t))
N = ["trunk", *TOPICS]
E = sorted({tuple(sorted(e)) for e in edges})
adj = {n: set() for n in N}
for a, b in E: adj[a].add(b); adj[b].add(a)
main = {"trunk", "text", "maps", "records", "things", "methods", "kinds", "failure", "files",
        "input", "rabbits", "tasks", "channels", "books", "modules"}

# graph distances (BFS), weighted: an edge touching a side quest is shorter
def elen(a, b): return 1.0 if (a in main and b in main) else 0.7
D = {}
for s in N:
    dist = {s: 0.0}; todo = [s]
    while todo:  # small graph: Bellman-style relaxation is fine
        nxt = []
        for u in todo:
            for v in adj[u]:
                nd = dist[u] + elen(u, v)
                if nd < dist.get(v, 1e9): dist[v] = nd; nxt.append(v)
        todo = nxt
    D[s] = dist
UNIT = 125

def cross(p1, p2, p3, p4):
    def o(a, b, c): return (b[0]-a[0])*(c[1]-a[1]) - (b[1]-a[1])*(c[0]-a[0])
    d1, d2, d3, d4 = o(p3, p4, p1), o(p3, p4, p2), o(p1, p2, p3), o(p1, p2, p4)
    return ((d1 > 0) != (d2 > 0)) and ((d3 > 0) != (d4 > 0))

def seg_dist(p, a, b):
    vx, vy = b[0]-a[0], b[1]-a[1]; L = vx*vx + vy*vy or 1e-9
    t = max(0, min(1, ((p[0]-a[0])*vx + (p[1]-a[1])*vy) / L))
    return math.hypot(p[0]-a[0]-t*vx, p[1]-a[1]-t*vy)

PAIRS = [(E[i], E[j]) for i in range(len(E)) for j in range(i+1, len(E)) if len(set(E[i]) | set(E[j])) == 4]
def crossings(pos): return sum(1 for (a, b), (u, v) in PAIRS if cross(pos[a], pos[b], pos[u], pos[v]))
def crossings_of(pos, n):
    return sum(1 for (a, b), (u, v) in PAIRS if (n in (a, b, u, v)) and cross(pos[a], pos[b], pos[u], pos[v]))
GAP = 30
def crowd_of(pos, n):
    """how far n's surroundings break the gap: n near foreign edges + n's edges near foreign nodes"""
    c = 0.0
    for a, b in E:
        if n in (a, b):
            for m in N:
                if m not in (a, b):
                    d = seg_dist(pos[m], pos[a], pos[b]); c += max(0, GAP - d)
        else:
            d = seg_dist(pos[n], pos[a], pos[b]); c += max(0, GAP - d)
    for m in N:
        if m != n:
            d = math.hypot(pos[n][0]-pos[m][0], pos[n][1]-pos[m][1]); c += max(0, 55 - d) * 2
    return c
def stress_of(pos, n):
    s = 0.0
    for m in N:
        if m == n: continue
        dij = D[n][m] * UNIT
        d = math.hypot(pos[n][0]-pos[m][0], pos[n][1]-pos[m][1])
        s += (d - dij) ** 2 / (dij * dij)
    return s

def stress_layout(rng):
    pos = {n: (rng.uniform(0, W), rng.uniform(0, H)) for n in N}
    for it in range(300):
        lr = 0.25 * (1 - it / 300) + 0.02
        for n in N:
            gx = gy = 0.0
            for m in N:
                if m == n: continue
                dij = D[n][m] * UNIT
                dx, dy = pos[n][0]-pos[m][0], pos[n][1]-pos[m][1]; d = math.hypot(dx, dy) or 0.01
                w = 1 / (dij * dij)
                gx += w * (d - dij) * dx / d; gy += w * (d - dij) * dy / d
            pos[n] = (pos[n][0] - lr * gx * UNIT * UNIT / len(N), pos[n][1] - lr * gy * UNIT * UNIT / len(N))
    return pos

def fit(pos):
    xs, ys = [p[0] for p in pos.values()], [p[1] for p in pos.values()]
    s = min((W - 2*PAD) / ((max(xs) - min(xs)) or 1), (H - 2*PAD) / ((max(ys) - min(ys)) or 1))
    ox = (W - (max(xs) - min(xs)) * s) / 2; oy = (H - (max(ys) - min(ys)) * s) / 2
    return {n: (ox + (p[0]-min(xs))*s, oy + (p[1]-min(ys))*s) for n, p in pos.items()}

def local(pos, n): return 5000 * crossings_of(pos, n) + 3 * stress_of(pos, n) + 0.5 * crowd_of(pos, n)

def branches():
    """for each node n: the nodes that hang off n, i.e. cut off from the trunk when n is removed."""
    out = {}
    for n in N:
        if n == "trunk": out[n] = set(); continue
        seen = {"trunk"}; todo = ["trunk"]
        while todo:
            u = todo.pop()
            for v in adj[u]:
                if v != n and v not in seen: seen.add(v); todo.append(v)
        out[n] = set(N) - seen - {n}
    return out
BR = branches()
GROUPS = [n for n in N if BR[n]]

def total(pos):
    return 5000 * crossings(pos) + 3 * sum(stress_of(pos, n) for n in N) / 2 + 0.5 * sum(crowd_of(pos, n) for n in N) / 2

def group_moves(pos, rng, steps):
    """move a node TOGETHER with everything hanging off it (translate, or rotate about it)."""
    cur = total(pos)
    for k in range(steps):
        n = rng.choice(GROUPS); grp = BR[n] | {n}
        old = {m: pos[m] for m in grp}
        if rng.random() < 0.5:
            dx, dy = rng.gauss(0, 80), rng.gauss(0, 80)
            for m in grp: pos[m] = (pos[m][0] + dx, pos[m][1] + dy)
        else:  # swing the branch around its root
            th = rng.uniform(-math.pi, math.pi); cx, cy = pos[n]
            c, s_ = math.cos(th), math.sin(th)
            for m in BR[n]:
                x, y = pos[m][0] - cx, pos[m][1] - cy
                pos[m] = (cx + c*x - s_*y, cy + s_*x + c*y)
        if any(not (PAD/2 <= pos[m][0] <= W-PAD/2 and PAD/2 <= pos[m][1] <= H-PAD/2) for m in grp):
            pos.update(old); continue
        e = total(pos)
        if e <= cur: cur = e
        else: pos.update(old)
    return pos

def cheap(pos):
    return 5000 * crossings(pos) + 3 * sum(stress_of(pos, n) for n in N) / 2

def fix_crossings(pos):
    """Grid search: for every node on a crossing edge, try it (alone, and with its hanging branch)
    at every 25px grid point; take the best placement. Repeat until no crossing or no progress."""
    for rounds in range(8):
        c = crossings(pos)
        if c == 0: return pos
        bad = set()
        for (a, b), (u, v) in PAIRS:
            if cross(pos[a], pos[b], pos[u], pos[v]): bad |= {a, b, u, v}
        best_e, best_move = cheap(pos), None
        for n in bad:
            for grp in ({n}, BR[n] | {n}):
                old = {m: pos[m] for m in grp}
                for gx in range(PAD // 2, W - PAD // 2, 25):
                    for gy in range(PAD // 2, H - PAD // 2, 25):
                        dx, dy = gx - old[n][0], gy - old[n][1]
                        moved = {m: (old[m][0] + dx, old[m][1] + dy) for m in grp}
                        if any(not (PAD/2 <= p[0] <= W-PAD/2 and PAD/2 <= p[1] <= H-PAD/2) for p in moved.values()): continue
                        if any(math.hypot(p[0]-pos[o][0], p[1]-pos[o][1]) < 45 for p in moved.values() for o in N if o not in grp): continue
                        pos.update(moved)
                        e = cheap(pos)
                        if e < best_e: best_e, best_move = e, dict(moved)
                        pos.update(old)
        if best_move is None: return pos
        pos.update(best_move)
    return pos

def relax(pos, rng, iters=600):
    """Spread things out with crossing-freedom as an INVARIANT: a move that crosses anything,
    or brings a node within GAP of an edge it is not on, is not taken."""
    def viol(n):
        v = 0.0
        for a, b in E:
            if n in (a, b):
                for m in N:
                    if m not in (a, b): v += max(0, GAP - seg_dist(pos[m], pos[a], pos[b]))
            else: v += max(0, GAP - seg_dist(pos[n], pos[a], pos[b]))
        return v
    def ok(n, before):
        return crossings_of(pos, n) == 0 and viol(n) <= before + 1e-6
    for it in range(iters):
        temp = 20 * (1 - it / iters) + 1
        for n in rng.sample(N, len(N)):
            x, y = pos[n]; fx = fy = 0.0
            for m in N:
                if m == n: continue
                dx, dy = x - pos[m][0], y - pos[m][1]; d = math.hypot(dx, dy) or 0.01
                fx += dx / d * 40000 / (d * d); fy += dy / d * 40000 / (d * d)
            for m in adj[n]:
                dx, dy = pos[m][0] - x, pos[m][1] - y; d = math.hypot(dx, dy) or 0.01
                L = UNIT * elen(n, m); fx += dx / d * (d - L) * 0.3; fy += dy / d * (d - L) * 0.3
            for a, b in E:
                if n in (a, b): continue
                d = seg_dist((x, y), pos[a], pos[b])
                if d < 60:
                    ax, ay = pos[a]; bx, by = pos[b]; vx, vy = bx-ax, by-ay; L2 = vx*vx+vy*vy or 1e-9
                    t = max(0, min(1, ((x-ax)*vx + (y-ay)*vy) / L2)); qx, qy = ax+t*vx, ay+t*vy
                    dd = math.hypot(x-qx, y-qy) or 0.01
                    fx += (x-qx)/dd*(60-d)*0.6; fy += (y-qy)/dd*(60-d)*0.6
            f = math.hypot(fx, fy) or 1; step = min(temp, f)
            before = viol(n)
            for sc in (1, 0.5, 0.25):
                old = pos[n]
                pos[n] = (min(W-PAD/2, max(PAD/2, x + fx/f*step*sc)), min(H-PAD/2, max(PAD/2, y + fy/f*step*sc)))
                if ok(n, before): break
                pos[n] = old
    return pos

def anneal(pos, rng, steps):
    T = 200.0
    for k in range(steps):
        n = rng.choice(N); old = pos[n]
        e0 = local(pos, n)
        r = (T + 4) if rng.random() < 0.85 else 250
        pos[n] = (min(W-PAD/2, max(PAD/2, old[0] + rng.gauss(0, r))), min(H-PAD/2, max(PAD/2, old[1] + rng.gauss(0, r))))
        e1 = local(pos, n)
        if not (e1 <= e0 or rng.random() < math.exp(-(e1 - e0) / max(T, 0.05))):
            pos[n] = old
        T *= 0.99975
    return pos

def run(seed):
    rng = random.Random(seed)
    pos = fit(stress_layout(rng))
    pos = anneal(pos, rng, 25000)
    if crossings(pos) == 0: pos = relax(pos, rng)
    return pos

def metrics(pos):
    c = crossings(pos)
    ne = min(seg_dist(pos[m], pos[a], pos[b]) for (a, b) in E for m in N if m not in (a, b))
    nn = min(math.hypot(pos[a][0]-pos[b][0], pos[a][1]-pos[b][1]) for i, a in enumerate(N) for b in N[i+1:])
    st = sum(stress_of(pos, n) for n in N) / 2
    return c, ne, nn, st

def job(s):
    pos = run(s); return s, pos, metrics(pos)

if __name__ == "__main__":
    from multiprocessing import Pool
    seeds = range(int(sys.argv[1]) if len(sys.argv) > 1 else 6)
    results = []
    with Pool(8) as pool:
        outs = pool.map(job, seeds)
    for s, pos, m in outs:
        print(f"start {s}: crossings {m[0]}  node-edge {m[1]:.0f}px  node-node {m[2]:.0f}px  stress {m[3]:.1f}", flush=True)
        if m[0] == 0: results.append((-(min(m[1], 30) + min(m[2], 60) / 2) + m[3] / 10, s, pos))
    if not results: sys.exit("NOT WRITTEN: every start still crosses")
    results.sort(key=lambda r: r[0]); _, s, pos = results[0]
    json.dump({n: [round(p[0], 1), round(p[1], 1)] for n, p in pos.items()}, open("layout.json", "w"))
    print("wrote layout.json from start", s)
