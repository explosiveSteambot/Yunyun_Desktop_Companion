"""Keeping the companion on the desktop work area (never over the taskbar). Pure functions.

Rectangles are (x, y, w, h) tuples in screen pixels; a work area is what
QScreen.availableGeometry() returns - the screen minus the taskbar / docks.
"""


def contains_point(rect, px, py):
    x, y, w, h = rect
    return x <= px < x + w and y <= py < y + h


def pick_area(areas, px, py):
    """Work area that contains the point, else the nearest one (multi-monitor)."""
    for a in areas:
        if contains_point(a, px, py):
            return a

    def dist2(a):
        x, y, w, h = a
        cx = min(max(px, x), x + w - 1)
        cy = min(max(py, y), y + h - 1)
        return (cx - px) ** 2 + (cy - py) ** 2

    return min(areas, key=dist2)


def clamp_rect(rect, area, visible=None):
    """Move rect (not resize) so it lies inside area.

    visible: optional (x, y, w, h) of the opaque part of the image inside the
    window; only that part has to stay inside the area, transparent margins may
    hang over the edge.
    """
    x, y, w, h = rect
    vx, vy, vw, vh = visible if visible else (0, 0, w, h)
    ax, ay, aw, ah = area
    left, top = x + vx, y + vy
    right, bottom = left + vw, top + vh
    dx = dy = 0
    if right > ax + aw:
        dx = ax + aw - right
    if left + dx < ax:
        dx = ax - left
    if bottom > ay + ah:
        dy = ay + ah - bottom
    if top + dy < ay:
        dy = ay - top
    return (x + dx, y + dy, w, h)
