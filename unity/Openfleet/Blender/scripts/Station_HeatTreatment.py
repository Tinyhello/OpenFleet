"""熱處理站：淬火爐、回火爐、淬火油槽。淬火站與回火站共用這個模型。"""
from ofkit import *

NAME = "Station_HeatTreatment"


def furnace(parent, name, position, size, door, glow, chimney):
    g = group(parent, name, position)
    sx, sy, sz = size
    box(g, "Plinth", (0, 0.06, 0), (sx + 0.06, 0.12, sz + 0.06), "MachineDark", bevel=0.01)
    box(g, "Body", (0, sy / 2 + 0.06, 0), (sx, sy - 0.12, sz), "Furnace", bevel=0.04, segments=3)
    # 外框補強鋼帶
    for y in (0.5, sy - 0.25):
        box(g, "Band", (0, y, 0), (sx + 0.03, 0.08, sz + 0.03), "MachineDark", bevel=0.01)
    for x in (-sx / 2, sx / 2):
        box(g, "Corner", (x, sy / 2, sz / 2), (0.08, sy, 0.08), "MachineDark", bevel=0.01)
    # 爐門：門板、觀察火口、升降導軌與配重
    dw, dh, dy = door
    front = sz / 2
    box(g, "Door", (0, dy, front + 0.02), (dw, dh, 0.04), "MachineDark", bevel=0.012)
    box(g, "Door Insulation", (0, dy, front + 0.045), (dw - 0.12, dh - 0.12, 0.01), "Steel", bevel=0.004)
    box(g, "Glow", (0, dy, front + 0.055), (glow, 0.1, 0.02), "FurnaceGlow", bevel=0.004)
    for x in (-dw / 2 - 0.06, dw / 2 + 0.06):
        box(g, "Door Guide", (x, dy + dh * 0.25, front + 0.03), (0.06, dh * 1.5, 0.06), "Steel", bevel=0.008)
    box(g, "Door Lift Beam", (0, dy + dh * 1.0, front + 0.03), (dw + 0.24, 0.08, 0.08), "Steel", bevel=0.01)
    cyl(g, "Lift Cylinder", (0, dy + dh * 0.85, front + 0.08), 0.08, 0.3, "Steel", segments=14, bevel=0.01)
    # 煙囪
    cx, ch, cd = chimney
    cyl(g, "Chimney", (cx, sy + ch / 2, -sz * 0.22), cd, ch, "Steel", segments=20)
    cyl(g, "Chimney Collar", (cx, sy + 0.05, -sz * 0.22), cd * 1.3, 0.1, "MachineDark", segments=20, bevel=0.01)
    cyl(g, "Chimney Cap", (cx, sy + ch + 0.06, -sz * 0.22), cd * 1.6, 0.04, "Steel", segments=20, bevel=0.01, top_diameter=cd * 0.6)
    # 燃燒器管線
    for z in (-sz * 0.25, sz * 0.15):
        cyl(g, "Burner", (sx / 2 + 0.08, sy * 0.45, z), 0.12, 0.16, "Steel", axis="x", segments=14, bevel=0.01)
        cyl(g, "Gas Pipe", (sx / 2 + 0.12, sy * 0.45 + 0.45, z), 0.04, 0.8, "LaneLine", segments=10)
    return g


def build():
    new_scene()
    r = root(NAME)

    quench = furnace(r, "Quench Furnace", (-1.3, 0, -0.6), (2.0, 2.2, 1.8), (1.2, 1.3, 1.0), 0.9, (-0.5, 1.0, 0.35))
    panel = group(quench, "Panel", (1.2, 0, 0.3))
    box(panel, "Cabinet", (0, 1.2, 0), (0.35, 0.9, 0.35), "MachineDark", bevel=0.015)
    box(panel, "Screen", (0, 1.35, 0.178), (0.25, 0.2, 0.008), "Screen", bevel=0.002)
    cyl(panel, "E-Stop", (0.08, 1.05, 0.18), 0.05, 0.02, "ToolCabinet", axis="z", segments=14, bevel=0.005)
    box(panel, "Stand", (0, 0.38, 0), (0.12, 0.76, 0.12), "Steel", bevel=0.005)
    merge(quench)

    temper = furnace(r, "Temper Furnace", (1.5, 0, -0.9), (1.6, 1.8, 1.4), (1.0, 1.0, 0.85), 0.7, (0.4, 0.6, 0.25))
    merge(temper)

    tank = group(r, "Quench Tank", (1.5, 0, 0.8))
    w, h, d, t = 1.4, 0.9, 0.9, 0.05
    box(tank, "Floor", (0, 0.04, 0), (w, 0.08, d), "Steel", bevel=0.01)
    for k in (-1, 1):
        box(tank, "Wall", (k * (w / 2 - t / 2), h / 2, 0), (t, h, d), "Steel", bevel=0.01)
        box(tank, "Wall", (0, h / 2, k * (d / 2 - t / 2)), (w, h, t), "Steel", bevel=0.01)
        box(tank, "Rim", (0, h + 0.015, k * (d / 2 - 0.01)), (w + 0.04, 0.03, 0.08), "MachineDark", bevel=0.006)
        box(tank, "Rim", (k * (w / 2 - 0.01), h + 0.015, 0), (0.08, 0.03, d + 0.04), "MachineDark", bevel=0.006)
    box(tank, "Oil", (0, 0.82, 0), (w - 2 * t, 0.02, d - 2 * t), "QuenchOil", bevel=0.0)
    box(tank, "Warning Stripe", (0, 0.7, d / 2 + 0.002), (w, 0.08, 0.004), "LaneLine", bevel=0.0)
    # 攪拌馬達與升降籃架
    cyl(tank, "Agitator Motor", (-0.5, h + 0.18, -0.3), 0.18, 0.3, "AccentRough", segments=16, bevel=0.015)
    cyl(tank, "Agitator Shaft", (-0.5, h - 0.1, -0.3), 0.03, 0.4, "Steel", segments=8)
    for x in (-0.55, 0.55):
        box(tank, "Elevator Post", (x, h + 0.45, -d / 2 - 0.05), (0.08, 0.9, 0.08), "Steel", bevel=0.008)
    box(tank, "Elevator Beam", (0, h + 0.9, -d / 2 - 0.05), (1.2, 0.1, 0.1), "RackBeam", bevel=0.01)
    box(tank, "Basket", (0, h + 0.25, 0), (0.6, 0.3, 0.5), "Steel", bevel=0.01)
    for x in (-0.2, 0.2):
        box(tank, "Basket Chain", (x, h + 0.6, -0.2), (0.015, 0.45, 0.015), "MachineDark", bevel=0.0, tilt=("x", 25))
    merge(tank)
    return save_and_export(NAME)
