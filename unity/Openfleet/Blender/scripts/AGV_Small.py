"""小型 AGV（刀具配送，頂部刀把架）。原點在車底中心，+Z 為車頭。
狀態燈條、標籤由 Unity 的 VehicleModels 另外產生。"""
from ofkit import *

NAME = "AGV_Small"


def tool_holder(parent, position):
    """BT 刀把：拉桿 + 錐柄 + 法蘭 + 刀身，總高約 0.14 m（對應原本的 0.07 x 0.14 圓柱）。"""
    g = group(parent, "Tool Holder", position)
    cyl(g, "Socket", (0, -0.065, 0), 0.08, 0.02, "AgvChassis", segments=16, bevel=0.003)
    cyl(g, "Taper", (0, -0.03, 0), 0.03, 0.05, "Steel", segments=16, top_diameter=0.05)
    cyl(g, "Flange", (0, 0.005, 0), 0.07, 0.02, "MachineDark", segments=20, bevel=0.004)
    cyl(g, "Collet", (0, 0.03, 0), 0.04, 0.03, "Steel", segments=16, top_diameter=0.03)
    cyl(g, "Cutter", (0, 0.06, 0), 0.02, 0.03, "MachineDark", segments=12)


def build():
    new_scene()
    r = root(NAME)

    body = group(r, "Body")
    box(body, "Shell", (0, 0.2, 0), (0.55, 0.26, 0.8), "AgvSmallBody", bevel=0.04, segments=3)
    box(body, "Skirt", (0, 0.085, 0), (0.53, 0.05, 0.78), "AgvChassis", bevel=0.01)
    box(body, "Rim", (0, 0.34, 0), (0.56, 0.03, 0.81), "AgvSmallAccent", bevel=0.012)
    box(body, "Front Sensor Window", (0, 0.23, 0.401), (0.32, 0.05, 0.01), "Sensor", bevel=0.003)
    box(body, "Front Bumper", (0, 0.12, 0.405), (0.45, 0.05, 0.03), "Rubber", bevel=0.01)
    box(body, "Rear Bumper", (0, 0.12, -0.405), (0.45, 0.05, 0.03), "Rubber", bevel=0.01)
    cyl(body, "E-Stop Base", (-0.18, 0.25, -0.402), 0.05, 0.012, "LaneLine", axis="z", segments=16)
    cyl(body, "E-Stop", (-0.18, 0.25, -0.415), 0.035, 0.02, "ToolCabinet", axis="z", segments=16, bevel=0.005)
    for sx in (-1, 1):
        for i in range(3):
            box(body, "Vent", (sx * 0.276, 0.17, 0.12 + i * 0.05), (0.004, 0.06, 0.02), "Sensor", bevel=0.0)

    tray = group(r, "Tool Tray")
    box(tray, "Tray", (0, 0.37, -0.06), (0.45, 0.03, 0.52), "Steel", bevel=0.006)
    for sx in (-1, 1):
        box(tray, "Lip", (sx * 0.22, 0.4, -0.06), (0.012, 0.03, 0.52), "Steel", bevel=0.003)
    for sz in (-1, 1):
        box(tray, "Lip", (0, 0.4, -0.06 + sz * 0.254), (0.45, 0.03, 0.012), "Steel", bevel=0.003)
    for ix in range(2):
        for iz in range(3):
            tool_holder(tray, (-0.1 + ix * 0.2, 0.45, -0.22 + iz * 0.16))

    lidar = group(r, "Lidar", (0, 0, 0.32))
    cyl(lidar, "Base", (0, 0.38, 0), 0.11, 0.02, "Steel", segments=20, bevel=0.004)
    cyl(lidar, "Head", (0, 0.41, 0), 0.09, 0.04, "Sensor", segments=20, bevel=0.008)
    cyl(lidar, "Cap", (0, 0.433, 0), 0.095, 0.008, "AgvSmallAccent", segments=20, bevel=0.003)

    wheels = group(r, "Wheels")
    for x in (-0.26, 0.26):
        for z in (-0.27, 0.27):
            w = group(wheels, "Wheel", (x, 0.08, z))
            cyl(w, "Tire", (0, 0, 0), 0.16, 0.06, "Rubber", axis="x", segments=24, bevel=0.014)
            cyl(w, "Hub", (0, 0, 0), 0.08, 0.064, "AgvSmallAccent", axis="x", segments=16, bevel=0.004)
    return save_and_export(NAME)
