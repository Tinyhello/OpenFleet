namespace OpenFleet.Layout
{
    /// <summary>
    /// 佈局規則的常數。1.6 m 等於預設路網的最短路段與最小間距（一台大型 AGV 長 1.6 m、寬 1.1 m）。
    /// </summary>
    public static class LayoutRules
    {
        /// <summary>路段最短長度（公尺）。</summary>
        public const double MinEdgeLength = 1.6;

        /// <summary>兩條沒有共用節點的路段之間的最小距離（公尺）。</summary>
        public const double MinClearance = 1.6;

        /// <summary>數值比較的容許誤差（公尺）。</summary>
        public const double Tolerance = 0.001;

        /// <summary>交接點與路網節點視為同一點的距離（公尺）。</summary>
        public const double HandoffMatch = 0.05;

        public const double MinFloorWidth = 40;
        public const double MaxFloorWidth = 100;
        public const double MinFloorDepth = 24;
        public const double MaxFloorDepth = 60;

        /// <summary>外牆每一段的最短長度（公尺）。Claude 暫定。</summary>
        public const double MinWallLength = 2.0;

        /// <summary>大門寬度（公尺）。沿用原本固定的 10 m 大門。</summary>
        public const double GateWidth = 10;

        /// <summary>大門兩端離牆端點的最小距離（公尺），所以放大門的牆至少 11 m。Claude 暫定。</summary>
        public const double GateMargin = 0.5;

        /// <summary>大門中心離牆多近算「在牆上」（公尺）。</summary>
        public const double GateOnWallTolerance = 0.05;

        /// <summary>路段離每段外牆至少這麼遠（公尺）：路面寬 1.6 m 的一半，再沒有餘裕。Claude 暫定。</summary>
        public const double RoadWallClearance = 0.8;

        /// <summary>編輯廠房頂點、加轉角、移大門時的對齊格距（公尺），和模組相同。Claude 暫定。</summary>
        public const double FloorVertexGrid = 0.2;

        /// <summary>外牆頂點數上限：驗證的自交檢查是頂點數的平方，文件可以任意大，所以要設限。Claude 暫定。</summary>
        public const int MaxFloorVertices = 64;

        public const int MaxModules = 120;
        public const int MaxRoadNodes = 1500;
        public const int MaxVehicles = 20;

        /// <summary>穿越檢查時，模組占地往內縮的距離（公尺）。</summary>
        public const double ThroughInset = 0.05;

        /// <summary>模組在廠房內的容許誤差（公尺）。</summary>
        public const double InsideTolerance = 0.01;

        /// <summary>兩個占地的重疊面積超過這個值才算重疊（貼邊不算）。</summary>
        public const double OverlapArea = 1e-4;

        /// <summary>路網編輯：新節點離既有節點這麼近就合併過去（公尺）。規劃時決定的數值。</summary>
        public const double MergeDistance = 0.3;

        /// <summary>路網編輯：畫出來的路段最長 4 m，更長的切成等長的幾段（公尺）。</summary>
        public const double MaxSegmentLength = 4.0;

        /// <summary>模組自動接路：從交接點往外找路的最遠距離，支線不切段（公尺）。</summary>
        public const double MaxSpurLength = 8.0;

        /// <summary>點離路段這麼近就視為落在路段上，會把路段切開（公尺）。</summary>
        public const double OnEdgeDistance = 0.01;

        /// <summary>路線編輯：點選節點的半徑（公尺）。</summary>
        public const double PickNodeRadius = 0.5;

        /// <summary>路線編輯：點選路段的半徑（公尺）。</summary>
        public const double PickEdgeRadius = 0.4;

        /// <summary>路線編輯：畫路時吸附到既有節點的半徑（公尺）。</summary>
        public const double SnapNodeRadius = 0.6;

        /// <summary>路線編輯：畫路時吸附到既有路段的半徑（公尺）。</summary>
        public const double SnapEdgeRadius = 0.4;

        /// <summary>路線編輯：沒吸附到東西時的對齊格距（公尺）。</summary>
        public const double RoadGrid = 0.2;

        /// <summary>路線編輯：拖曳畫路的最短長度（公尺），比這短視為點一下，不畫。</summary>
        public const double MinDrawLength = 1.0;

        public static readonly int[] AllowedRotations = { 0, 90, 180, 270 };
    }
}
