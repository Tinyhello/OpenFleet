"""出貨站：三板出貨棧板與手動拖板車。成品從西牆的工廠大門運出去，這一站沒有自己的門。
地面框線由 Unity 產生。"""
from ofkit import *

NAME = "Station_Shipping"


def build():
    new_scene()
    r = root(NAME)

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
