"""
YunYun ragdoll: cut-out character driven by the window motion.

Dependencies: numpy, opencv-python (cv2), pillow.

    rd = Ragdoll(read)                  # read("rig.json") -> bytes; or a folder with rig.json + PNGs
    rd.reset(window_x, window_y)        # where the character's canvas is on screen (rig px)
    each tick:
        rd.step(dt, window_x, window_y) # window position in rig px
        rgba = rd.render()              # HxWx4 uint8, premultiplied alpha

Model (measured on a 62 s recording of the game)
-----------------------------------------------
Every part has a "drive": a lean angle that moves towards a target at a limited
speed, exactly like the game does (linear ramps, then it stops at a limit):

    target = clamp(kx * vx + ky * vy, -max, +max)     vx, vy = window velocity, px/s
    lean  += clamp(target - lean, -rate*dt, +rate*dt)

  * arms, legs, big wing: the drive IS the angle (rigid rotation about a pivot);
  * hair strands and tail: the drive bends the chain (more towards the tip) and
    damped springs add a small secondary wobble on top (period/zeta/inertia).
"""
import io, json, math, os
import numpy as np
import cv2
from PIL import Image

# ---------------------------------------------------------------- parameters
PARAMS = {
    # drive: kx, ky   - target per px/s of window speed (deg for rigid parts, rad for chains)
    #        max      - target limit;  rate - how fast the lean moves, per second
    #        tau      - smoothing of the window velocity, s
    # chains only (secondary motion):
    #        period, zeta - spring of each link (root -> tip) and its damping ratio
    #        inertia      - kick from the window being yanked; follow - whip from the parent link
    #        input_tau    - smoothing of the motion fed to inertia, s
    #        max_bend     - per-joint limit, deg (number or list root -> tip); root_limit for link 0
    "legs": dict(drive=dict(kx=0.377, ky=0.0, max=7.8, rate=3.3, tau=0.05)),
    "arms": dict(drive=dict(kx=0.316, ky=0.0, max=3.9, rate=1.8, tau=0.05)),
    "wing": dict(drive=dict(kx=0.0455, ky=0.0, max=5.6, rate=12.9, tau=0.05)),
    "hair_r2": dict(drive=dict(kx=0.0017, ky=0.0, max=1.0, rate=3.0, tau=0.6),
                    period=(0.4, 0.3), zeta=0.8, inertia=0.0, follow=0.0, input_tau=0.15, max_bend=16, root_limit=30),
    "hair_r1": dict(drive=dict(kx=0.0017, ky=0.0, max=1.0, rate=3.0, tau=0.6),
                    period=(0.4, 0.3), zeta=0.8, inertia=0.0, follow=0.0, input_tau=0.15, max_bend=16, root_limit=30),
    "hair_l2": dict(drive=dict(kx=0.0017, ky=0.0, max=1.0, rate=3.0, tau=0.6),
                    period=(0.4, 0.3), zeta=0.8, inertia=0.0, follow=0.0, input_tau=0.15, max_bend=16, root_limit=30),
    "tail": dict(drive=dict(kx=0.08, ky=0.0, max=0.65, rate=0.55, tau=0.05), lean_profile="root",
                 period=(0.4, 0.3), zeta=0.8, inertia=0.0, follow=0.0, input_tau=0.15, max_bend=[4, 4, 5, 6, 8, 12, 12, 10, 4, 3, 3], root_limit=40),
}
SUBSTEP = 1.0 / 240.0
GRID = 3          # px step of the coarse deformation map


def _rot(v, a):
    c, s = math.cos(a), math.sin(a)
    return np.array([c * v[0] - s * v[1], s * v[0] + c * v[1]])


def _ang(v):
    return math.atan2(v[1], v[0])


def _wrap(a):
    return (a + math.pi) % (2 * math.pi) - math.pi


