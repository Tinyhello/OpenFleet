"""雜物倉庫：輕型層架（料箱、紙箱）、油桶、工具櫃、空棧板與鋁梯。
區域座標與 StationModels 相同：+Z 朝走道，占地約 X ±2.6、Z −1.8 ~ +1.8。只是場景，沒有對應的後端站點。"""
from ofkit import *

NAME = "Station_MiscStore"


def shelf(parent, name, position):
    g = group(parent, name, position)
    width, depth, height = 2.0, 0.6, 2.2
    for x in (-width / 2, width / 2):
        for z in (-depth / 2, depth / 2):
            box(g, "Post", (x, height / 2, z), (0.05, height, 0.05), "MachineBody", bevel=0.004)
    for i, y in enumerate((0.2, 0.85, 1.5, 2.15)):
        box(g, "Board", (0, y, 0), (width, 0.04, depth), "Steel", bevel=0.004)
        if i == 3:
            continue
        for k, x in enumerate((-0.65, 0.0, 0.65)):
            if (i + k) % 2 == 0:
                tote(g, "Tote", (x, y + 0.17, 0), (0.55, 0.28, 0.42))
            else:
                carton(g, "Carton", (x, y + 0.22, 0), (0.5, 0.38, 0.42))
    return merge(g)


def drum(parent, position, material):
    g = group(parent, "Drum", position)
    cyl(g, "Body", (0, 0.45, 0), 0.58, 0.9, material, segments=24, bevel=0.02)
    for y in (0.3, 0.6):
        cyl(g, "Hoop", (0, y, 0), 0.6, 0.03, material, segments=24, bevel=0.008)
    cyl(g, "Lid", (0, 0.905, 0), 0.54, 0.01, "Steel", segments=24)
    cyl(g, "Bung", (0.16, 0.92, 0), 0.07, 0.03, "MachineDark", segments=12)
    return g


def build():
    new_scene()
    r = root(NAME)
    shelf(r, "Shelf A", (-1.45, 0, -1.3))
    shelf(r, "Shelf B", (0.75, 0, -1.3))

    cabinet = group(r, "Cabinet", (2.25, 0, -0.2))
    box(cabinet, "Body", (0, 0.95, 0), (0.5, 1.9, 1.0), "MachineBody", bevel=0.015)
    box(cabinet, "Door Gap", (-0.252, 0.95, 0), (0.004, 1.8, 0.01), "MachineDark", bevel=0.0)
    for z in (-0.06, 0.06):
        box(cabinet, "Handle", (-0.27, 1.0, z), (0.03, 0.22, 0.02), "Steel", bevel=0.006)
    box(cabinet, "Label", (-0.252, 1.6, 0.25), (0.004, 0.12, 0.3), "LaneLine", bevel=0.0)
    merge(cabinet)

    drums = group(r, "Drums", (-1.9, 0, 0.7))
    drum(drums, (0, 0, 0), "AccentFine")
    drum(drums, (0.68, 0, 0.05), "AccentFine")
    drum(drums, (0.34, 0, 0.62), "ToolCabinet")
    merge(drums)

    pallets = group(r, "Empty Pallets", (0.3, 0, 0.8))
    for level in range(5):
        pallet(pallets, "Pallet", (0, 0.07 + level * 0.15, 0), (1.1, 0.14, 1.1))
    merge(pallets)

    ladder = group(r, "Ladder", (1.6, 0, 0.9))
    for x in (-0.22, 0.22):
        box(ladder, "Rail", (x, 0.9, 0), (0.05, 1.85, 0.03), "Steel", bevel=0.006, tilt=("x", 14))
    for i in range(5):
        y = 0.25 + i * 0.33
        box(ladder, "Rung", (0, y, -(y - 0.9) * 0.25), (0.44, 0.03, 0.08), "Steel", bevel=0.006)
    merge(ladder)
    return save_and_export(NAME)
