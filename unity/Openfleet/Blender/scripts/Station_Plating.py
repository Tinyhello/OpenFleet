"""電鍍站：四個鍍槽、龍門吊車（吊籃）與整流器櫃。"""
from ofkit import *

NAME = "Station_Plating"


def build():
    new_scene()
    r = root(NAME)

    tanks = group(r, "Tanks", (0, 0, -0.5))
    for x in (-1.8, -0.6, 0.6, 1.8):
        w, h, d, t = 1.05, 1.0, 1.4, 0.05
        box(tanks, "Tank Floor", (x, 0.05, 0), (w, 0.1, d), "TankWall", bevel=0.01)
        for k in (-1, 1):
            box(tanks, "Tank Wall", (x + k * (w / 2 - t / 2), h / 2, 0), (t, h, d), "TankWall", bevel=0.01)
            box(tanks, "Tank Wall", (x, h / 2, k * (d / 2 - t / 2)), (w, h, t), "TankWall", bevel=0.01)
        box(tanks, "Rim", (x, h + 0.015, d / 2 - 0.02), (w + 0.03, 0.03, 0.06), "MachineDark", bevel=0.006)
        box(tanks, "Rim", (x, h + 0.015, -d / 2 + 0.02), (w + 0.03, 0.03, 0.06), "MachineDark", bevel=0.006)
        box(tanks, "Liquid", (x, 0.9, 0), (w - 2 * t, 0.02, d - 2 * t), "Liquid", bevel=0.0)
        # 陽極匯流排與掛桿
        for z in (-0.45, 0.45):
            box(tanks, "Bus Bar", (x, h + 0.05, z), (w + 0.02, 0.04, 0.05), "LaneLine", bevel=0.006)
        box(tanks, "Label Plate", (x, 0.75, d / 2 + 0.003), (0.4, 0.12, 0.005), "ZoneLine", bevel=0.0)
        cyl(tanks, "Drain Valve", (x + 0.35, 0.12, d / 2 + 0.05), 0.06, 0.1, "Steel", axis="z", segments=12, bevel=0.008)
    box(tanks, "Splash Tray", (0, 0.01, 0), (4.9, 0.02, 1.6), "MachineDark", bevel=0.004)
    merge(tanks)

    gantry = group(r, "Gantry", (0, 0, -0.5))
    for x in (-2.55, 2.55):
        for z in (-0.85, 0.85):
            box(gantry, "Post", (x, 1.4, z), (0.12, 2.8, 0.12), "Steel", bevel=0.01)
            box(gantry, "Base Plate", (x, 0.01, z), (0.25, 0.02, 0.25), "Steel", bevel=0.003)
        box(gantry, "End Beam", (x, 2.8, 0), (0.14, 0.14, 1.84), "Steel", bevel=0.01)
        box(gantry, "Knee Brace", (x, 2.45, -0.6), (0.06, 0.06, 0.6), "Steel", bevel=0.005, tilt=("x", 45))
        box(gantry, "Knee Brace", (x, 2.45, 0.6), (0.06, 0.06, 0.6), "Steel", bevel=0.005, tilt=("x", -45))
    for z in (-0.85, 0.85):
        box(gantry, "Rail", (0, 2.8, z), (5.2, 0.14, 0.14), "Steel", bevel=0.01)
        box(gantry, "Rail Cap", (0, 2.885, z), (5.2, 0.03, 0.06), "RackBeam", bevel=0.004)
    box(gantry, "Hoist Beam", (0.6, 2.72, 0), (0.16, 0.16, 1.84), "RackBeam", bevel=0.012)
    for z in (-0.85, 0.85):
        box(gantry, "End Truck", (0.6, 2.9, z), (0.4, 0.12, 0.2), "RackBeam", bevel=0.012)
    box(gantry, "Hoist", (0.6, 2.45, 0), (0.3, 0.35, 0.3), "MachineDark", bevel=0.02)
    cyl(gantry, "Hoist Drum", (0.6, 2.45, 0), 0.22, 0.42, "Steel", axis="x", segments=16, bevel=0.01)
    box(gantry, "Cable", (0.6, 2.03, 0), (0.03, 0.5, 0.03), "MachineDark", bevel=0.0)
    box(gantry, "Hook Block", (0.6, 1.8, 0), (0.12, 0.08, 0.12), "LaneLine", bevel=0.01)
    basket = frame_box(gantry, "Rack Basket", (0.6, 1.55, 0), (0.6, 0.45, 0.5), 0.03, "Steel")
    for i in range(4):
        cyl(basket, "Part", (-0.2 + i * 0.13, -0.05, 0), 0.08, 0.3, "Steel", axis="z", segments=12)
    merge(gantry)

    rect = group(r, "Rectifier", (-2.3, 0, -1.7))
    box(rect, "Cabinet", (0, 0.8, 0), (0.55, 1.6, 0.4), "MachineBody", bevel=0.02)
    box(rect, "Screen", (0, 1.3, 0.202), (0.3, 0.2, 0.006), "Screen", bevel=0.002)
    for i in range(3):
        cyl(rect, "Dial", (-0.15 + i * 0.15, 1.05, 0.205), 0.06, 0.015, "MachineDark", axis="z", segments=14)
    for i in range(6):
        box(rect, "Vent", (0, 0.25 + i * 0.06, 0.201), (0.4, 0.025, 0.004), "MachineDark", bevel=0.0)
    box(rect, "Cable Tray", (0.9, 1.65, 0), (1.4, 0.06, 0.2), "Steel", bevel=0.006)
    merge(rect)
    return save_and_export(NAME)
