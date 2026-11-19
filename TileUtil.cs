using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Netcode;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Crops;
using StardewValley.GameData.WildTrees;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.Network;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using xTile;
using Object = StardewValley.Object;

namespace FarmQueue;

internal static class TileUtil
{
	internal enum TillBlock
	{
		None,
		NoMap,
		OutOfBounds,
		Occupied,
		ResourceClump,
		NotDiggable,
		NotReachable,
		NotSelected,
		WrongTool,
		NotSprinklered,
		/// <summary>这一格确实有活（空机器等着装），是背包放不下产物。追加在末尾，别插中间。</summary>
		BagFull
	}

	internal enum TileWork
	{
		None,
		Till,
		Weed,
		ArtifactSpot,
		SeedSpot,
		WaterDirt,
		WaterCrop,
		Sow,
		ScytheCrop,
		ScytheFlower,
		ScytheForage,
		ScytheMoss,
		ScytheBluegrass,
		ScytheGrass,
		ScytheDeadCrop,
		ScytheWeed,
		PickDeadCrop,
		PickEmptyDirt,
		PickFertilizedDirt,
		PickWeed,
		PickFloor,
		PickClump,
		PickObject,
		PickStone,
		PickBoulder,
		PickFence,
		AxeFruitTreeSeed,
		AxeFruitTreeSapling,
		AxeFruitTreeMature,
		AxeTreeSeed,
		AxeTreeSapling,
		AxeTreeMature,
		AxeStump,
		AxeDeadCrop,
		AxeLiveCrop,
		AxeGiantCrop,
		AxeTwig,
		AxeWeed,
		AxeFence,
		MachineLoad,
		SprinklerPlace,
		Fertilize,
		SaplingPlace,
		ScytheMachine,
		ScythePond,
		PlaceObject,
		// 摇灌木丛（春季大树莓 / 秋季黑莓 / 茶叶）。追加在末尾：别处有按枚举数值取范围的判定
		// （TillQueue 里 `(uint)(tileWork - 40) <= 2u` 那种），插在中间会把那些数值全挪位。
		ShakeBush,
		// 摇果树（摘树上的水果）。同样只能追加在末尾。
		HarvestFruit,
		// 摘下树上的采集器（树不砍）。同样只能追加在末尾。
		AxeTapper,
		// 砍灌木丛。原版只允许砍茶树和农场图边缘那几丛，普通灌木要装
		// Destroyable Bushes（Esca）这类 mod 才会被游戏自己认成可砍。追加在末尾。
		AxeBush
	}


	private static GameLocation? SprinklerCacheLocation;

	private static HashSet<Vector2>? SprinklerCacheSet;

	private static int SprinklerCacheTick;


	private static readonly Dictionary<string, bool> FlowerCache = new Dictionary<string, bool>();

	/// <summary>「机种|原料」→ 这台机器空着时收不收它。同一配方的答案恒定，一座桶阵只探一次。</summary>
	private static readonly Dictionary<string, bool> MachineAcceptCache = new Dictionary<string, bool>();

	private static GameLocation? StandableCacheLocation;

	private static bool[]? StandableCacheMap;

	private static int StandableCacheWidth;

	private static int StandableCacheHeight;

	private static int StandableCacheTick;


	// 「地形通行表」（掩码 249 = All 去掉 Characters|Farmers：建筑/栅栏/水/树/资源块/关着的门都算挡，
	// 动物和角色都不算）的缓存。
	// 为什么必须缓存：整图逐格问游戏太贵——IsTileBlockedBy 每格都要遍历 buildings / resourceClumps /
	// largeTerrainFeatures / furniture 再查地图属性，标准农场（141x65=9165 格）实测一次 BuildWalkField
	// 就要 5.77ms，大地图农场按面积线性放大（30000 格约 19ms、65536 格约 41ms）。
	// 而一次起步寻路最多要问三遍（BuildWalkField + FindPathAvoiding 的 GetWalkableMap），
	// 动物队列一个 tick 里连开 17 次 TryStart/TryStartThroughCritters 时就是 17 遍整图扫描。
	// 地形在两次挥动之间不会变，所以缓存；一直在动的角色不进缓存——每次调用把角色占位现算一遍叠上去
	// （O(角色数)，不是 O(地图格数)），语义和原来逐格调 IsWalkableThroughAnimals 完全一致。
	private static GameLocation? TerrainCacheLocation;

	private static bool[]? TerrainCacheMap;

	private static int TerrainCacheWidth;

	private static int TerrainCacheHeight;

	private static int TerrainCacheTick;

	private const int TerrainCacheTtl = 60;

	/// <summary>砍掉挡路的树/桩/巨大作物/栅栏后立刻作废站位缓存，别让旧地形继续参与距离计算。</summary>
	public static void InvalidateStandableCache()
	{
		StandableCacheLocation = null;
		StandableCacheMap = null;
		StandableCacheWidth = 0;
		StandableCacheHeight = 0;
		StandableCacheTick = 0;
		InvalidateTerrainCache();
	}

	/// <summary>放下/捡起实物之后作废地形通行表：新放的洒水器/树苗/实心物会改「哪格能走」。</summary>
	public static void InvalidateTerrainCache()
	{
		TerrainCacheLocation = null;
		TerrainCacheMap = null;
		TerrainCacheWidth = 0;
		TerrainCacheHeight = 0;
		TerrainCacheTick = 0;
	}

	/// <summary>局部作废窗口半径：砍掉的树/桩/巨岩/栅栏最大 3x3，掉下来的杂物也落在旁边，4 格足够盖住。</summary>
	private const int CacheRefreshRadius = 4;

	/// <summary>
	/// 局部作废：只重算 tile 周围 ±CacheRefreshRadius 那一小片（两张缓存表都补），不整图丢掉。
	/// 为什么要有这个版本：斧/镐跑里每清掉一个障碍都会作废一次站位缓存，整图作废意味着
	/// 下一次距离场要重新付一遍「全图逐格问游戏」（掩码 251 的 IsStandable 每格还要遍历
	/// animals + characters，是三种口径里最贵的），大地图上就是每清一格卡一下。
	/// 连通性不受影响：可通行表补好之后，BFS 距离场每次调用都是重算的。
	/// 缓存不在场、或这一格落在缓存覆盖不到/对不上的地方，就退回整图作废。
	/// </summary>
	public static void InvalidateStandableCache(GameLocation? location, Vector2 tile)
	{
		bool ok = RefreshCacheWindow(StandableCacheLocation, StandableCacheMap, StandableCacheWidth, StandableCacheHeight, location, tile, standable: true);
		ok &= RefreshCacheWindow(TerrainCacheLocation, TerrainCacheMap, TerrainCacheWidth, TerrainCacheHeight, location, tile, standable: false);
		if (!ok)
		{
			InvalidateStandableCache();
		}
	}

	/// <summary>把一张缓存表里 tile 周围那一小片重新问一遍游戏。返回 false = 补不了，调用方要整图作废。</summary>
	private static bool RefreshCacheWindow(GameLocation? cacheLocation, bool[]? cacheMap, int width, int height, GameLocation? location, Vector2 tile, bool standable)
	{
		try
		{
			if (cacheMap == null || width <= 0 || height <= 0 || cacheLocation == null)
			{
				return true;
			}
			if (location == null || cacheLocation != location)
			{
				// 缓存是别的地图的：这次改动动不到它，等下次用到那张图时自然会重建。
				return true;
			}
			int cx = (int)MathF.Floor(tile.X);
			int cy = (int)MathF.Floor(tile.Y);
			if (cx < 0 || cy < 0 || cx >= width || cy >= height)
			{
				return false;
			}
			int x0 = Math.Max(0, cx - CacheRefreshRadius);
			int x1 = Math.Min(width - 1, cx + CacheRefreshRadius);
			int y0 = Math.Max(0, cy - CacheRefreshRadius);
			int y1 = Math.Min(height - 1, cy + CacheRefreshRadius);
			for (int y = y0; y <= y1; y++)
			{
				for (int x = x0; x <= x1; x++)
				{
					Vector2 spot = new Vector2((float)x, (float)y);
					cacheMap[y * width + x] = (standable ? IsStandable(location, spot) : IsStandableIgnoringCritters(location, spot));
				}
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static Rectangle ToRect(Vector2 a, Vector2 b)
	{
		int num = (int)MathF.Floor(MathF.Min(a.X, b.X));
		int num2 = (int)MathF.Floor(MathF.Max(a.X, b.X));
		int num3 = (int)MathF.Floor(MathF.Min(a.Y, b.Y));
		int num4 = (int)MathF.Floor(MathF.Max(a.Y, b.Y));
		return new Rectangle(num, num3, num2 - num + 1, num4 - num3 + 1);
	}

	public static List<Vector2> BuildWorkOrder(Rectangle rect, Vector2 playerTile)
	{
		List<Vector2> list = new List<Vector2>(Math.Max(0, rect.Width * rect.Height));
		if (rect.Width <= 0 || rect.Height <= 0)
		{
			return list;
		}
		int left = (rect).Left;
		int num = (rect).Right - 1;
		int num2 = (int)MathF.Floor(playerTile.X);
		int value = (int)MathF.Floor(playerTile.Y);
		int num3 = Math.Clamp(value, (rect).Top, (rect).Bottom - 1);
		if (Math.Abs(num2 - left) <= Math.Abs(num2 - num))
		{
			for (int i = left; i <= num; i++)
			{
				list.Add(new Vector2((float)i, (float)num3));
			}
		}
		else
		{
			for (int num4 = num; num4 >= left; num4--)
			{
				list.Add(new Vector2((float)num4, (float)num3));
			}
		}
		List<int> list2 = new List<int>(rect.Height);
		for (int j = (rect).Top; j < (rect).Bottom; j++)
		{
			if (j != num3)
			{
				list2.Add(j);
			}
		}
		Vector2 val = list[list.Count - 1];
		while (list2.Count > 0)
		{
			int index = 0;
			bool flag = true;
			float num5 = float.MaxValue;
			for (int k = 0; k < list2.Count; k++)
			{
				int num6 = list2[k];
				float num7 = Vector2.DistanceSquared(val, new Vector2((float)left, (float)num6));
				if (num7 < num5)
				{
					num5 = num7;
					index = k;
					flag = true;
				}
				float num8 = Vector2.DistanceSquared(val, new Vector2((float)num, (float)num6));
				if (num8 < num5)
				{
					num5 = num8;
					index = k;
					flag = false;
				}
			}
			int num9 = list2[index];
			list2.RemoveAt(index);
			if (flag)
			{
				for (int l = left; l <= num; l++)
				{
					list.Add(new Vector2((float)l, (float)num9));
				}
			}
			else
			{
				for (int num10 = num; num10 >= left; num10--)
				{
					list.Add(new Vector2((float)num10, (float)num9));
				}
			}
			val = list[list.Count - 1];
		}
		return list;
	}

	public static TillBlock GetTillBlockReason(GameLocation location, Vector2 tile, ModConfig? config = null)
	{
		if (((location != null) ? location.Map : null) == null)
		{
			return TillBlock.NoMap;
		}
		int num = ((location.Map.Layers.Count > 0) ? location.Map.Layers[0].LayerWidth : 0);
		int num2 = ((location.Map.Layers.Count > 0) ? location.Map.Layers[0].LayerHeight : 0);
		if (num <= 0 || num2 <= 0)
		{
			return TillBlock.NoMap;
		}
		if (tile.X < 0f || tile.Y < 0f || tile.X >= (float)num || tile.Y >= (float)num2)
		{
			return TillBlock.OutOfBounds;
		}
		if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).ContainsKey(tile))
		{
			return TillBlock.Occupied;
		}
		if (location.objects.ContainsKey(tile))
		{
			return TillBlock.Occupied;
		}
		Rectangle tileArea = GetTileArea(tile);
		foreach (ResourceClump resourceClump in location.resourceClumps)
		{
			Rectangle boundingBox = ((TerrainFeature)resourceClump).getBoundingBox();
			if ((boundingBox).Intersects(tileArea))
			{
				return TillBlock.ResourceClump;
			}
		}
		if (location.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back", false) == null)
		{
			return TillBlock.NotDiggable;
		}
		if (location is MineShaft)
		{
			if (!location.IsTileOccupiedBy(tile, (CollisionMask)255, (CollisionMask)0, true))
			{
				return TillBlock.None;
			}
			return TillBlock.Occupied;
		}
		if (location.IsTileBlockedBy(tile, (CollisionMask)249, (CollisionMask)0, false))
		{
			return TillBlock.Occupied;
		}
		if (!IsTilePassableSafe(location, tile))
		{
			return TillBlock.NotReachable;
		}
		if (config != null && config.TillSprinkleredOnly && !IsSprinkleredTile(location, tile))
		{
			return TillBlock.NotSprinklered;
		}
		return TillBlock.None;
	}

	public static bool IsTilePassableSafe(GameLocation location, Vector2 tile)
	{
		try
		{
			return location.isTilePassable(tile);
		}
		catch
		{
			return true;
		}
	}

	/// <summary>
	/// 这格的地形特征按「真实移动口径」对玩家是不是可穿过：调 isPassable(Game1.player)。
	/// 「穿过作物」类 mod（PassableCrops 等）把放行补在 isPassable(Character) 上，且只在
	/// c 是农夫时生效；IsTileBlockedBy 内部调的是无参 isPassable()（等价 c=null），永远拿不到
	/// 那次放行——于是成片藤架（啤酒花挨着种）的内部格被当墙，找可站邻格全空，中间的格子
	/// 整块跳过。真实移动（isCollidingPosition）传的就是玩家角色，站位/走路场必须同口径。
	/// 没有这类 mod 时原版各 isPassable 都不看 c，结果与无参一致，行为不变。
	/// </summary>
	private static bool TerrainFeaturePassableToFarmer(GameLocation location, Vector2 tile)
	{
		try
		{
			if (location.terrainFeatures.TryGetValue(tile, out TerrainFeature feature) && feature != null)
			{
				return feature.isPassable(Game1.player);
			}
		}
		catch
		{
		}
		return false;
	}

	public static bool IsStandable(GameLocation location, Vector2 tile)
	{
		try
		{
			// 251 = All 去掉 Farmers(4)。原版农夫移动和存盘寻路都传 isFarmer=true，
			// GameLocation.isCollidingPosition 会因此跳过"玩家撞玩家"，玩家之间本来就能互相穿过。
			CollisionMask mask = (CollisionMask)251;
			if (TerrainFeaturePassableToFarmer(location, tile))
			{
				// 这格地形被放行（穿过作物类 mod）：把它从地形占用里摘掉再判，
				// 同格的其他东西（机器/角色/家具/建筑/地板）照挡。
				mask &= ~CollisionMask.TerrainFeatures;
			}
			return !location.IsTileBlockedBy(tile, mask, (CollisionMask)72, true);
		}
		catch
		{
			return false;
		}
	}

	public static bool IsStandableIgnoringCharacters(GameLocation location, Vector2 tile)
	{
		try
		{
			// 242 = 原来的 246 再去掉 Farmers(4)。这就是"玩家不算挡"的同一套语义，
			// 名字里的 IgnoringCharacters 是历史误称：246 实际排除的是 Buildings|Flooring。
			return !location.IsTileBlockedBy(tile, (CollisionMask)242, (CollisionMask)72, true);
		}
		catch
		{
			return false;
		}
	}

	public static bool CanTill(GameLocation location, Vector2 tile)
	{
		return GetTillBlockReason(location, tile) == TillBlock.None;
	}

	public static bool WouldTill(GameLocation location, Vector2 tile)
	{
		return CanTill(location, tile);
	}

	public static bool IsSprinkleredTile(GameLocation location, Vector2 tile)
	{
		return GetSprinkleredTiles(location).Contains(tile);
	}

	private static HashSet<Vector2> GetSprinkleredTiles(GameLocation location)
	{
		if (SprinklerCacheLocation == location && SprinklerCacheTick == Game1.ticks && SprinklerCacheSet != null)
		{
			return SprinklerCacheSet;
		}
		HashSet<Vector2> hashSet = new HashSet<Vector2>();
		if (location.objects != null)
		{
			foreach (Object value in location.objects.Values)
			{
				if (value == null || !value.IsSprinkler())
				{
					continue;
				}
				Vector2 tileLocation = value.TileLocation;
				if (tileLocation == Vector2.Zero && !location.objects.ContainsKey(tileLocation))
				{
					continue;
				}
				foreach (Vector2 item in SprinklerTilesFor(value, tileLocation))
				{
					if (!(location.doesTileHavePropertyNoNull((int)item.X, (int)item.Y, "NoSprinklers", "Back") == "T"))
					{
						hashSet.Add(item);
					}
				}
			}
		}
		SprinklerCacheLocation = location;
		SprinklerCacheSet = hashSet;
		SprinklerCacheTick = Game1.ticks;
		return hashSet;
	}

	private static List<Vector2> SprinklerTilesFor(Object sprinkler, Vector2 owner)
	{
		int modifiedRadiusForSprinkler = sprinkler.GetModifiedRadiusForSprinkler();
		if (modifiedRadiusForSprinkler == 0)
		{
			return Utility.getAdjacentTileLocations(owner);
		}
		List<Vector2> list = new List<Vector2>();
		for (int i = (int)owner.X - modifiedRadiusForSprinkler; i <= (int)owner.X + modifiedRadiusForSprinkler; i++)
		{
			for (int j = (int)owner.Y - modifiedRadiusForSprinkler; j <= (int)owner.Y + modifiedRadiusForSprinkler; j++)
			{
				list.Add(new Vector2((float)i, (float)j));
			}
		}
		return list;
	}

	public static bool IsScythe(Item? item)
	{
		MeleeWeapon val = (MeleeWeapon)((item is MeleeWeapon) ? item : null);
		if (val != null)
		{
			return ((Tool)val).isScythe();
		}
		return false;
	}

	public static bool IsPickaxe(Item? item)
	{
		return item is Pickaxe;
	}

	public static bool IsAxe(Item? item)
	{
		return item is Axe;
	}

	public static bool IsIridiumScythe(Item? item)
	{
		MeleeWeapon val = (MeleeWeapon)((item is MeleeWeapon) ? item : null);
		if (val != null)
		{
			return ((Item)val).ItemId == "66";
		}
		return false;
	}

	public static bool IsScytheHarvestCrop(GameLocation? location, Vector2 tile)
	{
		// 和 GetScytheWork 同一口径：盆栽作物也算（GetHoeDirtAtTile 地上/盆里的土都认）。
		HoeDirt? dirt = location?.GetHoeDirtAtTile(tile);
		if (dirt != null && dirt.crop != null)
		{
			return (int)dirt.crop.GetHarvestMethod() == 1;
		}
		return false;
	}

	public static bool CanScythePickForage(Item? item, GameLocation? location, Vector2 tile)
	{
		if (ModEntry.ScytheHarvestModLoaded)
		{
			return true;
		}
		TerrainFeature val = null;
		if (IsIridiumScythe(item) && location != null && ((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val))
		{
			return val is HoeDirt;
		}
		return false;
	}

	/// <summary>这一格上的花盆（没有就 null）。花盆里的土不在 terrainFeatures 里，得单独认。</summary>
	public static IndoorPot? PotAt(GameLocation location, Vector2 tile)
	{
		if (location?.objects == null)
		{
			return null;
		}
		Object val = null;
		return location.objects.TryGetValue(tile, out val) ? (val as IndoorPot) : null;
	}

	/// <summary>
	/// 这一格该不该浇：地上的耕地，或花盆里那块土（原版 GetHoeDirtAtTile 两种都认）。
	/// 盆里长着茶树时不算活——那块土浇了没用，否则每天都派一趟空差。
	/// </summary>
	private static TileWork GetWaterWork(GameLocation location, Vector2 tile)
	{
		IndoorPot? pot = PotAt(location, tile);
		if (pot != null && pot.bush.Value != null)
		{
			return TileWork.None;
		}
		HoeDirt dirt = location.GetHoeDirtAtTile(tile);
		if (dirt == null)
		{
			return TileWork.None;
		}
		if (dirt.state.Value == 1)
		{
			return TileWork.None;
		}
		Crop crop = dirt.crop;
		if (crop == null)
		{
			return TileWork.WaterDirt;
		}
		if (crop.forageCrop.Value && crop.whichForageCrop.Value == "2")
		{
			return TileWork.None;
		}
		return TileWork.WaterCrop;
	}

	public static TileWork GetTileWork(GameLocation location, Vector2 tile, Item? item, ModConfig config)
	{
		if (((location != null) ? location.Map : null) == null)
		{
			return TileWork.None;
		}
		int num = ((location.Map.Layers.Count > 0) ? location.Map.Layers[0].LayerWidth : 0);
		int num2 = ((location.Map.Layers.Count > 0) ? location.Map.Layers[0].LayerHeight : 0);
		if (num <= 0 || num2 <= 0)
		{
			return TileWork.None;
		}
		if (tile.X < 0f || tile.Y < 0f || tile.X >= (float)num || tile.Y >= (float)num2)
		{
			return TileWork.None;
		}
		if (item != null && IsScythe(item))
		{
			return GetScytheWork(location, tile, item, config);
		}
		Pickaxe val = (Pickaxe)((item is Pickaxe) ? item : null);
		if (val != null)
		{
			return GetPickaxeWork(location, tile, val, config);
		}
		Axe val2 = (Axe)((item is Axe) ? item : null);
		if (val2 != null)
		{
			return GetAxeWork(location, tile, val2, config);
		}
		Object val3 = (Object)((item is Object) ? item : null);
		if (val3 != null && !IsSowSeed((Item)val3))
		{
			if (IsSprinklerItem((Item)val3))
			{
				if (!CanPlaceSprinkler(location, tile))
				{
					return TileWork.None;
				}
				return TileWork.SprinklerPlace;
			}
			if (IsFertilizerItem((Item)val3))
			{
				return GetFertilizeWork(location, tile, val3);
			}
			if (IsPlaceableObjectItem((Item)val3))
			{
				return GetObjectPlaceWork(location, tile, val3);
			}
			return GetMachineWork(location, tile, val3, config);
		}
		if (item is WateringCan)
		{
			if (IsTileOccupiedByObject(location, tile))
			{
				return TileWork.None;
			}
			return GetWaterWork(location, tile);
		}
		Object val6 = (Object)((item is Object) ? item : null);
		if (val6 != null && IsSaplingItem((Item)val6))
		{
			return GetSaplingWork(location, tile, val6);
		}
		if (IsSowSeed(item))
		{
			Object val7 = (Object)((item is Object) ? item : null);
			if (val7 != null)
			{
				// 咖啡豆这类「既能让种子、又能进小桶」的东西：先问这一格的机器收不收它。
				// 旧版 IsSowSeed 一律走播种分支，小桶永远等不到放下——玩家说的「放小桶里没有用」。
				// 花盆得先摘出去：它对种子的 performObjectDropInAction(probe) 同样返回 true，
				// 不摘就会被判成「装机器」——受 LoadMachines 开关管、完工复核也按机器那套判，其实是下种。
				if (PotAt(location, tile) == null && GetMachineWork(location, tile, val7, config, onlyIfAccepts: true) == TileWork.MachineLoad)
				{
					return TileWork.MachineLoad;
				}
				if (!CanSowHere(location, tile, val7))
				{
					return TileWork.None;
				}
				if (IsTileOccupiedByObject(location, tile))
				{
					return TileWork.None;
				}
				if (config.SowSprinkleredOnly && !IsSprinkleredTile(location, tile))
				{
					return TileWork.None;
				}
				return TileWork.Sow;
			}
		}
		Object val8 = null;
		if (location.objects.TryGetValue(tile, out val8))
		{
			if (val8.IsWeeds())
			{
				return TileWork.Weed;
			}
			if (((Item)val8).QualifiedItemId == "(O)590")
			{
				return TileWork.ArtifactSpot;
			}
			if (((Item)val8).QualifiedItemId == "(O)SeedSpot")
			{
				return TileWork.SeedSpot;
			}
			return TileWork.None;
		}
		if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).ContainsKey(tile))
		{
			return TileWork.None;
		}
		if (GetTillBlockReason(location, tile, config) != TillBlock.None)
		{
			return TileWork.None;
		}
		return TileWork.Till;
	}

	private static TileWork GetScytheWork(GameLocation location, Vector2 tile, Item item, ModConfig config)
	{
		TerrainFeature val = null;
		((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val);
		Object val2 = null;
		location.objects.TryGetValue(tile, out val2);
		// 花盆里的土不在 terrainFeatures 里（IndoorPot.hoeDirt），只查 terrainFeatures 会让
		// 温室/室内的盆栽作物永远排不进队列：镰刀挥过去明明能割，队列却当那格没活。
		// GetHoeDirtAtTile 是原版自己用的入口，地上的土和盆里的土都认。
		HoeDirt val3 = location.GetHoeDirtAtTile(tile);
		Crop val4 = ((val3 != null) ? val3.crop : null);
		if (val4 != null && ((NetFieldBase<bool, NetBool>)(object)val4.dead).Value)
		{
			if (!config.ClearDeadCrops)
			{
				return TileWork.None;
			}
			return TileWork.ScytheDeadCrop;
		}
		if (val4 != null && val3.readyForHarvest())
		{
			if (!IsFlowerCrop(val4))
			{
				if (!config.HarvestCrops)
				{
					return TileWork.None;
				}
				return TileWork.ScytheCrop;
			}
			if (!config.HarvestFlowers)
			{
				return TileWork.None;
			}
			return TileWork.ScytheFlower;
		}
		if (val2 != null && val2.IsWeeds())
		{
			if (!config.ScytheClearWeeds)
			{
				return TileWork.None;
			}
			return TileWork.ScytheWeed;
		}
		if (val2 != null && ((NetFieldBase<bool, NetBool>)(object)val2.readyForHarvest).Value && ((NetFieldBase<Object, NetRef<Object>>)(object)val2.heldObject).Value != null)
		{
			if (!config.HarvestMachines)
			{
				return TileWork.None;
			}
			return TileWork.ScytheMachine;
		}
		if (config.HarvestMachines && GetPondWithOutputAt(location, tile) != null)
		{
			return TileWork.ScythePond;
		}
		if (val2 != null && val4 == null && val2.isForage())
		{
			if (!config.CollectForage)
			{
				return TileWork.None;
			}
			return TileWork.ScytheForage;
		}
		// 灌木丛上的浆果不在 objects 里，也不是 terrainFeatures，是 largeTerrainFeatures：
		// 上面这些分支全都看不见它，整框扫下来那几格永远是「没活儿」。
		// 它和地面采集品一样归「采集」开关管，动作也不是挥刀而是摇（见 TillQueue.ApplyBushShake）。
		// 花盆里的茶树走 IndoorPot.bush，同样不在 terrainFeatures 里，靠 BushAt 一起兜住。
		if (IsBerryBushReady(BushAt(location, tile)))
		{
			if (!config.CollectForage)
			{
				return TileWork.None;
			}
			return TileWork.ShakeBush;
		}
		// 果树上的水果同理：terrainFeatures 里是 FruitTree，原版是右键摇树把果子甩下来
		// （不是砍树）。以前这里只认 Tree（苔藓）和 Grass，果树的格子永远判成没活儿。
		FruitTree fruitTree = (FruitTree)((val is FruitTree) ? val : null);
		if (IsFruitTreeReady(fruitTree))
		{
			if (!config.CollectForage)
			{
				return TileWork.None;
			}
			return TileWork.HarvestFruit;
		}
		Tree val5 = (Tree)((val is Tree) ? val : null);
		if (val5 != null && ((NetFieldBase<bool, NetBool>)(object)val5.hasMoss).Value)
		{
			if (!config.HarvestMoss)
			{
				return TileWork.None;
			}
			return TileWork.ScytheMoss;
		}
		Grass val6 = (Grass)((val is Grass) ? val : null);
		if (val6 != null)
		{
			if (((NetFieldBase<byte, NetByte>)(object)val6.grassType).Value != 7)
			{
				if (!config.HarvestOtherGrass)
				{
					return TileWork.None;
				}
				return TileWork.ScytheGrass;
			}
			if (!config.HarvestBluegrass)
			{
				return TileWork.None;
			}
			return TileWork.ScytheBluegrass;
		}
		return TileWork.None;
	}

	private static TileWork GetPickaxeWork(GameLocation location, Vector2 tile, Pickaxe pickaxe, ModConfig config)
	{
		int upgradeLevel = ((Tool)pickaxe).UpgradeLevel;
		ResourceClump val = FindResourceClump(location, tile);
		if (val != null)
		{
			if (!config.PickResourceClumps || !CanPickClump(val, upgradeLevel))
			{
				return TileWork.None;
			}
			return TileWork.PickClump;
		}
		Object val2 = null;
		if (location.objects.TryGetValue(tile, out val2))
		{
			if (val2.IsWeeds())
			{
				if (!config.PickWeeds)
				{
					return TileWork.None;
				}
				return TileWork.PickWeed;
			}
			if (val2.IsBreakableStone())
			{
				if (!config.PickStones)
				{
					return TileWork.None;
				}
				return TileWork.PickStone;
			}
			if (((Item)val2).Name.Contains("Boulder"))
			{
				if (!config.PickResourceClumps || upgradeLevel < 2)
				{
					return TileWork.None;
				}
				return TileWork.PickBoulder;
			}
			Fence val3 = (Fence)((val2 is Fence) ? val2 : null);
			if (val3 != null)
			{
				if (!config.PickFences || ((NetFieldBase<bool, NetBool>)(object)val3.isGate).Value || !val3.IsValidRemovalTool((Tool)pickaxe))
				{
					return TileWork.None;
				}
				return TileWork.PickFence;
			}
			if (IsPickableObject(val2, config))
			{
				if (!config.PickObjects && !config.PickWorkingObjects)
				{
					return TileWork.None;
				}
				return TileWork.PickObject;
			}
			return TileWork.None;
		}
		TerrainFeature val4 = null;
		if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val4))
		{
			if (!(val4 is Flooring))
			{
				HoeDirt val5 = (HoeDirt)((val4 is HoeDirt) ? val4 : null);
				if (val5 != null)
				{
					if (val5.crop == null)
					{
						if (config.PickUnsprinkleredDirt && IsSprinkleredTile(location, tile))
						{
							return TileWork.None;
						}
						if (!val5.HasFertilizer())
						{
							if (!config.PickEmptyDirt)
							{
								return TileWork.None;
							}
							return TileWork.PickEmptyDirt;
						}
						if (!config.PickFertilizedDirt)
						{
							return TileWork.None;
						}
						return TileWork.PickFertilizedDirt;
					}
					if (!((NetFieldBase<bool, NetBool>)(object)val5.crop.dead).Value)
					{
						return TileWork.None;
					}
					if (!config.PickDeadCrops)
					{
						return TileWork.None;
					}
					return TileWork.PickDeadCrop;
				}
				return TileWork.None;
			}
			if (!config.PickFloor)
			{
				return TileWork.None;
			}
			return TileWork.PickFloor;
		}
		return TileWork.None;
	}

