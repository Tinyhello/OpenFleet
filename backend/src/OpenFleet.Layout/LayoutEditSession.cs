using System;
using System.Collections.Generic;
using System.Linq;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>
    /// 沙盒編輯的編輯模型（規劃第 7 節）：Unity 只負責輸入與繪製，所有對佈局文件的修改都經過這個類別，
    /// 所以規則（對齊、代碼、自動接路、復原）都能用單元測試驗證。
    ///
    /// 約定：
    /// - 建構時複製一份來源文件當草稿（<see cref="Document"/>）；呼叫端只讀、不可以直接修改。
    ///   復原與重做會換掉草稿物件，所以每次操作後都要重新讀 <see cref="Document"/>，不要留著舊的參考。
    /// - 每次成功改到文件的操作，先把操作前的狀態推進復原堆疊（最多 <see cref="MaxHistory"/> 步，最舊的丟掉）並清掉重做堆疊；
    ///   失敗或沒有改到東西的操作（回傳 false 或 null）不留紀錄。
    /// - 連續的 <see cref="SetFloorSize"/>（拖滑桿）合成一步復原，直到出現別的操作（含復原、重做、<see cref="MarkSaved"/>）為止。
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
        /// 大小沒變時回傳 false。連續呼叫（拖滑桿）合成一步復原。不管模組是否還在廠房內（驗證會回報）。
        /// </summary>
        public bool SetFloorSize(double width, double depth)
        {
            var newWidth = Clamp(Snap(width, FloorStep), LayoutRules.MinFloorWidth, LayoutRules.MaxFloorWidth);
            var newDepth = Clamp(Snap(depth, FloorStep), LayoutRules.MinFloorDepth, LayoutRules.MaxFloorDepth);
            var floor = _document.Floor;
            var centerX = (floor.MinX + floor.MaxX) / 2;
            var centerZ = (floor.MinZ + floor.MaxZ) / 2;
            var minX = RoadGraphEditor.Round(centerX - newWidth / 2);
            var maxX = RoadGraphEditor.Round(centerX + newWidth / 2);
            var minZ = RoadGraphEditor.Round(centerZ - newDepth / 2);
            var maxZ = RoadGraphEditor.Round(centerZ + newDepth / 2);
            if (minX == floor.MinX && maxX == floor.MaxX && minZ == floor.MinZ && maxZ == floor.MaxZ)
            {
                return false;
            }

            var before = LayoutDocuments.Clone(_document);
            floor.MinX = minX;
            floor.MaxX = maxX;
            floor.MinZ = minZ;
            floor.MaxZ = maxZ;
            var coalesce = _floorDrag;
            var result = Commit(before, coalesce);
            _floorDrag = true;
            return result;
        }

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
            var floor = _document.Floor;
            if (!LayoutGeometry.RectInside(footprint, new LayoutRect(floor.MinX, floor.MaxX, floor.MinZ, floor.MaxZ)))
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
