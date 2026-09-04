using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using GameEntitySystem;
using TemplatesDatabase;

namespace Game {
    public class SubsystemWandBehavior : SubsystemBlockBehavior, IUpdateable, IDrawable {
        public class WandState {
            public ComponentPlayer Player;
            public bool WasHoldingWand;
            public int WandContents;
            public int TargetContents;
            public List<Point3> PreviewCells = [];
        }

        public const int MaxBlocksPerUse = 128;

        public static readonly Color PreviewColor = Color.Lerp(Color.White, Color.Transparent, 0.35f);

        public SubsystemTerrain m_subsystemTerrain;
        public SubsystemAudio m_subsystemAudio;

        public PrimitivesRenderer3D m_primitivesRenderer3D = new();

        public int m_stoneWandIndex;
        public int m_ironWandIndex;
        public int m_diamondWandIndex;
        public int m_infinityWandIndex;

        public Dictionary<PlayerData, WandState> m_states = [];

        public override int[] HandledBlocks {
            get {
                List<int> indexes = [];
                int[] allIndexes = [
                    BlocksManager.GetBlockIndex(typeof(StoneWandBlock)),
                    BlocksManager.GetBlockIndex(typeof(IronWandBlock)),
                    BlocksManager.GetBlockIndex(typeof(DiamondWandBlock)),
                    BlocksManager.GetBlockIndex(typeof(InfinityWandBlock))
                ];
                foreach (int index in allIndexes) {
                    if (index >= 0) {
                        indexes.Add(index);
                    }
                }
                return indexes.ToArray();
            }
        }

        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        public int[] DrawOrders => [201];

        public static Point3 FaceNormal(int face) => CellFace.FaceToPoint3(face);

        public static Point3 Offset(Point3 p, int dx, int dy, int dz) => new(p.X + dx, p.Y + dy, p.Z + dz);

        public static int GetMode(int value) => Terrain.ExtractData(value) & 1;

        public static int SetMode(int value, int mode) =>
            Terrain.ReplaceData(value, (Terrain.ExtractData(value) & ~1) | (mode & 1));

        public static int GetDirection(int value) => (Terrain.ExtractData(value) >> 1) & 1;

        public static int SetDirection(int value, int direction) =>
            Terrain.ReplaceData(value, (Terrain.ExtractData(value) & ~2) | ((direction & 1) << 1));

        public static bool IsCrouching(ComponentPlayer player) => player.ComponentBody != null && player.ComponentBody.IsCrouching;

        public override void Load(ValuesDictionary valuesDictionary) {
            base.Load(valuesDictionary);
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(true);
            m_subsystemAudio = Project.FindSubsystem<SubsystemAudio>(true);
            m_stoneWandIndex = BlocksManager.GetBlockIndex<StoneWandBlock>();
            m_ironWandIndex = BlocksManager.GetBlockIndex<IronWandBlock>();
            m_diamondWandIndex = BlocksManager.GetBlockIndex<DiamondWandBlock>();
            m_infinityWandIndex = BlocksManager.GetBlockIndex<InfinityWandBlock>();
            Log.Information($"[ConstructionWand] Wand blocks registered: stone={m_stoneWandIndex}, iron={m_ironWandIndex}, diamond={m_diamondWandIndex}, infinity={m_infinityWandIndex}");
        }

        public bool IsWand(int contents) =>
            contents == m_stoneWandIndex
            || contents == m_ironWandIndex
            || contents == m_diamondWandIndex
            || contents == m_infinityWandIndex;

        public void Update(float dt) {
            foreach (WandState state in m_states.Values) {
                UpdatePlayerState(state);
            }
        }

