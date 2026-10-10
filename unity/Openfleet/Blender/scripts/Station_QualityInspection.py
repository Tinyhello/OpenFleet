"""品管站：三次元量床（CMM）與量測電腦桌。"""
from ofkit import *

NAME = "Station_QualityInspection"


def build():
    new_scene()
    r = root(NAME)

    cmm = group(r, "CMM", (-0.5, 0, -0.6))
    box(cmm, "Base", (0, 0.35, 0), (2.2, 0.7, 1.3), "MachineDark", bevel=0.02)
    for x in (-0.9, 0.9):
        for z in (-0.5, 0.5):
            cyl(cmm, "Isolator", (x, 0.04, z), 0.18, 0.08, "Rubber", segments=16, bevel=0.01)
    box(cmm, "Granite", (0, 0.82, 0), (2.6, 0.25, 1.7), "Granite", bevel=0.015)
    for i in range(5):
        box(cmm, "T-Slot", (-0.8 + i * 0.4, 0.946, 0), (0.02, 0.004, 1.4), "MachineDark", bevel=0.0)
    box(cmm, "Y Rail", (-1.15, 0.97, 0), (0.12, 0.05, 1.6), "Steel", bevel=0.006)
    for x in (-1.15, 1.15):
        box(cmm, "Post", (x, 1.5, 0.1), (0.15, 1.1, 0.22), "MachineBody", bevel=0.02, taper=(0.85, 0.9))
        box(cmm, "Air Bearing", (x, 0.98, 0.1), (0.22, 0.06, 0.3), "MachineDark", bevel=0.008)
    box(cmm, "Bridge", (0, 2.1, 0.1), (2.5, 0.2, 0.28), "MachineBody", bevel=0.02)
    box(cmm, "Scale", (0, 2.1, 0.242), (2.3, 0.03, 0.004), "Steel", bevel=0.0)
    box(cmm, "Carriage", (0.3, 2.1, 0.1), (0.3, 0.3, 0.36), "AccentFine", bevel=0.02)
    box(cmm, "Quill", (0.3, 1.65, 0.1), (0.1, 0.7, 0.1), "Steel", bevel=0.008)
    cyl(cmm, "Probe Head", (0.3, 1.25, 0.1), 0.07, 0.1, "MachineDark", segments=16, bevel=0.01)
    cyl(cmm, "Stylus", (0.3, 1.14, 0.1), 0.012, 0.12, "Steel", segments=8)
    cyl(cmm, "Ruby", (0.3, 1.075, 0.1), 0.02, 0.02, "ToolCabinet", segments=10, bevel=0.009)
    cyl(cmm, "Fixture", (0.3, 0.97, 0.1), 0.35, 0.05, "Steel", segments=20, bevel=0.006)
    cyl(cmm, "Part", (0.3, 1.02, 0.1), 0.25, 0.15, "Steel", segments=24, bevel=0.01)
    box(cmm, "Controller", (1.45, 0.45, -0.3), (0.3, 0.6, 0.4), "MachineBody", bevel=0.015)
    merge(cmm)

    desk = group(r, "Desk", (1.9, 0, 0.6))
    box(desk, "Top", (0, 0.77, 0), (0.9, 0.04, 0.6), "MachineBody", bevel=0.008)
    box(desk, "Drawer Unit", (0.28, 0.37, 0), (0.32, 0.74, 0.56), "MachineBody", bevel=0.01)
    for i in range(3):
        box(desk, "Drawer Handle", (0.28, 0.2 + i * 0.22, 0.285), (0.15, 0.015, 0.015), "Steel", bevel=0.003)
    box(desk, "Leg", (-0.42, 0.37, 0), (0.04, 0.74, 0.56), "MachineBody", bevel=0.006)
    box(desk, "Monitor", (0, 1.05, -0.1), (0.6, 0.4, 0.04), "MachineDark", bevel=0.01)
    box(desk, "Screen", (0, 1.05, -0.077), (0.54, 0.34, 0.006), "Screen", bevel=0.002)
    box(desk, "Monitor Neck", (0, 0.86, -0.14), (0.05, 0.16, 0.03), "MachineDark", bevel=0.005)
    box(desk, "Monitor Foot", (0, 0.795, -0.12), (0.22, 0.012, 0.16), "MachineDark", bevel=0.004)
    box(desk, "Keyboard", (0, 0.8, 0.1), (0.42, 0.02, 0.14), "MachineDark", bevel=0.004)
    box(desk, "Mouse", (0.3, 0.8, 0.1), (0.05, 0.02, 0.08), "MachineDark", bevel=0.008)
    # 椅子
    chair = group(desk, "Chair", (-0.1, 0, 0.6))
    box(chair, "Seat", (0, 0.48, 0), (0.45, 0.07, 0.45), "MachineDark", bevel=0.025)
    box(chair, "Back", (0, 0.82, 0.22), (0.42, 0.5, 0.05), "MachineDark", bevel=0.025)
    cyl(chair, "Column", (0, 0.27, 0), 0.05, 0.38, "Steel", segments=10)
    box(chair, "Base", (0, 0.05, 0), (0.55, 0.03, 0.06), "MachineDark", bevel=0.01)
    box(chair, "Base", (0, 0.05, 0), (0.06, 0.03, 0.55), "MachineDark", bevel=0.01)
    merge(desk)
    return save_and_export(NAME)
