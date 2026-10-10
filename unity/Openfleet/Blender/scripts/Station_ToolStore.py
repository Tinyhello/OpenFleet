"""刀具庫：兩座抽屜式刀具櫃、刀具預調儀、小型 AGV 取刀架。"""
from ofkit import *

NAME = "Station_ToolStore"


def tool_holder(parent, position, scale=1.0):
    """BT 刀把，原本是 0.09 x 0.24 的圓柱。"""
    s = scale
    g = group(parent, "Tool Holder", position)
    cyl(g, "Pull Stud", (0, -0.11 * s, 0), 0.025 * s, 0.03 * s, "Steel", segments=10)
    cyl(g, "Taper", (0, -0.06 * s, 0), 0.04 * s, 0.08 * s, "Steel", segments=16, top_diameter=0.065 * s)
    cyl(g, "Flange", (0, 0.0, 0), 0.09 * s, 0.035 * s, "MachineDark", segments=20, bevel=0.005)
    cyl(g, "Collet Nut", (0, 0.04 * s, 0), 0.055 * s, 0.045 * s, "Steel", segments=16, bevel=0.004)
    cyl(g, "Cutter", (0, 0.095 * s, 0), 0.022 * s, 0.07 * s, "MachineDark", segments=12)
    return g


def cabinet(parent, name, position):
    g = group(parent, name, position)
    box(g, "Body", (0, 1.0, 0), (1.2, 2.0, 0.8), "ToolCabinet", bevel=0.02)
    box(g, "Plinth", (0, 0.04, 0), (1.18, 0.08, 0.78), "MachineDark", bevel=0.006)
    for i in range(8):
        y = 0.2 + i * 0.22
        box(g, "Drawer Gap", (0, y, 0.401), (1.1, 0.015, 0.01), "MachineDark", bevel=0.0)
        box(g, "Handle", (0, y + 0.11, 0.42), (0.4, 0.03, 0.03), "Steel", bevel=0.008)
        box(g, "Label", (-0.4, y + 0.11, 0.402), (0.12, 0.05, 0.004), "ZoneLine", bevel=0.0)
    box(g, "Top Mat", (0, 2.005, 0), (1.15, 0.01, 0.75), "Rubber", bevel=0.002)
    cyl(g, "Lock", (0.5, 1.9, 0.405), 0.04, 0.012, "Steel", axis="z", segments=12)
    return merge(g)


def build():
    new_scene()
    r = root(NAME)
    cabinet(r, "Tool Cabinet A", (-1.6, 0, -1.0))
    cabinet(r, "Tool Cabinet B", (-0.3, 0, -1.0))

    pre = group(r, "Tool Presetter", (1.8, 0, -0.9))
    box(pre, "Bench", (0, 0.45, 0), (1.2, 0.9, 0.8), "MachineBody", bevel=0.02)
    box(pre, "Bench Door", (-0.25, 0.45, 0.401), (0.5, 0.7, 0.01), "MachineBody", bevel=0.004)
    box(pre, "Bench Top", (0, 0.915, 0), (1.22, 0.03, 0.82), "Granite", bevel=0.006)
    cyl(pre, "Spindle", (-0.3, 0.98, 0.1), 0.16, 0.1, "Steel", segments=20, bevel=0.01)
    tool_holder(pre, (-0.3, 1.15, 0.1), 0.9)
    box(pre, "Column", (-0.3, 1.3, -0.2), (0.15, 0.8, 0.15), "MachineDark", bevel=0.012)
    box(pre, "Camera Head", (-0.1, 1.6, -0.2), (0.35, 0.15, 0.15), "MachineDark", bevel=0.015)
    cyl(pre, "Lens", (0.05, 1.6, -0.1), 0.06, 0.06, "Sensor", axis="z", segments=14)
    box(pre, "Screen Arm", (0.35, 1.0, -0.25), (0.05, 0.25, 0.05), "Steel", bevel=0.006)
    box(pre, "Screen Housing", (0.35, 1.15, -0.215), (0.44, 0.34, 0.03), "MachineDark", bevel=0.008)
    box(pre, "Screen", (0.35, 1.15, -0.198), (0.4, 0.3, 0.006), "Screen", bevel=0.002)
    box(pre, "Label Printer", (0.4, 0.97, 0.22), (0.18, 0.08, 0.2), "MachineBody", bevel=0.015)
    merge(pre)

    rack = group(r, "Tool Pickup Rack", (0, 0, 1.3))
    box(rack, "Shelf", (0, 0.45, 0), (1.4, 0.9, 0.5), "Steel", bevel=0.015)
    box(rack, "Kick Plate", (0, 0.06, 0.255), (1.36, 0.1, 0.01), "LaneLine", bevel=0.0)
    box(rack, "Holder Plate", (0, 0.905, 0), (1.36, 0.02, 0.3), "AgvSmallAccent", bevel=0.004)
    for i in range(6):
        tool_holder(rack, (-0.55 + i * 0.22, 1.02, 0))
    merge(rack)
    return save_and_export(NAME)
