"""OpenFleet Blender 建模工具。

所有座標與尺寸都用 Unity 的區域座標（公尺，Y 朝上，+Z 為正面），
與 Assets/OpenFleet/Editor 的 ModelKit 參數一致，方便對照原本的基本幾何模型。
轉換：Unity (x, y, z) -> Blender (-x, -z, y)；FBX 以 -Z forward / Y up + Apply Transform 匯出後，
Unity 端原點、方向、比例都與原本程式組裝的模型相同。

材質名稱必須等於 Assets/OpenFleet/Materials 下的 .mat 檔名，Unity 匯入時依名稱換成專案材質。
"""
import math
import os

import bmesh
import bpy
from mathutils import Matrix, Vector

PROJECT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLEND_DIR = os.path.join(PROJECT, "Blender", "Models")
FBX_DIR = os.path.join(PROJECT, "Assets", "OpenFleet", "Models")

# 與 Palette.cs 相同：(sRGB 顏色, smoothness, metallic, emission)
PALETTE = {
    "Floor": ((0.30, 0.31, 0.33), 0.15, 0.0, None),
    "LaneLine": ((0.95, 0.76, 0.10), 0.3, 0.0, None),
    "ZoneLine": ((0.85, 0.87, 0.90), 0.3, 0.0, None),
    "MachineBody": ((0.86, 0.87, 0.89), 0.45, 0.0, None),
    "MachineDark": ((0.16, 0.17, 0.19), 0.4, 0.0, None),
    "Glass": ((0.18, 0.26, 0.32), 0.9, 0.0, None),
    "Screen": ((0.05, 0.12, 0.20), 0.8, 0.0, (0.10, 0.45, 0.80)),
    "Steel": ((0.62, 0.64, 0.67), 0.7, 0.85, None),
    "AccentRough": ((0.12, 0.55, 0.55), 0.4, 0.0, None),
    "AccentFine": ((0.16, 0.36, 0.75), 0.4, 0.0, None),
    "Furnace": ((0.33, 0.20, 0.18), 0.3, 0.0, None),
    "FurnaceGlow": ((1.00, 0.45, 0.10), 0.5, 0.0, (2.4, 0.9, 0.15)),
    "TankWall": ((0.20, 0.42, 0.30), 0.5, 0.0, None),
    "Liquid": ((0.20, 0.65, 0.70), 0.95, 0.0, (0.02, 0.12, 0.14)),
    "QuenchOil": ((0.10, 0.08, 0.05), 0.95, 0.0, None),
    "Granite": ((0.12, 0.12, 0.13), 0.6, 0.0, None),
    "PolishWheel": ((0.80, 0.70, 0.45), 0.2, 0.0, None),
    "RackUpright": ((0.12, 0.30, 0.70), 0.35, 0.0, None),
    "RackBeam": ((0.95, 0.45, 0.08), 0.35, 0.0, None),
    "Cardboard": ((0.72, 0.54, 0.32), 0.1, 0.0, None),
    "Pallet": ((0.62, 0.47, 0.30), 0.1, 0.0, None),
    "Bin": ((0.15, 0.38, 0.78), 0.35, 0.0, None),
    "ToolCabinet": ((0.78, 0.16, 0.14), 0.4, 0.0, None),
    "Conveyor": ((0.20, 0.21, 0.23), 0.3, 0.0, None),
    "ChargerPad": ((0.18, 0.40, 0.65), 0.3, 0.0, None),
    "ChargerLight": ((0.20, 0.70, 1.00), 0.5, 0.0, (0.30, 1.20, 2.00)),
    "AgvChassis": ((0.18, 0.19, 0.21), 0.45, 0.0, None),
    "AgvLargeDeck": ((0.96, 0.58, 0.10), 0.45, 0.0, None),
    "AgvSmallBody": ((0.90, 0.91, 0.93), 0.5, 0.0, None),
    "AgvSmallAccent": ((0.10, 0.45, 0.85), 0.45, 0.0, None),
    "AgvUnknown": ((0.55, 0.35, 0.65), 0.4, 0.0, None),
    "Rubber": ((0.05, 0.05, 0.05), 0.2, 0.0, None),
    "Sensor": ((0.03, 0.03, 0.04), 0.9, 0.0, None),
}

