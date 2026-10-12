"""成品倉：兩座料架放滿打包好的出貨箱，前方是等著上架的整板成品。
區域座標與 StationModels 相同：+Z 朝走道，占地約 X ±2.8、Z −2 ~ +2。只是場景，沒有對應的後端站點。"""
from ofkit import *

NAME = "Station_FinishedGoodsStore"


def boxed_pallet(parent, name, position, layers):
    """一板成品：2x2 的出貨箱疊 layers 層，滿板加打包帶。"""
    g = group(parent, name, position)
    pallet(g, "Pallet", (0, 0.06, 0), (1.0, 0.12, 0.95))
    for layer in range(layers):
        for cx in (-0.24, 0.24):
            for cz in (-0.23, 0.23):
                carton(g, "Carton", (cx, 0.3 + layer * 0.38, cz), (0.45, 0.36, 0.44))
    top = 0.12 + layers * 0.38
    for cx in (-0.24, 0.24):
        box(g, "Strap", (cx, top + 0.004, 0), (0.025, 0.004, 0.95), "MachineDark", bevel=0.0)
        box(g, "Strap", (cx, top / 2 + 0.06, 0.478), (0.025, top - 0.12, 0.004), "MachineDark", bevel=0.0)
    return g


def rack(parent, name, position):
    g = group(parent, name, position)
    width, depth, height = 2.5, 1.1, 3.2
    for x in (-width / 2, width / 2):
        for z in (-depth / 2, depth / 2):
            box(g, "Upright", (x, height / 2, z), (0.08, height, 0.08), "TankWall", bevel=0.006)
            box(g, "Foot Plate", (x, 0.005, z), (0.16, 0.01, 0.14), "Steel", bevel=0.002)
        for i in range(4):
            y0 = 0.3 + i * 0.7
            box(g, "Brace", (x, y0 + 0.35, 0), (0.03, 0.03, 1.1), "TankWall", bevel=0.004,
                tilt=("x", 32 if i % 2 == 0 else -32))
            box(g, "Brace H", (x, y0, 0), (0.03, 0.03, depth), "TankWall", bevel=0.004)
    for i, y in enumerate((0.15, 1.2, 2.25)):
        for z in (-depth / 2, depth / 2):
            box(g, "Beam", (0, y, z), (width, 0.1, 0.06), "RackBeam", bevel=0.008)
        if i > 0:
            for k in range(6):
                box(g, "Deck Bar", (-1.1 + k * 0.44, y + 0.045, 0), (0.03, 0.02, depth), "Steel", bevel=0.003)
        for x in (-0.6, 0.6):
            boxed_pallet(g, "Slot", (x, y + 0.05, 0), 2)
    box(g, "Bay Sign", (0, 2.95, depth / 2 + 0.03), (0.6, 0.18, 0.01), "ChargerLight", bevel=0.0)
    return merge(g)


def build():
    new_scene()
    r = root(NAME)
    rack(r, "Rack A", (-1.35, 0, -0.9))
    rack(r, "Rack B", (1.35, 0, -0.9))

    # 剛從包裝送來、等著上架的成品
    merge(boxed_pallet(r, "Staged Pallet A", (-1.3, 0, 1.2), 3))
    merge(boxed_pallet(r, "Staged Pallet B", (0.1, 0, 1.2), 2))

    guard = group(r, "Column Guards")
    for x in (-2.65, -0.05, 0.05, 2.65):
        cyl(guard, "Bollard", (x, 0.4, -0.3), 0.1, 0.8, "LaneLine", segments=16, bevel=0.01)
    merge(guard)
    return save_and_export(NAME)
