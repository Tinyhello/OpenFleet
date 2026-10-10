"""包裝站：包裝桌（開口紙箱、平放紙板、封箱機）與滾筒輸送機。"""
from ofkit import *

NAME = "Station_Packing"


def open_carton(parent, center, size):
    g = group(parent, "Open Carton", center)
    sx, sy, sz = size
    t = 0.006
    box(g, "Bottom", (0, -sy / 2 + t / 2, 0), (sx, t, sz), "Cardboard", bevel=0.0)
    for k in (-1, 1):
        box(g, "Side", (k * (sx / 2 - t / 2), 0, 0), (t, sy, sz), "Cardboard", bevel=0.0)
        box(g, "Side", (0, 0, k * (sz / 2 - t / 2)), (sx, sy, t), "Cardboard", bevel=0.0)
        # 外翻的上蓋
        box(g, "Flap", (k * (sx / 2 + 0.08), sy / 2 + 0.05, 0), (0.2, t, sz), "Cardboard", bevel=0.0, tilt=("z", k * 30))
    box(g, "Dunnage", (0, -0.03, 0), (sx - 0.02, 0.1, sz - 0.02), "ZoneLine", bevel=0.02)
    return g


def build():
    new_scene()
    r = root(NAME)

    t = table(r, "Packing Table", (-1.2, 0, -0.4), (1.8, 0.8, 1.0), "Steel")
    open_carton(t, (-0.3, 0.99, 0), (0.45, 0.3, 0.45))
    for i in range(4):
        box(t, "Flat Carton", (0.5, 0.79 + i * 0.02, 0), (0.6, 0.02, 0.6), "Cardboard", bevel=0.002)
    box(t, "Tape Gun Body", (0.15, 0.83, 0.32), (0.06, 0.06, 0.2), "ToolCabinet", bevel=0.015)
    cyl(t, "Tape Roll", (0.15, 0.87, 0.36), 0.1, 0.05, "LaneLine", axis="x", segments=16)
    box(t, "Scale", (-0.7, 0.79, 0.25), (0.35, 0.03, 0.35), "Steel", bevel=0.006)
    box(t, "Scale Display", (-0.7, 0.82, 0.42), (0.15, 0.06, 0.01), "Screen", bevel=0.002)
    # 桌上方的紙捲架
    for x in (-0.85, 0.85):
        box(t, "Upright", (x, 1.3, -0.45), (0.04, 1.0, 0.04), "Steel", bevel=0.004)
    box(t, "Top Bar", (0, 1.8, -0.45), (1.74, 0.04, 0.04), "Steel", bevel=0.004)
    cyl(t, "Paper Roll", (0, 1.62, -0.45), 0.2, 1.2, "ZoneLine", axis="x", segments=20, bevel=0.01)
    box(t, "Lamp", (0, 1.76, -0.3), (1.2, 0.04, 0.12), "MachineDark", bevel=0.01)
    merge(t)

    conv = group(r, "Conveyor", (1.3, 0, -0.4))
    for z in (-0.35, 0.35):
        box(conv, "Side Rail", (0, 0.68, z), (2.4, 0.16, 0.05), "Conveyor", bevel=0.008)
        box(conv, "Guide", (0, 0.82, z * 1.02), (2.4, 0.03, 0.02), "LaneLine", bevel=0.004)
    for x in (-1.1, 1.1):
        for z in (-0.3, 0.3):
            box(conv, "Leg", (x, 0.3, z), (0.06, 0.6, 0.06), "Steel", bevel=0.005)
            box(conv, "Foot", (x, 0.01, z), (0.12, 0.02, 0.12), "Rubber", bevel=0.004)
        box(conv, "Cross Brace", (x, 0.2, 0), (0.04, 0.04, 0.6), "Steel", bevel=0.004)
    for i in range(9):
        cyl(conv, "Roller", (-1.1 + i * 0.275, 0.72, 0), 0.07, 0.68, "Steel", axis="z", segments=14)
    box(conv, "Motor", (1.0, 0.45, 0.42), (0.18, 0.18, 0.2), "AccentFine", bevel=0.02)
    carton(conv, "Carton", (-0.4, 0.93, 0), (0.45, 0.35, 0.45))
    carton(conv, "Carton", (0.5, 0.93, 0), (0.45, 0.35, 0.45))
    merge(conv)
    return save_and_export(NAME)