SHARP_ANGLE = math.radians(35.0)


def U(x, y, z):
    """Unity 區域座標 -> Blender 座標。"""
    return Vector((-x, -z, y))


def _srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def mat(name):
    m = bpy.data.materials.get(name)
    if m is not None:
        return m
    color, smooth, metal, emission = PALETTE[name]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")  # 節點名稱會被介面語言翻譯
    lin = [_srgb_to_linear(c) for c in color]
    bsdf.inputs["Base Color"].default_value = (*lin, 1.0)
    bsdf.inputs["Roughness"].default_value = 1.0 - smooth
    bsdf.inputs["Metallic"].default_value = metal
    if emission:
        strength = max(emission)
        bsdf.inputs["Emission Color"].default_value = (*[e / strength for e in emission], 1.0)
        bsdf.inputs["Emission Strength"].default_value = strength
    m.diffuse_color = (*lin, 1.0)
    return m


def new_scene():
    """清空目前檔案的物件與資料（不重新載入 UI，避免中斷 MCP 連線）。"""
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for coll in list(bpy.data.collections):
        bpy.data.collections.remove(coll)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.cameras, bpy.data.lights, bpy.data.images):
        for item in list(block):
            block.remove(item)
    bpy.data.orphans_purge(do_recursive=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0


def group(parent, name, position=(0.0, 0.0, 0.0)):
    obj = bpy.data.objects.new(name, None)
    obj.empty_display_size = 0.2
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = parent
    obj.location = U(*position)
    return obj


def _finish(parent, name, bm, center, material, smooth=True):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat(material))
    if smooth:
        me.shade_smooth()
        me.set_sharp_from_angle(angle=SHARP_ANGLE)
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = parent
    obj.location = U(*center)
    return obj


def _bevel(bm, width, segments):
    if width <= 0:
        return
    bmesh.ops.bevel(bm, geom=list(bm.edges) + list(bm.verts), offset=width, offset_type="OFFSET",
                    segments=segments, profile=0.5, affect="EDGES", clamp_overlap=True)


_AXIS = {"x": "X", "y": "Z", "z": "Y"}


def _tilt(bm, tilt):
    """tilt=(Unity 軸, 角度)。只用在斜撐、斜板這類對稱物件，不在意左右手座標的正負號。"""
    if tilt:
        axis, degrees = tilt
        bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(degrees), 3, _AXIS[axis]))


def box(parent, name, center, size, material, bevel=0.01, segments=2, taper=None, tilt=None):
    """center / size 為 Unity 座標（完整尺寸）。taper=(sx, sz) 讓頂面縮小，做斜面外殼。"""
    sx, sy, sz = size
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * sx, v.co.y * sz, v.co.z * sy))
        if taper and v.co.z > 0:
            v.co.x *= taper[0]
            v.co.y *= taper[1]
    _bevel(bm, min(bevel, min(size) * 0.45), segments)
    _tilt(bm, tilt)
    return _finish(parent, name, bm, center, material)


def cyl(parent, name, center, diameter, height, material, axis="y", segments=24, bevel=0.0, top_diameter=None):
    """axis 為 Unity 軸向（'x'/'y'/'z'），與 ModelKit.Cylinder 的 euler 對應：(0,0,90)->'x'，(90,0,0)->'z'。"""
    bm = bmesh.new()
    r2 = (top_diameter if top_diameter is not None else diameter) / 2.0
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments,
                          radius1=diameter / 2.0, radius2=r2, depth=height)
    if bevel > 0:
        bm.normal_update()
        caps = [e for e in bm.edges if any(abs(f.normal.z) > 0.5 for f in e.link_faces)]
        bmesh.ops.bevel(bm, geom=caps, offset=min(bevel, height * 0.45), offset_type="OFFSET",
                        segments=2, profile=0.5, affect="EDGES", clamp_overlap=True)
    if axis == "x":
        bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / 2, 3, "Y"))
    elif axis == "z":
        bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / 2, 3, "X"))
    return _finish(parent, name, bm, center, material)