class _Part:
    def __init__(self, name, ent, load):
        self.name = name
        img = self.img = load(ent["file"])
        self.off = np.array(ent["offset"], float)          # top-left of the image in canvas coords
        self.type = ent["type"]
        p = PARAMS[name]
        if self.type == "chain":
            pts = np.array(ent["points"], float)
            self.half_width = ent["half_width"]
            self.seg_hw = np.array(ent["seg_half_width"], np.float32)
        else:
            piv = np.array(ent["pivot"], float)
            a = img[..., 3] > 0.5
            ys, xs = np.nonzero(a)
            far = np.array([xs.mean(), ys.max()]) + self.off   # a point far from the pivot (bottom centre)
            if name == "wing":
                far = np.array([xs.min(), ys.mean()]) + self.off
            pts = np.stack([piv, far])
        self.rest = pts                                     # canvas coords
        n = len(pts) - 1
        self.lengths = np.linalg.norm(np.diff(pts, axis=0), axis=1)
        dirs = np.diff(pts, axis=0)
        self.rest_abs = np.array([_ang(d) for d in dirs])  # absolute rest angle of each link
        self.rest_rel = np.r_[self.rest_abs[0], np.diff(self.rest_abs)]
        self.rest_rel = np.array([_wrap(a) for a in self.rest_rel])
        dr = p["drive"]
        self.kx, self.ky, self.dmax, self.drate, self.dtau = dr["kx"], dr["ky"], dr["max"], dr["rate"], dr["tau"]
        if self.type == "rigid":                            # rigid drives are given in degrees
            self.kx, self.ky, self.dmax, self.drate = (math.radians(v) for v in (self.kx, self.ky, self.dmax, self.drate))
            return
        t = np.linspace(0, 1, n)
        per = np.array(p["period"][0] + (p["period"][1] - p["period"][0]) * t)
        self.omega = 2 * math.pi / per
        self.zeta = p["zeta"]
        self.input_tau = p["input_tau"]
        self.follow = p["follow"]
        rem = np.cumsum(self.lengths[::-1])[::-1]
        self.inertia = p["inertia"] / np.maximum(rem * 0.5, 1.0)
        w = np.linspace(1.0, 2.0, n) if p.get("lean_profile", "tip") == "tip" else np.linspace(2.0, 0.3, n)
        self.lean_w = w / w.sum()
        mb = p["max_bend"]
        mb = list(mb) if isinstance(mb, (list, tuple)) else [mb] * max(n - 1, 1)
        mb = (mb + [mb[-1]] * n)[:max(n - 1, 1)]
        self.max_bend = np.radians(np.array([0.0] + mb))
        self.root_limit = math.radians(p["root_limit"])
        self.x = None

    # ------------------------------------------------------------ physics
    def reset(self, origin):
        n = len(self.rest) - 1
        self.dev = np.zeros(n)                              # angle of each link relative to rest
        self.w = np.zeros(n)
        self.vs = np.zeros(2)                               # velocity smoothed for the drive
        self.vf = np.zeros(2)                               # velocity smoothed for inertia
        self.lean = 0.0
        self.lean_override = None                           # for calibration / tests
        self.x = self.rest + origin

    def step(self, origin, vel, h):
        """One sub-step. origin: canvas origin on screen, vel: window velocity px/s, h: sub-step s."""
        self.vs += (1.0 - math.exp(-h / max(self.dtau, 1e-4))) * (vel - self.vs)
        tgt = max(-self.dmax, min(self.dmax, self.kx * self.vs[0] + self.ky * self.vs[1]))
        d = tgt - self.lean; lim = self.drate * h
        self.lean += max(-lim, min(lim, d))
        lean = self.lean if self.lean_override is None else self.lean_override
        n = len(self.dev)
        if self.type == "rigid":
            self.dev[0] = lean
        else:
            vf_old = self.vf.copy()
            self.vf += (1.0 - math.exp(-h / self.input_tau)) * (vel - self.vf)
            acc = (self.vf - vf_old) / h
            parent_alpha = 0.0
            phi = self.rest_abs[0]
            for i in range(n):
                phi_i = phi + self.dev[i]
                perp = np.array([-math.sin(phi_i), math.cos(phi_i)])
                om = self.omega[i]
                alpha = (-om * om * (self.dev[i] - lean * self.lean_w[i]) - 2 * self.zeta * om * self.w[i]
                         - self.inertia[i] * float(acc @ perp) - self.follow * parent_alpha)
                self.w[i] += alpha * h
                self.dev[i] += self.w[i] * h
                lim = self.root_limit if i == 0 else self.max_bend[i]
                if self.dev[i] > lim:
                    self.dev[i], self.w[i] = lim, min(self.w[i], 0.0)
                elif self.dev[i] < -lim:
                    self.dev[i], self.w[i] = -lim, max(self.w[i], 0.0)
                parent_alpha = alpha
                phi = phi_i + (self.rest_rel[i + 1] if i + 1 < n else 0.0)
        x = self.x
        x[0] = self.rest[0] + origin
        phi = self.rest_abs[0]
        for i in range(n):
            phi += self.dev[i]
            x[i + 1] = x[i] + self.lengths[i] * np.array([math.cos(phi), math.sin(phi)])
            if i + 1 < n:
                phi += self.rest_rel[i + 1]

    # ------------------------------------------------------------ drawing
    def draw(self, canvas, origin, under=False):
        self._comp = _under if under else _over
        q = self.x - origin                                # current points in canvas coords
        H, W = canvas.shape[:2]
        if self.type == "rigid":
            ang = _ang(q[1] - q[0]) - self.rest_abs[0]
            c, s_ = math.cos(ang), math.sin(ang)
            piv_img = self.rest[0] - self.off                # pivot in image coords
            # image -> canvas: rotate about pivot, then put pivot at q[0]
            M = np.array([[c, -s_, q[0][0] - (c * piv_img[0] - s_ * piv_img[1])],
                          [s_, c, q[0][1] - (s_ * piv_img[0] + c * piv_img[1])]], np.float32)
            h, w = self.img.shape[:2]
            corners = np.array([[0, 0, 1], [w, 0, 1], [0, h, 1], [w, h, 1]], np.float32) @ M.T
            x0 = int(max(np.floor(corners[:, 0].min()) - 1, 0)); x1 = int(min(np.ceil(corners[:, 0].max()) + 2, W))
            y0 = int(max(np.floor(corners[:, 1].min()) - 1, 0)); y1 = int(min(np.ceil(corners[:, 1].max()) + 2, H))
            if x1 <= x0 or y1 <= y0:
                return
            M2 = M.copy(); M2[:, 2] -= (x0, y0)
            patch = cv2.warpAffine(self.img, M2, (x1 - x0, y1 - y0), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT)
            self._comp(canvas, patch, x0, y0)
            return
        # chain: backward-map output pixels onto the rest strand (computed on a coarse grid)
        hw = self.half_width
        x0 = int(max(q[:, 0].min() - hw - 2, 0)); x1 = int(min(q[:, 0].max() + hw + 3, W))
        y0 = int(max(q[:, 1].min() - hw - 2, 0)); y1 = int(min(q[:, 1].max() + hw + 3, H))
        if x1 <= x0 or y1 <= y0:
            return
        st = GRID
        gx = np.arange(x0, x1 + st, st, dtype=np.float32); gy = np.arange(y0, y1 + st, st, dtype=np.float32)
        PX, PY = np.meshgrid(gx, gy)
        mx, my, far = self._map(q, PX, PY)
        out_w, out_h = x1 - x0, y1 - y0
        # upsample the coarse map (grid point k sits at pixel k*st)
        sx = (len(gx) - 1) / ((len(gx) - 1) * st); sy = (len(gy) - 1) / ((len(gy) - 1) * st)
        fx = cv2.resize(mx, (len(gx) * st, len(gy) * st), interpolation=cv2.INTER_LINEAR)
        fy = cv2.resize(my, (len(gx) * st, len(gy) * st), interpolation=cv2.INTER_LINEAR)
        ff = cv2.resize(far.astype(np.float32), (len(gx) * st, len(gy) * st), interpolation=cv2.INTER_LINEAR)
        sh = st // 2   # resize treats samples as pixel centres; shift so sample k -> pixel k*st
        fx = fx[sh:sh + out_h, sh:sh + out_w]; fy = fy[sh:sh + out_h, sh:sh + out_w]; ff = ff[sh:sh + out_h, sh:sh + out_w]
        fx = np.where(ff > 0.5, -10, fx).astype(np.float32); fy = np.where(ff > 0.5, -10, fy).astype(np.float32)
        patch = cv2.remap(self.img, fx, fy, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT)
        self._comp(canvas, patch, x0, y0)

    def _map(self, q, PX, PY):
        P = np.stack([PX, PY], -1)[..., None, :]            # h,w,1,2
        A = q[:-1].astype(np.float32); AB = (q[1:] - q[:-1]).astype(np.float32)
        L2 = (AB ** 2).sum(-1)
        t = ((P - A) * AB).sum(-1) / L2                     # h,w,n
        tc = np.clip(t, 0, 1)
        tc[..., 0] = np.minimum(t[..., 0], 1)               # extend before the root
        tc[..., -1] = np.maximum(t[..., -1], 0)             # and past the tip
        C = A + tc[..., None] * AB
        d2 = ((P - C) ** 2).sum(-1)
        j = np.argmin(d2, -1)
        dmin = np.sqrt(np.take_along_axis(d2, j[..., None], -1)[..., 0])
        tj = np.take_along_axis(tc, j[..., None], -1)[..., 0]
        u = AB[j] / np.sqrt(L2[j])[..., None]
        rel = P[..., 0, :] - (A[j] + tj[..., None] * AB[j])
        tang = (rel * u).sum(-1)
        nrm = rel[..., 0] * (-u[..., 1]) + rel[..., 1] * u[..., 0]
        R = self.rest.astype(np.float32)
        RA = R[:-1][j]; RAB = (R[1:] - R[:-1])[j]
        ru = RAB / np.linalg.norm(RAB, axis=-1)[..., None]
        src = RA + tj[..., None] * RAB + tang[..., None] * ru + nrm[..., None] * np.stack([-ru[..., 1], ru[..., 0]], -1)
        src -= self.off.astype(np.float32)
        far = dmin > (self.seg_hw[j] + 1.5)
        return src[..., 0].astype(np.float32), src[..., 1].astype(np.float32), far


