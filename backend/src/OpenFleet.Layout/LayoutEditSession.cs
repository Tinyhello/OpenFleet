using System;
using System.Collections.Generic;
using System.Linq;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>
    /// 沙盒編輯的編輯模型：Unity 只負責輸入與繪製，所有對佈局文件的修改都經過這個類別，
    /// 所以規則（對齊、代碼、自動接路、復原）都能用單元測試驗證。
    ///
    /// 約定：
    /// - 建構時複製一份來源文件當草稿（<see cref="Document"/>）；呼叫端只讀、不可以直接修改。
    ///   復原與重做會換掉草稿物件，所以每次操作後都要重新讀 <see cref="Document"/>，不要留著舊的參考。
    /// - 每次成功改到文件的操作，先把操作前的狀態推進復原堆疊（最多 <see cref="MaxHistory"/> 步，最舊的丟掉）並清掉重做堆疊；
    ///   失敗或沒有改到東西的操作（回傳 false 或 null）不留紀錄。
    /// - 連續的 <see cref="SetFloorSize"/>（拖滑桿）合成一步復原，直到出現別的操作（含復原、重做、<see cref="MarkSaved"/>）為止。
    /// - 廠房形狀的操作（<see cref="MoveFloorVertex"/>、<see cref="MoveFloorWall"/>、<see cref="InsertFloorVertex"/>、
    ///   <see cref="RemoveFloorVertex"/>、<see cref="MoveGate"/>）每個成功的操作一步復原。第一次改形狀時，舊文件的矩形
    ///   （沒有 Outline、Gate）會先寫成 Outline 與 Gate 再修改；之後 Min／Max 一律是外框。大門跟著它所在的那段牆走。
    ///   模組與路網不會跟著移動，超出廠房由驗證報錯。
    /// - 新增、移動不會因為重疊或超出廠房而拒絕：使用者可以刻意做出錯誤，由 <see cref="Validate"/> 回報；
    ///   拖曳時的紅綠框線用 <see cref="CanPlace"/>。（Claude 暫定）
    /// </summary>
    public sealed class LayoutEditSession
    {
        /// <summary>復原步數上限（使用者決定）。</summary>
        public const int MaxHistory = 50;

        /// <summary>模組擺放的對齊格距（公尺，使用者決定）。</summary>
        public const double ModuleGrid = 0.2;

        /// <summary>廠房大小滑桿的步距（公尺，使用者決定）。</summary>
        public const double FloorStep = 0.4;

        /// <summary>廠房頂點、加轉角、大門的對齊格距（公尺，Claude 暫定，和模組相同）。</summary>
        public const double FloorVertexGrid = LayoutRules.FloorVertexGrid;

        private readonly List<FactoryLayoutDocument> _undo = new List<FactoryLayoutDocument>();
        private readonly List<FactoryLayoutDocument> _redo = new List<FactoryLayoutDocument>();
        private FactoryLayoutDocument _document;
        private string? _canonical;
        private string _savedCanonical;
        private bool _floorDrag;

        /// <summary>以 <paramref name="source"/> 的複本開始編輯（不會改到來源）。</summary>
        public LayoutEditSession(FactoryLayoutDocument source)
        {
            _document = LayoutDocuments.Clone(source);
            _savedCanonical = Canonical;
        }

        /// <summary>目前的草稿（只讀）。復原、重做之後是另一個物件。</summary>
        public FactoryLayoutDocument Document => _document;

        /// <summary>草稿和上次儲存（或建構）時的狀態不同。復原回到儲存時的狀態就不再是 true。</summary>
        public bool IsDirty => Canonical != _savedCanonical;

        /// <summary>目前的狀態視為已儲存。</summary>
        public void MarkSaved()
        {
            _savedCanonical = Canonical;
            _floorDrag = false;
        }

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        public bool Undo()
        {
            if (_undo.Count == 0)
            {
                return false;
            }

            _floorDrag = false;
            _redo.Add(_document);
            _document = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _canonical = null;
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0)
            {
                return false;
            }

            _floorDrag = false;
            _undo.Add(_document);
            _document = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _canonical = null;
            return true;
        }

        /// <summary>某種模組目前的數量。</summary>
        public int CountOf(ModuleKind kind) => _document.Modules.Count(m => m.Kind == kind);

        /// <summary>能不能再新增這種模組：倉儲站已經有了不行；模組總數上限 120；AGV 車位數量受車輛上限 20 限制。</summary>
        public bool CanAdd(ModuleKind kind)
        {
            if (_document.Modules.Count >= LayoutRules.MaxModules)
            {
                return false;
            }

            if (ModuleCatalog.IsBay(kind) && _document.Modules.Count(m => ModuleCatalog.IsBay(m.Kind)) >= LayoutRules.MaxVehicles)
            {
                return false;
            }

            return ModuleCodes.Next(_document, kind) != null;
        }

        /// <summary>能不能刪除：模組存在，而且不是倉儲站（倉儲站只能移動、旋轉）。</summary>
        public bool CanRemove(string code)
        {
            var module = Find(code);
            return module != null && !ModuleCatalog.IsFacility(module.Kind);
        }

        /// <summary>
        /// 新增模組：位置對齊 0.2 m 格點、取最小沒用過的代碼、自動接路。朝向要是 90 的倍數（會取 0～359）。
        /// 不能新增（見 <see cref="CanAdd"/>）或朝向不合法時回傳 null。重疊與超出廠房不擋（見類別說明）。
        /// </summary>
        public LayoutModule? AddModule(ModuleKind kind, double x, double z, int rotation)
        {
            if (!TryNormalizeRotation(rotation, out var normalized) || !CanAdd(kind))
            {
                return null;
            }

            var before = LayoutDocuments.Clone(_document);
            var module = new LayoutModule
            {
                Code = ModuleCodes.Next(_document, kind)!,
                Kind = kind,
                X = Snap(x, ModuleGrid),
                Z = Snap(z, ModuleGrid),
                Rotation = normalized,
            };
            _document.Modules.Add(module);
            RoadGraphEditor.ConnectModule(_document, module);
            Commit(before, false);
            return module;
        }

        /// <summary>移動模組到新位置（對齊 0.2 m 格點），重新接路。位置沒變或找不到模組時回傳 false。</summary>
        public bool MoveModule(string code, double x, double z)
        {
            var module = Find(code);
            if (module == null)
            {
                return false;
            }

            var newX = Snap(x, ModuleGrid);
            var newZ = Snap(z, ModuleGrid);
            if (newX == module.X && newZ == module.Z)
            {
                return false;
            }

            var before = LayoutDocuments.Clone(_document);
            var oldModule = LayoutDocuments.Clone(module);
            module.X = newX;
            module.Z = newZ;
            RoadGraphEditor.ReconnectModule(_document, oldModule, module);
            return Commit(before, false);
        }

        /// <summary>順時針旋轉 90 度（0、90、180、270 循環），重新接路。找不到模組時回傳 false。</summary>
        public bool RotateModule(string code)
        {
            var module = Find(code);
            if (module == null)
            {
                return false;
            }

            var before = LayoutDocuments.Clone(_document);
            var oldModule = LayoutDocuments.Clone(module);
            module.Rotation = (module.Rotation + 90) % 360;
            RoadGraphEditor.ReconnectModule(_document, oldModule, module);
            return Commit(before, false);
        }

        /// <summary>刪除模組並拆掉它的接路。倉儲站不能刪（回傳 false）。</summary>
        public bool RemoveModule(string code)
        {
            if (!CanRemove(code))
            {
                return false;
            }

            var before = LayoutDocuments.Clone(_document);
            RoadGraphEditor.RemoveModule(_document, code);
            return Commit(before, false);
        }

        /// <summary>
        /// 設定廠房大小：以目前的中心往兩邊縮放，寬與深對齊 0.4 m 並限制在規則的範圍內，座標取到 0.01 m。
        /// 有外牆頂點（Outline）時，頂點以外框中心等比縮放到新的寬與深，大門跟著縮放（還在同一段牆的同一個相對位置）。
        /// 大小沒變時回傳 false。連續呼叫（拖滑桿）合成一步復原。不管模組是否還在廠房內（驗證會回報）。
        /// </summary>
        public bool SetFloorSize(double width, double depth)
        {
            var newWidth = Clamp(Snap(width, FloorStep), LayoutRules.MinFloorWidth, LayoutRules.MaxFloorWidth);
            var newDepth = Clamp(Snap(depth, FloorStep), LayoutRules.MinFloorDepth, LayoutRules.MaxFloorDepth);
            var floor = _document.Floor;
            var hasOutline = floor.Outline != null && floor.Outline.Count > 0;
            var bounds = hasOutline
                ? LayoutFloorShape.Bounds(floor.Outline!)
                : new LayoutRect(floor.MinX, floor.MaxX, floor.MinZ, floor.MaxZ);
            var oldWidth = bounds.MaxX - bounds.MinX;
            var oldDepth = bounds.MaxZ - bounds.MinZ;
            var centerX = (bounds.MinX + bounds.MaxX) / 2;
            var centerZ = (bounds.MinZ + bounds.MaxZ) / 2;
            var scaleX = oldWidth > 0 ? newWidth / oldWidth : 1;
            var scaleZ = oldDepth > 0 ? newDepth / oldDepth : 1;

            double minX;
            double maxX;
            double minZ;
            double maxZ;
            if (hasOutline)
            {
                if (Math.Abs(newWidth - oldWidth) < 1e-6 && Math.Abs(newDepth - oldDepth) < 1e-6)
                {
                    return false;
                }

                minX = double.NaN;
                maxX = double.NaN;
                minZ = double.NaN;
                maxZ = double.NaN;
            }
            else
            {
                minX = RoadGraphEditor.Round(centerX - newWidth / 2);
                maxX = RoadGraphEditor.Round(centerX + newWidth / 2);
                minZ = RoadGraphEditor.Round(centerZ - newDepth / 2);
                maxZ = RoadGraphEditor.Round(centerZ + newDepth / 2);
                if (minX == floor.MinX && maxX == floor.MaxX && minZ == floor.MinZ && maxZ == floor.MaxZ)
                {
                    return false;
                }
            }

            var before = LayoutDocuments.Clone(_document);
            if (hasOutline)
            {
                foreach (var point in floor.Outline!)
                {
                    point.X = RoadGraphEditor.Round(centerX + (point.X - centerX) * scaleX);
                    point.Z = RoadGraphEditor.Round(centerZ + (point.Z - centerZ) * scaleZ);
                }

                UpdateBounds(floor);
            }
            else
            {
                floor.MinX = minX;
                floor.MaxX = maxX;
                floor.MinZ = minZ;
                floor.MaxZ = maxZ;
            }

            if (floor.Gate != null)
            {
                floor.Gate.X = RoadGraphEditor.Round(centerX + (floor.Gate.X - centerX) * scaleX);
                floor.Gate.Z = RoadGraphEditor.Round(centerZ + (floor.Gate.Z - centerZ) * scaleZ);
            }

            var coalesce = _floorDrag;
            var result = Commit(before, coalesce);
            _floorDrag = true;
            return result;
        }

        /// <summary>外牆頂點（複本，順序同文件；舊文件回傳 Min／Max 的矩形）。</summary>
        public List<LayoutPoint> FloorOutline() => LayoutFloorShape.Outline(_document.Floor);

        /// <summary>外牆段數（等於頂點數）。</summary>
        public int FloorWallCount => LayoutFloorShape.WallCount(_document.Floor);

        /// <summary>廠房面積（平方公尺）。</summary>
        public double FloorArea => LayoutFloorShape.Area(_document.Floor);

        /// <summary>大門中心（複本；舊文件是最西邊那段牆的中點）。</summary>
        public LayoutPoint GateCenter() => LayoutFloorShape.Gate(_document.Floor);

        /// <summary>大門所在的牆編號；大門不在任何牆上回傳 -1。</summary>
        public int GateWallIndex => LayoutFloorShape.GateWallIndex(_document.Floor);

        /// <summary>能不能刪轉角：至少要留 3 個。</summary>
        public bool CanRemoveFloorVertex => FloorWallCount > 3;

        /// <summary>能不能再加轉角：頂點數上限見 <see cref="LayoutRules.MaxFloorVertices"/>。</summary>
        public bool CanInsertFloorVertex => FloorWallCount < LayoutRules.MaxFloorVertices;

        /// <summary>
        /// 移動第 <paramref name="index"/> 個頂點到 (x, z)（對齊 0.2 m 格點）。大門在相鄰兩段牆上時跟著走。
        /// 編號不對、座標不是有限數字或位置沒變時回傳 false。
        /// </summary>
        public bool MoveFloorVertex(int index, double x, double z)
        {
            var outline = FloorOutline();
            if (index < 0 || index >= outline.Count || !IsFinite(x, z))
            {
                return false;
            }

            var gate = GateCenter();
            var follow = GateFollow(outline, gate);
            var n = outline.Count;
            outline[index].X = Snap(x, FloorVertexGrid);
            outline[index].Z = Snap(z, FloorVertexGrid);
            if (follow != null && (follow.Index == index || follow.Index == (index + n - 1) % n))
            {
                gate = GatePointOn(outline, follow);
            }

            return ApplyFloor(outline, gate);
        }

        /// <summary>
        /// 整段牆沿它的法線方向平移：拖曳向量 (dx, dz) 投影到法線上，對齊 0.2 m；牆兩端的頂點一起移動，
        /// 相鄰兩段牆跟著伸縮。大門在這段牆或相鄰的牆上時跟著走。編號不對、長度為 0 的牆或平移量對齊後是 0 時回傳 false。
        /// </summary>
        public bool MoveFloorWall(int index, double dx, double dz)
        {
            var outline = FloorOutline();
            if (index < 0 || index >= outline.Count || !IsFinite(dx, dz))
            {
                return false;
            }

            var a = FloorGeometry.WallStart(outline, index);
            var b = FloorGeometry.WallEnd(outline, index);
            var length = LayoutGeometry.Distance(a, b);
            if (length <= 0)
            {
                return false;
            }

            // 法線取行進方向的右手邊；正負只影響拖曳向量投影的符號，結果不受影響。
            var nx = (b.Z - a.Z) / length;
            var nz = -(b.X - a.X) / length;
            var offset = Snap(dx * nx + dz * nz, FloorVertexGrid);
            if (offset == 0)
            {
                return false;
            }

            var gate = GateCenter();
            var follow = GateFollow(outline, gate);
            var n = outline.Count;
            var first = outline[index];
            var second = outline[(index + 1) % n];
            first.X = RoadGraphEditor.Round(first.X + nx * offset);
            first.Z = RoadGraphEditor.Round(first.Z + nz * offset);
            second.X = RoadGraphEditor.Round(second.X + nx * offset);
            second.Z = RoadGraphEditor.Round(second.Z + nz * offset);
            if (follow != null && (follow.Index == index || follow.Index == (index + n - 1) % n || follow.Index == (index + 1) % n))
            {
                gate = GatePointOn(outline, follow);
            }

            return ApplyFloor(outline, gate);
        }

        /// <summary>
        /// 在第 <paramref name="wall"/> 段牆上加一個轉角：(x, z) 投影到牆上，沿牆對齊 0.2 m，把牆切成兩段。
        /// 大門座標不變（落在包含它的那一段）。編號不對、頂點數已達上限、或對齊後落在牆的端點時回傳 false。
        /// </summary>
        public bool InsertFloorVertex(int wall, double x, double z)
        {
            var outline = FloorOutline();
            if (wall < 0 || wall >= outline.Count || !IsFinite(x, z) || outline.Count >= LayoutRules.MaxFloorVertices)
            {
                return false;
            }

            var length = FloorGeometry.WallLength(outline, wall);
            var hit = FloorGeometry.ProjectOnWall(outline, wall, x, z);
            var along = Snap(hit.Along, FloorVertexGrid);
            if (length <= 0 || along <= 1e-6 || along >= length - 1e-6)
            {
                return false;
            }

            var point = FloorGeometry.PointOnWall(outline, wall, along / length);
            var created = new LayoutPoint { X = RoadGraphEditor.Round(point.X), Z = RoadGraphEditor.Round(point.Z) };
            var start = FloorGeometry.WallStart(outline, wall);
            var end = FloorGeometry.WallEnd(outline, wall);
            if (LayoutGeometry.SamePoint((created.X, created.Z), start, 1e-9) || LayoutGeometry.SamePoint((created.X, created.Z), end, 1e-9))
            {
                return false;
            }

            outline.Insert(wall + 1, created);
            return ApplyFloor(outline, GateCenter());
        }

        /// <summary>
        /// 刪除第 <paramref name="index"/> 個頂點（相鄰兩段牆併成一段）。至少留 3 個頂點，編號不對時回傳 false。
        /// 大門在被併掉的兩段牆上時，投影到新外牆上最近的點。
        /// </summary>
        public bool RemoveFloorVertex(int index)
        {
            var outline = FloorOutline();
            if (index < 0 || index >= outline.Count || outline.Count <= 3)
            {
                return false;
            }

            var gate = GateCenter();
            var follow = GateFollow(outline, gate);
            var n = outline.Count;
            outline.RemoveAt(index);
            if (follow != null && (follow.Index == index || follow.Index == (index + n - 1) % n))
            {
                var hit = FloorGeometry.NearestWall(outline, gate.X, gate.Z)!;
                gate = new LayoutPoint { X = RoadGraphEditor.Round(hit.X), Z = RoadGraphEditor.Round(hit.Z) };
            }

            return ApplyFloor(outline, gate);
        }

        /// <summary>
        /// 移動大門：(x, z) 投影到最近的一段牆，沿牆對齊 0.2 m（從牆的起點量起）。
        /// 不會因為牆太短或太靠牆端而拒絕，由驗證的 gate-invalid 回報。位置沒變或座標不是有限數字時回傳 false。
        /// </summary>
        public bool MoveGate(double x, double z)
        {
            var outline = FloorOutline();
            if (!IsFinite(x, z) || outline.Count == 0)
            {
                return false;
            }

            var hit = FloorGeometry.NearestWall(outline, x, z)!;
            var length = FloorGeometry.WallLength(outline, hit.Index);
            var along = length <= 0 ? 0 : Clamp(Snap(hit.Along, FloorVertexGrid), 0, length);
            var point = length <= 0 ? FloorGeometry.WallStart(outline, hit.Index) : FloorGeometry.PointOnWall(outline, hit.Index, along / length);
            var gate = new LayoutPoint { X = RoadGraphEditor.Round(point.X), Z = RoadGraphEditor.Round(point.Z) };
            return ApplyFloor(outline, gate);
        }

        /// <summary>
        /// 畫一條路（<see cref="RoadGraphEditor.AddRoad"/>）：交叉處變路口、長路段切成 4 m 以內。回傳沿線節點代碼；
        /// 起點終點是同一個節點，或畫的路已經完全存在（文件沒變）時回傳 null、不留復原紀錄。
        /// </summary>
        public List<string>? AddRoad(double startX, double startZ, double endX, double endZ)
        {
            var before = LayoutDocuments.Clone(_document);
            var codes = RoadGraphEditor.AddRoad(_document, (startX, startZ), (endX, endZ));
            return Commit(before, false) ? codes : null;
        }

        /// <summary>
        /// 移動節點（0.3 m 內有別的節點就合併）。回傳移動後的節點代碼（合併時是被合併到的那個）。
        /// 找不到節點、節點是模組交接點（跟著模組走，Claude 暫定）或位置沒變時回傳 null。
        /// </summary>
        public string? MoveNode(string code, double x, double z)
        {
            if (RoadGraphEditor.FindNode(_document, code) == null || IsHandoffNode(code))
            {
                return null;
            }

            var before = LayoutDocuments.Clone(_document);
            var result = RoadGraphEditor.MoveNode(_document, code, x, z);
            return Commit(before, false) ? result : null;
        }

        /// <summary>刪除節點與相連的路段。找不到節點或節點是模組交接點（Claude 暫定）時回傳 false。</summary>
        public bool DeleteNode(string code)
        {
            if (IsHandoffNode(code))
            {
                return false;
            }

            var before = LayoutDocuments.Clone(_document);
            return RoadGraphEditor.DeleteNode(_document, code) && Commit(before, false);
        }

        /// <summary>刪除路段；兩端變成沒有路段又不是交接點的節點一起刪。找不到路段時回傳 false。交接點節點會保留。</summary>
        public bool DeleteEdge(string from, string to)
        {
            var before = LayoutDocuments.Clone(_document);
            return RoadGraphEditor.DeleteEdge(_document, from, to) && Commit(before, false);
        }

        /// <summary>節點是否在某個模組的交接點上。</summary>
        public bool IsHandoffNode(string code) => RoadGraphEditor.IsHandoffNode(_document, code);

        /// <summary>
        /// 模組放在這裡（對齊格點後）是否可行：占地完全在廠房內，而且和其他模組沒有重疊（貼邊不算）。
        /// <paramref name="ignoreCode"/> 是拖曳中的那個模組自己。朝向不合法時回傳 false。
        /// </summary>
        public bool CanPlace(ModuleKind kind, double x, double z, int rotation, string? ignoreCode)
        {
            if (!TryNormalizeRotation(rotation, out var normalized))
            {
                return false;
            }

            var candidate = new LayoutModule { Kind = kind, X = Snap(x, ModuleGrid), Z = Snap(z, ModuleGrid), Rotation = normalized };
            var footprint = LayoutGeometry.Footprint(candidate);
            if (!FloorGeometry.RectInsidePolygon(FloorOutline(), footprint))
            {
                return false;
            }

            foreach (var other in _document.Modules)
            {
                if (other.Code != ignoreCode && LayoutGeometry.RectsOverlap(footprint, LayoutGeometry.Footprint(other)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>用共用的驗證器檢查目前的草稿（和後端套用前的檢查同一份程式）。</summary>
        public LayoutValidationDto Validate() => LayoutValidator.Validate(_document);

        /// <summary>對齊到格點（四捨五入到最近的格點，結果取到 0.01 m；-0 變 0）。</summary>
        public static double Snap(double value, double step) =>
            RoadGraphEditor.Round(Math.Round(value / step, MidpointRounding.AwayFromZero) * step);

        // 大門所在的牆與在牆上的位置（t：0 是起點、1 是終點）。大門不在任何牆上時回傳 null（改形狀時就不去動它）。
        private static WallHit? GateFollow(List<LayoutPoint> outline, LayoutPoint gate)
        {
            var hit = FloorGeometry.NearestWall(outline, gate.X, gate.Z);
            return hit != null && hit.Distance <= LayoutRules.GateOnWallTolerance ? hit : null;
        }

        private static LayoutPoint GatePointOn(List<LayoutPoint> outline, WallHit follow)
        {
            var point = FloorGeometry.PointOnWall(outline, follow.Index, follow.T);
            return new LayoutPoint { X = RoadGraphEditor.Round(point.X), Z = RoadGraphEditor.Round(point.Z) };
        }

        private static bool IsFinite(double a, double b) => !double.IsNaN(a) && !double.IsInfinity(a) && !double.IsNaN(b) && !double.IsInfinity(b);

        // 外牆與大門和目前的文件相同就什麼都不改（舊文件的矩形也不轉成 Outline）；
        // 否則寫進文件、把 Min／Max 更新成外框，留一步復原。
        private bool ApplyFloor(List<LayoutPoint> outline, LayoutPoint gate)
        {
            var floor = _document.Floor;
            var oldOutline = LayoutFloorShape.Outline(floor);
            var oldGate = LayoutFloorShape.Gate(floor);
            if (SameOutline(oldOutline, outline) && Math.Abs(oldGate.X - gate.X) < 1e-9 && Math.Abs(oldGate.Z - gate.Z) < 1e-9)
            {
                return false;
            }

            var before = LayoutDocuments.Clone(_document);
            floor.Outline = outline;
            floor.Gate = gate;
            UpdateBounds(floor);
            return Commit(before, false);
        }

        private static bool SameOutline(List<LayoutPoint> a, List<LayoutPoint> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (var i = 0; i < a.Count; i++)
            {
                if (Math.Abs(a[i].X - b[i].X) >= 1e-9 || Math.Abs(a[i].Z - b[i].Z) >= 1e-9)
                {
                    return false;
                }
            }

            return true;
        }

        // Min／Max 保持是外牆的外框（舊的程式與工具還在讀它們）。
        private static void UpdateBounds(LayoutFloor floor)
        {
            var bounds = LayoutFloorShape.Bounds(floor.Outline!);
            floor.MinX = bounds.MinX;
            floor.MaxX = bounds.MaxX;
            floor.MinZ = bounds.MinZ;
            floor.MaxZ = bounds.MaxZ;
        }

        private string Canonical => _canonical ??= LayoutDocuments.Canonical(_document);

        private LayoutModule? Find(string code) => _document.Modules.FirstOrDefault(m => m.Code == code);

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

        private static bool TryNormalizeRotation(int rotation, out int normalized)
        {
            normalized = ((rotation % 360) + 360) % 360;
            return normalized % 90 == 0;
        }

        // 操作已經改完草稿：有真的改到才留復原紀錄（before 是操作前的複本）；coalesce 為 true 時併入上一步。
        private bool Commit(FactoryLayoutDocument before, bool coalesce)
        {
            var beforeCanonical = LayoutDocuments.Canonical(before);
            _canonical = null;
            if (Canonical == beforeCanonical)
            {
                return false;
            }

            if (!coalesce)
            {
                _undo.Add(before);
                if (_undo.Count > MaxHistory)
                {
                    _undo.RemoveAt(0);
                }

                _floorDrag = false;
            }

            _redo.Clear();
            return true;
        }
    }
}
