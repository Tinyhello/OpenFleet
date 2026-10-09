using UnityEngine;
using static OpenFleet.Unity.Editor.ModelKit;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// 各站點的簡化模型。區域座標：+Z 朝走道（交接點那側），占地約 X ±2.8、Z −2 ~ +2。
    /// 模型只表示「這裡是哪種站」，不代表機台數量或實際設備規格。
    /// </summary>
    internal static class StationModels
    {
        /// <summary>備料區、成品區「有料時才顯示」的物件名稱；場景產生器依名稱找到它們交給 StationAnchor。</summary>
        public const string InboundContentsName = "Inbound Contents";
        public const string OutboundContentsName = "Outbound Contents";

        public static void Build(StationKind kind, Transform root)
        {
            switch (kind)
            {
                case StationKind.RawMaterialStore: RawMaterialStore(root); break;
                case StationKind.RoughMachining: Lathe(root, Palette.RoughAccent); break;
                case StationKind.FineMachining: Lathe(root, Palette.FineAccent); break;
                case StationKind.HeatTreatment: HeatTreatment(root); break;
                case StationKind.Plating: Plating(root); break;
                case StationKind.Polishing: Polishing(root); break;
                case StationKind.QualityInspection: QualityInspection(root); break;
                case StationKind.Packing: Packing(root); break;
                case StationKind.Shipping: Shipping(root); break;
                case StationKind.ToolStore: ToolStore(root); break;
            }
        }

        private static void RawMaterialStore(Transform root)
        {
            Rack(Group(root, "Rack A", new Vector3(-1.35f, 0f, -0.9f)));
            Rack(Group(root, "Rack B", new Vector3(1.35f, 0f, -0.9f)));

            // 出庫暫存區
            FloorFrame(root, "Staging", new Vector3(0f, 0f, 1.3f), new Vector2(4.8f, 1.3f), 0.06f, Palette.ZoneLine);
            var staged = Group(root, "Staged Pallet", new Vector3(-1.2f, 0f, 1.3f));
            Box(staged, "Pallet", new Vector3(0f, 0.07f, 0f), new Vector3(1.0f, 0.14f, 1.0f), Palette.Pallet);
            BarBundle(staged, new Vector3(0f, 0.14f, 0f));
        }

        private static void Rack(Transform rack)
        {
            const float width = 2.5f, depth = 1.1f, height = 3.2f;
            foreach (var x in new[] { -width / 2f, width / 2f })
            {
                foreach (var z in new[] { -depth / 2f, depth / 2f })
                {
                    Box(rack, "Upright", new Vector3(x, height / 2f, z), new Vector3(0.08f, height, 0.08f), Palette.RackUpright);
                }
            }

            var levels = new[] { 0.15f, 1.2f, 2.25f };
            for (var i = 0; i < levels.Length; i++)
            {
                var y = levels[i];
                Box(rack, "Beam", new Vector3(0f, y, depth / 2f), new Vector3(width, 0.1f, 0.06f), Palette.RackBeam);
                Box(rack, "Beam", new Vector3(0f, y, -depth / 2f), new Vector3(width, 0.1f, 0.06f), Palette.RackBeam);

                foreach (var x in new[] { -0.6f, 0.6f })
                {
                    var slot = Group(rack, "Slot", new Vector3(x, y + 0.05f, 0f));
                    Box(slot, "Pallet", new Vector3(0f, 0.06f, 0f), new Vector3(1.0f, 0.12f, 0.95f), Palette.Pallet);
                    if (i < 2)
                    {
                        BarBundle(slot, new Vector3(0f, 0.12f, 0f));
                    }
                    else
                    {
                        Box(slot, "Carton", new Vector3(0f, 0.37f, 0f), new Vector3(0.8f, 0.5f, 0.8f), Palette.Cardboard);
                    }
                }
            }
        }

        /// <summary>一捆鐵件原料（棒材）。</summary>
        private static void BarBundle(Transform parent, Vector3 basePosition)
        {
            const float d = 0.13f;
            for (var row = 0; row < 2; row++)
            {
                for (var i = 0; i < 4 - row; i++)
                {
                    var x = (i - (3 - row) / 2f) * d;
                    var y = d / 2f + row * d * 0.87f;
                    Cylinder(parent, "Bar", basePosition + new Vector3(x, y, 0f), d, 0.9f, Palette.Steel, new Vector3(90f, 0f, 0f));
                }
            }
        }

        /// <summary>CNC 車床（有外罩）加上機邊緩衝台。粗加工與精加工只差顏色。</summary>
        private static void Lathe(Transform root, Material accent)
        {
            var machine = Group(root, "CNC Lathe", new Vector3(-0.4f, 0f, -0.5f));
            Box(machine, "Base", new Vector3(0f, 0.45f, 0f), new Vector3(3.4f, 0.9f, 1.7f), Palette.MachineBody);
            Box(machine, "Enclosure", new Vector3(-0.2f, 1.6f, 0f), new Vector3(3.0f, 1.4f, 1.7f), Palette.MachineBody);
            Box(machine, "Roof", new Vector3(-0.2f, 2.33f, 0f), new Vector3(3.0f, 0.06f, 1.7f), accent);
            Box(machine, "Stripe", new Vector3(0f, 0.95f, 0.86f), new Vector3(3.4f, 0.1f, 0.02f), accent);
            Box(machine, "Door Window", new Vector3(-0.6f, 1.62f, 0.86f), new Vector3(1.5f, 0.85f, 0.02f), Palette.Glass);
            Box(machine, "Door Frame", new Vector3(-0.6f, 2.1f, 0.87f), new Vector3(1.6f, 0.08f, 0.03f), accent);
            Box(machine, "Door Handle", new Vector3(0.2f, 1.62f, 0.9f), new Vector3(0.05f, 0.5f, 0.05f), Palette.Steel);
            Box(machine, "Chip Conveyor", new Vector3(-1.95f, 0.3f, -0.2f), new Vector3(0.5f, 0.6f, 1.2f), Palette.MachineDark);

            var panel = Group(machine, "Control Panel", new Vector3(1.55f, 0f, 0.55f));
            Box(panel, "Housing", new Vector3(0f, 1.55f, 0f), new Vector3(0.55f, 0.9f, 0.25f), Palette.MachineDark);
            Box(panel, "Screen", new Vector3(0f, 1.7f, 0.13f), new Vector3(0.42f, 0.32f, 0.01f), Palette.Screen);

            // 備料區（待加工）與成品區（已加工、等待搬走）：各一張台子。容量與數量在後端，畫面上用進度條表示；
            // 台上的料箱只在有料時顯示（StationAnchor 切換），沒有逐件的動畫。
            var inbound = Group(root, "Inbound Table", new Vector3(2.1f, 0f, 1.3f));
            Table(inbound, new Vector3(1.0f, 0.7f, 0.8f), Palette.Steel);
            var inboundContents = Group(inbound, InboundContentsName, Vector3.zero);
            Box(inboundContents, "Bin", new Vector3(0f, 0.86f, 0f), new Vector3(0.6f, 0.26f, 0.42f), Palette.Bin);
            Cylinder(inboundContents, "Blank", new Vector3(-0.12f, 1.02f, 0f), 0.1f, 0.34f, Palette.MachineDark, new Vector3(90f, 0f, 0f));
            Cylinder(inboundContents, "Blank", new Vector3(0.12f, 1.02f, 0f), 0.1f, 0.34f, Palette.MachineDark, new Vector3(90f, 0f, 0f));
            Label(inbound, "備料", new Vector3(0f, 1.25f, 0f), 0.045f, Color.white);

            var outbound = Group(root, "Outbound Table", new Vector3(-2.1f, 0f, 1.3f));
            Table(outbound, new Vector3(1.0f, 0.7f, 0.8f), Palette.Steel);
            var outboundContents = Group(outbound, OutboundContentsName, Vector3.zero);
            Box(outboundContents, "Bin", new Vector3(0f, 0.86f, 0f), new Vector3(0.6f, 0.26f, 0.42f), Palette.Bin);
            Cylinder(outboundContents, "Part", new Vector3(-0.12f, 1.02f, 0f), 0.1f, 0.34f, Palette.Steel, new Vector3(90f, 0f, 0f));
            Cylinder(outboundContents, "Part", new Vector3(0.12f, 1.02f, 0f), 0.1f, 0.34f, Palette.Steel, new Vector3(90f, 0f, 0f));
            Label(outbound, "成品", new Vector3(0f, 1.25f, 0f), 0.045f, Color.white);
        }

        private static void HeatTreatment(Transform root)
        {
            var quench = Group(root, "Quench Furnace", new Vector3(-1.3f, 0f, -0.6f));
            Box(quench, "Body", new Vector3(0f, 1.1f, 0f), new Vector3(2.0f, 2.2f, 1.8f), Palette.Furnace);
            Box(quench, "Door", new Vector3(0f, 1.0f, 0.92f), new Vector3(1.2f, 1.3f, 0.04f), Palette.MachineDark);
            Box(quench, "Glow", new Vector3(0f, 1.0f, 0.95f), new Vector3(0.9f, 0.12f, 0.02f), Palette.Glow);
            Cylinder(quench, "Chimney", new Vector3(-0.5f, 2.7f, -0.4f), 0.35f, 1.0f, Palette.Steel);
            Box(quench, "Panel", new Vector3(1.2f, 1.2f, 0.3f), new Vector3(0.35f, 0.9f, 0.35f), Palette.MachineDark);

            var temper = Group(root, "Temper Furnace", new Vector3(1.5f, 0f, -0.9f));
            Box(temper, "Body", new Vector3(0f, 0.9f, 0f), new Vector3(1.6f, 1.8f, 1.4f), Palette.Furnace);
            Box(temper, "Door", new Vector3(0f, 0.85f, 0.72f), new Vector3(1.0f, 1.0f, 0.04f), Palette.MachineDark);
            Box(temper, "Glow", new Vector3(0f, 0.85f, 0.75f), new Vector3(0.7f, 0.1f, 0.02f), Palette.Glow);
            Cylinder(temper, "Chimney", new Vector3(0.4f, 2.1f, -0.3f), 0.25f, 0.6f, Palette.Steel);

            var tank = Group(root, "Quench Tank", new Vector3(1.5f, 0f, 0.8f));
            Box(tank, "Tank", new Vector3(0f, 0.45f, 0f), new Vector3(1.4f, 0.9f, 0.9f), Palette.Steel);
            Box(tank, "Oil", new Vector3(0f, 0.91f, 0f), new Vector3(1.3f, 0.02f, 0.8f), Palette.QuenchOil);
        }

        private static void Plating(Transform root)
        {
            var tanks = Group(root, "Tanks", new Vector3(0f, 0f, -0.5f));
            var xs = new[] { -1.8f, -0.6f, 0.6f, 1.8f };
            foreach (var x in xs)
            {
                Box(tanks, "Tank", new Vector3(x, 0.5f, 0f), new Vector3(1.05f, 1.0f, 1.4f), Palette.TankWall);
                Box(tanks, "Liquid", new Vector3(x, 1.01f, 0f), new Vector3(0.9f, 0.02f, 1.25f), Palette.Liquid);
            }

            var gantry = Group(root, "Gantry", new Vector3(0f, 0f, -0.5f));
            foreach (var x in new[] { -2.55f, 2.55f })
            {
                foreach (var z in new[] { -0.85f, 0.85f })
                {
                    Box(gantry, "Post", new Vector3(x, 1.4f, z), new Vector3(0.12f, 2.8f, 0.12f), Palette.Steel);
                }
            }

            Box(gantry, "Rail", new Vector3(0f, 2.8f, -0.85f), new Vector3(5.2f, 0.14f, 0.14f), Palette.Steel);
            Box(gantry, "Rail", new Vector3(0f, 2.8f, 0.85f), new Vector3(5.2f, 0.14f, 0.14f), Palette.Steel);
            Box(gantry, "Hoist Beam", new Vector3(0.6f, 2.72f, 0f), new Vector3(0.16f, 0.16f, 1.84f), Palette.RackBeam);
            Box(gantry, "Hoist", new Vector3(0.6f, 2.45f, 0f), new Vector3(0.3f, 0.35f, 0.3f), Palette.MachineDark);
            Box(gantry, "Rack Basket", new Vector3(0.6f, 1.55f, 0f), new Vector3(0.6f, 0.45f, 0.5f), Palette.Steel);
            Box(gantry, "Cable", new Vector3(0.6f, 2.03f, 0f), new Vector3(0.03f, 0.5f, 0.03f), Palette.MachineDark);
        }

        private static void Polishing(Transform root)
        {
            foreach (var x in new[] { -1.3f, 1.3f })
            {
                var machine = Group(root, "Polisher", new Vector3(x, 0f, -0.5f));
                Box(machine, "Pedestal", new Vector3(0f, 0.5f, 0f), new Vector3(1.2f, 1.0f, 0.9f), Palette.MachineBody);
                Box(machine, "Stripe", new Vector3(0f, 0.9f, 0.46f), new Vector3(1.2f, 0.08f, 0.02f), Palette.PolishWheel);
                Box(machine, "Motor", new Vector3(0f, 1.2f, 0f), new Vector3(0.5f, 0.4f, 0.4f), Palette.MachineDark);
                Cylinder(machine, "Shaft", new Vector3(0f, 1.25f, 0f), 0.06f, 1.1f, Palette.Steel, new Vector3(0f, 0f, 90f));
                Cylinder(machine, "Wheel", new Vector3(-0.5f, 1.25f, 0f), 0.5f, 0.12f, Palette.PolishWheel, new Vector3(0f, 0f, 90f));
                Cylinder(machine, "Wheel", new Vector3(0.5f, 1.25f, 0f), 0.5f, 0.12f, Palette.PolishWheel, new Vector3(0f, 0f, 90f));
                Box(machine, "Hood", new Vector3(0f, 1.6f, -0.3f), new Vector3(1.5f, 0.3f, 0.45f), Palette.MachineDark);
                Cylinder(machine, "Duct", new Vector3(0f, 2.35f, -0.35f), 0.25f, 1.2f, Palette.Steel);
            }

            var buffer = Group(root, "Work Table", new Vector3(0f, 0f, 1.2f));
            Table(buffer, new Vector3(1.2f, 0.75f, 0.7f), Palette.Steel);
            Box(buffer, "Bin", new Vector3(0f, 0.91f, 0f), new Vector3(0.6f, 0.26f, 0.42f), Palette.Bin);
        }

        private static void QualityInspection(Transform root)
        {
            var cmm = Group(root, "CMM", new Vector3(-0.5f, 0f, -0.6f));
            Box(cmm, "Base", new Vector3(0f, 0.35f, 0f), new Vector3(2.2f, 0.7f, 1.3f), Palette.MachineDark);
            Box(cmm, "Granite", new Vector3(0f, 0.82f, 0f), new Vector3(2.6f, 0.25f, 1.7f), Palette.Granite);
            Box(cmm, "Post", new Vector3(-1.15f, 1.5f, 0.1f), new Vector3(0.15f, 1.1f, 0.22f), Palette.MachineBody);
            Box(cmm, "Post", new Vector3(1.15f, 1.5f, 0.1f), new Vector3(0.15f, 1.1f, 0.22f), Palette.MachineBody);
            Box(cmm, "Bridge", new Vector3(0f, 2.1f, 0.1f), new Vector3(2.5f, 0.2f, 0.28f), Palette.MachineBody);
            Box(cmm, "Carriage", new Vector3(0.3f, 2.1f, 0.1f), new Vector3(0.3f, 0.3f, 0.36f), Palette.FineAccent);
            Box(cmm, "Probe", new Vector3(0.3f, 1.55f, 0.1f), new Vector3(0.1f, 0.8f, 0.1f), Palette.Steel);
            Cylinder(cmm, "Part", new Vector3(0.3f, 1.02f, 0.1f), 0.25f, 0.15f, Palette.Steel);

            var desk = Group(root, "Desk", new Vector3(1.9f, 0f, 0.6f));
            Box(desk, "Desk", new Vector3(0f, 0.4f, 0f), new Vector3(0.9f, 0.8f, 0.6f), Palette.MachineBody);
            Box(desk, "Monitor", new Vector3(0f, 1.05f, -0.1f), new Vector3(0.6f, 0.4f, 0.04f), Palette.MachineDark);
            Box(desk, "Screen", new Vector3(0f, 1.05f, -0.07f), new Vector3(0.54f, 0.34f, 0.01f), Palette.Screen);
        }

        private static void Packing(Transform root)
        {
            var table = Group(root, "Packing Table", new Vector3(-1.2f, 0f, -0.4f));
            Table(table, new Vector3(1.8f, 0.8f, 1.0f), Palette.Steel);
            Box(table, "Open Carton", new Vector3(-0.3f, 0.99f, 0f), new Vector3(0.45f, 0.3f, 0.45f), Palette.Cardboard);
            for (var i = 0; i < 4; i++)
            {
                Box(table, "Flat Carton", new Vector3(0.5f, 0.85f + i * 0.02f, 0f), new Vector3(0.6f, 0.02f, 0.6f), Palette.Cardboard);
            }

            var conveyor = Group(root, "Conveyor", new Vector3(1.3f, 0f, -0.4f));
            Box(conveyor, "Frame", new Vector3(0f, 0.65f, 0f), new Vector3(2.4f, 0.12f, 0.75f), Palette.Conveyor);
            foreach (var x in new[] { -1.1f, 1.1f })
            {
                foreach (var z in new[] { -0.3f, 0.3f })
                {
                    Box(conveyor, "Leg", new Vector3(x, 0.3f, z), new Vector3(0.06f, 0.6f, 0.06f), Palette.Steel);
                }
            }

            for (var i = 0; i < 9; i++)
            {
                Cylinder(conveyor, "Roller", new Vector3(-1.1f + i * 0.275f, 0.72f, 0f), 0.07f, 0.72f, Palette.Steel, new Vector3(90f, 0f, 0f));
            }

            Box(conveyor, "Carton", new Vector3(-0.4f, 0.93f, 0f), new Vector3(0.45f, 0.35f, 0.45f), Palette.Cardboard);
            Box(conveyor, "Carton", new Vector3(0.5f, 0.93f, 0f), new Vector3(0.45f, 0.35f, 0.45f), Palette.Cardboard);
        }

        private static void Shipping(Transform root)
        {
            FloorFrame(root, "Dock Zone", new Vector3(0f, 0f, 0.2f), new Vector2(5.0f, 3.2f), 0.08f, Palette.LaneLine);

            var door = Group(root, "Dock Door", new Vector3(0f, 0f, -2.0f));
            Box(door, "Door", new Vector3(0f, 1.6f, 0f), new Vector3(3.2f, 3.2f, 0.1f), Palette.Steel);
            for (var i = 1; i < 10; i++)
            {
                Box(door, "Slat", new Vector3(0f, i * 0.32f, 0.06f), new Vector3(3.2f, 0.03f, 0.02f), Palette.MachineDark);
            }

            Box(door, "Frame", new Vector3(0f, 3.25f, 0.05f), new Vector3(3.5f, 0.15f, 0.2f), Palette.LaneLine);

            foreach (var x in new[] { -1.5f, 0f, 1.5f })
            {
                var pallet = Group(root, "Outbound Pallet", new Vector3(x, 0f, 0.3f));
                Box(pallet, "Pallet", new Vector3(0f, 0.07f, 0f), new Vector3(1.1f, 0.14f, 1.1f), Palette.Pallet);
                var layers = x < 0f ? 3 : x > 0f ? 1 : 2;
                for (var layer = 0; layer < layers; layer++)
                {
                    foreach (var cx in new[] { -0.27f, 0.27f })
                    {
                        foreach (var cz in new[] { -0.27f, 0.27f })
                        {
                            Box(pallet, "Carton", new Vector3(cx, 0.34f + layer * 0.42f, cz), new Vector3(0.5f, 0.4f, 0.5f), Palette.Cardboard);
                        }
                    }
                }
            }
        }

        private static void ToolStore(Transform root)
        {
            foreach (var x in new[] { -1.6f, -0.3f })
            {
                var cabinet = Group(root, "Tool Cabinet", new Vector3(x, 0f, -1.0f));
                Box(cabinet, "Body", new Vector3(0f, 1.0f, 0f), new Vector3(1.2f, 2.0f, 0.8f), Palette.ToolCabinet);
                for (var i = 0; i < 8; i++)
                {
                    var y = 0.2f + i * 0.22f;
                    Box(cabinet, "Drawer Gap", new Vector3(0f, y, 0.41f), new Vector3(1.1f, 0.015f, 0.01f), Palette.MachineDark);
                    Box(cabinet, "Handle", new Vector3(0f, y + 0.11f, 0.42f), new Vector3(0.4f, 0.03f, 0.03f), Palette.Steel);
                }
            }

            var presetter = Group(root, "Tool Presetter", new Vector3(1.8f, 0f, -0.9f));
            Box(presetter, "Bench", new Vector3(0f, 0.45f, 0f), new Vector3(1.2f, 0.9f, 0.8f), Palette.MachineBody);
            Box(presetter, "Column", new Vector3(-0.3f, 1.3f, -0.2f), new Vector3(0.15f, 0.8f, 0.15f), Palette.MachineDark);
            Box(presetter, "Camera Head", new Vector3(-0.1f, 1.6f, -0.2f), new Vector3(0.35f, 0.15f, 0.15f), Palette.MachineDark);
            Box(presetter, "Screen", new Vector3(0.35f, 1.15f, -0.2f), new Vector3(0.4f, 0.3f, 0.03f), Palette.Screen);

            // 小型 AGV 的取刀位置：刀把架。
            var rack = Group(root, "Tool Pickup Rack", new Vector3(0f, 0f, 1.3f));
            Box(rack, "Shelf", new Vector3(0f, 0.45f, 0f), new Vector3(1.4f, 0.9f, 0.5f), Palette.Steel);
            for (var i = 0; i < 6; i++)
            {
                Cylinder(rack, "Tool Holder", new Vector3(-0.55f + i * 0.22f, 1.02f, 0f), 0.09f, 0.24f, Palette.MachineDark);
            }
        }

        private static void Table(Transform parent, Vector3 size, Material material)
        {
            Box(parent, "Top", new Vector3(0f, size.y - 0.03f, 0f), new Vector3(size.x, 0.06f, size.z), material);
            var lx = size.x / 2f - 0.05f;
            var lz = size.z / 2f - 0.05f;
            foreach (var x in new[] { -lx, lx })
            {
                foreach (var z in new[] { -lz, lz })
                {
                    Box(parent, "Leg", new Vector3(x, (size.y - 0.06f) / 2f, z), new Vector3(0.05f, size.y - 0.06f, 0.05f), material);
                }
            }
        }
    }
}