	public static string? GetWorkKindKey(GameLocation location, Vector2 tile, TileWork work)
	{
		try
		{
			if (work == TileWork.ScytheGrass || work == TileWork.ScytheBluegrass)
			{
				return "Grass";
			}
			TerrainFeature val = null;
			if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val) && val != null)
			{
				Tree val2 = (Tree)((val is Tree) ? val : null);
				if (val2 != null)
				{
					return "Tree:" + ((NetFieldBase<string, NetString>)(object)val2.treeType).Value;
				}
				FruitTree val3 = (FruitTree)((val is FruitTree) ? val : null);
				if (val3 != null)
				{
					return "FruitTree:" + ((NetFieldBase<string, NetString>)(object)val3.treeId).Value;
				}
				if (IsFeatureOwnWork(work))
				{
					return work.ToString();
				}
			}
			Object val4 = default(Object);
			if (location.objects.TryGetValue(tile, out val4) && val4 != null)
			{
				return work.ToString() + ":Object:" + ((Item)val4).QualifiedItemId;
			}
			ResourceClump val5 = FindResourceClump(location, tile);
			if (val5 != null)
			{
				return work.ToString() + ":Clump:" + ((NetFieldBase<int, NetInt>)(object)val5.parentSheetIndex).Value;
			}
			return work.ToString();
		}
		catch
		{
			return null;
		}
	}

	private static bool IsFeatureOwnWork(TileWork work)
	{
		bool result;
		switch (work)
		{
		case TileWork.WaterDirt:
		case TileWork.WaterCrop:
		case TileWork.Sow:
		case TileWork.ScytheCrop:
		case TileWork.ScytheDeadCrop:
		case TileWork.PickDeadCrop:
		case TileWork.PickEmptyDirt:
		case TileWork.PickFertilizedDirt:
		case TileWork.PickFloor:
		case TileWork.AxeDeadCrop:
		case TileWork.AxeLiveCrop:
		case TileWork.Fertilize:
			result = true;
			break;
		default:
			result = false;
			break;
		}
		return result;
	}


	public static ResourceClump? FindResourceClump(GameLocation location, Vector2 tile)
	{
		int num = (int)tile.X * 64;
		int num2 = (int)tile.Y * 64;
		foreach (ResourceClump resourceClump in location.resourceClumps)
		{
			if (resourceClump != null)
			{
				Rectangle boundingBox = ((TerrainFeature)resourceClump).getBoundingBox();
				if ((boundingBox).Contains(num, num2))
				{
					return resourceClump;
				}
			}
		}
		return null;
	}

	private static bool CanPickClump(ResourceClump clump, int level)
	{
		switch (((NetFieldBase<int, NetInt>)(object)clump.parentSheetIndex).Value)
		{
		case 148:
			return level >= 3;
		case 622:
			return level >= 3;
		case 672:
			return level >= 2;
		case 752:
		case 754:
		case 756:
		case 758:
			return true;
		default:
			return false;
		}
	}

	private static bool IsPickableObject(Object obj, ModConfig config)
	{
		if (obj.Fragility == 2)
		{
			return false;
		}
		Chest val = (Chest)((obj is Chest) ? obj : null);
		if (val != null)
		{
			if (!config.PickWorkingObjects)
			{
				return val.isEmpty();
			}
			return true;
		}
		if (obj.Type != "Crafting")
		{
			return false;
		}
		if (config.PickWorkingObjects)
		{
			return true;
		}
		if (((NetFieldBase<Object, NetRef<Object>>)(object)obj.heldObject).Value == null && !((NetFieldBase<bool, NetBool>)(object)obj.readyForHarvest).Value)
		{
			return obj.MinutesUntilReady <= 0;
		}
		return false;
	}

	private static TileWork GetAxeWork(GameLocation location, Vector2 tile, Axe axe, ModConfig config)
	{
		int upgradeLevel = ((Tool)axe).UpgradeLevel;
		ResourceClump val = FindResourceClump(location, tile);
		if (val != null)
		{
			if (val is GiantCrop)
			{
				if (!config.AxeGiantCrops)
				{
					return TileWork.None;
				}
				return TileWork.AxeGiantCrop;
			}
			if (!config.AxeStumps || !CanAxeClump(val, upgradeLevel))
			{
				return TileWork.None;
			}
			return TileWork.AxeStump;
		}
		TerrainFeature val2 = null;
		if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val2))
		{
			TerrainFeature val3 = val2;
			FruitTree val4 = (FruitTree)((val3 is FruitTree) ? val3 : null);
			if (val4 != null)
			{
				if (IsDead(val4))
				{
					return TileWork.None;
				}
				if (((NetFieldBase<bool, NetBool>)(object)val4.stump).Value)
				{
					if (!config.AxeStumps)
					{
						return TileWork.None;
					}
					return TileWork.AxeStump;
				}
				int value = ((NetFieldBase<int, NetInt>)(object)val4.growthStage).Value;
				if (value < 4)
				{
					if (value >= 1)
					{
						return config.AxeFruitTreeSaplings ? TileWork.AxeFruitTreeSapling : TileWork.None;
					}
					return config.AxeFruitTreeSeeds ? TileWork.AxeFruitTreeSeed : TileWork.None;
				}
				return config.AxeFruitTreeMature ? TileWork.AxeFruitTreeMature : TileWork.None;
			}
			Tree val5 = (Tree)((val3 is Tree) ? val3 : null);
			if (val5 != null)
			{
				if (((NetFieldBase<float, NetFloat>)(object)val5.health).Value <= -99f)
				{
					return TileWork.None;
				}
				if (((NetFieldBase<bool, NetBool>)(object)val5.stump).Value)
				{
					if (!config.AxeStumps)
					{
						return TileWork.None;
					}
					return TileWork.AxeStump;
				}
				int value2 = ((NetFieldBase<int, NetInt>)(object)val5.growthStage).Value;
				if (((NetFieldBase<bool, NetBool>)(object)val5.tapped).Value)
				{
					// 挂着采集器的树，原版斧头砍不进去（Tree.performToolAction 见到 tapped 直接 return false），
					// 但斧头砍在采集器上，采集器会掉（原版 Axe.DoFunction：先打树 → tapped 不掉血，
					// 再打物件 → 采集器连同树液作为掉落物弹出来）。斧头这一侧的规矩：
					//   · 「清除树液采集器」没开 → 整棵树不动（不替玩家拆机器）；
					//   · 开了 + 「清除树（成熟）」也开 → 砍树（第一斧先把采集器敲下来，接着砍）；
					//   · 只开了「清除树液采集器」→ 只摘采集器，树留着。
					// 镰刀那边的「收获机器产物」只负责收树液，跟摘不摘采集器无关。
					if (value2 < 5 || !config.AxeTappers)
					{
						return TileWork.None;
					}
					return config.AxeTreeMature ? TileWork.AxeTreeMature : TileWork.AxeTapper;
				}
				if (value2 < 5)
				{
					if (value2 >= 1)
					{
						return config.AxeTreeSaplings ? TileWork.AxeTreeSapling : TileWork.None;
					}
					return config.AxeTreeSeeds ? TileWork.AxeTreeSeed : TileWork.None;
				}
				return config.AxeTreeMature ? TileWork.AxeTreeMature : TileWork.None;
			}
			HoeDirt val6 = (HoeDirt)((val3 is HoeDirt) ? val3 : null);
			if (val6 != null && val6.crop != null)
			{
				if (((NetFieldBase<bool, NetBool>)(object)val6.crop.dead).Value)
				{
					if (!config.AxeDeadCrops)
					{
						return TileWork.None;
					}
					return TileWork.AxeDeadCrop;
				}
				if (!config.AxeLiveCrops)
				{
					return TileWork.None;
				}
				return TileWork.AxeLiveCrop;
			}
		}
		Object val7 = null;
		if (location.objects.TryGetValue(tile, out val7))
		{
			if (val7.IsTwig())
			{
				if (!config.AxeTwigs)
				{
					return TileWork.None;
				}
				return TileWork.AxeTwig;
			}
			if (val7.IsWeeds())
			{
				if (!config.AxeWeeds)
				{
					return TileWork.None;
				}
				return TileWork.AxeWeed;
			}
			Fence val8 = (Fence)((val7 is Fence) ? val7 : null);
			if (val8 != null && (((NetFieldBase<bool, NetBool>)(object)val8.isGate).Value || val8.IsValidRemovalTool((Tool)axe)))
			{
				if (!config.AxeFences)
				{
					return TileWork.None;
				}
				return TileWork.AxeFence;
			}
		}
		// 灌木丛既不在 terrainFeatures 也不在 objects 里，是 largeTerrainFeatures（见 FindBush）。
		// 「砍不砍得动」不自己判断，直接问游戏：Bush.isDestroyable()。原版只会对茶树（size 3）
		// 和农场图边缘那几丛答 true；装了 Destroyable Bushes 之后由它的 Harmony Postfix 替
		// 普通灌木（size 0/1/2/4）答 true —— 这样「有没有装那个 mod」不用我们去查注册表，
		// 玩家在它自己的配置里关掉某类灌木，这边也自动跟着不排。
		Bush val9 = FindBush(location, tile);
		if (val9 != null && IsChoppableBush(val9, tile))
		{
			if (!config.AxeBushes)
			{
				return TileWork.None;
			}
			if (!ModEntry.DestroyableBushesLoaded && ((Tool)axe).UpgradeLevel < 1)
			{
				// 原版 0 级斧头砍灌木只摇不扣血（Bush.performToolAction 里 `upgradeLevel >= 1`），
				// 排进去就是白挥 16 下。装了 mod 之后它把这句换成了可配置项（默认 0 级也能砍）。
				return TileWork.None;
			}
			return TileWork.AxeBush;
		}
		return TileWork.None;
	}

	/// <summary>
	/// 这一格值不值得排一把「砍灌木」。
	/// 一丛灌木的包围盒横跨 2~3 格（size 1 是 2 格宽，size 2 是 3 格宽），
	/// 只认它自己的基准格 —— 否则同一丛会被排成两三份活，砍完第一份之后剩下的全变成「已无活」。
	/// 茶树（size 3）不砍：那是产茶叶的作物，而且原版本来就允许斧头砍掉，是最容易被误伤的一种。
	/// 金核桃灌木（size 4）原版 performToolAction 直接 return false，isDestroyable 也管不到它。
	/// </summary>
	public static bool IsChoppableBush(Bush? bush, Vector2 tile)
	{
		if (bush == null)
		{
			return false;
		}
		if (bush.size.Value == 3 || bush.size.Value == 4)
		{
			return false;
		}
		Vector2 anchor = bush.Tile;
		if ((int)MathF.Floor(anchor.X) != (int)MathF.Floor(tile.X) || (int)MathF.Floor(anchor.Y) != (int)MathF.Floor(tile.Y))
		{
			return false;
		}
		return bush.isDestroyable();
	}

	/// <summary>
	/// 这一格上是不是还挂着树液采集器。
	///
	/// 采集器怎么下来的：原版就是这么做的 —— 斧头砍在采集器上，采集器连同树液作为掉落物弹出来。
	/// 原版 <c>Axe.DoFunction</c> 的命中顺序：
	///   ① 地面 → ② 地形（树：tapped 时直接 return false，掉血不了也倒不了）→ ③ 物件（采集器：被敲下来）。
	/// 所以「砍挂采集器的树」不用另想办法：**斧头真的挥下去，采集器自己会掉**，
	/// 掉出来的采集器和树液是 Debris，落在玩家脚边，走过去就捡进背包。
	///
	/// 本方法只用来验收（这一格的活干成了没有），不再替玩家拆采集器。
	/// </summary>
	public static bool HasTapperAt(GameLocation location, Vector2 tile)
	{
		Object obj = null;
		return location != null && location.objects.TryGetValue(tile, out obj) && obj != null && obj.IsTapper();
	}

	private static bool CanAxeClump(ResourceClump clump, int level)
	{
		int value = ((NetFieldBase<int, NetInt>)(object)clump.parentSheetIndex).Value;
		bool result = value switch
		{
			600 => level >= 1, 
			602 => level >= 2, 
			_ => false, 
		};
		return result;
	}

	public static ResourceClump? FindAxeClump(GameLocation location, Vector2 tile)
	{
		ResourceClump val = FindResourceClump(location, tile);
		bool flag = val != null && !(val is GiantCrop);
		bool flag2 = flag;
		if (flag2)
		{
			int value = ((NetFieldBase<int, NetInt>)(object)val.parentSheetIndex).Value;
			bool flag3 = ((value == 600 || value == 602) ? true : false);
			flag2 = flag3;
		}
		if (!flag2)
		{
			return null;
		}
		return val;
	}

	public static Bush? FindBush(GameLocation location, Vector2 tile)
	{
		if (location.largeTerrainFeatures == null)
		{
			return null;
		}
		Rectangle tileArea = GetTileArea(tile);
		foreach (LargeTerrainFeature largeTerrainFeature in location.largeTerrainFeatures)
		{
			Bush val = (Bush)((largeTerrainFeature is Bush) ? largeTerrainFeature : null);
			if (val != null)
			{
				Rectangle boundingBox = ((TerrainFeature)val).getBoundingBox();
				if ((boundingBox).Intersects(tileArea))
				{
					return val;
				}
			}
		}
		return null;
	}

	/// <summary>
	/// 这丛灌木此刻值不值得摇。<c>readyForHarvest()</c> 只说明「精灵图是挂果那一帧」，
	/// 还得再确认真的在这一季的产果窗口里（<c>inBloom()</c>：春 15~18 大树莓、秋 8~11 黑莓、
	/// 茶树要 20 天苗龄 + 每月 22 号以后），否则摇下去一颗都没有。
	/// 镇上的装饰灌木（townBush）永远不会掉果；金核桃灌木（size 4）交给原版自己的流程，也别碰。
	/// </summary>
	public static bool IsBerryBushReady(Bush? bush)
	{
		if (bush == null)
		{
			return false;
		}
		if (bush.size.Value == 4 || bush.townBush.Value)
		{
			return false;
		}
		if (!bush.readyForHarvest() || !bush.inBloom())
		{
			return false;
		}
		return bush.GetShakeOffItem() != null;
	}

	/// <summary>
	/// 这一格上有没有「能摇的灌木」——地里的（<c>largeTerrainFeatures</c>）和花盆里种的茶树
	/// （<c>IndoorPot.bush</c>）都算。花盆里的那棵不在 largeTerrainFeatures 里，
	/// 只查 <see cref="FindBush"/> 会把温室里那一排茶树全漏掉。
	/// </summary>
	public static Bush? BushAt(GameLocation location, Vector2 tile)
	{
		Bush? bush = FindBush(location, tile);
		if (bush != null)
		{
			return bush;
		}
		IndoorPot? pot = PotAt(location, tile);
		return pot?.bush.Value;
	}

	/// <summary>
	/// 这棵果树此刻有没有水果可摇。原版 <c>FruitTree.shake()</c> 只在
	/// 「长成了（growthStage ≥ 4）且不是树桩」时才把 <c>fruit</c> 甩下来，
	/// 所以判定跟着这两条走；树上没果时摇它只会掉叶子。
	/// </summary>
	public static bool IsFruitTreeReady(FruitTree? tree)
	{
		if (tree == null)
		{
			return false;
		}
		if (tree.stump.Value || tree.growthStage.Value < 4)
		{
			return false;
		}
		return tree.fruit.Count > 0;
	}

	public static TerrainFeature? GetTree(GameLocation location, Vector2 tile)
	{
		TerrainFeature val = null;
		bool flag = ((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val);
		bool flag2 = flag;
		if (flag2)
		{
			bool flag3 = ((val is Tree || val is FruitTree) ? true : false);
			flag2 = flag3;
		}
		if (!flag2)
		{
			return null;
		}
		return val;
	}

	public static bool IsStump(TerrainFeature? feature)
	{
		return (feature as Tree)?.stump.Value ?? (feature as FruitTree)?.stump.Value ?? false;
	}

	/// <summary>已砍死、但还没被游戏从 terrainFeatures 里移除的树/果树。</summary>
	public static bool IsDead(TerrainFeature? feature)
	{
		if (feature is Tree tree)
		{
			return ((NetFieldBase<float, NetFloat>)(object)tree.health).Value <= -99f;
		}
		if (feature is FruitTree fruitTree)
		{
			return ((NetFieldBase<float, NetFloat>)(object)fruitTree.health).Value <= -99f;
		}
		return false;
	}

	public static bool IsMachineLoadItem(Item? item)
	{
		Object val = (Object)((item is Object) ? item : null);
		if (val != null && !IsSowSeed((Item)val) && !IsSprinklerItem((Item)val) && !IsFertilizerItem((Item)val))
		{
			return !IsPlaceableObjectItem((Item)val);
		}
		return false;
	}

	/// <summary>
	/// 是不是「同一种物品的另一个品质」：物品 id 相同，品质不管。
	/// 用途：手上那一叠用完之后，自动接上背包里的另一个品质（银星 / 金星 / 铱星）。
	/// 果酒 / 果汁 / 腌菜在原版里共用一个 id（(O)348 / (O)350 / (O)344），
	/// 原料记在 preservedParentSheetIndex 上，所以那里也得一样——
	/// 否则杨桃酒会去顶蓝莓酒。
	/// </summary>
	public static bool IsSameItemFamily(Item? a, Item? b)
	{
		if (a == null || b == null || a.QualifiedItemId != b.QualifiedItemId)
		{
			return false;
		}
		Object oa = a as Object;
		Object ob = b as Object;
		if (oa != null && ob != null)
		{
			string pa = oa.preservedParentSheetIndex.Value;
			string pb = ob.preservedParentSheetIndex.Value;
			if (!string.Equals(pa, pb, StringComparison.Ordinal))
			{
				return false;
			}
		}
		return true;
	}

	public static bool IsSprinklerItem(Item? item)
	{
		Object val = (Object)((item is Object) ? item : null);
		if (val != null)
		{
			return val.GetBaseRadiusForSprinkler() >= 0;
		}
		return false;
	}

	public static int SprinklerGapFor(ModConfig config)
	{
		return Math.Clamp(config.SprinklerGap, 0, 5);
	}

	public static bool TrellisLayout(ModConfig config)
	{
		return string.Equals(config.SprinklerLayout, "trellis", StringComparison.Ordinal);
	}

	public static bool IsFertilizerItem(Item? item)
	{
		Object val = (Object)((item is Object) ? item : null);
		if (val != null)
		{
			return ((Item)val).Category == -19;
		}
		return false;
	}

	public static bool IsSaplingItem(Item? item)
	{
		Object val = (Object)((item is Object) ? item : null);
		if (val != null)
		{
			return val.isSapling();
		}
		return false;
	}

	public static bool IsPlaceableObjectItem(Item? item)
	{
		Object val = (Object)((item is Object) ? item : null);
		if (val != null && ((Item)val).isPlaceable() && !IsSowSeed((Item)val) && !IsFertilizerItem((Item)val))
		{
			return !IsSprinklerItem((Item)val);
		}
		return false;
	}

	public static List<Vector2> BuildSparseWorkOrder(IEnumerable<Vector2> tiles, Vector2 playerTile)
	{
		List<Vector2> list = new List<Vector2>(tiles);
		if (IsSingleLine(list, out var horizontal))
		{
			float num = float.MaxValue;
			float num2 = float.MinValue;
			foreach (Vector2 item in list)
			{
				float num3 = (horizontal ? item.X : item.Y);
				if (num3 < num)
				{
					num = num3;
				}
				if (num3 > num2)
				{
					num2 = num3;
				}
			}
			float num4 = (horizontal ? playerTile.X : playerTile.Y);
			bool ascending = num4 - num <= num2 - num4;
			list.Sort(delegate(Vector2 a, Vector2 b)
			{
				return (!horizontal) ? ((!ascending) ? b.Y.CompareTo(a.Y) : a.Y.CompareTo(b.Y)) : ((!ascending) ? b.X.CompareTo(a.X) : a.X.CompareTo(b.X));
			});
			return list;
		}
		List<Vector2> list2 = new List<Vector2>(list.Count);
		Vector2 val = playerTile;
		while (list.Count > 0)
		{
			int index = 0;
			Vector2 val2 = list[0];
			float num5 = Vector2.DistanceSquared(val, val2);
			for (int num6 = 1; num6 < list.Count; num6++)
			{
				Vector2 val3 = list[num6];
				float num7 = Vector2.DistanceSquared(val, val3);
				if (num7 < num5 || (num7 == num5 && (val3.Y < val2.Y || (val3.Y == val2.Y && val3.X < val2.X))))
				{
					index = num6;
					val2 = val3;
					num5 = num7;
				}
			}
			list2.Add(val2);
			val = val2;
			list[index] = list[list.Count - 1];
			list.RemoveAt(list.Count - 1);
		}
		return list2;
	}

	/// <summary>
	/// 锄地/浇水的扫法：先按连通块分（中间断一格算两块），块间按离玩家近的先做；
	/// 块内从玩家脚下那一行开始，先扫近的半边再扫远的半边，然后蛇形往下、扫到底折回往上
	/// （细节见 <see cref="SweepBlock"/>）。玩家不在块里时从最近的行、最近的一端进。
	/// <see cref="BuildPathWorkOrder"/> 的「最近优先」在空地上会退化成以起点为圆心的同心方环，
	/// 玩家看到的是「上下左右来回锄」；不分块的话一整行会横穿水沟拐到对面去。
	/// <paramref name="interleaveOrphans"/> 开启时，不相连的小块不再等大块扫完才轮到：
	/// 只要它离主路线不远（绕行 ≤ 5 格），就插到路线里离它最近的位置顺手做掉，
	/// 否则外围零散单格会攒到最后再横穿回来。
	/// </summary>
	public static List<Vector2> BuildRowSWorkOrder(IEnumerable<Vector2> tiles, Vector2 playerTile, bool interleaveOrphans = false)
	{
		List<Vector2> list = new List<Vector2>(tiles);
		if (list.Count <= 1)
		{
			return list;
		}
		List<List<Vector2>> blocks = SplitConnectedBlocks(list, 3);
		if (!interleaveOrphans || blocks.Count <= 1)
		{
			return SweepBlocksInNearestOrder(blocks, playerTile);
		}
		const int smallBlockSize = 6;
		const float maxDetour = 5f;
		List<List<Vector2>> largeBlocks = blocks.Where((List<Vector2> block) => block.Count > smallBlockSize).ToList();
		List<List<Vector2>> smallBlocks = blocks.Where((List<Vector2> block) => block.Count <= smallBlockSize).ToList();
		if (largeBlocks.Count == 0)
		{
			return SweepBlocksInNearestOrder(blocks, playerTile);
		}
		List<Vector2> result = SweepBlocksInNearestOrder(largeBlocks, playerTile);
		while (smallBlocks.Count > 0)
		{
			int bestBlock = -1;
			int bestIndex = -1;
			float bestCost = float.MaxValue;
			for (int i = 0; i < smallBlocks.Count; i++)
			{
				float cost;
				int index;
				if (TryFindOpportunisticInsertion(result, playerTile, smallBlocks[i], maxDetour, out cost, out index) && cost < bestCost)
				{
					bestCost = cost;
					bestBlock = i;
					bestIndex = index;
				}
			}
			if (bestBlock < 0)
			{
				break;
			}
			Vector2 previous = ((bestIndex == 0) ? playerTile : result[bestIndex - 1]);
			List<Vector2> ordered = new List<Vector2>();
			SweepBlock(smallBlocks[bestBlock], previous, ordered);
			result.InsertRange(bestIndex, ordered);
			smallBlocks.RemoveAt(bestBlock);
		}
		if (smallBlocks.Count > 0)
		{
			Vector2 cursor = ((result.Count > 0) ? result[result.Count - 1] : playerTile);
			result.AddRange(SweepBlocksInNearestOrder(smallBlocks, cursor));
		}
		return result;
	}

	private static List<Vector2> SweepBlocksInNearestOrder(List<List<Vector2>> blocks, Vector2 cursor)
	{
		List<Vector2> result = new List<Vector2>();
		Vector2 previous = cursor;
		while (blocks.Count > 0)
		{
			int pick = 0;
			float best = float.MaxValue;
			float bestMinY = float.MaxValue;
			float bestMinX = float.MaxValue;
			for (int i = 0; i < blocks.Count; i++)
			{
				float minY = float.MaxValue;
				float minX = float.MaxValue;
				float nearest = float.MaxValue;
				foreach (Vector2 tile in blocks[i])
				{
					minY = MathF.Min(minY, tile.Y);
					minX = MathF.Min(minX, tile.X);
					nearest = MathF.Min(nearest, ChebyshevDistance(previous, tile));
				}
				if (nearest < best || (nearest == best && (minY < bestMinY || (minY == bestMinY && minX < bestMinX))))
				{
					best = nearest;
					bestMinY = minY;
					bestMinX = minX;
					pick = i;
				}
			}
			previous = SweepBlock(blocks[pick], previous, result);
			blocks.RemoveAt(pick);
		}
		return result;
	}

	private static bool TryFindOpportunisticInsertion(List<Vector2> route, Vector2 start, List<Vector2> block, float maxDetour, out float bestCost, out int bestIndex)
	{
		bestCost = float.MaxValue;
		bestIndex = -1;
		for (int i = 0; i <= route.Count; i++)
		{
			Vector2 previous = ((i == 0) ? start : route[i - 1]);
			Vector2 next = ((i < route.Count) ? route[i] : previous);
			Vector2 pivot = NearestBlockPivot(block, previous, next);
			float cost = ChebyshevDistance(previous, pivot);
			if (i < route.Count)
			{
				cost += ChebyshevDistance(pivot, next) - ChebyshevDistance(previous, next);
			}
			if (cost <= maxDetour && cost < bestCost)
			{
				bestCost = cost;
				bestIndex = i;
			}
		}
		return bestIndex >= 0;
	}

	private static Vector2 NearestBlockPivot(List<Vector2> block, Vector2 previous, Vector2 next)
	{
		Vector2 midpoint = (previous + next) * 0.5f;
		Vector2 best = block[0];
		float bestDistance = ChebyshevDistance(midpoint, best);
		for (int i = 1; i < block.Count; i++)
		{
			Vector2 candidate = block[i];
			float distance = ChebyshevDistance(midpoint, candidate);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = candidate;
			}
		}
		return best;
	}

	/// <summary>镰刀作物批的 S 型：按 bandHeight 行一条带子排，带内按列蛇形。
	/// 为什么不是逐行：镰刀一刀清掉 3~5 行 × 3~4 列（实机日志 0.5.67），逐行排等于把同一片地
	/// 排三遍，后两遍只剩零头；零头在队列里隔着整行，人扫完一行就得横穿回另一头捡零头
	/// （实机现象：第二行扫到右端后飞回左端）。按带子排则一条带子一趟扫完（含它覆盖的上下行），
	/// 下一条带子从收尾那端折返，零头只出现在带子内部、就地处理。</summary>
	public static List<Vector2> BuildBandSWorkOrder(IEnumerable<Vector2> tiles, Vector2 playerTile, int bandHeight)
	{
		List<Vector2> list = new List<Vector2>(tiles);
		if (list.Count <= 1)
		{
			return list;
		}
		// 带子按「空行」切：行与行之间缺了一行以上就断带，断出来的段内再按 bandHeight 行一带。
		// 旧版按固定 (y-minY)/3 切，隔一行种的田会把不相邻的两行兜进同一带（实机：5 万格日志里
		// 一段带子同时含 y=9 的整行和 y=11 的一个零头），带内按列走就成了在那两行之间来回跳，
		// 玩家看到的就是「从右到左收完又回到右边」。密植田各行连续，切出来和旧版完全一样。
		Dictionary<float, int> rowBandOf = new Dictionary<float, int>();
		List<float> rowKeys = new List<float>();
		foreach (Vector2 t in list)
		{
			if (!rowBandOf.ContainsKey(t.Y))
			{
				rowBandOf[t.Y] = -1;
				rowKeys.Add(t.Y);
			}
		}
		rowKeys.Sort();
		int bandIndex = -1;
		int rowsInBand = 0;
		float previousRow = float.NaN;
		foreach (float row in rowKeys)
		{
			bool continuesBand = rowsInBand > 0 && rowsInBand < bandHeight && MathF.Abs(row - previousRow - 1f) < 0.5f;
			if (!continuesBand)
			{
				bandIndex++;
				rowsInBand = 0;
			}
			rowBandOf[row] = bandIndex;
			rowsInBand++;
			previousRow = row;
		}
		List<List<Vector2>> bands = list.GroupBy((Vector2 t) => rowBandOf[t.Y]).OrderBy((IGrouping<int, Vector2> g) => g.Key).Select((IGrouping<int, Vector2> g) =>
		{
			List<Vector2> row = g.ToList();
			row.Sort((Vector2 a, Vector2 b) => (a.X != b.X) ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
			return row;
		}).ToList();
		List<Vector2> result = new List<Vector2>(list.Count);
		SweepBands(bands, playerTile, result);
		return result;
	}

	/// <summary>SweepBlock 的带子版：从离玩家近的那条带子起单向推进到底再折回；
	/// 首带从离玩家近的那端进，之后每带接着上一带收尾的那端（蛇形）。</summary>
	private static void SweepBands(List<List<Vector2>> bands, Vector2 cursor, List<Vector2> result)
	{
		if (bands.Count == 0)
		{
			return;
		}
		float firstY = bands[0][0].Y;
		float lastY = bands[bands.Count - 1][0].Y;
		bool startTop = MathF.Abs(firstY - cursor.Y) <= MathF.Abs(lastY - cursor.Y);
		bool endedLeft = false;
		bool firstBand = true;
		int r = startTop ? 0 : (bands.Count - 1);
		int step = startTop ? 1 : -1;
		while (r >= 0 && r < bands.Count)
		{
			List<Vector2> band = bands[r];
			bool enterLeft = endedLeft;
			if (firstBand)
			{
				enterLeft = MathF.Abs(band[0].X - cursor.X) <= MathF.Abs(band[band.Count - 1].X - cursor.X);
				firstBand = false;
			}
			if (enterLeft)
			{
				result.AddRange(band);
				endedLeft = false;
			}
			else
			{
				for (int i = band.Count - 1; i >= 0; i--)
				{
					result.Add(band[i]);
				}
				endedLeft = true;
			}
			r += step;
		}
	}

	/// <summary>按 4 邻接连通切块；中间断一格就算两块（斜角不算连通，免得跨缝）。</summary>
	private static List<List<Vector2>> SplitConnectedBlocks(List<Vector2> tiles, int rowGap = 0)
	{
		HashSet<Vector2> remaining = new HashSet<Vector2>(tiles);
		List<List<Vector2>> blocks = new List<List<Vector2>>();
		Queue<Vector2> queue = new Queue<Vector2>();
		foreach (Vector2 start in tiles)
		{
			if (!remaining.Remove(start))
			{
				continue;
			}
			List<Vector2> block = new List<Vector2> { start };
			queue.Clear();
			queue.Enqueue(start);
			while (queue.Count > 0)
			{
				Vector2 current = queue.Dequeue();
				Vector2[] neighbors = new Vector2[4]
				{
					current + new Vector2(1f, 0f),
					current + new Vector2(-1f, 0f),
					current + new Vector2(0f, 1f),
					current + new Vector2(0f, -1f)
				};
				foreach (Vector2 next in neighbors)
				{
					if (remaining.Remove(next))
					{
						block.Add(next);
						queue.Enqueue(next);
					}
				}
				// 同行容隙：rowGap>0 时，同一行内水平距离 ≤ rowGap 的格子也算连通，
				// 这样一小段空地（走道/未耕格）不会把一行拆成两块、打断 S 形。
				if (rowGap > 1)
				{
					for (int dx = 2; dx <= rowGap; dx++)
					{
						Vector2 right = current + new Vector2(dx, 0f);
						if (remaining.Remove(right))
						{
							block.Add(right);
							queue.Enqueue(right);
						}
						Vector2 left = current + new Vector2(-dx, 0f);
						if (remaining.Remove(left))
						{
							block.Add(left);
							queue.Enqueue(left);
						}
					}
				}
			}
			blocks.Add(block);
		}
		return blocks;
	}

	/// <summary>
	/// 一块地内部的扫法（玩家定的规则）：
	/// 1. 从 cursor 所在那一行开始；那一行先扫离 cursor 近的半边，扫完回头扫远的半边；
	/// 2. 之后一行接一行往下蛇形：下一行从上一行收尾的那一端开始，扫到另一端；
	/// 3. 扫到底后折回来，从起始行的上一行开始继续蛇形往上，把剩下的行扫完；
	/// 4. cursor 不在块里（在圈外）时，从离 cursor 最近的那行、离 cursor 近的那一端进。
	/// 返回最后一格，供下一块接着算。
	/// </summary>
	private static Vector2 SweepBlock(List<Vector2> block, Vector2 cursor, List<Vector2> result)
	{
		List<List<Vector2>> rows = block.GroupBy(delegate(Vector2 t)
		{
			return t.Y;
		}).OrderBy(delegate(IGrouping<float, Vector2> g)
		{
			return g.Key;
		}).Select(delegate(IGrouping<float, Vector2> g)
		{
			List<Vector2> row = g.ToList();
			row.Sort(delegate(Vector2 a, Vector2 b)
			{
				return a.X.CompareTo(b.X);
			});
			return row;
		}).ToList();
		// 从离玩家最近的一条「边」开始，朝另一边一整趟 S 扫完：中间起头会把地分成两半，
		// 总要多折一趟（实机：从脚下开始，玩家周围先挖出一块缺口，之后路线来回走）。
		// 起始边那一行从离玩家近的一端进，之后每行接着上一行收尾的那一端（蛇形）。
		bool startTop = MathF.Abs(rows[0][0].Y - cursor.Y) <= MathF.Abs(rows[rows.Count - 1][0].Y - cursor.Y);
		bool endedLeft = false;
		bool first = true;
		int r = (startTop ? 0 : (rows.Count - 1));
		int step = (startTop ? 1 : (-1));
		while (r >= 0 && r < rows.Count)
		{
			List<Vector2> row = rows[r];
			bool enterLeft = endedLeft;
			if (first)
			{
				enterLeft = (cursor.X - row[0].X) <= (row[row.Count - 1].X - cursor.X);
				first = false;
			}
			if (enterLeft)
			{
				result.AddRange(row);
				endedLeft = false;
			}
			else
			{
				List<Vector2> reversed = row.ToList();
				reversed.Reverse();
				result.AddRange(reversed);
				endedLeft = true;
			}
			r += step;
		}
		return ((result.Count > 0) ? result[result.Count - 1] : cursor);
	}

	public static List<Vector2> BuildPathWorkOrder(IEnumerable<Vector2> tiles, Vector2 playerTile, GameLocation location, bool sealPlacedTiles = false, bool boundFlood = true)
	{
		List<Vector2> list = new List<Vector2>(tiles);
		if (list.Count == 0)
		{
			return list;
		}
		if (IsSingleLine(list, out var horizontal))
		{
			float num = float.MaxValue;
			float num2 = float.MinValue;
			foreach (Vector2 item in list)
			{
				float num3 = (horizontal ? item.X : item.Y);
				if (num3 < num)
				{
					num = num3;
				}
				if (num3 > num2)
				{
					num2 = num3;
				}
			}
			float num4 = (horizontal ? playerTile.X : playerTile.Y);
			bool ascending = num4 - num <= num2 - num4;
			list.Sort(delegate(Vector2 a, Vector2 b)
			{
				return (!horizontal) ? ((!ascending) ? b.Y.CompareTo(a.Y) : a.Y.CompareTo(b.Y)) : ((!ascending) ? b.X.CompareTo(a.X) : a.X.CompareTo(b.X));
			});
			return list;
		}
		Map map = location.Map;
		int num5 = ((map != null && map.Layers.Count > 0) ? location.Map.Layers[0].LayerWidth : 0);
		Map map2 = location.Map;
		int num6 = ((map2 != null && map2.Layers.Count > 0) ? location.Map.Layers[0].LayerHeight : 0);
		int num7 = num5 * num6;
		if (num5 <= 0 || num6 <= 0 || (long)num5 * (long)num6 > 65536 || list.Count * ((long)num5 * (long)num6) > 100000000)
		{
			return BuildSparseWorkOrder(list, playerTile);
		}
		// 这张表和 GetStandableMap 是同一个口径（掩码 251 的 IsStandable），直接拿缓存克隆一份：
		// 下面会把玩家脚下那格强行改成 true，克隆是为了不把缓存改脏。
		// 原来这里每格都问一次游戏，而本方法在镰刀/斧/镐跑里每挥一次就可能被调一次（RequeueMissedCovers）。
		bool[] array;
		bool[]? cachedStandable = GetStandableMap(location, out var standableWidth, out var standableHeight);
		if (cachedStandable != null && standableWidth == num5 && standableHeight == num6)
		{
			array = (bool[])cachedStandable.Clone();
		}
		else
		{
			array = new bool[num7];
			for (int num8 = 0; num8 < num6; num8++)
			{
				for (int num9 = 0; num9 < num5; num9++)
				{
					array[num8 * num5 + num9] = IsStandable(location, new Vector2((float)num9, (float)num8));
				}
			}
		}
		int num10 = Math.Clamp((int)MathF.Floor(playerTile.X), 0, num5 - 1);
		int num11 = Math.Clamp((int)MathF.Floor(playerTile.Y), 0, num6 - 1);
		int num12 = num11 * num5 + num10;
		array[num12] = true;
		int[][] array2 = new int[list.Count][];
		for (int num13 = 0; num13 < list.Count; num13++)
		{
			int num14 = (int)MathF.Floor(list[num13].X);
			int num15 = (int)MathF.Floor(list[num13].Y);
			List<int> list2 = new List<int>(9);
			for (int num16 = -1; num16 <= 1; num16++)
			{
				for (int num17 = -1; num17 <= 1; num17++)
				{
					int num18 = num14 + num17;
					int num19 = num15 + num16;
					if (num18 >= 0 && num18 < num5 && num19 >= 0 && num19 < num6)
					{
						int num20 = num19 * num5 + num18;
						if (array[num20])
						{
							list2.Add(num20);
						}
					}
				}
			}
			array2[num13] = list2.ToArray();
		}
		int[] array3 = new int[num7];
		Queue<int> queue = new Queue<int>(Math.Min(num7, 65536));
		List<Vector2> list3 = new List<Vector2>(list.Count);
		List<int> list4 = new List<int>(list.Count);
		for (int num21 = 0; num21 < list.Count; num21++)
		{
			list4.Add(num21);
		}
		// 灌水范围：把所有待办格的「可站位」和玩家自己框起来，四周留余量。
		// 为什么值得做：这个循环每挑一格就把整张地图重灌一遍（Array.Fill + BFS），
		// 200x150 的地图上就是 3 万格 × N 格待办；而镰/斧/镐跑里每挥一次都可能调一次本方法。
		// 余量给得宽（至少 12 格，或跨度的一半）：框太紧会把「要绕出框才连得通」的格读成不可达。
		int rx0 = num10;
		int rx1 = num10;
		int ry0 = num11;
		int ry1 = num11;
		for (int spotOwner = 0; spotOwner < array2.Length; spotOwner++)
		{
			int[] spots = array2[spotOwner];
			for (int s = 0; s < spots.Length; s++)
			{
				int sx = spots[s] % num5;
				int sy = spots[s] / num5;
				rx0 = Math.Min(rx0, sx);
				rx1 = Math.Max(rx1, sx);
				ry0 = Math.Min(ry0, sy);
				ry1 = Math.Max(ry1, sy);
			}
		}
		if (boundFlood)
		{
			// 余量规则和三处裁剪共用：至少 12 格、最多 24 格、或跨度的一半。
			// 下限保证绕湖绕围栏的路不会被裁断（裁断=距离被高估=顺序可能变），上限保证大选框时还真省得下来。
			int margin = Math.Max(12, Math.Min(24, Math.Max(rx1 - rx0, ry1 - ry0) / 2));
			rx0 = Math.Max(0, rx0 - margin);
			ry0 = Math.Max(0, ry0 - margin);
			rx1 = Math.Min(num5 - 1, rx1 + margin);
			ry1 = Math.Min(num6 - 1, ry1 + margin);
		}
		else
		{
			// 兜底那一趟：灌满整张图，和裁剪之前的口径逐格一致。
			rx0 = 0;
			ry0 = 0;
			rx1 = num5 - 1;
			ry1 = num6 - 1;
		}
		// 灌水抽成局部函数：裁剪版和整图兜底版共用同一段 BFS，只是范围参数不同。
		void FloodFrom(int originIndex, int fx0, int fy0, int fx1, int fy1)
		{
			for (int fillRow = fy0; fillRow <= fy1; fillRow++)
			{
				Array.Fill(array3, -1, fillRow * num5 + fx0, fx1 - fx0 + 1);
			}
			queue.Clear();
			array3[originIndex] = 0;
			queue.Enqueue(originIndex);
			while (queue.Count > 0)
			{
				int num23 = queue.Dequeue();
				int num24 = array3[num23] + 1;
				int cellX = num23 % num5;
				int cellY = num23 / num5;
				if (cellY > fy0 && array3[num23 - num5] < 0 && array[num23 - num5])
				{
					array3[num23 - num5] = num24;
					queue.Enqueue(num23 - num5);
				}
				if (cellY < fy1 && array3[num23 + num5] < 0 && array[num23 + num5])
				{
					array3[num23 + num5] = num24;
					queue.Enqueue(num23 + num5);
				}
				if (cellX > fx0 && array3[num23 - 1] < 0 && array[num23 - 1])
				{
					array3[num23 - 1] = num24;
					queue.Enqueue(num23 - 1);
				}
				if (cellX < fx1 && array3[num23 + 1] < 0 && array[num23 + 1])
				{
					array3[num23 + 1] = num24;
					queue.Enqueue(num23 + 1);
				}
			}
		}
		// 先整图灌一次，记下「从玩家这里真能走到」的可站位（等于标了一次连通分量）。
		// 为什么值得多灌这一次：内圈目标在外圈清开之前本来就走不到（这方法上面的注释写过），
		// 那种「真不可达」不该触发兜底——否则斧/镐跑每一轮都兜底，等于白裁（离线复算量到 0.6-1.0x，
		// 比不裁还慢）。只有「整图能到、裁剪后读成不可达」才说明是框把路切断了。
		// 一次整图灌水相对 N 次可以忽略。
		HashSet<int>? reachableSpots = null;
		if (boundFlood)
		{
			FloodFrom(num12, 0, 0, num5 - 1, num6 - 1);
			reachableSpots = new HashSet<int>();
			for (int spotOwner = 0; spotOwner < array2.Length; spotOwner++)
			{
				int[] spotsOfTile = array2[spotOwner];
				for (int s = 0; s < spotsOfTile.Length; s++)
				{
					if (array3[spotsOfTile[s]] >= 0)
					{
						reachableSpots.Add(spotsOfTile[s]);
					}
				}
			}
		}
		int num22 = num12;
		Vector2 val = playerTile;
		while (list4.Count > 0)
		{
			FloodFrom(num22, rx0, ry0, rx1, ry1);
			int index = 0;
			int num25 = int.MaxValue;
			int num26 = -1;
			bool flag = false;
			for (int num27 = 0; num27 < list4.Count; num27++)
			{
				Vector2 val2 = list[list4[num27]];
				if (Math.Abs(val2.X - playerTile.X) + Math.Abs(val2.Y - playerTile.Y) <= 2f)
				{
					flag = true;
					break;
				}
			}
			for (int num28 = 0; num28 < list4.Count; num28++)
			{
				int[] array4 = array2[list4[num28]];
				int num29 = 1000000;
				int num30 = -1;
				int[] array5 = array4;
				int[] array6 = array5;
				foreach (int num32 in array6)
				{
					if (array3[num32] >= 0 && array3[num32] < num29)
					{
						num29 = array3[num32];
						num30 = num32;
					}
				}
				if (num30 < 0 && reachableSpots != null)
				{
					// 这一格的可站位在裁剪范围里一个都读不到。整图口径下它本来可达的话，
					// 就是框把路切断了（不是真够不着）——整图重排一次兜底，顺序和没裁剪时逐格一致。
					// 真不可达的格（内圈目标、围栏死角）不会进这里，所以不会每轮都白兜一次。
					foreach (int spotIndex in array4)
					{
						if (reachableSpots.Contains(spotIndex))
						{
							return BuildPathWorkOrder(list, playerTile, location, sealPlacedTiles, boundFlood: false);
						}
					}
				}
				if (num29 > num25)
				{
					continue;
				}
				if (num29 < num25)
				{
					index = num28;
					num25 = num29;
					num26 = num30;
					continue;
				}
				Vector2 val3 = list[list4[index]];
				Vector2 val4 = list[list4[num28]];
				if (flag)
				{
					float num33 = Math.Abs(val3.Y - val.Y);
					float num34 = Math.Abs(val4.Y - val.Y);
					if (num34 != num33)
					{
						if (num34 < num33)
						{
							index = num28;
							num25 = num29;
							num26 = num30;
						}
						continue;
					}
					float num35 = Math.Abs(val3.X - playerTile.X) + Math.Abs(val3.Y - playerTile.Y);
					float num36 = Math.Abs(val4.X - playerTile.X) + Math.Abs(val4.Y - playerTile.Y);
					if (num36 != num35)
					{
						if (num36 < num35)
						{
							index = num28;
							num25 = num29;
							num26 = num30;
						}
						continue;
					}
				}
				float num37 = Math.Abs(val3.X - val.X) + Math.Abs(val3.Y - val.Y);
				float num38 = Math.Abs(val4.X - val.X) + Math.Abs(val4.Y - val.Y);
				if (num38 < num37 || (num38 == num37 && (val4.Y < val3.Y || (val4.Y == val3.Y && val4.X < val3.X))))
				{
					index = num28;
					num25 = num29;
					num26 = num30;
				}
			}
			Vector2 val5 = list[list4[index]];
			list3.Add(val5);
			val = val5;
			if (sealPlacedTiles)
			{
				array[(int)MathF.Floor(val5.Y) * num5 + (int)MathF.Floor(val5.X)] = false;
			}
			if (num26 >= 0)
			{
				num22 = num26;
			}
			list4[index] = list4[list4.Count - 1];
			list4.RemoveAt(list4.Count - 1);
		}
		return list3;
	}

	/// <summary>
	/// 镰刀收割的路线：按 8 邻接把待办格分成连通片，一片走完再换下一片（就近优先）。
	/// 摆放用行段（<see cref="BuildSegmentedPlacementOrder"/>）；收割的作物不占地，
	/// 斜角相连仍算同一片，所以这里用连通片而不是行段。
	/// </summary>

	private static bool[]? GetStandableMap(GameLocation location, out int width, out int height)
	{
		width = 0;
		height = 0;
		try
		{
			if (StandableCacheLocation == location && StandableCacheMap != null && Game1.ticks - StandableCacheTick < 60)
			{
				width = StandableCacheWidth;
				height = StandableCacheHeight;
				return StandableCacheMap;
			}
			Map map = location.Map;
			int num = ((map != null && map.Layers.Count > 0) ? map.Layers[0].LayerWidth : 0);
			Map map2 = location.Map;
			int num2 = ((map2 != null && map2.Layers.Count > 0) ? map2.Layers[0].LayerHeight : 0);
			if (num <= 0 || num2 <= 0 || (long)num * (long)num2 > 65536)
			{
				return null;
			}
			bool[] array = new bool[num * num2];
			for (int i = 0; i < num2; i++)
			{
				for (int j = 0; j < num; j++)
				{
					array[i * num + j] = IsStandable(location, new Vector2((float)j, (float)i));
				}
			}
			StandableCacheLocation = location;
			StandableCacheMap = array;
			StandableCacheWidth = num;
			StandableCacheHeight = num2;
			StandableCacheTick = Game1.ticks;
			width = num;
			height = num2;
			return array;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 地形通行表（掩码 249）：只看地形，动物和角色都当空气。缓存 TerrainCacheTtl tick。
	/// 返回的是**共享**数组，调用方只能读；要改必须先 Clone()。
	/// </summary>
	private static bool[]? GetTerrainMap(GameLocation location, out int width, out int height)
	{
		width = 0;
		height = 0;
		try
		{
			if (TerrainCacheLocation == location && TerrainCacheMap != null && Game1.ticks - TerrainCacheTick < TerrainCacheTtl)
			{
				width = TerrainCacheWidth;
				height = TerrainCacheHeight;
				return TerrainCacheMap;
			}
			Map map = location.Map;
			int num = ((map != null && map.Layers.Count > 0) ? map.Layers[0].LayerWidth : 0);
			Map map2 = location.Map;
			int num2 = ((map2 != null && map2.Layers.Count > 0) ? map2.Layers[0].LayerHeight : 0);
			if (num <= 0 || num2 <= 0 || (long)num * (long)num2 > 65536)
			{
				return null;
			}
			bool[] array = new bool[num * num2];
			for (int i = 0; i < num2; i++)
			{
				for (int j = 0; j < num; j++)
				{
					array[i * num + j] = IsStandableIgnoringCritters(location, new Vector2((float)j, (float)i));
				}
			}
			TerrainCacheLocation = location;
			TerrainCacheMap = array;
			TerrainCacheWidth = num;
			TerrainCacheHeight = num2;
			TerrainCacheTick = Game1.ticks;
			width = num;
			height = num2;
			return array;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 实心角色（村民/宠物/马/拖拉机这些 farmerPassesThrough=false 的）占住的格子。
	/// 和 SolidCharacterAt 逐格判定同一口径：Rectangle.Intersects 是严格相交，
	/// 对应 Left/64 … (Right-1)/64 的取整范围。
	/// </summary>
	private static HashSet<Point> CollectSolidCharacterTiles(GameLocation location, int width, int height)
	{
		HashSet<Point> set = new HashSet<Point>();
		try
		{
			foreach (NPC npc in location.characters)
			{
				if (npc == null || npc.IsInvisible || npc.farmerPassesThrough)
				{
					continue;
				}
				Rectangle box = ((Character)npc).GetBoundingBox();
				int x0 = Math.Max(0, box.Left / 64);
				int x1 = Math.Min(width - 1, (box.Right - 1) / 64);
				int y0 = Math.Max(0, box.Top / 64);
				int y1 = Math.Min(height - 1, (box.Bottom - 1) / 64);
				for (int x = x0; x <= x1; x++)
				{
					for (int y = y0; y <= y1; y++)
					{
						set.Add(new Point(x, y));
					}
				}
			}
		}
		catch
		{
		}
		return set;
	}

	/// <summary>
	/// 走路用的通行表：农场动物当空气，实心角色照挡。
	/// 地形部分走缓存，角色部分每次现算——角色一直在动，绕路时现算才准。
	/// 原来是每格调一次 IsWalkableThroughAnimals（内含 IsTileBlockedBy + 遍历 location.characters），
	/// 大地图上一次几十毫秒；现在整图只剩一次数组克隆 + O(角色数) 的占位叠加。
	/// </summary>
	private static bool[]? GetWalkableMap(GameLocation location, out int width, out int height)
	{
		width = 0;
		height = 0;
		try
		{
			bool[] terrain = GetTerrainMap(location, out var w, out var h);
			if (terrain == null || w <= 0 || h <= 0)
			{
				return null;
			}
			bool[] array = (bool[])terrain.Clone();
			foreach (Point tile in CollectSolidCharacterTiles(location, w, h))
			{
				array[tile.Y * w + tile.X] = false;
			}
			width = w;
			height = h;
			return array;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 压线段用的阻挡图：和 WalkDriver.TileBlocked 同一口径（角色占位 + 地形不可站）。
	/// 等价性：TileBlocked(p) = 角色占位(p) || (isCollidingPosition(p) &amp;&amp; !IsWalkableThroughAnimals(p))，
	/// 而 IsWalkableThroughAnimals = 地形可站 &amp;&amp; !实心角色，实心角色占的格子必然已经在角色占位表里
	/// （那张表收的是全部可见 NPC 的包围盒，是实心角色的超集），所以整体就等于「角色占位 || 地形不可站」。
	/// Cells==null 表示建不出来，调用方必须退回逐格问游戏。
	/// </summary>
	public struct BlockedGrid
	{
		public bool[]? Cells;

		public int Width;

		public int Height;

		/// <summary>图左上角在地图上的格子坐标——图通常只覆盖路线外接框那一片，不是整张地图。</summary>
		public int OriginX;

		public int OriginY;

		/// <summary>
		/// 图外一律当「不挡」：和 isCollidingPosition 越界返回 false 同口径。
		/// 实际上到不了这里——直线/斜线插值不会走出两个端点的外接框。
		/// </summary>
		public bool IsBlocked(int x, int y)
		{
			int ix = x - OriginX;
			int iy = y - OriginY;
			if (Cells == null || ix < 0 || iy < 0 || ix >= Width || iy >= Height)
			{
				return false;
			}
			return Cells[iy * Width + ix];
		}
	}

	/// <summary>局部阻挡图超过这个格数就不划算，改用缓存的整图地形表。</summary>
	private const int BlockedGridRegionMax = 4096;

	/// <summary>
	/// 按 region（路线的外接框 + 余量）建阻挡图。
	/// 为什么不整图建：一次起步寻路多半只走两三格，整图建图反而比原来逐格问游戏还贵
	/// （实测：短路线场景下每次起步多花 4ms）；而压线段的判定次数是 O(路线长²)，
	/// 路线一长逐格问就爆炸。所以小区域现问现建（不走缓存，没有陈旧问题），
	/// 大区域才改用缓存的整图地形表（一次缓存多次用）。
	/// </summary>
	public static BlockedGrid BuildBlockedGrid(GameLocation location, HashSet<Point>? characterTiles, Rectangle region)
	{
		BlockedGrid result = default(BlockedGrid);
		try
		{
			if (region.Width <= 0 || region.Height <= 0)
			{
				return result;
			}
			// 夹到地图内：图外的格子问出来是「不可站」，会被当成挡，和越界不挡的原口径不一致。
			int x0 = region.Left;
			int y0 = region.Top;
			int x1 = region.Right - 1;
			int y1 = region.Bottom - 1;
			Map map = location.Map;
			int mapWidth = ((map != null && map.Layers.Count > 0) ? map.Layers[0].LayerWidth : 0);
			Map map2 = location.Map;
			int mapHeight = ((map2 != null && map2.Layers.Count > 0) ? map2.Layers[0].LayerHeight : 0);
			if (mapWidth > 0 && mapHeight > 0)
			{
				x0 = Math.Clamp(x0, 0, mapWidth - 1);
				x1 = Math.Clamp(x1, 0, mapWidth - 1);
				y0 = Math.Clamp(y0, 0, mapHeight - 1);
				y1 = Math.Clamp(y1, 0, mapHeight - 1);
			}
			if (x1 < x0 || y1 < y0)
			{
				return result;
			}
			int width = x1 - x0 + 1;
			int height = y1 - y0 + 1;
			if (width * height <= BlockedGridRegionMax)
			{
				bool[] cells = new bool[width * height];
				for (int y = y0; y <= y1; y++)
				{
					for (int x = x0; x <= x1; x++)
					{
						bool blocked = !IsStandableIgnoringCritters(location, new Vector2((float)x, (float)y));
						if (!blocked && characterTiles != null)
						{
							blocked = characterTiles.Contains(new Point(x, y));
						}
						cells[(y - y0) * width + (x - x0)] = blocked;
					}
				}
				result.Cells = cells;
				result.Width = width;
				result.Height = height;
				result.OriginX = x0;
				result.OriginY = y0;
				return result;
			}
			// 区域太大（长距离转移）：整图地形表更划算，而且超过 65536 格的地图拿不到表就退回逐格问游戏。
			bool[]? terrain = GetTerrainMap(location, out var terrainWidth, out var terrainHeight);
			if (terrain == null || terrainWidth <= 0 || terrainHeight <= 0)
			{
				return result;
			}
			bool[] whole = new bool[terrainWidth * terrainHeight];
			for (int i = 0; i < whole.Length; i++)
			{
				whole[i] = !terrain[i];
			}
			if (characterTiles != null)
			{
				foreach (Point tile in characterTiles)
				{
					if (tile.X >= 0 && tile.Y >= 0 && tile.X < terrainWidth && tile.Y < terrainHeight)
					{
						whole[tile.Y * terrainWidth + tile.X] = true;
					}
				}
			}
			result.Cells = whole;
			result.Width = terrainWidth;
			result.Height = terrainHeight;
			result.OriginX = 0;
			result.OriginY = 0;
			return result;
		}
		catch
		{
			return default(BlockedGrid);
		}
	}

	/// <summary>一批格子的外接框，四周留 margin 格余量。</summary>
	public static Rectangle BoundsOf(IEnumerable<Point> tiles, int margin)
	{
		int minX = int.MaxValue;
		int minY = int.MaxValue;
		int maxX = int.MinValue;
		int maxY = int.MinValue;
		foreach (Point tile in tiles)
		{
			minX = Math.Min(minX, tile.X);
			minY = Math.Min(minY, tile.Y);
			maxX = Math.Max(maxX, tile.X);
			maxY = Math.Max(maxY, tile.Y);
		}
		if (maxX < minX || maxY < minY)
		{
			return Rectangle.Empty;
		}
		return new Rectangle(minX - margin, minY - margin, maxX - minX + 1 + margin * 2, maxY - minY + 1 + margin * 2);
	}

	/// <summary>
	/// 建这张阻挡图大概要花多少次「逐格问游戏」：局部图 = 区域格数；区域太大要用整图地形表时 = 整图格数，
	/// 但那张表已经在缓存里就几乎白拿（只剩一次数组反转 + 角色叠加）。建不出来时返回一个天文数字，
	/// 调用方据此把「建图前的探针预算」拉到无限大，等于永远走逐格判定。
	/// WalkDriver 用它决定压线段时什么时候值得停下来建图：逐格问一次约是建图一格的 4 倍贵。
	/// </summary>
	public static int BlockedGridCost(GameLocation location, Rectangle region)
	{
		try
		{
			int area = Math.Max(0, region.Width) * Math.Max(0, region.Height);
			if (area <= 0)
			{
				return 0;
			}
			if (area <= BlockedGridRegionMax)
			{
				return area;
			}
			if (TerrainCacheLocation == location && TerrainCacheMap != null && Game1.ticks - TerrainCacheTick < TerrainCacheTtl)
			{
				return 64;
			}
			Map map = location.Map;
			int num = ((map != null && map.Layers.Count > 0) ? map.Layers[0].LayerWidth : 0);
			Map map2 = location.Map;
			int num2 = ((map2 != null && map2.Layers.Count > 0) ? map2.Layers[0].LayerHeight : 0);
			if (num <= 0 || num2 <= 0 || (long)num * (long)num2 > 65536)
			{
				return int.MaxValue / 4;
			}
			return num * num2;
		}
		catch
		{
			return int.MaxValue / 4;
		}
	}

	/// <summary>
	/// 这批格子附近有没有农场动物（含 radius 格余量）。
	/// 用来决定要不要为「推开动物走直线」再建一次整图距离场：原版寻路把动物包围盒当墙、
	/// 却看不见 NPC（pathfinding:true），所以它绕远只可能是绕动物——附近一只都没有时那趟扫描是白跑的。
	/// </summary>
	public static bool FarmAnimalNearTiles(GameLocation? location, IEnumerable<Point> tiles, int radius = 3)
	{
		try
		{
			if (location == null || tiles == null)
			{
				return false;
			}
			HashSet<Point> animalTiles = new HashSet<Point>();
			var enumerator = ((NetDictionary<long, FarmAnimal, NetRef<FarmAnimal>, SerializableDictionary<long, FarmAnimal>, NetLongDictionary<FarmAnimal, NetRef<FarmAnimal>>>)(object)location.animals).Values.GetEnumerator();
			try
			{
				while (enumerator.MoveNext())
				{
					FarmAnimal current = enumerator.Current;
					if (current == null)
					{
						continue;
					}
					Rectangle box = ((Character)current).GetBoundingBox();
					for (int x = box.Left / 64 - radius; x <= (box.Right - 1) / 64 + radius; x++)
					{
						for (int y = box.Top / 64 - radius; y <= (box.Bottom - 1) / 64 + radius; y++)
						{
							animalTiles.Add(new Point(x, y));
						}
					}
				}
			}
			finally
			{
				((IDisposable)enumerator/*cast due to constrained. prefix*/).Dispose();
			}
			if (animalTiles.Count == 0)
			{
				return false;
			}
			foreach (Point tile in tiles)
			{
				if (animalTiles.Contains(tile))
				{
					return true;
				}
			}
			return false;
		}
		catch
		{
			return false;
		}
	}
	public static int[]? BuildDistanceField(GameLocation location, Vector2 origin, out int width, out int height)
	{
		width = 0;
		height = 0;
		bool[] standableMap = GetStandableMap(location, out width, out height);
		if (standableMap == null)		{
			return null;
		}
		return FloodDistances(standableMap, width, height, origin);
	}

	/// <summary>
	/// 步行距离场（四邻测地）：-1=不可达，0=起点。**可能是裁剪过的**——只灌了某一片，
	/// OriginX/OriginY 是那一片左上角的地图格坐标，所以取数一律走 At()/Serve()，不要直接下标。
	/// </summary>
	public struct DistanceField
	{
		public int[]? Cells;

		public int Width;

		public int Height;

		public int OriginX;

		public int OriginY;

		public bool IsValid => Cells != null && Width > 0 && Height > 0;

		/// <summary>某一格的步行步数；范围外或不可达返回 -1。</summary>
		public int At(int x, int y)
		{
			int ix = x - OriginX;
			int iy = y - OriginY;
			if (Cells == null || ix < 0 || iy < 0 || ix >= Width || iy >= Height)
			{
				return -1;
			}
			return Cells[iy * Width + ix];
		}

		/// <summary>和 ServeDistance 同口径：取目标 3x3 邻域里最小的可达步数，全不可达返回 1000000。</summary>
		public int Serve(Vector2 target)
		{
			int best = 1000000;
			int num = (int)MathF.Floor(target.X);
			int num2 = (int)MathF.Floor(target.Y);
			for (int i = -1; i <= 1; i++)
			{
				for (int j = -1; j <= 1; j++)
				{
					int value = At(num + j, num2 + i);
					if (value >= 0 && value < best)
					{
						best = value;
					}
				}
			}
			return best;
		}
	}

	/// <summary>
	/// 只在 bounds 那一片里灌距离场，返回裁剪过的场（bounds 为 null 就是整图）。
	/// 为什么要能裁剪：整图灌一次要 new int[地图格数] + 全图 BFS——大地图农场上单次就是
	/// 120KB 以上的分配（超过约 85000 字节就进 LOH，直接给 gen2 GC 加压，表现出来是没规律的卡顿）
	/// 加几万个格子的 BFS，而调用方（每取一格挑最近的那格）是「每格一次」的频率。
	/// 裁到「玩家 + 待办格」的外接框之后，分配和 BFS 都只跟选框大小有关，跟地图多大无关。
	/// 通行表仍旧用整图缓存（GetStandableMap），所以这里不重复付逐格问游戏的钱。
	/// ⚠ 裁剪的代价：要绕出 bounds 才连得通的格子会读成不可达。调用方必须在发现不可达时
	/// 用 bounds=null 重算一次兜底，否则会把「绕得通」误判成「够不着」。
	/// 起点不在 bounds 里时返回无效场（调用方给的框不对，宁可不算也不要算错）。
	/// </summary>
	public static DistanceField BuildDistanceField(GameLocation location, Vector2 origin, Rectangle? bounds)
	{
		try
		{
			bool[] standableMap = GetStandableMap(location, out var mapWidth, out var mapHeight);
			if (standableMap == null || mapWidth <= 0 || mapHeight <= 0)
			{
				return default(DistanceField);
			}
			return FloodRegion(standableMap, mapWidth, mapHeight, origin, bounds);
		}
		catch
		{
			return default(DistanceField);
		}
	}

	/// <summary>
	/// 和 BuildWalkField 同一套通行表，但只灌 bounds 那一片、返回裁剪过的场（见 DistanceField）。
	/// 动物队列每试一个站位就要灌一次，一个 tick 里最多能连试 17 次；整图灌一次在大地图上是
	/// 几万格 BFS + 一个 120KB 级的大对象分配（进 LOH，给 gen2 GC 加压）。
	/// ⚠ 裁剪会让「要绕出 bounds 才连得通」的格子读成不可达，调用方必须在这种情况下用
	/// bounds=null（整图）重算一次兜底，否则会把「绕得通」误判成「够不着」。
	/// </summary>
	public static DistanceField BuildWalkFieldIn(GameLocation location, Vector2 origin, bool terrainOnly, Rectangle? bounds)
	{
		try
		{
			int mapWidth;
			int mapHeight;
			bool[] passable = (terrainOnly ? GetTerrainMap(location, out mapWidth, out mapHeight) : GetWalkableMap(location, out mapWidth, out mapHeight));
			if (passable == null || mapWidth <= 0 || mapHeight <= 0)
			{
				return default(DistanceField);
			}
			return FloodRegion(passable, mapWidth, mapHeight, origin, bounds);
		}
		catch
		{
			return default(DistanceField);
		}
	}

	/// <summary>在 bounds（null = 整图）范围内灌四邻测地距离，返回裁剪过的场。
	/// 起点不在范围内就返回无效场——宁可不算，也不要按夹过的起点算出一张错的图。</summary>
	private static DistanceField FloodRegion(bool[] passable, int mapWidth, int mapHeight, Vector2 origin, Rectangle? bounds)
	{
		DistanceField result = default(DistanceField);
		int x0 = 0;
		int y0 = 0;
		int x1 = mapWidth - 1;
		int y1 = mapHeight - 1;
		if (bounds.HasValue)
		{
			Rectangle value = bounds.Value;
			x0 = Math.Clamp(value.Left, 0, mapWidth - 1);
			y0 = Math.Clamp(value.Top, 0, mapHeight - 1);
			x1 = Math.Clamp(value.Right - 1, 0, mapWidth - 1);
			y1 = Math.Clamp(value.Bottom - 1, 0, mapHeight - 1);
			if (x1 < x0 || y1 < y0)
			{
				return result;
			}
		}
		int originX = (int)MathF.Floor(origin.X);
		int originY = (int)MathF.Floor(origin.Y);
		if (originX < x0 || originY < y0 || originX > x1 || originY > y1)
		{
			return result;
		}
		int width = x1 - x0 + 1;
		int height = y1 - y0 + 1;
		int[] cells = new int[width * height];
		Array.Fill(cells, -1);
		int start = (originY - y0) * width + (originX - x0);
		cells[start] = 0;
		Queue<int> queue = new Queue<int>();
		queue.Enqueue(start);
		while (queue.Count > 0)
		{
			int index = queue.Dequeue();
			int next = cells[index] + 1;
			int cx = index % width;
			int cy = index / width;
			if (cy > 0 && cells[index - width] < 0 && passable[(cy - 1 + y0) * mapWidth + (cx + x0)])
			{
				cells[index - width] = next;
				queue.Enqueue(index - width);
			}
			if (cy < height - 1 && cells[index + width] < 0 && passable[(cy + 1 + y0) * mapWidth + (cx + x0)])
			{
				cells[index + width] = next;
				queue.Enqueue(index + width);
			}
			if (cx > 0 && cells[index - 1] < 0 && passable[cy * mapWidth + (cx - 1 + x0)])
			{
				cells[index - 1] = next;
				queue.Enqueue(index - 1);
			}
			if (cx < width - 1 && cells[index + 1] < 0 && passable[cy * mapWidth + (cx + 1 + x0)])
			{
				cells[index + 1] = next;
				queue.Enqueue(index + 1);
			}
		}
		result.Cells = cells;
		result.Width = width;
		result.Height = height;
		result.OriginX = x0;
		result.OriginY = y0;
		return result;
	}

	/// <summary>只按地形判能不能站：建筑/栅栏/水/树/关着的门都算挡，动物/村民/玩家不算。</summary>
	public static bool IsStandableIgnoringCritters(GameLocation location, Vector2 tile)
	{
		try
		{
			// 249 = All 去掉 Characters(2)|Farmers(4)。不能拿 242（IsStandableIgnoringCharacters）顶替：
			// 那个连 Buildings 一起放行，畜棚墙里也会算「能站」。
			// ignorePassables=All：和 isCollidingPosition 同口径——物体只看 isPassable()，
			// 开着的围栏门要放行（关着的门 isPassable()=false，照样算挡）。
			CollisionMask mask = (CollisionMask)249;
			if (TerrainFeaturePassableToFarmer(location, tile))
			{
				mask &= ~CollisionMask.TerrainFeatures;
			}
			return !location.IsTileBlockedBy(tile, mask, CollisionMask.All, true);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>走路口径：农场动物当空气（推得开），村民/宠物/马这些真推不动的照挡。</summary>
	public static bool IsWalkableThroughAnimals(GameLocation location, Vector2 tile)
	{
		try
		{
			// 249 把整个 Characters 分支跳过（动物和实心角色一起放行），实心角色要单独补回来。
			// 原版掩码拼不出「只放行动物」：FarmAnimal.farmerPassesThrough 是 false，
			// ignorePassables 去不掉它，只能整支跳过。
			CollisionMask mask = (CollisionMask)249;
			if (TerrainFeaturePassableToFarmer(location, tile))
			{
				mask &= ~CollisionMask.TerrainFeatures;
			}
			if (location.IsTileBlockedBy(tile, mask, CollisionMask.All, true))
			{
				return false;
			}
			return !SolidCharacterAt(location, tile);
		}
		catch
		{
			return false;
		}
	}

	private static bool SolidCharacterAt(GameLocation location, Vector2 tile)
	{
		Rectangle rect = new Rectangle((int)tile.X * 64, (int)tile.Y * 64, 64, 64);
		foreach (NPC npc in location.characters)
		{
			if (npc != null && !npc.IsInvisible && !npc.farmerPassesThrough && npc.GetBoundingBox().Intersects(rect))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>走路用的四邻测地距离场：-1=不可达，0=起点。
	/// 通行表的地形部分走缓存、角色部分每次现算（动物/村民一直在动，缓存下来会失真）——
	/// 灌水本身是纯数组操作，真正贵的是逐格问游戏那一步。</summary>
	/// <param name="terrainOnly">true=所有角色都当空气，只看地形；false=农场动物当空气（推得开），村民/宠物/马照挡。</param>
	public static int[]? BuildWalkField(GameLocation location, Vector2 origin, bool terrainOnly, out int width, out int height)
	{
		width = 0;
		height = 0;
		try
		{
			// terrainOnly 走 249：整个 Characters 分支跳过，动物和实心角色都当空气 —— 就是缓存里的地形表本身。
			// 否则走「动物当空气、实心角色照挡」——推得开的只有农场动物。
			int w;
			int h;
			bool[]? array;
			if (terrainOnly)
			{
				array = GetTerrainMap(location, out w, out h);
			}
			else
			{
				array = GetWalkableMap(location, out w, out h);
			}
			if (array == null || w <= 0 || h <= 0)
			{
				return null;
			}
			width = w;
			height = h;
			return FloodDistances(array, w, h, origin);
		}
		catch
		{
			width = 0;
			height = 0;
			return null;
		}
	}

	/// <summary>沿距离场从 to 往数值小的方向回溯到 from（四邻），返回 from→to 的路径；不可达返回 null。</summary>
	public static List<Point>? TracePath(int[] field, int width, int height, Point from, Point to)
	{
		if (field == null || width <= 0 || height <= 0)
		{
			return null;
		}
		if (to.X < 0 || to.Y < 0 || to.X >= width || to.Y >= height)
		{
			return null;
		}
		int index = to.Y * width + to.X;
		if (field[index] < 0)
		{
			return null;
		}
		List<Point> list = new List<Point>();
		Point point = to;
		int guard = field[index] + 2;
		int[] dx = { 1, -1, 0, 0 };
		int[] dy = { 0, 0, 1, -1 };
		while (point != from)
		{
			list.Add(point);
			if (guard-- <= 0)
			{
				return null;
			}
			int best = int.MaxValue;
			Point next = point;
			for (int i = 0; i < 4; i++)
			{
				int nx = point.X + dx[i];
				int ny = point.Y + dy[i];
				if (nx < 0 || ny < 0 || nx >= width || ny >= height)
				{
					continue;
				}
				int value = field[ny * width + nx];
				if (value >= 0 && value < best)
				{
					best = value;
					next = new Point(nx, ny);
				}
			}
			if (best == int.MaxValue)
			{
				return null;
			}
			point = next;
		}
		list.Add(from);
		list.Reverse();
		return list;
	}

	private static int[] FloodDistances(bool[] passable, int width, int height, Vector2 origin)
	{
		int[] array = new int[width * height];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = -1;
		}
		int num = Math.Clamp((int)MathF.Floor(origin.X), 0, width - 1);
		int num2 = Math.Clamp((int)MathF.Floor(origin.Y), 0, height - 1);
		int num3 = num2 * width + num;
		array[num3] = 0;
		Queue<int> queue = new Queue<int>();
		queue.Enqueue(num3);
		while (queue.Count > 0)
		{
			int num4 = queue.Dequeue();
			int num5 = array[num4] + 1;
			if (num4 >= width && array[num4 - width] < 0 && passable[num4 - width])
			{
				array[num4 - width] = num5;
				queue.Enqueue(num4 - width);
			}
			if (num4 < width * height - width && array[num4 + width] < 0 && passable[num4 + width])
			{
				array[num4 + width] = num5;
				queue.Enqueue(num4 + width);
			}
			if (num4 % width > 0 && array[num4 - 1] < 0 && passable[num4 - 1])
			{
				array[num4 - 1] = num5;
				queue.Enqueue(num4 - 1);
			}
			if (num4 % width < width - 1 && array[num4 + 1] < 0 && passable[num4 + 1])
			{
				array[num4 + 1] = num5;
				queue.Enqueue(num4 + 1);
			}
		}
		return array;
	}

	public static int ServeDistance(int[] field, int width, int height, Vector2 target)
	{
		int num = (int)MathF.Floor(target.X);
		int num2 = (int)MathF.Floor(target.Y);
		int num3 = 1000000;
		for (int i = -1; i <= 1; i++)
		{
			for (int j = -1; j <= 1; j++)
			{
				int num4 = num + j;
				int num5 = num2 + i;
				if (num4 >= 0 && num5 >= 0 && num4 < width && num5 < height)
				{
					int num6 = field[num5 * width + num4];
					if (num6 >= 0 && num6 < num3)
					{
						num3 = num6;
					}
				}
			}
		}
		return num3;
	}

	public static bool IsSingleLine(IEnumerable<Vector2> tiles, out bool horizontal)
	{
		horizontal = false;
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		bool flag = true;
		bool flag2 = true;
		foreach (Vector2 tile in tiles)
		{
			if (num == 0)
			{
				num2 = (int)tile.Y;
				num3 = (int)tile.X;
			}
			else
			{
				if ((int)tile.Y != num2)
				{
					flag = false;
				}
				if ((int)tile.X != num3)
				{
					flag2 = false;
				}
			}
			num++;
		}
		if (num < 2)
		{
			return false;
		}
		if (flag && !flag2)
		{
			horizontal = true;
			return true;
		}
		if (flag2 && !flag)
		{
			horizontal = false;
			return true;
		}
		return false;
	}


	public static List<Vector2> BuildTrailRoute(IEnumerable<Vector2> tiles, Rectangle box, Vector2 playerTile, GameLocation location, out Vector2? exit)
	{
		List<Vector2> list = BuildSafePlacementOrder(tiles, playerTile);
		exit = null;
		if (list.Count == 0)
		{
			return list;
		}
		if (IsSingleLine(list, out var lineHorizontal))
		{
			list = list.OrderBy(delegate(Vector2 t)
			{
				return (!lineHorizontal) ? t.Y : t.X;
			}).ToList();
			List<Vector2> list2 = list;
			if (Vector2.DistanceSquared(playerTile, list2[list2.Count - 1]) < Vector2.DistanceSquared(playerTile, list[0]))
			{
				list.Reverse();
			}
		}
		HashSet<Vector2> hashSet = new HashSet<Vector2>(list);
		List<Vector2> list3 = list;
		Vector2 val = list3[list3.Count - 1];
		Vector2 val2 = default(Vector2);
		for (int num = 1; num <= 8; num++)
		{
			Vector2? val3 = exit;
			if (val3.HasValue)
			{
				break;
			}
			for (int num2 = -num; num2 <= num; num2++)
			{
				val3 = exit;
				if (val3.HasValue)
				{
					break;
				}
				for (int num3 = -num; num3 <= num; num3++)
				{
					if (Math.Max(Math.Abs(num3), Math.Abs(num2)) == num)
					{
						val2 = new Vector2(val.X + (float)num3, val.Y + (float)num2);
						if (!hashSet.Contains(val2) && IsTileStandable(location, val2))
						{
							exit = val2;
							break;
						}
					}
				}
			}
		}
		return list;
	}

	public static Vector2? FindFreeNeighbor(GameLocation location, Vector2 target, ICollection<Vector2> blocked, Vector2 from)
	{
		Vector2? result = null;
		float num = float.MaxValue;
		Vector2 val = default(Vector2);
		for (int i = -1; i <= 1; i++)
		{
			for (int j = -1; j <= 1; j++)
			{
				if (j == 0 && i == 0)
				{
					continue;
				}
				val = new Vector2(target.X + (float)j, target.Y + (float)i);
				if (!blocked.Contains(val) && IsTileStandable(location, val))
				{
					float num2 = Vector2.DistanceSquared(val, from);
					if (!(num2 >= num))
					{
						num = num2;
						result = val;
					}
				}
			}
		}
		return result;
	}

	private static bool IsTileStandable(GameLocation location, Vector2 tile)
	{
		Rectangle val = default(Rectangle);
		val = new Rectangle((int)tile.X * 64 + 8, (int)tile.Y * 64 + 8, 48, 32);
		try
		{
			return !location.isCollidingPosition(val, Game1.viewport, true, 0, false, (Character)Game1.player);
		}
		catch
		{
			return false;
		}
	}

	public static List<Vector2> BuildSafePlacementOrder(IEnumerable<Vector2> tiles, Vector2 playerTile)
	{
		List<Vector2> list = tiles.ToList();
		if (list.Count == 0)
		{
			return list;
		}
		List<IGrouping<int, Vector2>> source = list.GroupBy(delegate(Vector2 t)
		{
			return (int)t.Y;
		}).ToList();
		float y = playerTile.Y;
		int num = source.Min((IGrouping<int, Vector2> g) => g.Key);
		int num2 = source.Max((IGrouping<int, Vector2> g) => g.Key);
		bool topToBottom = y - (float)num <= (float)num2 - y;
		List<Vector2> list2 = new List<Vector2>(list.Count);
		bool flag = true;
		foreach (IGrouping<int, Vector2> item in source.OrderBy((IGrouping<int, Vector2> g) => (!topToBottom) ? (-g.Key) : g.Key))
		{
			List<Vector2> list3 = item.OrderBy(delegate(Vector2 t)
			{
				return (int)t.X;
			}).ToList();
			if (list2.Count == 0)
			{
				float x = playerTile.X;
				flag = Math.Abs(list3[0].X - x) <= Math.Abs(list3[list3.Count - 1].X - x);
			}
			if (flag)
			{
				list2.AddRange(list3);
			}
			else
			{
				for (int num3 = list3.Count - 1; num3 >= 0; num3--)
				{
					list2.Add(list3[num3]);
				}
			}
			flag = !flag;
		}
		return list2;
	}

	/// <summary>同一行里中间只隔 1~2 格的小豁口（缺一格、窄走道）仍算同一段；再宽就当被挡住了。</summary>
	private const int PlacementRunGap = 2;

	private sealed class PlacementRun
	{
		public int Row;

		public List<Vector2> Tiles = new List<Vector2>();
	}

	/// <summary>
	/// 实心实物的摆放顺序：按“行段”扫。同行里只隔 1~2 格的小豁口照旧一路扫过去；
	/// 碰到池塘/建筑这种大豁口就停在这半边，换到下一行的同一侧接着扫，
	/// 等这一侧摆完，再一次跨到另一边。整框一路 S 会每经过一行就绕障碍物一次。
	/// 被大片障碍隔开的零散小片（上下都没有挨着的行段）先做掉：这时地还空着、路最短；
	/// 留到最后再补往往要绕大半个场子（实机日志里出现过 56 格的回头路）。
	/// <para>
	/// 起手边＝<b>离玩家最近的那一侧</b>（就近角起手），朝对面一路 S 扫过去；
	/// 只有收尾那一侧退不出去时才翻面 —— 实心物摆完就是一堵墙，收尾落在墙边等于把自己砌在里头
	/// （2026-10-28 实机：从下往上扫，最后几排被围墙和自己的桶封死，466 条重试全是 findPath 空）。
	/// <paramref name="checkEscape"/> 只给「真会把自己封住」的实心摆放开；种子/花盆/机器检修
	/// 的目标格本来就站在过道里够，不做这个检查（过道永远退得出去，检查只会白翻方向）。
	/// </para>
	/// </summary>
	/// <param name="note">这次为什么选了这个起手边（给调用方拼提示用）。</param>
	public static List<Vector2> BuildSegmentedPlacementOrder(IEnumerable<Vector2> tiles, Vector2 playerTile, GameLocation? location, bool checkEscape, out string note)
	{
		note = "";
		List<Vector2> list = tiles.ToList();
		if (list.Count <= 1)
		{
			return list;
		}
		List<PlacementRun> runs = BuildRuns(list);
		SplitIsolatedRuns(runs, out List<PlacementRun> isolated, out List<PlacementRun> body);
		List<Vector2> result = new List<Vector2>(list.Count);
		Vector2 cursor = playerTile;
		while (isolated.Count > 0)
		{
			int index = PickNearestRun(isolated, cursor, out var enterFromTail);
			cursor = AppendRun(isolated[index], enterFromTail, result);
			isolated.RemoveAt(index);
		}
		if (body.Count > 0)
		{
			// 起手边按「离玩家最近的那一侧」定，朝对面整块扫过去 —— 就近角起手 + S。
			// 0.4.33 曾写死"从最上面一行往下扫"，那是拿站位当敌人：站在框上方时它反而从脚边起手、
			// 往远处扫，收尾留在远端，既不是最近角也不安全。
			int topRow = body.Min((PlacementRun r) => r.Row);
			int bottomRow = body.Max((PlacementRun r) => r.Row);
			bool startTop = MathF.Abs(playerTile.Y - (float)topRow) <= MathF.Abs(playerTile.Y - (float)bottomRow);
			int rowDirection = (startTop ? 1 : (-1));
			string side = (startTop ? "最上面那排往下" : "最下面那排往上");
			note = "摆放起手：从" + side + "扫（就近角）";
			if (checkEscape && !HasEscapeAtEnd(location, body, rowDirection))
			{
				// 收尾那一侧外面连一格都站不住 → 摆到最后会被自己的桶 + 围墙围死。
				// 翻面：改从对面起手，把这堵墙留在身后，收尾落在玩家这一侧（通常是空地）。
				int flipped = ((rowDirection > 0) ? (-1) : 1);
				if (HasEscapeAtEnd(location, body, flipped))
				{
					rowDirection = flipped;
					startTop = !startTop;
					note = "摆放起手：从" + (startTop ? "最上面那排往下" : "最下面那排往上") + "扫（就近那侧退不出去，已翻面）";
				}
				else
				{
					note = "摆放起手：从" + side + "扫（两侧都退不出去，按就近侧硬扫）";
				}
			}
			int firstRow = (startTop ? topRow : bottomRow);
			List<PlacementRun> firstRowRuns = body.Where((PlacementRun r) => r.Row == firstRow).ToList();
			int index = PickNearestRun(firstRowRuns, cursor, out var enterFromTail);
			PlacementRun pick = firstRowRuns[index];
			cursor = AppendRun(pick, enterFromTail, result);
			body.Remove(pick);
			int lastRow = pick.Row;
			while (body.Count > 0)
			{
				// 行序只朝初始方向走，中途绝不掉头：前方还有行段时跳过身后的；
				// 前方没活了就回到这一块的上沿（初始方向的反端）那一行，再顺着原方向扫下来。
				// 中途掉头的实机后果：人停在某一侧、被自己的桶和池塘封住，够不到对面剩下的格子。
				// 只有“更靠前的那几行”才算前方；同一行的另一段（池塘另一侧）不算——
				// 否则换到另一侧时会先把最下面那行做了，把底部通道封上再走上去。
				bool hasAhead = body.Any((PlacementRun r) => (r.Row - lastRow) * rowDirection > 0);
				int restartRow = (hasAhead ? 0 : ((rowDirection > 0) ? body.Min((PlacementRun r) => r.Row) : body.Max((PlacementRun r) => r.Row)));
				int chosen = -1;
				float chosenDistance = float.MaxValue;
				bool chosenTail = false;
				for (int i = 0; i < body.Count; i++)
				{
					PlacementRun run = body[i];
					if (hasAhead)
					{
						if ((run.Row - lastRow) * rowDirection < 0)
						{
							continue;
						}
					}
					else if (run.Row != restartRow)
					{
						continue;
					}
					float head = Vector2.DistanceSquared(cursor, run.Tiles[0]);
					float tail = Vector2.DistanceSquared(cursor, run.Tiles[run.Tiles.Count - 1]);
					bool fromTail = tail < head;
					float distance = (fromTail ? tail : head);
					bool continues = run.Row == lastRow + rowDirection;
					if (chosen < 0 || PreferRun(distance, continues, run.Row, run.Tiles[0].X, chosenDistance, body[chosen].Row == lastRow + rowDirection, body[chosen].Row, body[chosen].Tiles[0].X))
					{
						chosen = i;
						chosenDistance = distance;
						chosenTail = fromTail;
					}
				}
				PlacementRun next = body[chosen];
				cursor = AppendRun(next, chosenTail, result);
				body.RemoveAt(chosen);
				lastRow = next.Row;
			}
		}
		return result;
	}

	private static bool PreferRun(float distance, bool continues, int row, float x, float otherDistance, bool otherContinues, int otherRow, float otherX)
	{
		if (distance != otherDistance)
		{
			return distance < otherDistance;
		}
		if (continues != otherContinues)
		{
			return continues;
		}
		if (row != otherRow)
		{
			return row < otherRow;
		}
		return x < otherX;
	}

		private static List<PlacementRun> BuildRuns(List<Vector2> list)
	{
		List<PlacementRun> runs = new List<PlacementRun>();
		foreach (IGrouping<int, Vector2> row in list.GroupBy(delegate(Vector2 t)
		{
			return (int)t.Y;
		}))
		{
			List<Vector2> sorted = row.OrderBy(delegate(Vector2 t)
			{
				return t.X;
			}).ToList();
			PlacementRun run = new PlacementRun
			{
				Row = row.Key
			};
			run.Tiles.Add(sorted[0]);
			for (int i = 1; i < sorted.Count; i++)
			{
				if (sorted[i].X - sorted[i - 1].X > (float)(PlacementRunGap + 1))
				{
					runs.Add(run);
					run = new PlacementRun
					{
						Row = row.Key
					};
				}
				run.Tiles.Add(sorted[i]);
			}
			runs.Add(run);
		}
		return runs;
	}

	private static void SplitIsolatedRuns(List<PlacementRun> runs, out List<PlacementRun> isolated, out List<PlacementRun> body)
	{
		isolated = new List<PlacementRun>();
		body = new List<PlacementRun>();
		foreach (PlacementRun run in runs)
		{
			bool joined = false;
			foreach (PlacementRun other in runs)
			{
				if (other != run && Math.Abs(other.Row - run.Row) <= 1 && RunsTouch(run, other))
				{
					joined = true;
					break;
				}
			}
			(joined ? body : isolated).Add(run);
		}
	}

	private static bool RunsTouch(PlacementRun a, PlacementRun b)
	{
		return a.Tiles[0].X <= b.Tiles[b.Tiles.Count - 1].X + 1f && b.Tiles[0].X <= a.Tiles[a.Tiles.Count - 1].X + 1f;
	}

	/// <summary>
	/// 朝 <paramref name="rowDirection"/> 一路扫到底之后，人还能不能从那一侧走出去。
	/// 实心物摆完是一堵墙：最后一排的「外面那一行」如果连一格都站不住（围墙、水面、建筑），
	/// 人就只能站在自己刚摆的那排上，除了回头翻墙没有别的路 —— 那就是 0.4.33 那次 466 条
	/// findPath 空的现场。左右两端也算出口：收尾那一格常常是靠块边摆的，人从侧面就能出去。
	/// 判断不了（没传 location）时一律当"出得去"，别为了一个探测不到的风险把顺序翻来翻去。
	/// </summary>
	private static bool HasEscapeAtEnd(GameLocation? location, List<PlacementRun> runs, int rowDirection)
	{
		if (location == null || runs == null || runs.Count == 0)
		{
			return true;
		}
		int edgeRow = runs[0].Row;
		foreach (PlacementRun run in runs)
		{
			edgeRow = ((rowDirection > 0) ? Math.Max(edgeRow, run.Row) : Math.Min(edgeRow, run.Row));
		}
		int minX = int.MaxValue;
		int maxX = int.MinValue;
		foreach (PlacementRun run in runs)
		{
			if (run.Row != edgeRow)
			{
				continue;
			}
			minX = Math.Min(minX, (int)run.Tiles[0].X);
			maxX = Math.Max(maxX, (int)run.Tiles[run.Tiles.Count - 1].X);
		}
		if (minX > maxX)
		{
			return true;
		}
		if (IsTileStandable(location, new Vector2((float)(minX - 1), (float)edgeRow)) || IsTileStandable(location, new Vector2((float)(maxX + 1), (float)edgeRow)))
		{
			return true;
		}
		int outRow = edgeRow + rowDirection;
		for (int x = minX - 1; x <= maxX + 1; x++)
		{
			if (IsTileStandable(location, new Vector2((float)x, (float)outRow)))
			{
				return true;
			}
		}
		return false;
	}

	private static int PickNearestRun(List<PlacementRun> runs, Vector2 cursor, out bool enterFromTail)
	{
		int chosen = 0;
		float chosenDistance = float.MaxValue;
		bool chosenTail = false;
		for (int i = 0; i < runs.Count; i++)
		{
			PlacementRun run = runs[i];
			float head = Vector2.DistanceSquared(cursor, run.Tiles[0]);
			float tail = Vector2.DistanceSquared(cursor, run.Tiles[run.Tiles.Count - 1]);
			bool fromTail = tail < head;
			float distance = (fromTail ? tail : head);
			if (distance < chosenDistance || (distance == chosenDistance && (run.Row < runs[chosen].Row || (run.Row == runs[chosen].Row && run.Tiles[0].X < runs[chosen].Tiles[0].X))))
			{
				chosen = i;
				chosenDistance = distance;
				chosenTail = fromTail;
			}
		}
		enterFromTail = chosenTail;
		return chosen;
	}

	private static Vector2 AppendRun(PlacementRun run, bool fromTail, List<Vector2> result)
	{
		if (fromTail)
		{
			for (int i = run.Tiles.Count - 1; i >= 0; i--)
			{
				result.Add(run.Tiles[i]);
			}
			return run.Tiles[0];
		}
		foreach (Vector2 tile in run.Tiles)
		{
			result.Add(tile);
		}
		return run.Tiles[run.Tiles.Count - 1];
	}

	public static List<Vector2> PlanSprinklers(Rectangle box, int radius, int gap = 0, bool trellis = false)
	{
		List<Vector2> list = new List<Vector2>();
		if (box.Width <= 0 || box.Height <= 0)
		{
			return list;
		}
		int num = box.Width * box.Height;
		Vector2[] array = (Vector2[])(object)new Vector2[num];
		Dictionary<Vector2, int> dictionary = new Dictionary<Vector2, int>(num);
		int num2 = 0;
		Vector2 val = default(Vector2);
		for (int i = (box).Top; i < (box).Bottom; i++)
		{
			for (int j = (box).Left; j < (box).Right; j++)
			{
				val = new Vector2((float)j, (float)i);
				array[num2] = val;
				dictionary[val] = num2;
				num2++;
			}
		}
		int[][] array2 = new int[num][];
		for (int k = 0; k < num; k++)
		{
			array2[k] = GetSprinklerCoverage(array[k], radius, box, dictionary);
		}
		List<int>[] array3 = new List<int>[num];
		for (int l = 0; l < num; l++)
		{
			array3[l] = new List<int>();
		}
		for (int m = 0; m < num; m++)
		{
			int[] array4 = array2[m];
			int[] array5 = array4;
			foreach (int num3 in array5)
			{
				array3[num3].Add(m);
			}
		}
		int[] array6 = new int[num];
		for (int num4 = 0; num4 < num; num4++)
		{
			array6[num4] = array2[num4].Length;
		}
		bool[] array7 = new bool[num];
		for (int num5 = 0; num5 < num; num5++)
		{
			array7[num5] = true;
		}
		int num6 = num;
		if (radius <= 0)
		{
			int num7 = -1;
			int num8 = -1;
			int num9 = int.MaxValue;
			int num10 = (trellis ? 4 : 5);
			for (int num11 = 0; num11 < num10; num11++)
			{
				bool[] array8 = new bool[num];
				int num12 = 0;
				int num13 = 0;
				for (int num14 = 0; num14 < num; num14++)
				{
					if (LatticeResidue(array[num14], trellis) != num11)
					{
						continue;
					}
					num12++;
					int[] array9 = array2[num14];
					int[] array10 = array9;
					foreach (int num16 in array10)
					{
						if (!array8[num16])
						{
							array8[num16] = true;
							num13++;
						}
					}
				}
				if (num13 > num8 || (num13 == num8 && num12 < num9))
				{
					num8 = num13;
					num9 = num12;
					num7 = num11;
				}
			}
			if (num7 >= 0)
			{
				for (int num17 = 0; num17 < num; num17++)
				{
					if (LatticeResidue(array[num17], trellis) == num7)
					{
						list.Add(array[num17]);
					}
				}
			}
			return list;
		}
		int num18 = 2 * radius + 1 + Math.Max(0, gap);
		for (int num19 = (box).Top + radius; num19 < (box).Bottom; num19 += num18)
		{
			for (int num20 = (box).Left + radius; num20 < (box).Right; num20 += num18)
			{
				list.Add(new Vector2((float)num20, (float)num19));
			}
		}
		return list;
	}

	private static int DiagonalResidue(Vector2 tile)
	{
		int num = ((int)tile.X - 2 * (int)tile.Y) % 5;
		if (num >= 0)
		{
			return num;
		}
		return num + 5;
	}

	private static int LatticeResidue(Vector2 tile, bool trellis)
	{
		if (!trellis)
		{
			return DiagonalResidue(tile);
		}
		int num = (int)tile.Y;
		if ((num & 1) != 0)
		{
			return -1;
		}
		int num2 = ((int)tile.X + num / 2) % 4;
		if (num2 >= 0)
		{
			return num2;
		}
		return num2 + 4;
	}

	private static int[] GetSprinklerCoverage(Vector2 center, int radius, Rectangle box, Dictionary<Vector2, int> index)
	{
		List<int> result = new List<int>((radius <= 0) ? 4 : ((radius * 2 + 1) * (radius * 2 + 1)));
		if (radius <= 0)
		{
			Add(center + new Vector2(0f, -1f));
			Add(center + new Vector2(0f, 1f));
			Add(center + new Vector2(-1f, 0f));
			Add(center + new Vector2(1f, 0f));
		}
		else
		{
			for (int i = -radius; i <= radius; i++)
			{
				for (int j = -radius; j <= radius; j++)
				{
					Add(center + new Vector2((float)j, (float)i));
				}
			}
		}
		return result.ToArray();
		void Add(Vector2 tile)
		{
			if (!(tile.X < (float)(box).Left) && !(tile.X >= (float)(box).Right) && !(tile.Y < (float)(box).Top) && !(tile.Y >= (float)(box).Bottom) && index.TryGetValue(tile, out var value))
			{
				result.Add(value);
			}
		}
	}

	private static bool CanPlaceSprinkler(GameLocation location, Vector2 tile)
	{
		if (location.objects.ContainsKey(tile))
		{
			return false;
		}
		TerrainFeature val = null;
		bool flag = ((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val);
		bool flag2 = flag;
		if (flag2)
		{
			bool flag3 = ((val is HoeDirt || val is Flooring) ? true : false);
			flag2 = !flag3;
		}
		if (flag2)
		{
			return false;
		}
		return location.CanItemBePlacedHere(tile, false, (CollisionMask)251, (CollisionMask)223, false, false);
	}

	public static bool NeedsPlacement(GameLocation location, Vector2 tile, Item? item)
	{
		Object val = (Object)((item is Object) ? item : null);
		if (val == null)
		{
			return false;
		}
		try
		{
			if (IsTerrainFeaturePlaceItem(val))
			{
				return !location.objects.ContainsKey(tile) && !((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).ContainsKey(tile) && location.doesTileHavePropertyNoNull((int)tile.X, (int)tile.Y, "Water", "Back") != "T" && location.doesTileHavePropertyNoNull((int)tile.X, (int)tile.Y, "Water", "Buildings") != "T";
			}
			if (IsRaisedCropSeed((Item)val, location))
			{
				return CanSowHere(location, tile, val);
			}
			return ((Item)val).canBePlacedHere(location, tile, (CollisionMask)251, false);
		}
		catch
		{
			return true;
		}
	}

	public static bool IsRaisedCropSeed(Item? item, GameLocation? location)
	{
		Object val = (Object)((item is Object) ? item : null);
		if (val == null || !IsSowSeed((Item)val))
		{
			return false;
		}
		try
		{
			string text = Crop.ResolveSeedId(((Item)val).ItemId, location);
			CropData val2 = null;
			return Crop.TryGetData(text, out val2) && val2.IsRaised;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsSolidPlaceItem(Item? item)
	{
		Object val = (Object)((item is Object) ? item : null);
		if (val != null && !val.isPassable())
		{
			if (!IsPlaceableObjectItem((Item)val))
			{
				return IsSprinklerItem((Item)val);
			}
			return true;
		}
		return false;
	}

	public static bool TryCenterInTile(Farmer player, out Vector2 original)
	{
		original = ((Character)player).Position;
		if (((Character)player).currentLocation == null)
		{
			return false;
		}
		Rectangle boundingBox = ((Character)player).GetBoundingBox();
		if (boundingBox.Width > 64 || boundingBox.Height > 64)
		{
			return false;
		}
		Vector2 tile = ((Character)player).Tile;
		Point standingPixel = ((Character)player).StandingPixel;
		Vector2 val = (standingPixel).ToVector2() - ((Character)player).Position;
		Vector2 val2 = tile * 64f + new Vector2(32f, 32f) - val;
		if (val2 == original)
		{
			return false;
		}
		((Character)player).Position = val2;
		if (((Character)player).Tile != tile)
		{
			((Character)player).Position = original;
			return false;
		}
		return true;
	}

	public static bool BboxOverlapsTile(Farmer player, Vector2 tile)
	{
		Rectangle boundingBox = ((Character)player).GetBoundingBox();
		return (boundingBox).Intersects(TileRect(tile));
	}

	public static bool TryClearOfTile(Farmer player, GameLocation location, Vector2 tile, out Vector2 original)
	{
		original = ((Character)player).Position;
		Rectangle val = TileRect(tile);
		Rectangle boundingBox = ((Character)player).GetBoundingBox();
		if (!(boundingBox).Intersects(val))
		{
			return false;
		}
		Vector2 tile2 = ((Character)player).Tile;
		(int, int)[] source = new(int, int)[4]
		{
			((val).Left - (boundingBox).Right - 1, 0),
			((val).Right - (boundingBox).Left + 1, 0),
			(0, (val).Top - (boundingBox).Bottom - 1),
			(0, (val).Bottom - (boundingBox).Top + 1)
		};
		foreach (var (num, num2) in source.OrderBy(((int Dx, int Dy) c) => Math.Abs(c.Dx) + Math.Abs(c.Dy)))
		{
			((Character)player).Position = new Vector2(original.X + (float)num, original.Y + (float)num2);
			if (((Character)player).Tile != tile2)
			{
				((Character)player).Position = original;
				continue;
			}
			Rectangle boundingBox2 = ((Character)player).GetBoundingBox();
			if ((boundingBox2).Intersects(val) || Collides(location, boundingBox2, player))
			{
				((Character)player).Position = original;
				continue;
			}
			return true;
		}
		if (TryCenterInTile(player, out original) && !BboxOverlapsTile(player, tile) && !Collides(location, ((Character)player).GetBoundingBox(), player))
		{
			return true;
		}
		((Character)player).Position = original;
		return false;
	}

	private static bool Collides(GameLocation location, Rectangle box, Farmer player)
	{
		try
		{
			return location.isCollidingPosition(box, Game1.viewport, true, 0, false, (Character)player);
		}
		catch
		{
			return true;
		}
	}

	private static Rectangle TileRect(Vector2 tile)
	{
		return new Rectangle((int)tile.X * 64, (int)tile.Y * 64, 64, 64);
	}

	public static List<Vector2> FindSqueezingObjects(GameLocation location, Farmer player)
	{
		List<Vector2> list = new List<Vector2>();
		Rectangle boundingBox = ((Character)player).GetBoundingBox();
		try
		{
			foreach (KeyValuePair<Vector2, Object> pair in location.objects.Pairs)
			{
				if (pair.Value != null && !pair.Value.isPassable())
				{
					Rectangle boundingBox2 = pair.Value.GetBoundingBox();
					if ((boundingBox2).Intersects(boundingBox))
					{
						list.Add(pair.Key);
					}
				}
			}
		}
		catch
		{
		}
		return list;
	}

	public static (int StepX, int StepY) GetSaplingSpacing(Item? item, ModConfig config)
	{
		int num = Math.Max(1, config.SaplingGapX + 1);
		int num2 = Math.Max(1, config.SaplingGapY + 1);
		Object val = (Object)((item is Object) ? item : null);
		if (val != null && val.IsFruitTreeSapling())
		{
			num = Math.Max(num, 3);
			num2 = Math.Max(num2, 3);
		}
		return (StepX: num, StepY: num2);
	}

	private static TileWork GetSaplingWork(GameLocation location, Vector2 tile, Object sapling)
	{
		if (location.objects.ContainsKey(tile))
		{
			return TileWork.None;
		}
		TerrainFeature val = null;
		if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val))
		{
			HoeDirt val2 = (HoeDirt)((val is HoeDirt) ? val : null);
			if (val2 != null && val2.crop != null)
			{
				return TileWork.None;
			}
			if (!(val is HoeDirt))
			{
				return TileWork.None;
			}
		}
		if (sapling.IsFruitTreeSapling() && FruitTree.IsGrowthBlocked(tile, location))
		{
			return TileWork.None;
		}
		if (!IsTreePlantableGround(location, tile))
		{
			return TileWork.None;
		}
		return TileWork.SaplingPlace;
	}

	private static bool IsTreePlantableGround(GameLocation location, Vector2 tile)
	{
		int num = (int)tile.X;
		int num2 = (int)tile.Y;
		bool flag = location.doesTileHaveProperty(num, num2, "Diggable", "Back", false) != null;
		string text = location.doesTileHaveProperty(num, num2, "Type", "Back", false);
		if (location.doesEitherTileOrTileIndexPropertyEqual(num, num2, "CanPlantTrees", "Back", "T"))
		{
			return true;
		}
		bool flag2 = flag;
		bool flag3 = flag2;
		if (!flag3)
		{
			bool flag4;
			switch (text)
			{
			case "Grass":
			case "Dirt":
			case "Stone":
				flag4 = true;
				break;
			default:
				flag4 = false;
				break;
			}
			flag3 = flag4;
		}
		return flag3;
	}

	private static TileWork GetFertilizeWork(GameLocation location, Vector2 tile, Object fertilizer)
	{
		// 花盆：照原版 Object.canBePlacedHere 的肥料分支——盆里那块土收这袋，且这东西原版认它能进盆
		// （树肥那种会被 IsPlantableItem 挡掉）。盆里有茶树时原版也放不进去，别派一趟空活。
		IndoorPot? pot = PotAt(location, tile);
		if (pot != null)
		{
			HoeDirt potDirt = pot.hoeDirt.Value;
			if (potDirt == null || pot.bush.Value != null || !pot.IsPlantableItem((Item)fertilizer) || !potDirt.CanApplyFertilizer(((Item)fertilizer).QualifiedItemId))
			{
				return TileWork.None;
			}
			return TileWork.Fertilize;
		}
		TerrainFeature val = null;
		if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val))
		{
			HoeDirt val2 = (HoeDirt)((val is HoeDirt) ? val : null);
			if (val2 != null)
			{
				if (IsTileOccupiedByObject(location, tile))
				{
					return TileWork.None;
				}
				if (!val2.CanApplyFertilizer(((Item)fertilizer).QualifiedItemId))
				{
					return TileWork.None;
				}
				return TileWork.Fertilize;
			}
			// 树肥：(O)805 在原版走 Object.placementAction -> Tree.fertilize()，
			// 目标不是 HoeDirt 而是野树。条件照抄 Tree.fertilize()：成长阶段 < 5 且还没施过。
			// 果树（FruitTree）原版不接受树肥；1.6 里 FruitTree 不是 Tree 的子类，
			// 所以 `val is Tree` 天然就把它排除了。
			if (((Item)fertilizer).QualifiedItemId == "(O)805" && val is Tree tree
				&& ((NetFieldBase<int, NetInt>)tree.growthStage).Value < 5
				&& !((NetFieldBase<bool, NetBool>)tree.fertilized).Value)
			{
				return TileWork.Fertilize;
			}
		}
		return TileWork.None;
	}

	/// <summary>
	/// 野树是否已经施过树肥。树肥的结果记在 <see cref="Tree.fertilized"/>，不在 HoeDirt 上，
	/// 所以只查 <see cref="HoeDirt.HasFertilizer"/> 会把施好的树判成没施。
	/// 果树/灌木不吃树肥：1.6 里 FruitTree 不是 Tree 的子类，天然被排除。
	/// </summary>
	public static bool IsTreeFertilized(GameLocation location, Vector2 tile)
	{
		return GetTree(location, tile) is Tree tree
			&& ((NetFieldBase<bool, NetBool>)tree.fertilized).Value;
	}

	public static bool IsTerrainFeaturePlaceItem(Object? obj)
	{
		if (obj == null)
		{
			return false;
		}
		string qualifiedItemId = ((Item)obj).QualifiedItemId;
		if (qualifiedItemId == "(O)297" || qualifiedItemId == "(O)BlueGrassStarter")
		{
			return true;
		}
		try
		{
			return obj.IsFloorPathItem();
		}
		catch
		{
			return false;
		}
	}

	private static TileWork GetObjectPlaceWork(GameLocation location, Vector2 tile, Object item)
	{
		if (location.objects.ContainsKey(tile))
		{
			return TileWork.None;
		}
		if (item.IsTapper())
		{
			TerrainFeature val = null;
			if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val))
			{
				Tree val2 = (Tree)((val is Tree) ? val : null);
				if (val2 != null)
				{
					if (((NetFieldBase<bool, NetBool>)(object)val2.stump).Value || ((NetFieldBase<int, NetInt>)(object)val2.growthStage).Value < 5)
					{
						return TileWork.None;
					}
					WildTreeData data = val2.GetData();
					if (data == null || !data.CanBeTapped())
					{
						return TileWork.None;
					}
					return TileWork.PlaceObject;
				}
			}
			return TileWork.None;
		}
		if (IsTerrainFeaturePlaceItem(item))
		{
			if (location.objects.ContainsKey(tile))
			{
				return TileWork.None;
			}
			if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).ContainsKey(tile))
			{
				return TileWork.None;
			}
			if (location.doesTileHavePropertyNoNull((int)tile.X, (int)tile.Y, "Water", "Back") == "T" || location.doesTileHavePropertyNoNull((int)tile.X, (int)tile.Y, "Water", "Buildings") == "T")
			{
				return TileWork.None;
			}
			return TileWork.PlaceObject;
		}
		if (!((Item)item).canBePlacedHere(location, tile, (CollisionMask)251, false))
		{
			return TileWork.None;
		}
		return TileWork.PlaceObject;
	}

	private static TileWork GetMachineWork(GameLocation location, Vector2 tile, Object item, ModConfig config, bool onlyIfAccepts = false)
	{
		if (!config.LoadMachines)
		{
			return TileWork.None;
		}
		Object val = null;
		if (!location.objects.TryGetValue(tile, out val) || val == null)
		{
			return TileWork.None;
		}
		// onlyIfAccepts：只认「这台机器收这件东西」，不认「这台机器正好有产出可收」。
		// 种子兼原料的（咖啡豆）走这条路，免得拿着种子路过熟机器就把产出顺走。
		if (((NetFieldBase<bool, NetBool>)(object)val.readyForHarvest).Value && ((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value != null)
		{
			// 熟机器 = 先收产物再装新料，本来是一趟活儿。
			// onlyIfAccepts 下要先确认「这台机器空着也收手里这件」才排；不收就跳过，
			// 不然只收不填是耍流氓。旧版一刀切跳过熟机器，玩家拿咖啡豆框一片熟桶
			// 想要「收咖啡 + 灌豆子」，结果 238 格全红（实机 2026-11-02 截图）。
			if (onlyIfAccepts && !MachineAcceptsItem(val, item))
			{
				return TileWork.None;
			}
			// 装填熟机器 = 先收产物再装新料。背包放不下产物时两半都干不成：
			// 排进队列只会让农夫走过去空手而归、格子被静默甩去队尾，整列熟机器原封不动
			// （实机 2026-11-01：左列整列 why=machine 弹队尾）。这种格干脆不排，并由调用方提示原因。
			Object output = ((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value;
			return ReadyMachineBlockedByFullBag((Item)(object)output) ? TileWork.None : TileWork.MachineLoad;
		}
		if (((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value != null || val.MinutesUntilReady > 0)
		{
			return TileWork.None;
		}
		try
		{
			return val.performObjectDropInAction((Item)item, true, Game1.player, false) ? TileWork.MachineLoad : TileWork.None;
		}
		catch
		{
			return TileWork.None;
		}
	}

	/// <summary>这台机器「空着的时候」收不收这件东西。</summary>
	/// <remarks>
	/// 熟机器的 heldObject 占着位置，probe 一律返回 false，所以先临时借走再放回去；
	/// 机器种类里混着一堆 mod 机器，没有通用规则表，probe 是唯一权威。
	/// 同一「机种 + 原料」的答案恒定，缓存住，238 格桶阵也只探一次。
	/// </remarks>
	private static bool MachineAcceptsItem(Object machine, Object item)
	{
		string key = ((Item)machine).QualifiedItemId + "|" + ((Item)item).QualifiedItemId;
		if (MachineAcceptCache.TryGetValue(key, out bool cached))
		{
			return cached;
		}
		bool ok = false;
		Object savedObject = null;
		bool savedReady = false;
		try
		{
			savedObject = ((NetFieldBase<Object, NetRef<Object>>)(object)machine.heldObject).Value;
			savedReady = ((NetFieldBase<bool, NetBool>)(object)machine.readyForHarvest).Value;
			((NetFieldBase<Object, NetRef<Object>>)(object)machine.heldObject).Value = null;
			((NetFieldBase<bool, NetBool>)(object)machine.readyForHarvest).Value = false;
			ok = machine.performObjectDropInAction((Item)item, true, Game1.player, false);
		}
		catch
		{
			ok = false;
		}
		finally
		{
			try
			{
				((NetFieldBase<Object, NetRef<Object>>)(object)machine.heldObject).Value = savedObject;
				((NetFieldBase<bool, NetBool>)(object)machine.readyForHarvest).Value = savedReady;
			}
			catch
			{
			}
		}
		MachineAcceptCache[key] = ok;
		return ok;
	}

	/// <summary>这一格上「已完成、等收产物」的机器里装着什么；没有则 null。</summary>
	public static Object? GetReadyMachineOutput(GameLocation location, Vector2 tile)
	{
		Object val = null;
		if (!location.objects.TryGetValue(tile, out val) || val == null)
		{
			return null;
		}
		if (!((NetFieldBase<bool, NetBool>)(object)val.readyForHarvest).Value)
		{
			return null;
		}
		return ((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value;
	}

	/// <summary>背包收不下这份产物 ⇒ 「先收再装」必然失败。</summary>
	public static bool ReadyMachineBlockedByFullBag(Item? output)
	{
		if (output == null)
		{
			return false;
		}
		Farmer player = Game1.player;
		if (player == null)
		{
			return false;
		}
		return !player.couldInventoryAcceptThisItem(output);
	}

	private static bool IsFlowerCrop(Crop crop)
	{
		string value = ((NetFieldBase<string, NetString>)(object)crop.indexOfHarvest).Value;
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}
		if (FlowerCache.TryGetValue(value, out var value2))
		{
			return value2;
		}
		bool flag = false;
		try
		{
			ParsedItemData dataOrErrorItem = ItemRegistry.GetDataOrErrorItem(value);
			flag = dataOrErrorItem != null && dataOrErrorItem.Category == -80;
		}
		catch
		{
			flag = false;
		}
		FlowerCache[value] = flag;
		return flag;
	}

	public static bool IsWaterSource(GameLocation location, Vector2 tile)
	{
		return location.CanRefillWateringCanOnTile((int)tile.X, (int)tile.Y);
	}

	private static bool IsTileOccupiedByObject(GameLocation location, Vector2 tile)
	{
		Object val = null;
		if (!location.objects.TryGetValue(tile, out val) || val == null)
		{
			return false;
		}
		if (val is IndoorPot)
		{
			return false;
		}
		return true;
	}

	public static bool IsSowSeed(Item? item)
	{
		Object val = (Object)((item is Object) ? item : null);
		if (val != null)
		{
			return ((Item)val).Category == -74;
		}
		return false;
	}

	public static bool CanSowHere(GameLocation location, Vector2 tile, Object seed)
	{
		if (((location != null) ? location.Map : null) == null)
		{
			return false;
		}
		// 花盆：照原版 Object.canBePlacedHere 的种子分支来——盆里没茶树、这东西原版认它能进盆，
		// 再问盆里那块土收不收。规则全交给原版，不在这里重写一遍。
		// 这里不用 CanSowHereIgnoringPlayer：人站不到花盆那一格上，攀爬作物那条包围盒判定碰不着。
		IndoorPot? pot = PotAt(location, tile);
		if (pot != null)
		{
			if (pot.bush.Value != null || pot.hoeDirt.Value == null || !pot.IsPlantableItem((Item)seed))
			{
				return false;
			}
			return pot.hoeDirt.Value.canPlantThisSeedHere(((Item)seed).ItemId, false);
		}
		TerrainFeature val = null;
		if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val))
		{
			HoeDirt val2 = (HoeDirt)((val is HoeDirt) ? val : null);
			if (val2 != null)
			{
				if (IsRaisedCropSeed((Item)seed, location))
				{
					return CanSowHereIgnoringPlayer(val2, seed, location);
				}
				return val2.canPlantThisSeedHere(((Item)seed).ItemId, false);
			}
		}
		return false;
	}

	private static bool CanSowHereIgnoringPlayer(HoeDirt dirt, Object seed, GameLocation location)
	{
		if (dirt.crop != null)
		{
			return false;
		}
		try
		{
			string text = Crop.ResolveSeedId(((Item)seed).ItemId, location);
			CropData val = null;
			if (!Crop.TryGetData(text, out val))
			{
				return false;
			}
			if (val.Seasons.Count == 0)
			{
				return false;
			}
			if (!location.IsOutdoors || location.SeedsIgnoreSeasonsHere() || val.Seasons.Contains(location.GetSeason()))
			{
				return true;
			}
			bool result;
			switch (text)
			{
			case "309":
			case "310":
			case "311":
				result = true;
				break;
			default:
				result = false;
				break;
			}
			return result;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsWanted(TileWork work, ModConfig config)
	{
		bool result = work switch
		{
			TileWork.Till => config.TillDirt, 
			TileWork.Weed => config.ClearWeeds, 
			TileWork.ArtifactSpot => true, 
			TileWork.SeedSpot => true, 
			TileWork.WaterDirt => !config.WaterCropsOnly, 
			TileWork.WaterCrop => true, 
			TileWork.Sow => true, 
			TileWork.ScytheCrop => config.HarvestCrops, 
			TileWork.ScytheFlower => config.HarvestFlowers, 
			TileWork.ScytheForage => config.CollectForage, 
			TileWork.ShakeBush => config.CollectForage, 
			TileWork.HarvestFruit => config.CollectForage, 
			TileWork.ScytheMoss => config.HarvestMoss, 
			TileWork.ScytheBluegrass => config.HarvestBluegrass, 
			TileWork.ScytheGrass => config.HarvestOtherGrass, 
			TileWork.ScytheDeadCrop => config.ClearDeadCrops, 
			TileWork.ScytheWeed => config.ScytheClearWeeds, 
			TileWork.ScytheMachine => config.HarvestMachines,
			TileWork.ScythePond => config.HarvestMachines, 
			TileWork.PickDeadCrop => config.PickDeadCrops, 
			TileWork.PickEmptyDirt => config.PickEmptyDirt, 
			TileWork.PickFertilizedDirt => config.PickFertilizedDirt, 
			TileWork.PickWeed => config.PickWeeds, 
			TileWork.PickFloor => config.PickFloor, 
			TileWork.PickClump => config.PickResourceClumps, 
			TileWork.PickObject => config.PickObjects, 
			TileWork.PickStone => config.PickStones, 
			TileWork.PickBoulder => config.PickResourceClumps, 
			TileWork.PickFence => config.PickFences, 
			TileWork.AxeFruitTreeSeed => config.AxeFruitTreeSeeds, 
			TileWork.AxeFruitTreeSapling => config.AxeFruitTreeSaplings, 
			TileWork.AxeFruitTreeMature => config.AxeFruitTreeMature, 
			TileWork.AxeTreeSeed => config.AxeTreeSeeds, 
			TileWork.AxeTreeSapling => config.AxeTreeSaplings, 
			TileWork.AxeTreeMature => config.AxeTreeMature,
			TileWork.AxeTapper => config.AxeTappers,
			TileWork.AxeStump => config.AxeStumps, 
			TileWork.AxeDeadCrop => config.AxeDeadCrops, 
			TileWork.AxeLiveCrop => config.AxeLiveCrops, 
			TileWork.AxeGiantCrop => config.AxeGiantCrops, 
			TileWork.AxeTwig => config.AxeTwigs, 
			TileWork.AxeWeed => config.AxeWeeds, 
			TileWork.AxeFence => config.AxeFences, 
			TileWork.AxeBush => config.AxeBushes, 
			TileWork.MachineLoad => config.LoadMachines, 
			TileWork.SprinklerPlace => true, 
			TileWork.Fertilize => true, 
			TileWork.SaplingPlace => true, 
			TileWork.PlaceObject => true, 
			_ => false, 
		};
		return result;
	}

	/// <summary>鱼塘产物格（池塘中心那格）上还有没有没收的产物。</summary>
	public static FishPond? GetPondWithOutputAt(GameLocation location, Vector2 tile)
	{
		Farm farm = (Farm)((location is Farm) ? location : null);
		if (farm == null)
		{
			return null;
		}
		Point want = new Point((int)tile.X, (int)tile.Y);
		foreach (Building building in ((GameLocation)farm).buildings)
		{
			FishPond pond = (FishPond)((building is FishPond) ? building : null);
			if (pond == null || ((NetFieldBase<Item, NetRef<Item>>)(object)pond.output).Value == null)
			{
				continue;
			}
			if (PondOutputTile(pond) == want)
			{
				return pond;
			}
		}
		return null;
	}

	public static Point PondOutputTile(FishPond pond)
	{
		return new Point(((NetFieldBase<int, NetInt>)(object)((Building)pond).tileX).Value + ((NetFieldBase<int, NetInt>)(object)((Building)pond).tilesWide).Value / 2, ((NetFieldBase<int, NetInt>)(object)((Building)pond).tileY).Value + ((NetFieldBase<int, NetInt>)(object)((Building)pond).tilesHigh).Value / 2);
	}

	/// <summary>鱼塘占地格；待收鱼塘的池心除外，池心由 GetTileWork 单独标成工作格。</summary>
	public static HashSet<Point> PondFootprintExceptOutput(GameLocation location)
	{
		HashSet<Point> tiles = new HashSet<Point>();
		Farm farm = (Farm)((location is Farm) ? location : null);
		if (farm == null)
		{
			return tiles;
		}
		foreach (Building building in ((GameLocation)farm).buildings)
		{
			FishPond pond = (FishPond)((building is FishPond) ? building : null);
			if (pond == null)
			{
				continue;
			}
			Point output = PondOutputTile(pond);
			bool hasOutput = ((NetFieldBase<Item, NetRef<Item>>)(object)pond.output).Value != null;
			int left = ((NetFieldBase<int, NetInt>)(object)((Building)pond).tileX).Value;
			int top = ((NetFieldBase<int, NetInt>)(object)((Building)pond).tileY).Value;
			int right = left + ((NetFieldBase<int, NetInt>)(object)((Building)pond).tilesWide).Value;
			int bottom = top + ((NetFieldBase<int, NetInt>)(object)((Building)pond).tilesHigh).Value;
			for (int x = left; x < right; x++)
			{
				for (int y = top; y < bottom; y++)
				{
					Point tile = new Point(x, y);
					if (!hasOutput || tile != output)
					{
						tiles.Add(tile);
					}
				}
			}
		}
		return tiles;
	}

	/// <summary>框里是否存在「手持这件物品真有活干」的格子——镰刀框选分流和框选标记共用这条判据。</summary>
	public static bool BoxHasTileWork(GameLocation location, Rectangle box, Item? item, ModConfig config)
	{
		for (int x = box.Left; x < box.Right; x++)
		{
			for (int y = box.Top; y < box.Bottom; y++)
			{
				TileWork work = GetTileWork(location, new Vector2((float)x, (float)y), item, config);
				if (work != TileWork.None && IsWanted(work, config))
				{
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>
	/// 框选扫描用：这一格的活儿对指定工具算不算「杂物」。
	/// 杂物 = 玩家口中「砍树、清理杂草、清理石头、清理树枝」这类清理对象，
	/// 各工具能清什么仍由通用配置里的开关决定（GetTileWork 已经读过开关）。
	/// </summary>
	public static bool IsScytheDebrisWork(TileWork work)
	{
		return work == TileWork.ScytheWeed;
	}

	/// <summary>斧头杂物：树 / 果树（各生长阶段跟开关走）、树桩、树枝。不含栅栏、作物、巨大作物。</summary>
	public static bool IsAxeDebrisWork(TileWork work)
	{
		switch (work)
		{
		case TileWork.AxeTreeSeed:
		case TileWork.AxeTreeSapling:
		case TileWork.AxeTreeMature:
		case TileWork.AxeTapper:
		case TileWork.AxeFruitTreeSeed:
		case TileWork.AxeFruitTreeSapling:
		case TileWork.AxeFruitTreeMature:
		case TileWork.AxeStump:
		case TileWork.AxeTwig:
			return true;
		default:
			return false;
		}
	}

	/// <summary>镐头杂物：石头、巨石、树桩堆等 clump。不含杂草 / 地板 / 栅栏 / 摆放物。</summary>
	public static bool IsPickDebrisWork(TileWork work)
	{
		switch (work)
		{
		case TileWork.PickStone:
		case TileWork.PickBoulder:
		case TileWork.PickClump:
			return true;
		default:
			return false;
		}
	}

	/// <summary>扫一遍选框：有没有哪怕一格「这把工具想干、且 want 认可」的活。杂物换工具链用它挑工具。</summary>
	public static bool BoxHasTileWork(GameLocation location, Rectangle box, Item? item, ModConfig config, Func<TileWork, bool> want)
	{
		for (int x = box.Left; x < box.Right; x++)
		{
			for (int y = box.Top; y < box.Bottom; y++)
			{
				TileWork work = GetTileWork(location, new Vector2((float)x, (float)y), item, config);
				if (work != TileWork.None && IsWanted(work, config) && want(work))
				{
					return true;
				}
			}
		}
		return false;
	}

	public static bool CanWork(GameLocation location, Vector2 tile, ModConfig config, Item? item)
	{
		return IsWanted(GetTileWork(location, tile, item, config), config);
	}

	public static bool LooksWorkable(GameLocation location, Vector2 tile, ModConfig config, Item? item)
	{
		if (IsScythe(item))
		{
			return GetTileWork(location, tile, item, config) != TileWork.None;
		}
		if (item is Pickaxe)
		{
			return GetTileWork(location, tile, item, config) != TileWork.None;
		}
		if (item is Axe)
		{
			return GetTileWork(location, tile, item, config) != TileWork.None;
		}
		if (item is WateringCan)
		{
			// 和 GetTileWork 共用同一份判定（含花盆里那块土）；两边各写一份迟早漂。
			return IsWanted(GetWaterWork(location, tile), config);
		}
		if (IsSowSeed(item))
		{
			Object val3 = (Object)((item is Object) ? item : null);
			if (val3 != null)
			{
				if (!CanSowHere(location, tile, val3))
				{
					return false;
				}
				if (config.SowSprinkleredOnly)
				{
					return IsSprinkleredTile(location, tile);
				}
				return true;
			}
		}
		Object val4 = null;
		if (location.objects.TryGetValue(tile, out val4))
		{
			if (val4.IsWeeds())
			{
				return config.ClearWeeds;
			}
			if (((Item)val4).QualifiedItemId == "(O)590")
			{
				return true;
			}
			if (((Item)val4).QualifiedItemId == "(O)SeedSpot")
			{
				return true;
			}
			return false;
		}
		if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).ContainsKey(tile))
		{
			return false;
		}
		if (config.TillDirt)
		{
			return location.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back", false) != null;
		}
		return false;
	}

	public static string Describe(TillBlock reason)
	{
		string result = reason switch
		{
			TillBlock.NoMap => "地图没加载好", 
			TillBlock.OutOfBounds => "超出地图范围", 
			TillBlock.Occupied => "格子上已经有东西（杂草/石头/建筑/已有耕地等）", 
			TillBlock.ResourceClump => "被大石头或树桩压着", 
			TillBlock.NotDiggable => "地砖不是可耕地（草地/水面/铺装路面）", 
			TillBlock.NotReachable => "地砖可挖但站不上去（贴着围栏/建筑那圈）", 
			TillBlock.NotSelected => "这类活儿没勾选（杂草）", 
			TillBlock.WrongTool => "手里的东西在这一格干不了活（水壶/种子要的是翻好的耕地）", 
			TillBlock.NotSprinklered => "这一格没有洒水器浇得到（配置里开着\"仅…洒水器能灌溉到的土地\"）", 
			_ => "可用", 
		};
		return result;
	}

	public static Rectangle GetTileArea(Vector2 tile)
	{
		return new Rectangle((int)tile.X * 64, (int)tile.Y * 64, 64, 64);
	}

	public static Vector2 GetTileCenterPixel(Vector2 tile)
	{
		return tile * 64f + new Vector2(32f);
	}

	public static Vector2 ToScreen(Vector2 tile)
	{
		return Game1.GlobalToLocal(Game1.viewport, tile * 64f);
	}

	public static float ChebyshevDistance(Vector2 a, Vector2 b)
	{
		return MathF.Max(MathF.Abs(a.X - b.X), MathF.Abs(a.Y - b.Y));
	}

	/// <summary>四方向走路真实要迈的步数（斜对角 1 格 = 2 步）。选「下一格去哪」要用它：切氏距离会把斜向当近路。</summary>
	public static float ManhattanDistance(Vector2 a, Vector2 b)
	{
		return MathF.Abs(a.X - b.X) + MathF.Abs(a.Y - b.Y);
	}

	public static bool MonsterNear(GameLocation? location, Vector2 tile, int radius = 2)
	{
		if (location?.characters == null)
		{
			return false;
		}
		foreach (NPC character in location.characters)
		{
			if (character is Monster && Math.Abs(((Character)character).Tile.X - tile.X) <= (float)radius && Math.Abs(((Character)character).Tile.Y - tile.Y) <= (float)radius)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>附近有没有「推不动」的角色：宠物/马/拖拉机/村民（farmerPassesThrough=false）。
	/// 农场动物不算——玩家能推着它们走，不该因为它们在旁边就绕路或放弃。</summary>
	public static bool SolidCharacterNear(GameLocation? location, Vector2 tile, int radius = 2)
	{
		if (location?.characters == null)
		{
			return false;
		}
		foreach (NPC character in location.characters)
		{
			if (character != null && !character.farmerPassesThrough && ChebyshevDistance(((Character)character).Tile, tile) <= (float)radius)
			{
				return true;
			}
		}
		return false;
	}

	public static bool DynamicBlockerNear(GameLocation? location, Vector2 tile, int radius = 2)
	{
		return CollectBlockerTiles(location, tile, radius).Count > 0;
	}

	public unsafe static HashSet<Point> CollectBlockerTiles(GameLocation? location, Vector2 tile, int radius = 3, bool includeAnimals = true)
	{
		HashSet<Point> hashSet = new HashSet<Point>();
		try
		{
			if (location == null)
			{
				return hashSet;
			}
			foreach (NPC character in location.characters)
			{
				if (character != null && ChebyshevDistance(((Character)character).Tile, tile) <= (float)radius)
				{
					hashSet.Add(((Character)character).TilePoint);
				}
			}
			// 收动物是为了让「卡住」能报出「被动物挡」；走路绕行时传 includeAnimals:false——
			// 动物推得开，绕它反而把路线拉长（绕＝围着羊群转圈）。
			if (includeAnimals)
			{
				var enumerator2 = ((NetDictionary<long, FarmAnimal, NetRef<FarmAnimal>, SerializableDictionary<long, FarmAnimal>, NetLongDictionary<FarmAnimal, NetRef<FarmAnimal>>>)(object)location.animals).Values.GetEnumerator();
				try
				{
					while (enumerator2.MoveNext())
					{
						FarmAnimal current2 = enumerator2.Current;
						if (current2 != null && ChebyshevDistance(((Character)current2).Tile, tile) <= (float)radius)
						{
							hashSet.Add(((Character)current2).TilePoint);
							Rectangle boundingBox = ((Character)current2).GetBoundingBox();
							hashSet.Add(new Point((boundingBox).Center.X / 64, (boundingBox).Center.Y / 64));
						}
					}
				}
				finally
				{
					((IDisposable)enumerator2/*cast due to constrained. prefix*/).Dispose();
				}
			}
			// 其他玩家不收：原版农夫移动不把玩家算碰撞，收进来会让走路去绕一堵不存在的墙。
		}
		catch
		{
		}
		return hashSet;
	}

	public static List<Point>? FindPathAvoiding(GameLocation location, Point from, Point to, HashSet<Point> blocked)
	{
		try
		{
			bool[] standableMap = GetWalkableMap(location, out var width, out var height);
			if (standableMap == null || width <= 0 || height <= 0)
			{
				return null;
			}
			if ((uint)from.X >= (uint)width || (uint)from.Y >= (uint)height || (uint)to.X >= (uint)width || (uint)to.Y >= (uint)height)
			{
				return null;
			}
			if (!standableMap[to.Y * width + to.X] || blocked.Contains(to))
			{
				return null;
			}
			if (from == to)
			{
				return new List<Point> { from };
			}
			int num = from.Y * width + from.X;
			int num2 = to.Y * width + to.X;
			int[] array = new int[width * height];
			Array.Fill(array, -1);
			array[num] = num;
			Queue<int> queue = new Queue<int>();
			queue.Enqueue(num);
			while (queue.Count > 0)
			{
				int num3 = queue.Dequeue();
				if (num3 == num2)
				{
					break;
				}
				int num4 = num3 % width;
				int num5 = num3 / width;
				if (num5 > 0 && array[num3 - width] < 0 && standableMap[num3 - width] && !blocked.Contains(new Point(num4, num5 - 1)))
				{
					array[num3 - width] = num3;
					queue.Enqueue(num3 - width);
				}
				if (num5 < height - 1 && array[num3 + width] < 0 && standableMap[num3 + width] && !blocked.Contains(new Point(num4, num5 + 1)))
				{
					array[num3 + width] = num3;
					queue.Enqueue(num3 + width);
				}
				if (num4 > 0 && array[num3 - 1] < 0 && standableMap[num3 - 1] && !blocked.Contains(new Point(num4 - 1, num5)))
				{
					array[num3 - 1] = num3;
					queue.Enqueue(num3 - 1);
				}
				if (num4 < width - 1 && array[num3 + 1] < 0 && standableMap[num3 + 1] && !blocked.Contains(new Point(num4 + 1, num5)))
				{
					array[num3 + 1] = num3;
					queue.Enqueue(num3 + 1);
				}
			}
			if (array[num2] < 0)
			{
				return null;
			}
			List<Point> list = new List<Point>();
			for (int num6 = num2; num6 != num; num6 = array[num6])
			{
				list.Add(new Point(num6 % width, num6 / width));
			}
			list.Add(from);
			list.Reverse();
			return list;
		}
		catch
		{
			return null;
		}
	}

	public static int GetFacingDirection(Vector2 fromTile, Vector2 toTile)
	{
		Vector2 val = toTile - fromTile;
		if (MathF.Abs(val.X) >= MathF.Abs(val.Y))
		{
			if (!(val.X >= 0f))
			{
				return 3;
			}
			return 1;
		}
		if (!(val.Y >= 0f))
		{
			return 0;
		}
		return 2;
	}
}
