"""原料倉：兩座選料架（棒材捆、紙箱）與出庫暫存托盤。
區域座標與 StationModels 相同：+Z 朝走道，占地約 X ±2.8、Z −2 ~ +2。暫存區地面框線由 Unity 產生。"""
from ofkit import *

NAME = "Station_RawMaterialStore"


def bar_bundle(parent, base):
    """一捆棒材（4+3 根）加兩條綁帶，與原本 BarBundle 相同排列。"""
    g = group(parent, "Bar Bundle", base)
    d = 0.13
    for row in range(2):
        for i in range(4 - row):
            x = (i - (3 - row) / 2) * d
            y = d / 2 + row * d * 0.87
            cyl(g, "Bar", (x, y, 0), d, 0.9, "Steel", axis="z", segments=16, bevel=0.006)
            cyl(g, "Bar End", (x, y, 0.451), d * 0.8, 0.004, "MachineDark", axis="z", segments=16)
    for z in (-0.28, 0.28):
        box(g, "Strap", (0, 0.12, z), (0.56, 0.26, 0.025), "AccentFine", bevel=0.01)
    return g


def rack(parent, name, position):
    g = group(parent, name, position)
    width, depth, height = 2.5, 1.1, 3.2
    frame = group(g, "Frame")
    for x in (-width / 2, width / 2):
        for z in (-depth / 2, depth / 2):
            box(frame, "Upright", (x, height / 2, z), (0.08, height, 0.08), "RackUpright", bevel=0.006)
            box(frame, "Foot Plate", (x, 0.005, z), (0.16, 0.01, 0.14), "Steel", bevel=0.002)
        # 側框斜撐（Z 字形）
        for i in range(4):
            y0 = 0.3 + i * 0.7
            box(frame, "Brace", (x, y0 + 0.35, 0), (0.03, 0.03, 1.1), "RackUpright", bevel=0.004,
                tilt=("x", 32 if i % 2 == 0 else -32))
            box(frame, "Brace H", (x, y0, 0), (0.03, 0.03, depth), "RackUpright", bevel=0.004)
    for i, y in enumerate((0.15, 1.2, 2.25)):
        for z in (-depth / 2, depth / 2):
            box(frame, "Beam", (0, y, z), (width, 0.1, 0.06), "RackBeam", bevel=0.008)
        if i > 0:
            for k in range(6):
                box(frame, "Deck Bar", (-1.1 + k * 0.44, y + 0.045, 0), (0.03, 0.02, depth), "Steel", bevel=0.003)
        for x in (-0.6, 0.6):
            slot = group(g, "Slot", (x, y + 0.05, 0))
            pallet(slot, "Pallet", (0, 0.06, 0), (1.0, 0.12, 0.95))
            if i < 2:
                bar_bundle(slot, (0, 0.12, 0))
            else:
                carton(slot, "Carton", (0, 0.37, 0), (0.8, 0.5, 0.8))
    box(frame, "Safety Sign", (0, 2.95, depth / 2 + 0.03), (0.6, 0.18, 0.01), "LaneLine", bevel=0.0)
    return merge(g)


def build():
    new_scene()
    r = root(NAME)
    rack(r, "Rack A", (-1.35, 0, -0.9))
    rack(r, "Rack B", (1.35, 0, -0.9))

    staged = group(r, "Staged Pallet", (-1.2, 0, 1.3))
    pallet(staged, "Pallet", (0, 0.07, 0), (1.0, 0.14, 1.0))
    bar_bundle(staged, (0, 0.14, 0))
    merge(staged)

    # 地面的柱腳護欄
    guard = group(r, "Column Guards")
    for x in (-2.65, -0.05, 0.05, 2.65):
        cyl(guard, "Bollard", (x, 0.4, -0.3), 0.1, 0.8, "LaneLine", segments=16, bevel=0.01)
    merge(guard)
    return save_and_export(NAME)
