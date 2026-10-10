"""出貨站：捲門月台（門、導軌、捲軸箱、月台緩衝器、調整板）與三板出貨棧板。
月台地面框線由 Unity 產生。"""
from ofkit import *

NAME = "Station_Shipping"


def build():
    new_scene()
    r = root(NAME)

    door = group(r, "Dock Door", (0, 0, -2.0))
    box(door, "Door", (0, 1.6, 0), (3.2, 3.2, 0.06), "Steel", bevel=0.005)
    for i in range(1, 10):
        box(door, "Slat", (0, i * 0.32, 0.035), (3.2, 0.03, 0.02), "MachineDark", bevel=0.004)
    box(door, "Window", (0, 1.9, 0.033), (2.4, 0.16, 0.01), "Glass", bevel=0.002)
    box(door, "Bottom Seal", (0, 0.03, 0.04), (3.2, 0.06, 0.04), "Rubber", bevel=0.01)
    for x in (-1.66, 1.66):
        box(door, "Guide", (x, 1.65, 0.04), (0.12, 3.3, 0.14), "Steel", bevel=0.01)
        for i in range(8):
            box(door, "Hazard", (x, 0.15 + i * 0.4, 0.115), (0.12, 0.2, 0.006), "LaneLine" if i % 2 == 0 else "MachineDark", bevel=0.0)
    box(door, "Frame", (0, 3.25, 0.05), (3.5, 0.15, 0.2), "LaneLine", bevel=0.01)
    box(door, "Drum Housing", (0, 3.55, 0.05), (3.5, 0.45, 0.35), "Steel", bevel=0.04, segments=3)
    box(door, "Motor", (1.9, 3.55, 0.1), (0.3, 0.3, 0.3), "MachineDark", bevel=0.02)
    box(door, "Control Box", (2.1, 1.3, 0.1), (0.25, 0.35, 0.12), "MachineBody", bevel=0.01)
    for i, mtl in enumerate(("ChargerLight", "LaneLine", "ToolCabinet")):
        cyl(door, "Button", (2.1, 1.38 - i * 0.08, 0.165), 0.04, 0.015, mtl, axis="z", segments=12)
    for x in (-1.3, 1.3):
        box(door, "Dock Bumper", (x, 0.35, 0.15), (0.25, 0.3, 0.12), "Rubber", bevel=0.02)
    box(door, "Dock Leveler", (0, 0.01, 0.65), (2.0, 0.02, 1.1), "Steel", bevel=0.005)
    box(door, "Leveler Lip", (0, 0.02, 1.22), (2.0, 0.02, 0.06), "LaneLine", bevel=0.004)
    box(door, "Traffic Light", (-2.0, 2.4, 0.1), (0.2, 0.45, 0.12), "MachineDark", bevel=0.02)
    cyl(door, "Red", (-2.0, 2.5, 0.165), 0.12, 0.02, "ToolCabinet", axis="z", segments=14)
    cyl(door, "Green", (-2.0, 2.3, 0.165), 0.12, 0.02, "ChargerLight", axis="z", segments=14)
    merge(door)

    for x, layers in ((-1.5, 3), (0.0, 2), (1.5, 1)):
        p = group(r, "Outbound Pallet", (x, 0, 0.3))
        pallet(p, "Pallet", (0, 0.07, 0), (1.1, 0.14, 1.1))
        for layer in range(layers):
            for cx in (-0.27, 0.27):
                for cz in (-0.27, 0.27):
                    carton(p, "Carton", (cx, 0.34 + layer * 0.42, cz), (0.5, 0.4, 0.5))
        # 滿板的打包帶
        if layers > 1:
            top = 0.14 + layers * 0.42
            for cx in (-0.27, 0.27):
                box(p, "Strap", (cx, top / 2 + 0.07, 0.552), (0.025, top - 0.1, 0.004), "MachineDark", bevel=0.0)
                box(p, "Strap", (cx, top / 2 + 0.07, -0.552), (0.025, top - 0.1, 0.004), "MachineDark", bevel=0.0)
                box(p, "Strap", (cx, top + 0.005, 0), (0.025, 0.004, 1.1), "MachineDark", bevel=0.0)
        merge(p)

    jack = group(r, "Pallet Jack", (2.3, 0, 1.4))
    for x in (-0.17, 0.17):
        box(jack, "Fork", (x, 0.05, 0.1), (0.16, 0.06, 1.1), "ToolCabinet", bevel=0.012)
        cyl(jack, "Fork Roller", (x, 0.04, 0.6), 0.07, 0.1, "Rubber", axis="x", segments=12)
    box(jack, "Pump Body", (0, 0.2, -0.5), (0.5, 0.3, 0.2), "ToolCabinet", bevel=0.03)
    cyl(jack, "Steer Wheel", (0, 0.09, -0.55), 0.18, 0.08, "Rubber", axis="x", segments=16, bevel=0.01)
    box(jack, "Handle", (0, 0.75, -0.72), (0.04, 0.9, 0.04), "MachineDark", bevel=0.008, tilt=("x", 15))
    box(jack, "Grip", (0, 1.18, -0.83), (0.3, 0.05, 0.05), "Rubber", bevel=0.015)
    merge(jack)
    return save_and_export(NAME)
