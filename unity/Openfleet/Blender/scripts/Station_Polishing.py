"""拋光站：兩台雙頭拋光機（含集塵罩、風管）與工作台。"""
from ofkit import *

NAME = "Station_Polishing"


def polisher(parent, name, position):
    g = group(parent, name, position)
    box(g, "Pedestal", (0, 0.5, 0), (1.2, 1.0, 0.9), "MachineBody", bevel=0.03, segments=3, taper=(0.9, 0.9))
    box(g, "Base Plate", (0, 0.02, 0), (1.3, 0.04, 1.0), "MachineDark", bevel=0.006)
    box(g, "Stripe", (0, 0.9, 0.42), (1.1, 0.08, 0.02), "PolishWheel", bevel=0.004)
    box(g, "Access Door", (0, 0.45, 0.43), (0.6, 0.55, 0.02), "MachineBody", bevel=0.008)
    box(g, "Door Handle", (0.22, 0.45, 0.45), (0.03, 0.15, 0.03), "Steel", bevel=0.006)
    box(g, "Motor", (0, 1.2, 0), (0.5, 0.4, 0.4), "MachineDark", bevel=0.03)
    for i in range(5):
        box(g, "Fin", (0, 1.2, -0.12 + i * 0.06), (0.52, 0.42, 0.012), "MachineDark", bevel=0.0)
    cyl(g, "Shaft", (0, 1.25, 0), 0.06, 1.1, "Steel", axis="x", segments=14)
    for x in (-0.5, 0.5):
        for k, d in ((-0.035, 0.5), (0.0, 0.48), (0.035, 0.5)):
            cyl(g, "Buff", (x + k, 1.25, 0), d, 0.035, "PolishWheel", axis="x", segments=32, bevel=0.01)
        cyl(g, "Flange", (x + (0.07 if x > 0 else -0.07), 1.25, 0), 0.14, 0.02, "Steel", axis="x", segments=16, bevel=0.004)
        # 拋光輪後方半罩
        box(g, "Guard", (x, 1.32, -0.24), (0.18, 0.55, 0.08), "MachineDark", bevel=0.015)
        box(g, "Guard Top", (x, 1.56, -0.1), (0.18, 0.06, 0.3), "MachineDark", bevel=0.015)
        box(g, "Work Rest", (x, 1.02, 0.28), (0.16, 0.02, 0.12), "Steel", bevel=0.004)
    box(g, "Hood", (0, 1.6, -0.3), (1.5, 0.3, 0.45), "MachineDark", bevel=0.025)
    cyl(g, "Duct", (0, 2.35, -0.35), 0.25, 1.2, "Steel", segments=20)
    for y in (1.9, 2.3, 2.7):
        cyl(g, "Duct Ring", (0, y, -0.35), 0.27, 0.03, "Steel", segments=20, bevel=0.005)
    cyl(g, "Switch", (0.45, 0.8, 0.44), 0.06, 0.03, "ToolCabinet", axis="z", segments=12, bevel=0.005)
    return merge(g)


def build():
    new_scene()
    r = root(NAME)
    polisher(r, "Polisher L", (-1.3, 0, -0.5))
    polisher(r, "Polisher R", (1.3, 0, -0.5))
    # 兩台共用集塵主管
    duct = group(r, "Dust Main")
    cyl(duct, "Main", (0, 2.95, -0.85), 0.3, 3.4, "Steel", axis="x", segments=20)
    for x in (-1.3, 1.3):
        box(duct, "Elbow", (x, 2.95, -0.6), (0.26, 0.26, 0.5), "Steel", bevel=0.08, segments=4)
    box(duct, "Collector", (0, 1.2, -1.6), (0.9, 2.4, 0.6), "MachineBody", bevel=0.03)
    box(duct, "Collector Bin", (0, 0.25, -1.28), (0.6, 0.4, 0.05), "MachineDark", bevel=0.01)
    cyl(duct, "Riser", (0, 2.7, -1.35), 0.3, 0.6, "Steel", segments=20)
    merge(duct)

    work = table(r, "Work Table", (0, 0, 1.2), (1.2, 0.75, 0.7), "Steel")
    tote(work, "Bin", (0, 0.91, 0), (0.6, 0.26, 0.42))
    for i in range(3):
        cyl(work, "Part", (-0.12 + i * 0.12, 0.83, 0), 0.1, 0.34, "Steel", axis="z", segments=14)
    merge(work)
    return save_and_export(NAME)
