"""AGV 充電車位（充電維護區有三個）。原點在車位中心地面，+Z 朝走道，充電樁在 -Z 側。
區域框線與文字標籤由 Unity 產生。"""
from ofkit import *

NAME = "ChargingBay"


def build():
    new_scene()
    r = root(NAME)

    pad = group(r, "Pad")
    box(pad, "Pad", (0, 0.02, 0.2), (1.6, 0.04, 2.2), "ChargerPad", bevel=0.01)
    for x in (-0.75, 0.75):
        box(pad, "Edge", (x, 0.042, 0.2), (0.06, 0.004, 2.2), "LaneLine", bevel=0.0)
        box(pad, "Wheel Guide", (x * 0.85, 0.07, -0.3), (0.06, 0.06, 1.0), "Steel", bevel=0.01, taper=(0.7, 1.0))
    for i in range(4):
        box(pad, "Arrow", (0, 0.042, 0.9 - i * 0.35), (0.25, 0.004, 0.08), "ZoneLine", bevel=0.0)
    merge(pad)

    charger = group(r, "Charger")
    box(charger, "Base", (0, 0.05, -1.3), (0.6, 0.1, 0.45), "MachineDark", bevel=0.01)
    box(charger, "Body", (0, 0.65, -1.3), (0.5, 1.1, 0.35), "MachineBody", bevel=0.03, segments=3)
    box(charger, "Top", (0, 1.21, -1.3), (0.52, 0.04, 0.37), "ChargerPad", bevel=0.01)
    box(charger, "Light", (0, 0.95, -1.12), (0.3, 0.08, 0.02), "ChargerLight", bevel=0.004)
    box(charger, "Screen", (0, 0.78, -1.123), (0.22, 0.14, 0.006), "Screen", bevel=0.002)
    for i in range(5):
        box(charger, "Vent", (0.251, 0.45 + i * 0.05, -1.3), (0.004, 0.02, 0.22), "MachineDark", bevel=0.0)
    cyl(charger, "E-Stop", (0.15, 1.05, -1.12), 0.045, 0.02, "ToolCabinet", axis="z", segments=12, bevel=0.005)
    # 地面充電接點：刷片與導引斜面
    box(charger, "Contact", (0, 0.15, -1.05), (0.3, 0.1, 0.15), "Steel", bevel=0.01)
    for x in (-0.08, 0.08):
        box(charger, "Brush", (x, 0.15, -0.97), (0.05, 0.06, 0.03), "LaneLine", bevel=0.006)
    box(charger, "Cable Conduit", (0, 0.6, -1.5), (0.08, 1.2, 0.06), "MachineDark", bevel=0.01)
    merge(charger)
    return save_and_export(NAME)