        public void UpdatePlayerState(WandState state) {
            ComponentPlayer player = state.Player;
            if (player?.ComponentMiner == null) {
                state.WasHoldingWand = false;
                state.PreviewCells.Clear();
                return;
            }
            int activeValue = player.ComponentMiner.ActiveBlockValue;
            int activeContents = Terrain.ExtractContents(activeValue);
            bool holding = IsWand(activeContents);

            if (holding && !state.WasHoldingWand) {
                state.WandContents = activeContents;
                state.TargetContents = 0;
                if (GetMode(activeValue) == 0) {
                    state.TargetContents = GetBlockRightOfActive(player);
                }
            }
            else if (holding && state.WasHoldingWand && state.WandContents != activeContents) {
                state.WandContents = activeContents;
                state.TargetContents = 0;
            }

            state.WasHoldingWand = holding;
            state.PreviewCells.Clear();

            if (!holding || GetMode(activeValue) != 0 || IsCrouching(player)) {
                return;
            }
            if (state.TargetContents == 0) {
                return;
            }

            Camera camera = player.GameWidget?.ActiveCamera;
            if (camera == null) {
                return;
            }
            Ray3 ray = new(camera.ViewPosition, camera.ViewDirection);
            TerrainRaycastResult? hit = player.ComponentMiner.Raycast<TerrainRaycastResult>(ray, RaycastMode.Digging);
            if (!hit.HasValue) {
                return;
            }

            int limit = GetPlaceLimit(player, activeValue);
            int available = CountTargetItems(player.ComponentMiner.Inventory, state.TargetContents);
            if (limit > available) {
                limit = available;
            }
            if (limit <= 0) {
                return;
            }
            state.PreviewCells = ComputePlaceCells(hit.Value, state.TargetContents, GetDirection(activeValue) != 0, limit);
        }

        public override bool OnUse(Ray3 ray, ComponentMiner componentMiner) {
            int activeValue = componentMiner.ActiveBlockValue;
            int activeContents = Terrain.ExtractContents(activeValue);
            if (!IsWand(activeContents)) {
                return false;
            }
            ComponentPlayer player = componentMiner.Entity?.FindComponent<ComponentPlayer>();
            if (player == null) {
                return false;
            }

            // 蹲下 + 点击：切换 放置模式 / 调整模式
            if (IsCrouching(player)) {
                ToggleMode(player, activeValue);
                return true;
            }

            // 调整模式：点击切换 横向 / 竖向（不放置方块，不显示预览）
            if (GetMode(activeValue) != 0) {
                ToggleDirection(player, activeValue);
                return true;
            }

            // 放置模式：执行放置
            WandState state = GetState(player);
            PlaceBlocks(player, componentMiner, ray, state, activeValue);
            return true;
        }

        public void ToggleMode(ComponentPlayer player, int wandValue) {
            int newMode = GetMode(wandValue) == 0 ? 1 : 0;
            UpdateActiveItem(player, SetMode(wandValue, newMode));
            WandState state = GetState(player);
            state.PreviewCells.Clear();
            if (newMode == 0) {
                state.TargetContents = GetBlockRightOfActive(player);
            }
            else {
                state.TargetContents = 0;
            }
            ShowMessage(player, "ModeChanged", LanguageControl.Get("ConstructionWand", newMode == 0 ? "PlacementMode" : "AdjustMode"));
        }

        public void ToggleDirection(ComponentPlayer player, int wandValue) {
            int newDirection = GetDirection(wandValue) == 0 ? 1 : 0;
            UpdateActiveItem(player, SetDirection(wandValue, newDirection));
            ShowMessage(player, "DirectionChanged", LanguageControl.Get("ConstructionWand", newDirection == 0 ? "Horizontal" : "Vertical"));
        }

        public static void UpdateActiveItem(ComponentPlayer player, int newValue) {
            IInventory inventory = player.ComponentMiner?.Inventory;
            if (inventory == null) {
                return;
            }
            int slot = inventory.ActiveSlotIndex;
            int count = inventory.GetSlotCount(slot);
            if (count <= 0) {
                return;
            }
            inventory.RemoveSlotItems(slot, count);
            inventory.AddSlotItems(slot, newValue, count);
        }

        public static int GetBlockRightOfActive(ComponentPlayer player) {
            IInventory inventory = player.ComponentMiner?.Inventory;
            if (inventory == null) {
                return 0;
            }
            ShortInventoryWidget widget = player.ComponentGui?.ShortInventoryWidget;
            int max = widget != null
                ? inventory is ComponentCreativeInventory ? widget.MaxVisibleSlotsCountInCreative : widget.MaxVisibleSlotsCount
                : 10;
            int slot = SettingsManager.ShortInventoryLooping
                ? (inventory.ActiveSlotIndex + 1 + max) % max
                : inventory.ActiveSlotIndex + 1;
            if (slot < 0 || slot >= inventory.SlotsCount) {
                return 0;
            }
            int value = inventory.GetSlotValue(slot);
            if (value == 0) {
                return 0;
            }
            int contents = Terrain.ExtractContents(value);
            Block block = BlocksManager.Blocks[contents];
            return block != null && block.IsPlaceable_(value) ? contents : 0;
        }