def tube(parent, name, center, outer, inner, height, material, axis="y", segments=24):
    """中空圓管（例如料筒、管道開口）。"""
    bm = bmesh.new()
    bmesh.ops.create_circle(bm, cap_ends=False, segments=segments, radius=outer / 2.0)
    outer_ring = list(bm.edges)
    bmesh.ops.create_circle(bm, cap_ends=False, segments=segments, radius=inner / 2.0)
    bmesh.ops.bridge_loops(bm, edges=list(bm.edges))
    faces = list(bm.faces)
    ret = bmesh.ops.extrude_face_region(bm, geom=faces)
    verts = [e for e in ret["geom"] if isinstance(e, bmesh.types.BMVert)]
    bmesh.ops.translate(bm, verts=verts, vec=(0, 0, height))
    bmesh.ops.translate(bm, verts=bm.verts, vec=(0, 0, -height / 2.0))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    if axis == "x":
        bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / 2, 3, "Y"))
    elif axis == "z":
        bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / 2, 3, "X"))
    return _finish(parent, name, bm, center, material)


def frame_box(parent, name, center, size, bar, material):
    """只有 12 條邊的框架（例如籠子、龍門）。"""
    g = group(parent, name, center)
    sx, sy, sz = size
    hx, hy, hz = sx / 2 - bar / 2, sy / 2 - bar / 2, sz / 2 - bar / 2
    for x in (-hx, hx):
        for z in (-hz, hz):
            box(g, "Post", (x, 0, z), (bar, sy, bar), material, bevel=0.004)
    for y in (-hy, hy):
        for z in (-hz, hz):
            box(g, "Rail X", (0, y, z), (sx - bar * 2, bar, bar), material, bevel=0.004)
        for x in (-hx, hx):
            box(g, "Rail Z", (x, y, 0), (bar, bar, sz - bar * 2), material, bevel=0.004)
    return g


def table(parent, name, center, size, material):
    """與 StationModels.Table 相同尺寸的工作台，加上下層橫撐。"""
    g = group(parent, name, center)
    sx, sy, sz = size
    box(g, "Top", (0, sy - 0.03, 0), (sx, 0.06, sz), material, bevel=0.008)
    lx, lz = sx / 2 - 0.05, sz / 2 - 0.05
    for x in (-lx, lx):
        for z in (-lz, lz):
            box(g, "Leg", (x, (sy - 0.06) / 2, z), (0.05, sy - 0.06, 0.05), material, bevel=0.004)
    box(g, "Shelf", (0, 0.15, 0), (sx - 0.1, 0.03, sz - 0.1), material, bevel=0.004)
    return g


def tote(parent, name, center, size, material="Bin"):
    """開口料箱：底板 + 四面牆 + 上緣。center 為整個料箱的中心。"""
    g = group(parent, name, center)
    sx, sy, sz = size
    t = 0.015
    box(g, "Bottom", (0, -sy / 2 + t / 2, 0), (sx - 0.02, t, sz - 0.02), material, bevel=0.003)
    for k in (-1, 1):
        box(g, "Wall", (k * (sx / 2 - t / 2), 0, 0), (t, sy, sz), material, bevel=0.004, taper=(1.0, 1.0))
        box(g, "Wall", (0, 0, k * (sz / 2 - t / 2)), (sx, sy, t), material, bevel=0.004)
        box(g, "Handle Slot", (k * (sx / 2 + 0.001), sy / 2 - 0.05, 0), (0.004, 0.03, sz * 0.35), "MachineDark", bevel=0.0)
    for k in (-1, 1):
        box(g, "Rim", (k * (sx / 2 + 0.005), sy / 2 - 0.01, 0), (0.012, 0.02, sz + 0.02), material, bevel=0.004)
        box(g, "Rim", (0, sy / 2 - 0.01, k * (sz / 2 + 0.005)), (sx + 0.02, 0.02, 0.012), material, bevel=0.004)
    return g


def pallet(parent, name, center, size):
    """木托盤：上層 5 片面板、3 條墊木、下層 3 片底板。center 為托盤中心，size 為外框尺寸。"""
    g = group(parent, name, center)
    sx, sy, sz = size
    board = sy * 0.18
    block = sy - board * 2
    for i in range(5):
        x = (i - 2) * (sx - 0.1) / 4
        box(g, "Top Board", (x, sy / 2 - board / 2, 0), (0.1 if 0 < i < 4 else 0.12, board, sz), "Pallet", bevel=0.004)
    for z in (-sz / 2 + 0.05, 0, sz / 2 - 0.05):
        box(g, "Stringer", (0, 0, z), (sx, block, 0.1), "Pallet", bevel=0.004)
        box(g, "Bottom Board", (0, -sy / 2 + board / 2, z), (sx, board, 0.12), "Pallet", bevel=0.004)
    return g


