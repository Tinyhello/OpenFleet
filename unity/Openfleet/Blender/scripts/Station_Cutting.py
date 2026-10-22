"""切料站：臥式帶鋸機，右側滾筒進料台上放著一根 Φ40 圓棒，左側出料滑槽；前方是機邊的圓棒台與備料台。
區域座標與 StationModels 相同：+Z 朝走道，占地約 X ±2.8、Z −2 ~ +2。
台上的料（有料時才顯示）與文字標籤由 Unity 產生；圓棒台在右（x = 2.1）、備料台在左（x = -2.1），和車床的備料台／成品台同一個位置。"""
from ofkit import *

NAME = "Station_Cutting"
ACCENT = "RackBeam"


def band_saw(r):
    m = group(r, "Band Saw", (-0.3, 0, -0.6))
    # 底座機箱與鐵屑盤
    box(m, "Plinth", (0, 0.05, 0), (1.9, 0.1, 1.0), "MachineDark", bevel=0.01)
    box(m, "Base", (0, 0.45, 0), (1.8, 0.7, 0.95), "MachineBody", bevel=0.03, segments=3)
    box(m, "Chip Tray", (0, 0.83, 0.05), (1.7, 0.06, 0.85), "MachineDark", bevel=0.01)
    box(m, "Base Stripe", (0, 0.72, 0.48), (1.8, 0.06, 0.02), ACCENT, bevel=0.004)
    # 工作台與虎鉗（夾住圓棒）
    box(m, "Bed", (0, 0.9, 0), (1.4, 0.08, 0.6), "Steel", bevel=0.01)
    box(m, "Vise Fixed", (0.15, 1.02, -0.18), (0.18, 0.16, 0.12), "MachineDark", bevel=0.01)
    box(m, "Vise Moving", (0.15, 1.02, 0.18), (0.18, 0.16, 0.12), "MachineDark", bevel=0.01)
    cyl(m, "Vise Screw", (0.15, 1.02, 0.36), 0.04, 0.24, "Steel", axis="z", segments=12)
    # 鋸弓：兩個帶輪外殼、上橫樑、鋸帶護蓋，整組微微抬起
    head = group(m, "Saw Head", (0, 0, 0))
    box(head, "Bow Beam", (0, 1.55, -0.15), (1.6, 0.22, 0.26), ACCENT, bevel=0.03, segments=3)
    for x in (-0.68, 0.68):
        cyl(head, "Wheel Housing", (x, 1.32, -0.12), 0.62, 0.3, ACCENT, axis="z", segments=32, bevel=0.02)
        cyl(head, "Wheel Cap", (x, 1.32, 0.04), 0.3, 0.02, "MachineDark", axis="z", segments=24)
    box(head, "Blade Guard", (0, 1.12, -0.12), (1.0, 0.1, 0.12), "LaneLine", bevel=0.01)
    box(head, "Blade", (0, 1.06, -0.12), (0.9, 0.02, 0.005), "Steel", bevel=0.0)
    # 油壓缸與樞軸
    cyl(m, "Hydraulic Cylinder", (-0.85, 1.05, -0.38), 0.08, 0.6, "Steel", segments=12, bevel=0.005)
    cyl(m, "Pivot", (0.85, 1.2, -0.38), 0.12, 0.2, "MachineDark", axis="z", segments=16)
    # 冷卻液管
    cyl(m, "Coolant Hose", (0.25, 1.25, 0.0), 0.03, 0.4, "Rubber", segments=8)
    # 操作箱
    panel = group(m, "Control Box", (1.15, 0, 0.35))
    cyl(panel, "Stand", (0, 0.6, 0), 0.06, 1.2, "Steel", segments=10)
    box(panel, "Housing", (0, 1.35, 0), (0.4, 0.5, 0.22), "MachineDark", bevel=0.02, segments=3)
    box(panel, "Screen", (0, 1.45, 0.113), (0.3, 0.18, 0.008), "Screen", bevel=0.002)
    cyl(panel, "E-Stop", (0.11, 1.22, 0.12), 0.05, 0.03, "ToolCabinet", axis="z", segments=16, bevel=0.006)
    return merge(m)


def infeed(r):
    """右側滾筒進料台：一根長圓棒從這裡送進鋸床。"""
    g = group(r, "Infeed Conveyor", (1.55, 0, -0.6))
    for x in (-0.55, 0.55):
        for z in (-0.2, 0.2):
            box(g, "Leg", (x, 0.42, z), (0.06, 0.84, 0.06), "MachineDark", bevel=0.005)
    for z in (-0.24, 0.24):
        box(g, "Rail", (0, 0.86, z), (1.3, 0.08, 0.04), ACCENT, bevel=0.006)
    for i in range(6):
        cyl(g, "Roller", (-0.55 + i * 0.22, 0.9, 0), 0.08, 0.44, "Steel", axis="z", segments=14)
    cyl(g, "Bar", (-0.3, 0.96, 0), 0.04 * 1.6, 1.9, "Steel", axis="x", segments=18, bevel=0.004)
    cyl(g, "Bar End", (0.651, 0.96, 0), 0.05, 0.004, "MachineDark", axis="x", segments=18)
    return merge(g)


def outfeed(r):
    """左側出料滑槽：切好的備料滑進鐵箱。"""
    g = group(r, "Outfeed Chute", (-1.55, 0, -0.6))
    box(g, "Chute", (0.1, 0.78, 0), (0.6, 0.04, 0.36), "Steel", bevel=0.006, tilt=("z", -18))
    box(g, "Catch Box", (-0.2, 0.3, 0), (0.5, 0.5, 0.5), "MachineDark", bevel=0.015, taper=(1.05, 1.05))
    for i in range(3):
        cyl(g, "Blank", (-0.32 + i * 0.12, 0.58, 0.05 * (i - 1)), 0.064, 0.1, "Steel", axis="x", segments=14)
    return merge(g)


def bar_rack(r):
    """後方的圓棒架：幾根長圓棒橫放在兩支 A 字架上，表示待切的原料。"""
    g = group(r, "Bar Rack", (0.2, 0, -1.65))
    for x in (-1.6, 1.6):
        box(g, "Upright", (x, 0.45, 0), (0.08, 0.9, 0.08), "RackUpright", bevel=0.006)
        box(g, "Foot", (x, 0.03, 0), (0.1, 0.06, 0.5), "RackUpright", bevel=0.006)
        box(g, "Arm", (x, 0.55, 0.12), (0.06, 0.05, 0.3), "RackUpright", bevel=0.004)
    for i in range(5):
        cyl(g, "Stock Bar", (0, 0.6 + (i % 2) * 0.055, 0.04 + i * 0.05), 0.064, 3.6, "Steel", axis="x", segments=14)
    return merge(g)


def build():
    new_scene()
    r = root(NAME)
    band_saw(r)
    infeed(r)
    outfeed(r)
    bar_rack(r)
    # 圓棒台（待切）與備料台（切好、等搬走）：位置與 StationModels 相同。
    for name, x in (("Inbound Table", 2.1), ("Outbound Table", -2.1)):
        t = table(r, name, (x, 0, 1.3), (1.0, 0.7, 0.8), "Steel")
        box(t, "Edge Stripe", (0, 0.672, 0.401), (1.0, 0.04, 0.005), ACCENT, bevel=0.0)
        merge(t)
    return save_and_export(NAME)
