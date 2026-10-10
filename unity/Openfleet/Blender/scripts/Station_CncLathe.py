"""CNC 車床（有外罩）加上機邊備料台、成品台。粗加工與精加工共用：
強調色材質名稱為 AccentRough，精加工站由 Unity 換成 AccentFine。
台上的料箱（有料時才顯示）與文字標籤由 Unity 產生。"""
from ofkit import *

NAME = "Station_CncLathe"
ACCENT = "AccentRough"


def lathe(r):
    m = group(r, "CNC Lathe", (-0.4, 0, -0.5))
    box(m, "Plinth", (0, 0.05, 0), (3.42, 0.1, 1.72), "MachineDark", bevel=0.01)
    box(m, "Base", (0, 0.5, 0), (3.4, 0.8, 1.7), "MachineBody", bevel=0.03, segments=3)
    box(m, "Enclosure", (-0.2, 1.6, -0.02), (3.0, 1.4, 1.66), "MachineBody", bevel=0.04, segments=3)
    box(m, "Roof", (-0.2, 2.33, 0), (3.0, 0.06, 1.7), ACCENT, bevel=0.015)
    box(m, "Stripe", (0, 0.95, 0.86), (3.4, 0.1, 0.02), ACCENT, bevel=0.005)
    # 滑門：門板、觀察窗、上方軌道、把手
    box(m, "Door Rail", (-0.2, 2.18, 0.86), (2.9, 0.05, 0.04), "Steel", bevel=0.008)
    box(m, "Door", (-0.6, 1.6, 0.855), (1.7, 1.1, 0.03), "MachineBody", bevel=0.01)
    box(m, "Door Window", (-0.6, 1.64, 0.872), (1.4, 0.78, 0.01), "Glass", bevel=0.004)
    box(m, "Door Frame", (-0.6, 2.1, 0.87), (1.6, 0.08, 0.03), ACCENT, bevel=0.006)
    box(m, "Door Handle", (0.2, 1.62, 0.9), (0.05, 0.5, 0.05), "Steel", bevel=0.012)
    for y in (1.4, 1.84):
        box(m, "Handle Mount", (0.2, y, 0.88), (0.04, 0.04, 0.03), "Steel", bevel=0.005)
    # 側面檢修門與散熱孔
    box(m, "Side Door", (-1.71, 1.55, 0), (0.02, 1.0, 1.2), "MachineBody", bevel=0.006)
    box(m, "Side Window", (-1.722, 1.65, 0), (0.01, 0.5, 0.8), "Glass", bevel=0.003)
    for i in range(6):
        box(m, "Vent", (1.701, 0.45 + i * 0.07, -0.4), (0.006, 0.03, 0.6), "MachineDark", bevel=0.0)
    # 排屑機（斜升段）與鐵屑車
    box(m, "Chip Conveyor", (-1.95, 0.3, -0.2), (0.5, 0.6, 1.2), "MachineDark", bevel=0.02)
    box(m, "Chip Riser", (-1.98, 0.75, 0.25), (0.44, 0.12, 0.7), "MachineDark", bevel=0.015, tilt=("x", 40))
    cart = group(m, "Chip Cart", (-1.95, 0, 0.85))
    box(cart, "Bin", (0, 0.3, 0), (0.5, 0.36, 0.4), "Steel", bevel=0.01, taper=(1.1, 1.1))
    for x in (-0.18, 0.18):
        for z in (-0.13, 0.13):
            cyl(cart, "Caster", (x, 0.05, z), 0.08, 0.03, "Rubber", axis="x", segments=12)
    # 冷卻液箱
    box(m, "Coolant Tank", (0.6, 0.35, -1.05), (1.2, 0.6, 0.35), "MachineDark", bevel=0.015)
    cyl(m, "Coolant Pump", (1.0, 0.75, -1.05), 0.16, 0.2, "Steel", segments=16, bevel=0.01)
    # 三色警示燈
    tower = group(m, "Signal Tower", (1.2, 0.9, -0.55))
    cyl(tower, "Pole", (0, 0.25, 0), 0.03, 0.5, "Steel", segments=10)
    for i, mtl in enumerate(("ToolCabinet", "LaneLine", "ChargerLight")):
        cyl(tower, "Lamp", (0, 0.55 + i * 0.09, 0), 0.08, 0.085, mtl, segments=16, bevel=0.005)
    cyl(tower, "Cap", (0, 0.83, 0), 0.085, 0.02, "MachineDark", segments=16, bevel=0.005)
    # 操作面板（懸臂）
    panel = group(m, "Control Panel", (1.55, 0, 0.55))
    box(panel, "Arm", (0, 2.1, -0.2), (0.08, 0.08, 0.45), "Steel", bevel=0.01)
    box(panel, "Housing", (0, 1.55, 0), (0.55, 0.9, 0.25), "MachineDark", bevel=0.025, segments=3)
    box(panel, "Screen", (0, 1.72, 0.128), (0.42, 0.32, 0.01), "Screen", bevel=0.003)
    box(panel, "Keypad", (0, 1.4, 0.128), (0.42, 0.2, 0.008), "Steel", bevel=0.002)
    cyl(panel, "E-Stop", (0.17, 1.2, 0.135), 0.05, 0.03, "ToolCabinet", axis="z", segments=16, bevel=0.006)
    cyl(panel, "Feed Knob", (-0.12, 1.2, 0.135), 0.05, 0.025, "MachineBody", axis="z", segments=16, bevel=0.005)
    return merge(m)


def build():
    new_scene()
    r = root(NAME)
    lathe(r)
    # 備料台、成品台：位置與 StationModels 相同（Unity 在同位置放料箱與標籤）。
    for name, x in (("Inbound Table", 2.1), ("Outbound Table", -2.1)):
        t = table(r, name, (x, 0, 1.3), (1.0, 0.7, 0.8), "Steel")
        box(t, "Edge Stripe", (0, 0.672, 0.401), (1.0, 0.04, 0.005), ACCENT, bevel=0.0)
        merge(t)
    return save_and_export(NAME)