        public int GetPlaceLimit(ComponentPlayer player, int wandValue) {
            int contents = Terrain.ExtractContents(wandValue);
            Block block = BlocksManager.Blocks[contents];
            int durability = block.GetDurability(wandValue);
            int limit = durability < 0 ? MaxBlocksPerUse : durability - block.GetDamage(wandValue);
            if (limit < 0) {
                limit = 0;
            }
            if (limit > MaxBlocksPerUse) {
                limit = MaxBlocksPerUse;
            }
            return limit;
        }

        public static int CountTargetItems(IInventory inventory, int contents) {
            if (inventory == null) {
                return 0;
            }
            int count = 0;
            for (int i = 0; i < inventory.SlotsCount; i++) {
                int value = inventory.GetSlotValue(i);
                if (value != 0 && Terrain.ExtractContents(value) == contents) {
                    count += inventory.GetSlotCount(i);
                }
            }
            return count;
        }

        public static int TakeTargetItem(IInventory inventory, int contents) {
            if (inventory == null) {
                return 0;
            }
            for (int i = 0; i < inventory.SlotsCount; i++) {
                int value = inventory.GetSlotValue(i);
                if (value != 0 && Terrain.ExtractContents(value) == contents) {
                    inventory.RemoveSlotItems(i, 1);
                    return value;
                }
            }
            return 0;
        }

        public int PlaceBlocks(ComponentPlayer player, ComponentMiner componentMiner, Ray3 ray, WandState state, int wandValue) {
            int targetContents = state.TargetContents;
            int wandContents = Terrain.ExtractContents(wandValue);
            if (targetContents == 0) {
                ShowMessage(player, "NoTarget");
                return 0;
            }
            IInventory inventory = componentMiner.Inventory;
            if (inventory == null) {
                return 0;
            }
            int limit = GetPlaceLimit(player, wandValue);
            if (limit <= 0) {
                ShowMessage(player, "Broken");
                return 0;
            }
            int available = CountTargetItems(inventory, targetContents);
            if (available <= 0) {
                ShowMessage(player, "NoItems", BlocksManager.Blocks[targetContents].GetDisplayName(m_subsystemTerrain, 0));
                return 0;
            }
            if (limit > available) {
                limit = available;
            }

            TerrainRaycastResult? hit = componentMiner.Raycast<TerrainRaycastResult>(ray, RaycastMode.Digging);
            if (!hit.HasValue) {
                return 0;
            }

            List<Point3> cells = ComputePlaceCells(hit.Value, targetContents, GetDirection(wandValue) != 0, limit);
            if (cells.Count == 0) {
                return 0;
            }

            int placed = 0;
            foreach (Point3 cell in cells) {
                if (placed >= limit) {
                    break;
                }
                int itemValue = TakeTargetItem(inventory, targetContents);
                if (itemValue == 0) {
                    break;
                }
                int placeValue = Terrain.MakeBlockValue(targetContents, 0, Terrain.ExtractData(itemValue));
                m_subsystemTerrain.ChangeCell(cell.X, cell.Y, cell.Z, placeValue, true, null);
                placed++;

                // 放置一个方块消耗 1 耐久（无限之杖除外，耐久在 csv 中定义为 -1）
                if (wandContents != m_infinityWandIndex) {
                    componentMiner.DamageActiveTool(1);
                    int currentWandContents = Terrain.ExtractContents(componentMiner.ActiveBlockValue);
                    if (currentWandContents != wandContents
                        || GetPlaceLimit(player, componentMiner.ActiveBlockValue) <= 0) {
                        break;
                    }
                }
            }

            if (placed > 0) {
                m_subsystemAudio.PlaySound("Audio/BlockPlaced", 1f, 0f, 0f, 0f);
                state.PreviewCells.Clear();
            }
            return placed;
        }

