"""粗加工、精加工站：一台小型 CNC 車床（有外罩），前方兩張台子：備料台在 +X、成品台在 -X。
P7 第 1b 步起每台車床一站，所以模型只畫一台車床；站的區域寬 3.2 m（FactorySceneBuilder）。
區域座標與 StationModels 相同：+Z 朝走道，占地約 X ±1.6、Z −2.4 ~ +2.2。
粗加工與精加工共用：強調色材質名稱為 AccentRough，精加工站由 Unity 換成 AccentFine。
台上的料箱（有料時才顯示）與文字標籤由 Unity 產生。"""
from ofkit import *

NAME = "Station_CncLathe"
ACCENT = "AccentRough"

# 車床的中心（站的原點在 X 0）；Z 往後退 0.55 m。
LATHES = (("Lathe", 0.0),)
LATHE_Z = -0.55

# 備料台在 +X、成品台在 -X（與 StationModels 的台子位置相同）。
TABLES = (("Inbound Table", 0.62), ("Outbound Table", -0.62))
TABLE_Z = 1.3
TABLE_SIZE = (0.95, 0.7, 0.8)


def lathe(r, name, x):
    m = group(r, name, (x, 0, LATHE_Z))
    box(m, "Plinth", (0, 0.05, 0), (1.94, 0.1, 1.36), "MachineDark", bevel=0.01)
    box(m, "Base", (0, 0.47, 0), (1.9, 0.74, 1.34), "MachineBody", bevel=0.03, segments=3)
    box(m, "Enclosure", (-0.07, 1.42, -0.01), (1.72, 1.16, 1.3), "MachineBody", bevel=0.04, segments=3)
    box(m, "Roof", (-0.07, 2.025, 0), (1.72, 0.05, 1.34), ACCENT, bevel=0.012)
    box(m, "Stripe", (0, 0.8, 0.675), (1.9, 0.08, 0.02), ACCENT, bevel=0.004)
    # 滑門：門板、觀察窗、上方軌道、把手
    box(m, "Door Rail", (-0.1, 1.92, 0.665), (1.5, 0.04, 0.04), "Steel", bevel=0.006)
    box(m, "Door", (-0.32, 1.38, 0.66), (0.95, 0.9, 0.03), "MachineBody", bevel=0.01)
    box(m, "Door Window", (-0.32, 1.42, 0.677), (0.75, 0.6, 0.01), "Glass", bevel=0.004)
    box(m, "Door Frame", (-0.32, 1.81, 0.675), (0.9, 0.06, 0.03), ACCENT, bevel=0.005)
    box(m, "Door Handle", (0.18, 1.38, 0.7), (0.04, 0.4, 0.04), "Steel", bevel=0.01)
    # 操作面板（嵌在門右側）
    box(m, "Panel Housing", (0.55, 1.42, 0.665), (0.42, 0.72, 0.06), "MachineDark", bevel=0.015)
    box(m, "Screen", (0.55, 1.58, 0.697), (0.32, 0.24, 0.008), "Screen", bevel=0.002)
    box(m, "Keypad", (0.55, 1.33, 0.697), (0.32, 0.16, 0.008), "Steel", bevel=0.002)
    cyl(m, "E-Stop", (0.66, 1.16, 0.705), 0.05, 0.03, "ToolCabinet", axis="z", segments=16, bevel=0.006)
    cyl(m, "Feed Knob", (0.44, 1.16, 0.705), 0.05, 0.025, "MachineBody", axis="z", segments=16, bevel=0.005)
    # 側面散熱孔
    for i in range(5):
        box(m, "Vent", (0.951, 0.4 + i * 0.07, -0.3), (0.006, 0.03, 0.5), "MachineDark", bevel=0.0)
    # 後方的鐵屑車與冷卻液箱（靠牆）
    cart = group(m, "Chip Cart", (-0.45, 0, -0.95))
    box(cart, "Bin", (0, 0.28, 0), (0.5, 0.34, 0.4), "Steel", bevel=0.01, taper=(1.1, 1.1))
    for cx in (-0.18, 0.18):
        for cz in (-0.13, 0.13):
            cyl(cart, "Caster", (cx, 0.05, cz), 0.08, 0.03, "Rubber", axis="x", segments=12)
    box(m, "Chip Chute", (-0.45, 0.62, -0.75), (0.36, 0.08, 0.3), "MachineDark", bevel=0.01, tilt=("x", -30))
    box(m, "Coolant Tank", (0.45, 0.3, -0.95), (0.7, 0.5, 0.32), "MachineDark", bevel=0.015)
    cyl(m, "Coolant Pump", (0.65, 0.65, -0.95), 0.14, 0.18, "Steel", segments=16, bevel=0.01)
    # 三色警示燈（屋頂右後方）
    tower = group(m, "Signal Tower", (0.62, 2.05, -0.45))
    cyl(tower, "Pole", (0, 0.15, 0), 0.03, 0.3, "Steel", segments=10)
    for i, mtl in enumerate(("ToolCabinet", "LaneLine", "ChargerLight")):
        cyl(tower, "Lamp", (0, 0.34 + i * 0.085, 0), 0.075, 0.08, mtl, segments=16, bevel=0.005)
    cyl(tower, "Cap", (0, 0.6, 0), 0.08, 0.02, "MachineDark", segments=16, bevel=0.005)
    return merge(m)


def build():
    new_scene()
    r = root(NAME)
    for name, x in LATHES:
        lathe(r, name, x)
    # 機邊的備料台、成品台：位置與 StationModels 相同（Unity 在同位置放料箱與標籤）。
    for name, x in TABLES:
        t = table(r, name, (x, 0, TABLE_Z), TABLE_SIZE, "Steel")
        box(t, "Edge Stripe", (0, 0.672, 0.401), (TABLE_SIZE[0], 0.04, 0.005), ACCENT, bevel=0.0)
        merge(t)
    return save_and_export(NAME)
