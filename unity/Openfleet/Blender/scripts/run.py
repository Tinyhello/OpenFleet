"""在 Blender（Scripting 或 MCP）執行：

    exec(open(r"D:/Portfolio/OpenFleet/unity/Openfleet/Blender/scripts/run.py", encoding="utf-8").read())
    build("AGV_Large")      # 單一模型
    build_all()             # 全部重建

每個模型一個 .blend（Blender/Models/<名稱>.blend），同名 FBX 輸出到 Assets/OpenFleet/Models。
"""
import importlib
import os
import runpy
import sys

_SCRIPTS = r"D:/Portfolio/OpenFleet/unity/Openfleet/Blender/scripts"
_KIT = os.path.dirname(_SCRIPTS)
for _p in (_KIT, _SCRIPTS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

MODELS = [
    "AGV_Large", "AGV_Small", "AGV_Unknown",
    "Station_Cutting", "Station_RawMaterialStore", "Station_CncLathe", "Station_HeatTreatment", "Station_Plating",
    "Station_Polishing", "Station_QualityInspection", "Station_Packing", "Station_Shipping",
    "Station_ToolStore", "Station_FinishedGoodsStore", "Station_MiscStore", "ChargingBay",
]


def build(name):
    import ofkit
    importlib.reload(ofkit)
    return runpy.run_path(os.path.join(_SCRIPTS, name + ".py"))["build"]()


def build_all():
    return {name: build(name) for name in MODELS}