        /// <summary>
        ///     参考原 Java 版 ActionConstruction 的填充算法：
        ///     从点击的方块开始，沿着点击面的法线方向逐格扩展。
        ///     只有前方可以放置(空气或可替换方块)且后方有支撑(非空气)的格子才会被选中。
        ///     横向：水平面内扩展（侧面沿垂直于面方向的水平轴，上下表面沿东西/南北两个水平轴）。
        ///     竖向：沿竖直方向扩展（上下表面沿法线方向单向延伸）。
        /// </summary>
        public List<Point3> ComputePlaceCells(TerrainRaycastResult hit, int targetContents, bool vertical, int limit) {
            List<Point3> cells = [];
            if (limit <= 0) {
                return cells;
            }
            CellFace cellFace = hit.CellFace;
            Point3 normal = FaceNormal(cellFace.Face);
            Point3 start = Offset(cellFace.Point, normal.X, normal.Y, normal.Z);
            if (!IsPlaceableCell(start)) {
                return cells;
            }

            HashSet<Point3> allCandidates = [start];
            Queue<Point3> candidates = new();
            candidates.Enqueue(start);
            int face = cellFace.Face;

            while (candidates.Count > 0 && cells.Count < limit) {
                Point3 current = candidates.Dequeue();
                int supportContents = GetCellContentsSafe(Offset(current, -normal.X, -normal.Y, -normal.Z));
                if (supportContents == 0) {
                    continue;
                }
                cells.Add(current);
                foreach (Point3 offset in ExpansionOffsets(face, vertical)) {
                    Point3 next = Offset(current, offset.X, offset.Y, offset.Z);
                    if (IsPlaceableCell(next) && allCandidates.Add(next)) {
                        candidates.Enqueue(next);
                    }
                }
            }
            return cells;
        }

        public static IEnumerable<Point3> ExpansionOffsets(int face, bool vertical) {
            // 面编号：0=+Z 1=+X 2=-Z 3=-X 4=+Y(上) 5=-Y(下)
            if (vertical) {
                switch (face) {
                    case 0:
                    case 1:
                    case 2:
                    case 3:
                        yield return new Point3(0, 1, 0);
                        yield return new Point3(0, -1, 0);
                        yield break;
                    case 4:
                        yield return new Point3(0, 1, 0);
                        yield break;
                    default:
                        yield return new Point3(0, -1, 0);
                        yield break;
                }
            }
            switch (face) {
                case 0:
                case 2:
                    yield return new Point3(1, 0, 0);
                    yield return new Point3(-1, 0, 0);
                    break;
                case 1:
                case 3:
                    yield return new Point3(0, 0, 1);
                    yield return new Point3(0, 0, -1);
                    break;
                default:
                    yield return new Point3(1, 0, 0);
                    yield return new Point3(-1, 0, 0);
                    yield return new Point3(0, 0, 1);
                    yield return new Point3(0, 0, -1);
                    break;
            }
        }

        public bool IsPlaceableCell(Point3 p) {
            if (p.Y < 0 || p.Y > 255) {
                return false;
            }
            int value = m_subsystemTerrain.Terrain.GetCellValue(p.X, p.Y, p.Z);
            if (value == 0) {
                return true;
            }
            Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
            return !block.IsCollidable_(value);
        }

        public int GetCellContentsSafe(Point3 p) {
            if (p.Y < 0 || p.Y > 255) {
                return 0;
            }
            return m_subsystemTerrain.Terrain.GetCellContents(p.X, p.Y, p.Z);
        }

        public WandState GetState(ComponentPlayer player) {
            if (player.PlayerData == null) {
                return new WandState { Player = player };
            }
            if (!m_states.TryGetValue(player.PlayerData, out WandState state)) {
                state = new WandState { Player = player };
                m_states[player.PlayerData] = state;
            }
            return state;
        }

        public void ShowMessage(ComponentPlayer player, string key, params object[] args) {
            string text = LanguageControl.Get("ConstructionWand", key);
            if (args.Length > 0) {
                text = string.Format(text, args);
            }
            player.ComponentGui.DisplaySmallMessage(text, Color.White, false, true);
        }

        public void Draw(Camera camera, int drawOrder) {
            FlatBatch3D batch = null;
            foreach (WandState state in m_states.Values) {
                if (state.Player == null
                    || camera.GameWidget?.PlayerData != state.Player.PlayerData
                    || state.PreviewCells.Count == 0) {
                    continue;
                }
                batch ??= m_primitivesRenderer3D.FlatBatch(0, DepthStencilState.None);
                foreach (Point3 p in state.PreviewCells) {
                    Vector3 min = new(p.X, p.Y, p.Z);
                    batch.QueueBoundingBox(new BoundingBox(min, min + Vector3.One), PreviewColor);
                }
            }
            if (batch != null) {
                batch.Flush(camera.ViewProjectionMatrix);
            }
        }
    }
}