def _under(canvas, img, x, y):
    h, w = img.shape[:2]
    H, W = canvas.shape[:2]
    if x >= W or y >= H:
        return
    h = min(h, H - y); w = min(w, W - x)
    dst = canvas[y:y + h, x:x + w]
    dst += img[:h, :w] * (1.0 - dst[..., 3:4])


def _over(canvas, img, x, y):
    h, w = img.shape[:2]
    H, W = canvas.shape[:2]
    if x >= W or y >= H:
        return
    h = min(h, H - y); w = min(w, W - x)
    dst = canvas[y:y + h, x:x + w]
    src = img[:h, :w]
    dst *= (1.0 - src[..., 3:4])
    dst += src


class Ragdoll:
    def __init__(self, source):
        """source: read(name) -> bytes (e.g. from the packed resources), or a folder path."""
        if callable(source):
            read = source
        else:
            read = lambda name: open(os.path.join(source, name), "rb").read()

        def load(name):                                # premultiplied float RGBA
            im = np.asarray(Image.open(io.BytesIO(read(name))).convert("RGBA")).astype(np.float32) / 255.0
            im[..., :3] *= im[..., 3:4]
            return im

        rig = json.loads(read("rig.json").decode("utf-8"))
        self.W, self.H = rig["size"]
        b = load(rig["body"]["file"])
        self.body = b
        self.statics = {"body": (b, (0, 0))}
        for k in ("back", "cover"):
            if k in rig:
                self.statics[k] = (load(rig[k]["file"]), tuple(rig[k]["offset"]))
        self.parts = {k: _Part(k, e, load) for k, e in rig["parts"].items()}
        self.order = rig["draw_order"]
        self.grab_point = tuple(rig.get("grab_point", (224, 194)))
        self.origin = np.zeros(2)
        self.acc = 0.0

    def reset(self, wx, wy):
        self.origin = np.array([wx, wy], float)
        for p in self.parts.values():
            p.reset(self.origin)

    def step(self, dt, wx, wy):
        """Advance physics by dt seconds; (wx, wy) = current window position on screen."""
        new = np.array([wx, wy], float)
        start = self.origin.copy()
        self.acc += min(dt, 0.1)
        nsub = int(self.acc / SUBSTEP)
        self.acc -= nsub * SUBSTEP
        vel = (new - start) / max(min(dt, 0.1), 1e-4)
        for s in range(nsub):
            o = start + (new - start) * (s + 1) / max(nsub, 1)   # interpolate the anchor within the frame
            for p in self.parts.values():
                p.step(o, vel, SUBSTEP)
        self.origin = new

    def render(self, premultiplied=True):
        """HxWx4 uint8. premultiplied=True (fast) suits Qt's Format_RGBA8888_Premultiplied
        and most compositors; pass False for straight alpha (PIL, pygame)."""
        canvas = np.zeros((self.H, self.W, 4), np.float32)
        for k in self.order:                          # back to front
            if k in self.statics:
                img, off = self.statics[k]
                _over(canvas, img, *off)
            else:
                self.parts[k].draw(canvas, self.origin)
        if premultiplied:
            return (canvas * 255 + 0.5).astype(np.uint8)
        a = canvas[..., 3:4]
        rgb = np.where(a > 1e-4, canvas[..., :3] / np.maximum(a, 1e-4), 0)
        return (np.concatenate([rgb, a], -1) * 255 + 0.5).clip(0, 255).astype(np.uint8)
