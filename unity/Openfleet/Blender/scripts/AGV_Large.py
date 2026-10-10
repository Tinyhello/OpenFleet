"""大型 AGV（車床物料用，頂升平台）。原點在車底中心，+Z 為車頭。
狀態燈條、標籤、載貨由 Unity 的 VehicleModels 另外產生，這裡只有車體。"""
from ofkit import *

NAME = "AGV_Large"


def build():
    new_scene()
    r = root(NAME)

    body = group(r, "Body")
    box(body, "Chassis", (0, 0.24, 0), (1.1, 0.3, 1.6), "AgvChassis", bevel=0.035, segments=3)
    # 側面檢修蓋與散熱孔
    for sx in (-1, 1):
        box(body, "Service Panel", (sx * 0.551, 0.24, 0.0), (0.006, 0.16, 0.7), "MachineDark", bevel=0.002)
        for i in range(5):
            box(body, "Vent", (sx * 0.555, 0.24, -0.16 + i * 0.08), (0.004, 0.1, 0.025), "Sensor", bevel=0.0)
    # 前方避障感測窗與後方充電接點
    box(body, "Front Sensor Window", (0, 0.3, 0.801), (0.56, 0.06, 0.012), "Sensor", bevel=0.004)
    cyl(body, "Front Light L", (-0.38, 0.3, 0.802), 0.06, 0.012, "Screen", axis="z", segments=16)
    cyl(body, "Front Light R", (0.38, 0.3, 0.802), 0.06, 0.012, "Screen", axis="z", segments=16)
    for x in (-0.12, 0.12):
        box(body, "Charge Contact", (x, 0.12, -0.795), (0.12, 0.05, 0.02), "Steel", bevel=0.005)
    # 後方急停按鈕
    for x in (-0.4, 0.4):
        cyl(body, "E-Stop Base", (x, 0.33, -0.805), 0.07, 0.02, "LaneLine", axis="z", segments=20)
        cyl(body, "E-Stop", (x, 0.33, -0.825), 0.05, 0.03, "ToolCabinet", axis="z", segments=20, bevel=0.008)

    bumpers = group(r, "Bumpers")
    for sz in (-1, 1):
        box(bumpers, "Bumper", (0, 0.18, sz * 0.82), (1.0, 0.1, 0.05), "LaneLine", bevel=0.018)
        box(bumpers, "Bumper Edge", (0, 0.105, sz * 0.83), (0.96, 0.03, 0.04), "Rubber", bevel=0.01)

    deck = group(r, "Lift Deck")
    box(deck, "Deck Plate", (0, 0.42, 0), (1.0, 0.06, 1.4), "AgvLargeDeck", bevel=0.02, segments=3)
    for x in (-0.3, 0.0, 0.3):
        box(deck, "Grip Strip", (x, 0.4505, 0), (0.06, 0.003, 1.2), "Rubber", bevel=0.0)
    for x in (-0.46, 0.46):
        for z in (-0.66, 0.66):
            cyl(deck, "Locating Pin", (x, 0.465, z), 0.035, 0.03, "Steel", segments=12, bevel=0.006)

    lidar = group(r, "Lidar", (0, 0, 0.68))
    cyl(lidar, "Base", (0, 0.465, 0), 0.15, 0.03, "Steel", segments=24, bevel=0.005)
    cyl(lidar, "Head", (0, 0.51, 0), 0.12, 0.06, "Sensor", segments=24, bevel=0.01)
    cyl(lidar, "Cap", (0, 0.545, 0), 0.13, 0.012, "AgvChassis", segments=24, bevel=0.004)

    wheels = group(r, "Wheels")
    for x in (-0.5, 0.5):
        for z in (-0.55, 0.55):
            w = group(wheels, "Wheel", (x, 0.12, z))
            cyl(w, "Tire", (0, 0, 0), 0.24, 0.1, "Rubber", axis="x", segments=28, bevel=0.02)
            cyl(w, "Hub", (0, 0, 0), 0.13, 0.104, "Steel", axis="x", segments=20, bevel=0.005)
    return save_and_export(NAME)
