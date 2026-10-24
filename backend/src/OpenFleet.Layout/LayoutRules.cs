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

        public const int MaxModules = 120;
        public const int MaxRoadNodes = 1500;
        public const int MaxVehicles = 20;

        /// <summary>穿越檢查時，模組占地往內縮的距離（公尺）。</summary>
        public const double ThroughInset = 0.05;

        /// <summary>模組在廠房內的容許誤差（公尺）。</summary>
        public const double InsideTolerance = 0.01;

        /// <summary>兩個占地的重疊面積超過這個值才算重疊（貼邊不算）。</summary>
        public const double OverlapArea = 1e-4;

        public static readonly int[] AllowedRotations = { 0, 90, 180, 270 };
    }
}