def carton(parent, name, center, size, tape=True):
    g = group(parent, name, center)
    sx, sy, sz = size
    box(g, "Box", (0, 0, 0), size, "Cardboard", bevel=0.006)
    if tape:
        box(g, "Tape", (0, sy / 2 + 0.001, 0), (0.05, 0.003, sz + 0.004), "LaneLine", bevel=0.0)
        box(g, "Tape", (0, 0, sz / 2 + 0.001), (0.05, sy * 0.4, 0.003), "LaneLine", bevel=0.0)
        box(g, "Tape", (0, 0, -sz / 2 - 0.001), (0.05, sy * 0.4, 0.003), "LaneLine", bevel=0.0)
    return g


def merge(group_obj, name=None):
    """把群組底下所有網格合併成一個物件（多材質保留），減少 Unity 端物件數。"""
    meshes = [o for o in group_obj.children_recursive if o.type == "MESH"]
    if not meshes:
        return None
    layer = bpy.context.view_layer
    layer.update()  # 新建物件的 matrix_world 要更新後才正確
    for o in meshes:
        world = o.matrix_world.copy()
        o.parent = group_obj
        o.matrix_world = world
    layer.update()
    active = meshes[0]
    with bpy.context.temp_override(active_object=active, object=active,
                                   selected_objects=meshes, selected_editable_objects=meshes):
        bpy.ops.object.join()
    for o in list(group_obj.children_recursive):
        if o.type == "EMPTY":
            bpy.data.objects.remove(o, do_unlink=True)
    # 合併後的物件取代群組：接到群組的父物件下，原點維持在群組位置。
    layer.update()
    final_name = name or group_obj.name
    parent, offset = group_obj.parent, group_obj.matrix_world.copy()
    active.data.transform(offset.inverted() @ active.matrix_world)
    bpy.data.objects.remove(group_obj, do_unlink=True)
    active.parent = parent
    active.matrix_world = offset
    active.name = final_name
    active.data.name = final_name
    return active


def root(name):
    return group(None, name)


def save_and_export(name):
    """一個模型一個 .blend；同名 FBX 匯出到 Unity 專案。"""
    os.makedirs(BLEND_DIR, exist_ok=True)
    os.makedirs(FBX_DIR, exist_ok=True)
    blend_path = os.path.join(BLEND_DIR, name + ".blend")
    fbx_path = os.path.join(FBX_DIR, name + ".fbx")
    bpy.ops.wm.save_as_mainfile(filepath=blend_path, compress=True)
    bpy.ops.export_scene.fbx(
        filepath=fbx_path,
        use_selection=False,
        object_types={"EMPTY", "MESH"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z",
        axis_up="Y",
        bake_space_transform=True,
        use_mesh_modifiers=True,
        mesh_smooth_type="OFF",
        use_tspace=False,
        add_leaf_bones=False,
        bake_anim=False,
        path_mode="STRIP",
        embed_textures=False,
    )
    stats = {"objects": len([o for o in bpy.data.objects if o.type == "MESH"]),
             "tris": sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in bpy.data.objects if o.type == "MESH")}
    return {"blend": blend_path, "fbx": fbx_path, **stats}


def preview(tilt=62.0, yaw=-35.0):
    """把 3D 視窗切到材質預覽並框住整個模型（yaw=-35 從車頭右前上方看）。"""
    from mathutils import Euler
    for win in bpy.context.window_manager.windows:
        for area in win.screen.areas:
            if area.type != "VIEW_3D":
                continue
            sp = area.spaces.active
            sp.shading.type = "MATERIAL"
            sp.overlay.show_extras = False
            sp.overlay.show_relationship_lines = False
            r3 = sp.region_3d
            r3.view_perspective = "PERSP"
            r3.view_rotation = Euler((math.radians(tilt), 0, math.radians(yaw)), "XYZ").to_quaternion()
            region = next(r for r in area.regions if r.type == "WINDOW")
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.view3d.view_all()
    return {"preview": True}
