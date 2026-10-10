"""未設定車型的 AGV：通用車體，紫色外殼提醒設定缺漏。原點在車底中心，+Z 為車頭。"""
from ofkit import *

NAME = "AGV_Unknown"


def build():
    new_scene()
    r = root(NAME)

    body = group(r, "Body")
    box(body, "Shell", (0, 0.25, 0), (0.8, 0.4, 1.1), "AgvUnknown", bevel=0.05, segments=3)
    box(body, "Top Panel", (0, 0.452, -0.05), (0.66, 0.008, 0.8), "AgvChassis", bevel=0.004)
    box(body, "Skirt", (0, 0.07, 0), (0.78, 0.06, 1.08), "AgvChassis", bevel=0.01)
    box(body, "Front Sensor Window", (0, 0.3, 0.551), (0.4, 0.06, 0.01), "Sensor", bevel=0.003)
    for sz in (-1, 1):
        box(body, "Bumper", (0, 0.13, sz * 0.565), (0.7, 0.07, 0.04), "Rubber", bevel=0.015)
    cyl(body, "E-Stop Base", (0.28, 0.33, -0.552), 0.06, 0.012, "LaneLine", axis="z", segments=16)
    cyl(body, "E-Stop", (0.28, 0.33, -0.566), 0.042, 0.022, "ToolCabinet", axis="z", segments=16, bevel=0.006)

    lidar = group(r, "Lidar", (0, 0, 0.45))
    cyl(lidar, "Base", (0, 0.465, 0), 0.13, 0.02, "Steel", segments=20, bevel=0.004)
    cyl(lidar, "Head", (0, 0.5, 0), 0.11, 0.05, "Sensor", segments=20, bevel=0.008)
    cyl(lidar, "Cap", (0, 0.528, 0), 0.115, 0.01, "AgvChassis", segments=20, bevel=0.003)

    wheels = group(r, "Wheels")
    for x in (-0.38, 0.38):
        for z in (-0.38, 0.38):
            w = group(wheels, "Wheel", (x, 0.09, z))
            cyl(w, "Tire", (0, 0, 0), 0.18, 0.08, "Rubber", axis="x", segments=24, bevel=0.016)
            cyl(w, "Hub", (0, 0, 0), 0.09, 0.084, "Steel", axis="x", segments=16, bevel=0.004)
    return save_and_export(NAME)
