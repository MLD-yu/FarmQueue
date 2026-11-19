using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Audio;
using StardewValley.Buildings;
using StardewValley.Enchantments;
using StardewValley.Network;
using StardewValley.Objects;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using Object = StardewValley.Object;

namespace FarmQueue;

internal sealed class TillQueue
{
	private const int WalkTimeoutTicks = 240;


	private const int MaxWalkAttempts = 2;


	private const int SplashScanRadius = 8;

	private int StaleCheckCountdown;

	private static readonly MethodInfo? AffectedTilesMethod = typeof(Tool).GetMethod("tilesAffected", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[3]
	{
		typeof(Vector2),
		typeof(int),
		typeof(Farmer)
	}, null);

	private readonly ModEntry Mod;

	private readonly List<Vector2> Tiles;

	private int Index;

	private bool PlacingMode;

	private bool SealPlacedTiles;

	private int? PreferredFacing;

	/// <summary>上一挥实际用的朝向：蓄力朝向要连续，不能每格都按「覆盖最多」翻方向。</summary>
	private int? LastSwingFacing;

	private Vector2? PreferredStand;

	private int? PendingPower;

	private const int MaxTileAttempts = 3;

	private readonly Dictionary<Vector2, int> TileAttempts;

	private readonly HashSet<Vector2> TileDone;

	private int? LastScytheFacing;

	private Vector2 LastScytheTarget;

	/// <summary>上一刀挥出时农夫站的格。和当前位置相减就是「最近一次移动方向」，
	/// 供 PickNextScytheTile 的方向惯性用（离线模拟 V6，见 CHANGELOG 0.5.65）。</summary>
	private Vector2 LastScytheStand;

	private bool ScytheMachineRoute;

	/// <summary>本趟镰刀作业里有没有「作物批」（枯萎作物/作物/花）。有则它们排在队列前段、
	/// 走行段 S，且不被 PickNextScytheTile 就近重挑（重挑会把 S 打散）。</summary>
	private bool ScytheCropRoute;

	/// <summary>作物批执行期间，覆盖数只统计作物格：否则杂草/草堆会把找站位的逻辑
	/// 从 S 路线上拽走。</summary>
	private bool ScytheCoverageCropsOnly;

	/// <summary>作物批的「带子扫完后补漏」是否已经插过队。只插一次。</summary>
	private bool ScytheCropMopUpDone;

	private List<Vector2>? ActiveSwingCovers;

	/// <summary>挥这一刀之前，覆盖范围内「确实还有活」的格。挥完仍全活着 = 这一刀白挥，
	/// 用于区分「站位/朝向没对准」和「这一格真的做不成」——前者不该记失败次数。</summary>
	private readonly HashSet<Vector2> ScytheWorkBefore = new HashSet<Vector2>();

	/// <summary>连续整刀白挥的次数。攒到 2 次就恢复正常的失败计数，避免永远卡在同一格。</summary>
	private int ScytheVainSwings;

	/// <summary>待复查的一刀（延迟几帧再看结果）。镰刀的实际收割常常比 beginUsing 晚几帧落地
	/// （原版逐帧判定 + 镰刀收割 mod 的补丁），挥完当下立刻复查会把「其实已经割掉」误判成没割到，
	/// 累计三次就把格子判死：气泡消失、作物还在地里。</summary>
	private int ScytheVerifyDelay;

	private Vector2 PendingScytheTarget;

	private List<Vector2>? PendingScytheCovers;

	private readonly HashSet<Vector2> PendingScytheBefore = new HashSet<Vector2>();

	/// <summary>这一格在哪些站位上试过挥不到（四朝向都够不着）。下次找站位跳过它们，
	/// 否则「站位选错 → 打不到 → 判失败」会反复发生在同一个错站上。</summary>
	private readonly Dictionary<Vector2, HashSet<Vector2>> ScytheBadStands = new Dictionary<Vector2, HashSet<Vector2>>();

	/// <summary>这一格换过几次站位。攒到 2 次就认输，避免在够不到的角落空转。</summary>
	private readonly Dictionary<Vector2, int> ScytheStandRetries = new Dictionary<Vector2, int>();

	private readonly List<TillPlanner.Swing> SwingPlan;

	private readonly Dictionary<Vector2, int> SegmentOf;

	private int SwingCursor;

	private GameLocation? Location;

	private bool Walking;

	private Vector2 WalkTarget;

	private int WalkTicks;

	private int WalkTotalTicks;

	private Vector2 WalkLastTile;

	private int ControllerGoneTicks;

	private string WalkStartFailure;

	private bool WalkingByDriver;

	private Point? WalkDestination;

	private bool WalkUnstuckTried;

	private Vector2? Current;

	private int WalkAttempts;

	// 走路失败限流：8 个候选站位全寻不到路时，别每帧再烧 8 次全图 A*
	// （一次失败寻路要把整片连通区展开到 10000 节点上限才放弃，一帧 8 次就是几十~几百毫秒）。
	// 实机 2026-11-03：围栏门后整片地不可达，帧率掉到个位数。
	// 注意这是「全局」冷却，换目标不清零 —— 整片区域不可达时本来就该整体降速；
	// 换一格就解锁等于没限流（400 格不可达 = 400 次 8 连 A*）。只有走成功、门推开、开新活时才清零。
	private int WalkFailCooldownTicks;

	private const int WalkFailCooldown = 30;

	// 围栏门：原版寻路把关着的门当墙（Fence.isPassable() 只认 gatePosition >= 88），
	// 所以门后的地根本搜不到路。游戏自己的 PathFindController 是在「移动」阶段顺手开门
	// （nonDestructivePathing → fence.toggleGate(open: true)），我们只借了它的搜索、自己按键走路，
	// 没人开门 —— 只能人走到门口 checkForAction 把门推开（顺带把该格塞进 TemporaryPassableTiles）。
	// 同一套做法动物队列早就有了（AnimalQueue.TryWalkToGate / TickGate），这里照搬。
	private Fence? GateTarget;

	private Vector2 GateTile;

	private Point GateStand;

	/// <summary>0 空闲 / 1 正走向门边站位 / 2 站在门口、准备推门。</summary>
	private int GatePhase;

	private int GateTicks;

	private Vector2 DriverDetachTarget;

	private int DriverDetachRetries;

	private bool Refilling;

	// 装完水回来：下一格先按原队序取，不许按水池那头的站位重排/就近跳
	// （实机：接完水后先就近做了水池边一格，再横跳回原来那一行，整条路线看起来全乱）。
	private bool SkipOrderOnce;

	private int Tilled;

	private int Skipped;

	private int Refills;

	private string ReportVerb;

	/// <summary>手持种子、但这一趟全是往机器里装（咖啡豆→小桶）：措辞和路线都按装载走。</summary>
	private bool SeedMachineRun;

	private readonly List<Vector2> RetryTiles;

	private bool RetryRoundUsed;

	private readonly HashSet<Vector2> RetriedTiles;

	/// <summary>这一趟已经去推过的门（按门格坐标记），防止在两扇门之间来回折腾。</summary>
	private readonly HashSet<Vector2> GatesTried;

	private const int MaxGatesTried = 4;

	private const int GateWalkTimeoutTicks = 90;

	private readonly HashSet<Vector2> UnreachableTiles;

	private readonly HashSet<Vector2> FailedTiles;

	private bool RescanLogged;

	private Vector2? MachineOutcomeTile;

	private bool MachineOutcomeProbed;

	private bool MachineOutcomePlaced;

	private bool MachineOutcomeConsumed;

	private readonly List<Vector2> TrailRoute;

	private Vector2? TrailExit;

	private int TrailCursor;

	private bool TrailMode;

	private int SplashTilled;

	private int FailedSwings;

	private readonly Dictionary<Vector2, int> GrassBefore;

	private TerrainFeature? PlaceSnapFeature;

	private bool PlaceSnapHadObject;

	private readonly HashSet<Vector2> PlacedTiles;

	private bool ScytheGrabsCrops;

	private readonly Dictionary<Vector2, int> PickHits;


	private bool WaitingSwing;

	private bool Charging;

	private int ChargeSteps;

	private int ChargePower;

	private int ChargeStepMs;

	private int ChargeElapsedMs;

	private Vector2 ChargeTarget;

	private Vector2 ChargeCutTile;

	private int ChargeCutCount;

	private int SwingTicks;

	private Vector2 SwingTarget;

	/// <summary>
	/// 这一挥真正对着的那一格（蓄力区的中心/起点）。它允许和队列里的队首差一格：
	/// 选框边缘时靠它把 3 行/3 列高的带子挪进框里，而记账、验收仍然按队首那格。
	/// </summary>
	private Vector2? SwingAim;

	private Item? StartItem;

	/// <summary>
	/// 杂物混合跑的工具分工表：非 null 时这一趟会沿途换工具（镰刀 / 斧头 / 镐头），
	/// 每一格该用哪把、算是什么活都问它，不再一路只认 StartItem。
	/// </summary>
	private DebrisPlan? Plan;

	/// <summary>这一趟沿途换了几次工具（收工的日志里报）。</summary>
	private int ToolSwitches;

	/// <summary>
	/// FindNearestLiveSlot 刚算出来的步行距离场，给 LONG-WALK 诊断复用。
	/// 它是**裁剪过的**（只灌了「玩家 + 待办格」那一片），所以取数一律走 LiveField.Serve()，不要当整图数组下标。
	/// </summary>
	private TileUtil.DistanceField LiveField;

	/// <summary>最近一次挑格时，有几格待办活读成「此刻走不过去」（被不在队列里的树/水/建筑挡着）。</summary>
	private int LastScanUnreachable;

	/// <summary>「到了目的地位能不能挥得比脚下更多」这个比较的缓存（见 Tick 里的用法）。
	/// 每次起步在 ResetWalkClocks 里作废，所以陈旧最多持续一段走路。</summary>
	private Vector2 CoverageCheckTile = new Vector2(float.MinValue, float.MinValue);

	private Vector2 CoverageCheckStand = new Vector2(float.MinValue, float.MinValue);

	private bool CoverageCheckResult;

	private Vector2 StartAnchor;

	private Dictionary<Vector2, TileUtil.TileWork> Planned;

	private Rectangle? SelectionBox;

	private bool AffectedTilesBroken;

	private bool MovementWasDown;


	public bool IsRunning => Tiles.Count > 0;

	/// <summary>
	/// 最近一次 Stop 是不是「自己干完收工」（没带打断消息）。
	/// 杂物自动换工具链靠它区分「这一轮干完了，可以换下一把工具」和「被打断了，整条链收摊」。
	/// </summary>
	internal bool LastStopWasOwnWork { get; private set; }

	/// <summary>最近一次 Stop 是不是「体力不支」收工：杂物链把它当打断收摊，但弹背包要放行。</summary>
	internal bool LastStopWasStamina { get; private set; }

	/// <summary>这一趟是不是「按格分工、沿途换工具」的杂物混合跑。</summary>

	/// <summary>这一趟沿途换了几次工具。</summary>
	internal int MixedToolSwitches => ToolSwitches;

	/// <summary>
	/// 这一格该用哪把工具：混合跑按分工表（镰刀 / 斧头 / 镐头），普通跑就是开局手里那把。
	/// 凡是「问某一格还有没有活」的地方都得走这里，不能直接拿 StartItem——
	/// 混合跑时手里可能正拿着镰刀，而那一格是要镐头敲的石头，拿镰刀问会得到「没活」，格子就被当成干完了。
	/// </summary>
	private Item? ToolFor(Vector2 tile)
	{
		DebrisPlan? plan = Plan;
		if (plan == null)
		{
			return StartItem;
		}
		return plan.ToolFor(tile) ?? StartItem;
	}

	/// <summary>这一格此刻还有没有活（按该用的那把工具问）。</summary>
	private bool CanWorkTile(GameLocation? location, Vector2 tile)
	{
		if (location == null)
		{
			return false;
		}
		Item? item = ToolFor(tile);
		if (item == null)
		{
			return false;
		}
		return TileUtil.CanWork(location, tile, Mod.Config, item);
	}

	/// <summary>
	/// 混合跑：把工具换到这一格该用的那把。换完这一帧后面都按新工具走（StartItem 一起更新），
	/// 所以「够不够得着、怎么站位、怎么挥」这些判断读到的都是新工具。
	/// </summary>
	private void SwitchToolFor(Farmer player, Vector2 tile, Item? tool)
	{
		if (player == null || tool == null || object.ReferenceEquals(player.CurrentItem, tool))
		{
			return;
		}
		int slot = -1;
		for (int i = 0; i < player.Items.Count; i++)
		{
			if (object.ReferenceEquals(player.Items[i], tool))
			{
				slot = i;
				break;
			}
		}
		if (slot < 0)
		{
			return;
		}
		player.CurrentToolIndex = slot;
		StartItem = tool;
		ToolSwitches++;
		// 上一把工具的朝向 / 站位 / 蓄力档位都不作数了，别让新工具照着旧计划挥。
		PreferredFacing = null;
		PreferredStand = null;
		PendingPower = null;
		LastSwingFacing = null;
		LastScytheFacing = null;
		SwingAim = null;
		ActiveSwingCovers = null;
	}

	/// <summary>
	/// 手里那一叠用完之后，从背包里找同一种物品的另一个品质接着干。
	/// 场景：普通品质的玉米装木桶装完了，包里还有银星、金星的那几叠——
	/// 原来这一步会停下来报「物品用完了」，得玩家自己手动切换品质。
	///
	/// 只认「同一种」：物品 id 相同（果酒 / 腌菜再比原料来源），品质不管——
	/// 品质正是要切换的那一维。种子 / 肥料 / 地板没有品质，天然不会走进来。
	///
	/// 只在「手上真的空了」时接管：玩家自己切到别的槽位 / 空槽是手动操作，不抢。
	/// </summary>
	private bool TrySwitchToSameItemQuality(Farmer player, Item? reference)
	{
		if (!Mod.Config.AutoSwitchItemQuality || player == null || reference == null)
		{
			return false;
		}
		// 手上那叠还在包里（玩家是自己切到空槽的）：这是手动操作，不抢。
		if (reference.Stack > 0 && ItemInInventory(player, reference))
		{
			return false;
		}
		int currentSlot = player.CurrentToolIndex;
		if (currentSlot < 0 || currentSlot >= player.Items.Count)
		{
			return false;
		}
		// 快捷栏优先（换过去就完事）；背包里的那一叠得挪到手上这一格——
		// 原版 SHIFT+滚轮也是这么换的（把手上这一格和背包那一格对调）。
		int slot = -1;
		for (int i = 0; i < player.Items.Count; i++)
		{
			if (i == currentSlot)
			{
				continue;
			}
			Item? candidate = player.Items[i];
			if (candidate == null || candidate.Stack <= 0)
			{
				continue;
			}
			if (!TileUtil.IsSameItemFamily(candidate, reference))
			{
				continue;
			}
			if (slot < 0)
			{
				// 先记下找到的第一个；快捷栏里还有同种的话，后面会被换掉。
				slot = i;
				if (i < 12)
				{
					break;
				}
			}
		}
		if (slot < 0)
		{
			return false;
		}
		if (slot >= 12)
		{
			Item? held = player.Items[currentSlot];
			player.Items[currentSlot] = player.Items[slot];
			player.Items[slot] = held;
			slot = currentSlot;
		}
		player.CurrentToolIndex = slot;
		StartItem = player.Items[slot];
		// 上一叠的朝向 / 站位 / 蓄力档位都不作数了（新物品按新计划挥）。
		PreferredFacing = null;
		PreferredStand = null;
		PendingPower = null;
		LastSwingFacing = null;
		LastScytheFacing = null;
		SwingAim = null;
		ActiveSwingCovers = null;
		return true;
	}

	/// <summary>这个物品还在背包里吗（按引用找，不是按 id）。</summary>
	private static bool ItemInInventory(Farmer player, Item item)
	{
		for (int i = 0; i < player.Items.Count; i++)
		{
			if (object.ReferenceEquals(player.Items[i], item))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>HUD 每帧都读 Remaining，而它是 O(剩余格数) 的（每格 3 个 HashSet + 1 个 Dictionary 查），
	/// 大选框下每帧几千次查找。缓存 4 tick（67ms，计数条上看不出来）；点选框改队列那种「数字要立刻对」
	/// 的地方调 InvalidateRemainingCache()。这个值只用于显示和日志、不参与任何判断，
	/// 所以漏掉一处作废最多是数字晚 67ms 更新，不会影响队列行为。</summary>
	private int RemainingCache = -1;

	private int RemainingCacheTick = -1;

	private void InvalidateRemainingCache()
	{
		RemainingCacheTick = -1;
	}

	public int Remaining
	{
		get
		{
			if (!TrailMode)
			{
				if (RemainingCache >= 0 && Game1.ticks - RemainingCacheTick < 4)
				{
					return RemainingCache;
				}
				// 口径与高亮（PendingTileWorks）一致：只数「还有活、队列还会去干」的格，
				// 被一挥手顺带干完的格在 MarkSplashDone 里记完成后就该立刻从数里消失。
				int pending = 0;
				for (int i = Math.Max(0, Index - 1); i < Tiles.Count; i++)
				{
					Vector2 tile = Tiles[i];
					if (TileDone.Contains(tile) || FailedTiles.Contains(tile) || PlacedTiles.Contains(tile))
					{
						continue;
					}
					if (TileAttempts.TryGetValue(tile, out var value) && value >= 3)
					{
						continue;
					}
					pending++;
				}
				RemainingCache = pending;
				RemainingCacheTick = Game1.ticks;
				return pending;
			}
			return Math.Max(0, TrailRoute.Count - TrailCursor);
		}
	}

	public string ActiveVerb => (Plan != null) ? I18n.T("verb-clear-debris") : I18n.T(TileUtil.IsSaplingItem(StartItem) ? "verb-plant" : (TileUtil.IsFertilizerItem(StartItem) ? "verb-fertilize" : (TileUtil.IsPlaceableObjectItem(StartItem) ? "verb-place" : (TileUtil.IsSowSeed(StartItem) ? (SeedMachineRun ? "verb-load" : "verb-sow") : ((StartItem is WateringCan) ? "verb-water" : (TileUtil.IsScythe(StartItem) ? "verb-harvest" : (TileUtil.IsPickaxe(StartItem) ? "verb-mine" : (TileUtil.IsAxe(StartItem) ? "verb-chop" : (TileUtil.IsSprinklerItem(StartItem) ? "verb-sprinkler" : (TileUtil.IsMachineLoadItem(StartItem) ? "verb-load" : "verb-till"))))))))));


	/// <summary>待办格 + 它的工作类型（高亮分「画网格」和「实体发光」两用）。</summary>
	public IEnumerable<(Vector2 Tile, TileUtil.TileWork Work)> PendingTileWorks()
	{
		for (int num = Math.Max(0, Index - 1); num < Tiles.Count; num++)
		{
			Vector2 tile = Tiles[num];
			if (TileDone.Contains(tile) || FailedTiles.Contains(tile) || PlacedTiles.Contains(tile))
			{
				continue;
			}
			TileUtil.TileWork work;
			if (Planned == null || !Planned.TryGetValue(tile, out work))
			{
				work = TileUtil.TileWork.None;
			}
			yield return (tile, work);
		}
	}

	/// <summary>高亮现算用：这一格此刻还有没有活（直接问世界，不信任队列账本）。</summary>
	internal bool StillHasWork(Vector2 tile)
	{
		return CanWorkTile(Location, tile);
	}

	/// <summary>这一格上的待收鱼塘（高亮用）。</summary>
	internal FishPond? PondAt(Vector2 tile)
	{
		GameLocation location = Location;
		return (location != null) ? TileUtil.GetPondWithOutputAt(location, tile) : null;
	}

	/// <summary>
	/// 兜底清扫：队列里挂着、但现场已经没活的格子记成完成。
	/// MarkSplashDone 只管挥落点周围一圈，这里每 10 tick 全量过一遍，保证格标和计数最多滞后 0.17 秒收敛。
	/// </summary>
	internal void MarkStaleTilesDone()
	{
		GameLocation location = Location;
		if (location == null || StartItem == null)
		{
			return;
		}
		for (int i = Index; i < Tiles.Count; i++)
		{
			Vector2 tile = Tiles[i];
			if (TileDone.Contains(tile) || FailedTiles.Contains(tile) || PlacedTiles.Contains(tile))
			{
				continue;
			}
			if (!CanWorkTile(location, tile))
			{
				TileDone.Add(tile);
			}
		}
	}

	/// <summary>
	/// 一挥手往往一次干完好几格（充能锄头/水壶都有覆盖范围），但队列只能一格一格弹出：
	/// 被顺带干完的格子会一直挂在队列里，格标不灭、计数也不掉。挥完当下把这一片里已经没有活的格子记成完成。
	/// </summary>
	private void MarkSplashDone(Vector2 center)
	{
		GameLocation location = Location;
		if (location == null || StartItem == null)
		{
			return;
		}
		int inRadius = 0;
		int marked = 0;
		for (int i = Index; i < Tiles.Count; i++)
		{
			Vector2 tile = Tiles[i];
			if (TileDone.Contains(tile) || FailedTiles.Contains(tile) || PlacedTiles.Contains(tile))
			{
				continue;
			}
			if (MathF.Abs(tile.X - center.X) > (float)SplashScanRadius || MathF.Abs(tile.Y - center.Y) > (float)SplashScanRadius)
			{
				continue;
			}
			inRadius++;
			if (!CanWorkTile(location, tile))
			{
				TileDone.Add(tile);
				marked++;
			}
		}
	}

	public bool ToggleTileAt(Vector2 tile)
	{
		GameLocation val = Location ?? ((Game1.player != null) ? ((Character)Game1.player).currentLocation : null);
		if (val == null || StartItem == null)
		{
			return false;
		}
		for (int i = Index; i < Tiles.Count; i++)
		{
			if (Tiles[i] == tile)
			{
				Tiles.RemoveAt(i);
				Planned.Remove(tile);
				RetriedTiles.Remove(tile);
				InvalidateRemainingCache();
				return false;
			}
		}
		TileUtil.TileWork tileWork = TileUtil.GetTileWork(val, tile, ToolFor(tile), Mod.Config);
		if (tileWork == TileUtil.TileWork.None || !TileUtil.IsWanted(tileWork, Mod.Config))
		{
			return false;
		}
		Planned[tile] = tileWork;
		Tiles.Insert(FindOrderSlot(tile), tile);
		InvalidateRemainingCache();
		return true;
	}

	public int FilterSameWork(Vector2 tile)
	{
		GameLocation val = Location ?? ((Game1.player != null) ? ((Character)Game1.player).currentLocation : null);
		if (val == null || StartItem == null)
		{
			return Remaining;
		}
		if (!Planned.TryGetValue(tile, out var value))
		{
			value = TileUtil.GetTileWork(val, tile, ToolFor(tile), Mod.Config);
			if (value == TileUtil.TileWork.None)
			{
				return Remaining;
			}
			Planned[tile] = value;
			Tiles.Insert(FindOrderSlot(tile), tile);
			InvalidateRemainingCache();
		}
		string workKindKey = TileUtil.GetWorkKindKey(val, tile, value);
		List<Vector2> list = new List<Vector2>();
		int num = 0;
		for (int i = Index; i < Tiles.Count; i++)
		{
			Vector2 val2 = Tiles[i];
			if (!Planned.TryGetValue(val2, out var value2))
			{
				value2 = TileUtil.GetTileWork(val, val2, ToolFor(val2), Mod.Config);
			}
			string text = ((workKindKey != null) ? TileUtil.GetWorkKindKey(val, val2, value2) : null);
			if ((workKindKey != null) ? (text == workKindKey) : (value2 == value))
			{
				list.Add(val2);
				continue;
			}
			num++;
			Planned.Remove(val2);
			RetriedTiles.Remove(val2);
		}
		Tiles.RemoveRange(Index, Tiles.Count - Index);
		Tiles.AddRange(list);
		return list.Count;
	}

	private static bool IsRepeatWork(TileUtil.TileWork work)
	{
		switch (work)
		{
		case TileUtil.TileWork.PickWeed:
		case TileUtil.TileWork.PickClump:
		case TileUtil.TileWork.PickStone:
		case TileUtil.TileWork.PickBoulder:
		case TileUtil.TileWork.AxeFruitTreeSapling:
		case TileUtil.TileWork.AxeFruitTreeMature:
		case TileUtil.TileWork.AxeTreeSapling:
		case TileUtil.TileWork.AxeTreeMature:
		case TileUtil.TileWork.AxeStump:
		case TileUtil.TileWork.AxeGiantCrop:
		case TileUtil.TileWork.AxeTapper:
		case TileUtil.TileWork.AxeBush:
			return true;
		default:
			return false;
		}
	}

	public TillQueue(ModEntry mod)
	{
		Tiles = new List<Vector2>();
		TileAttempts = new Dictionary<Vector2, int>();
		TileDone = new HashSet<Vector2>();
		LastScytheTarget = Vector2.Zero;
		LastScytheStand = Vector2.Zero;
		SwingPlan = new List<TillPlanner.Swing>();
		SegmentOf = new Dictionary<Vector2, int>();
		WalkStartFailure = "";
		ReportVerb = "锄地";
		RetryTiles = new List<Vector2>();
		RetriedTiles = new HashSet<Vector2>();
		GatesTried = new HashSet<Vector2>();
		UnreachableTiles = new HashSet<Vector2>();
		FailedTiles = new HashSet<Vector2>();
		TrailRoute = new List<Vector2>();
		GrassBefore = new Dictionary<Vector2, int>();
		PlacedTiles = new HashSet<Vector2>();
		PickHits = new Dictionary<Vector2, int>();
		Planned = new Dictionary<Vector2, TileUtil.TileWork>();
		Mod = mod;
	}

	internal bool IsDrivingPlayerWalk(FarmerSprite sprite)
	{
		if (Walking)
		{
			Farmer player = Game1.player;
			if (player != null)
			{
				return player.FarmerSprite == sprite;
			}
		}
		return false;
	}

	public void Start(List<Vector2> tiles, GameLocation location, Rectangle box)
	{
		Start(tiles, location, box, null);
	}

	/// <summary>
	/// 开一趟作业。<paramref name="plan"/> 非 null 时是杂物混合跑：整框按格分工，
	/// 一路「就近挑下一格」，走到哪格就换成那格该用的工具（镰刀 / 斧头 / 镐头）。
	/// </summary>
	public void Start(List<Vector2> tiles, GameLocation location, Rectangle box, DebrisPlan? plan)
	{
		Stop();
		if (tiles.Count == 0)
		{
			Notify(I18n.T("stop-no-work"), isError: true);
			return;
		}
		// 杂物混合跑：先把分工表记下来。下面逐格核对「这一格有活儿吗」要按各格该用的那把工具问，
		// 拿手里这把问会把「要镐头敲的石头」判成没活儿，整格直接从队列里漏掉。
		Plan = plan;
		ToolSwitches = 0;
		Farmer player = Game1.player;
		if (TileUtil.IsSprinklerItem((player != null) ? player.CurrentItem : null))
		{
			Farmer player2 = Game1.player;
			Item val = ((player2 != null) ? player2.CurrentItem : null);
			Object val2 = (Object)((val is Object) ? val : null);
			if (val2 != null && box.Width > 0 && box.Height > 0)
			{
				List<Vector2> list = TileUtil.PlanSprinklers(box, val2.GetBaseRadiusForSprinkler(), TileUtil.SprinklerGapFor(Mod.Config), TileUtil.TrellisLayout(Mod.Config));
				HashSet<Vector2> hashSet = new HashSet<Vector2>(list);
				tiles = tiles.Where(hashSet.Contains).ToList();
			}
		}
		Farmer player3 = Game1.player;
		if (TileUtil.IsSaplingItem((player3 != null) ? player3.CurrentItem : null) && box.Width > 0 && box.Height > 0)
		{
			Farmer player4 = Game1.player;
			(int, int) saplingSpacing = TileUtil.GetSaplingSpacing((player4 != null) ? player4.CurrentItem : null, Mod.Config);
			int stepX = saplingSpacing.Item1;
			int stepY = saplingSpacing.Item2;
			tiles = tiles.Where(delegate(Vector2 t)
			{
				return ((int)t.X - (box).Left) % stepX == 0 && ((int)t.Y - (box).Top) % stepY == 0;
			}).ToList();
		}
		Dictionary<TileUtil.TillBlock, int> dictionary = new Dictionary<TileUtil.TillBlock, int>();
		int num = 0;
		Farmer player5 = Game1.player;
		Item val3 = ((player5 != null) ? player5.CurrentItem : null);
		bool flag = val3 is WateringCan;
		bool flag2 = TileUtil.IsSowSeed(val3);
		Dictionary<Vector2, TileUtil.TileWork> planned = new Dictionary<Vector2, TileUtil.TileWork>();
		int bagFullSkip = 0;
		foreach (Vector2 tile in tiles)
		{
			TileUtil.TileWork tileWork = TileUtil.GetTileWork(location, tile, ToolFor(tile) ?? val3, Mod.Config);
			if (tileWork != TileUtil.TileWork.None && !TileUtil.IsWanted(tileWork, Mod.Config))
			{
				dictionary.TryGetValue(TileUtil.TillBlock.NotSelected, out var value);
				dictionary[TileUtil.TillBlock.NotSelected] = value + 1;
			}
			else if (tileWork == TileUtil.TileWork.None)
			{
				if (TileUtil.IsMachineLoadItem(ToolFor(tile) ?? val3))
				{
					Object readyOut = TileUtil.GetReadyMachineOutput(location, tile);
					if (readyOut != null && TileUtil.ReadyMachineBlockedByFullBag((Item)(object)readyOut))
					{
						bagFullSkip++;
						// 背包满就是这一格的真正原因，别再顺手记成「工具不对」——报原因时会被带偏。
						dictionary.TryGetValue(TileUtil.TillBlock.BagFull, out var bagValue);
						dictionary[TileUtil.TillBlock.BagFull] = bagValue + 1;
						continue;
					}
				}
				TileUtil.TillBlock key = ((val3 is Hoe) ? TileUtil.GetTillBlockReason(location, tile, Mod.Config) : TileUtil.TillBlock.WrongTool);
				dictionary.TryGetValue(key, out var value2);
				dictionary[key] = value2 + 1;
			}
			else
			{
				num++;
				planned[tile] = tileWork;
			}
		}
		if (bagFullSkip > 0 && num > 0)
		{
			Notify(I18n.T("bag-full-skip", new { count = bagFullSkip }), isError: false);
		}
		if (num == 0)
		{
			// 别一律「该区域无法工作」：说清楚到底卡在哪一条上。
			Notify(DescribeNoWorkReason(val3, dictionary), isError: true);
			return;
		}
		SeedMachineRun = TileUtil.IsSowSeed(val3) && planned.Count > 0 && planned.Values.All(delegate(TileUtil.TileWork w)
		{
			return w == TileUtil.TileWork.MachineLoad;
		});
		Farmer player6 = Game1.player;
		Vector2 val4 = ((player6 != null) ? ((Character)player6).Tile : Vector2.Zero);
		Farmer player7 = Game1.player;
		int num4;
		if (!TileUtil.IsSowSeed((player7 != null) ? player7.CurrentItem : null))
		{
			Farmer player8 = Game1.player;
			if (!TileUtil.IsSprinklerItem((player8 != null) ? player8.CurrentItem : null))
			{
				Farmer player9 = Game1.player;
				if (!TileUtil.IsFertilizerItem((player9 != null) ? player9.CurrentItem : null))
				{
					Farmer player10 = Game1.player;
					num4 = (TileUtil.IsPlaceableObjectItem((player10 != null) ? player10.CurrentItem : null) ? 1 : 0);
					goto IL_0c8a;
				}
			}
		}
		num4 = 1;
		goto IL_0c8a;
		IL_070f:
		bool flag3;
		List<Vector2> list2;
		// 锄/浇不再把「脚下那格」提到队首：行序是从最近的角开始一趟扫到对面，
		// 先把脚边锄一小块再跑去角落，反而多走一段、地里多出一小片。
		// 镰刀作物批同理：S 序从最近的边起手，把脚下那格插到队首会打断 S、
		// 还可能把剩余批的格插到作物批前面。
		if (!flag3 && !(val3 is Hoe || val3 is WateringCan) && !ScytheCropRoute)
		{
			int num5 = list2.IndexOf(val4);
			if (num5 > 0)
			{
				list2.RemoveAt(num5);
				list2.Insert(0, val4);
			}
		}
		Planned = planned;
		SwingPlan.Clear();
		SwingCursor = 0;
		TileAttempts.Clear();
		Tiles.AddRange(list2);
		StartAnchor = val4;
		SelectionBox = box;
		Location = location;
		Farmer player11 = Game1.player;
		StartItem = ((player11 != null) ? player11.CurrentItem : null);
		// 背包满就别开走：出发前把整趟「手摘进背包」的格子（采集品/浆果丛/鱼塘/机器产物）都探一遍，
		// 一个都装不下就直接收工——以前要等走到第一个目标面前才报「背包满了」，白走一趟。
		if (player11 != null && WholeRunHandPickBlockedByFullBag(location, player11))
		{
			Stop(I18n.T("stop-inventory-full"));
			return;
		}
		ReportVerb = I18n.T(Plan != null ? "verb-done-clear-debris" : (SeedMachineRun ? "verb-load" : DoneVerbKey(StartItem)));
		Index = 0;
		Walking = false;
		Current = null;
		WalkAttempts = 0;
		WalkFailCooldownTicks = 0;
		ResetGate();
		GatesTried.Clear();
		WalkDestination = null;
		WalkUnstuckTried = false;
		WaitingSwing = false;
		Charging = false;
		Refilling = false;
		ChargeSteps = 0;
		ChargePower = 0;
		ChargeElapsedMs = 0;
		ChargeStepMs = 0;
		ChargeTarget = Vector2.Zero;
		Farmer player12 = Game1.player;
		if (player12 != null)
		{
			((Character)player12).lastClick = Vector2.Zero;
		}
		PreferredFacing = null;
		PreferredStand = null;
		LastSwingFacing = null;
		OrderedRowY = null;
		RowResortUsed = false;
		LockedRowY = null;
		LockedRowFacing = -1;
		PendingPower = null;
		SwingTarget = Vector2.Zero;
		SwingAim = null;
		ActiveSwingCovers = null;
		Tilled = 0;
		Skipped = 0;
		GrassBefore.Clear();
		PlacedTiles.Clear();
		PlaceSnapFeature = null;
		ScytheGrabsCrops = Mod.ScytheHarvestsCrops;
		Item startItem2 = StartItem;
		TileDone.Clear();
		LastScytheFacing = null;
		LastScytheTarget = Vector2.Zero;
		LastScytheStand = Vector2.Zero;
		ActiveSwingCovers = null;
		PickHits.Clear();
		SplashTilled = 0;
		FailedSwings = 0;
		Refills = 0;
		RetryTiles.Clear();
		RetryRoundUsed = false;
		RetriedTiles.Clear();
		UnreachableTiles.Clear();
		FailedTiles.Clear();
		RescanLogged = false;
		MachineOutcomeTile = null;
		MovementWasDown = IsMovementInputDown();
		return;
		IL_0c8a:
		flag3 = (PlacingMode = (byte)num4 != 0);
		SealPlacedTiles = flag3 && TileUtil.IsSolidPlaceItem(StartItem);
		ScytheMachineRoute = TileUtil.IsScythe(val3) && BatchHasMachineWork(planned.Keys, planned);
		ScytheCropRoute = false;
		ScytheCoverageCropsOnly = false;
		ScytheCropMopUpDone = false;
		// 实心实物（小桶/宝箱/罐头瓶等）：回到 0.4.26 的行段 S（被障碍隔开时一侧摆完再跨另一边，
		// 孤立小片先做）。0.4.27 的步行距离排序在树丛地形里会把人物带进走不到的死角，撤回。
		// 只限实心物：种子/树苗/肥料/洒水器/装机器/工具都保持原顺序。
		bool sShapePlacement = TileUtil.IsPlaceableObjectItem(val3) && TileUtil.IsSolidPlaceItem(val3);
		List<Vector2> source;
		if (sShapePlacement)
		{
			source = TileUtil.BuildSegmentedPlacementOrder(planned.Keys, val4, location, checkEscape: true, out string startNote);
		}
		else if (TileUtil.IsScythe(val3))
		{
			// 0.5.66：作物批（枯萎作物 + 作物 + 花，不论能不能成片割）走水壶那套行段 S ——
			// 从离玩家最近的边起手、一行一趟蛇形扫到底；其余（杂草/草/苔藓/采集品/机器/鱼塘）
			// 仍按步行就近 + PickNextScytheTile 现挑。
			// 为什么又改回来：0.5.58 用离线步数撤回了行扫，但实机（2026-11-01，0.5.65 之后）
			// 收作物仍然「在乱走」，步数省了观感没救；这次按实机观感走，离线数据留档在 CHANGELOG。
			// 混合跑（Plan != null，杂物自动换工具）不拆，保持 0.5.63 的一趟走完不折返。
			List<Vector2> cropTiles = new List<Vector2>();
			List<Vector2> restTiles = new List<Vector2>();
			// 混合跑（Plan != null，杂物自动换工具）也把作物批拆出来走带子 S：
			// 作物是成片长在地里的，一趟扫过去最省；「就近贪心」只该用来管杂物。
			// 之前混合跑整框一条就近路线，于是选区里只要有杂草/石头/走道，作物收割就完全不 S
			// （用户 2026-11-02：有间隔的耕地每趟都同向、扫完横穿回另一头；大片纯田才正常）。
			foreach (KeyValuePair<Vector2, TileUtil.TileWork> kv in planned)
			{
				if (IsScytheCropWork(kv.Value))
				{
					cropTiles.Add(kv.Key);
				}
				else
				{
					restTiles.Add(kv.Key);
				}
			}
			ScytheCropRoute = cropTiles.Count > 0;
			List<Vector2> order = new List<Vector2>(planned.Count);
			if (cropTiles.Count > 0)
			{
				// 带子高 3：实机日志里铱镰一刀清 3~5 行 × 3~4 列，3 行一条带子正好不重不漏。
				order.AddRange(TileUtil.BuildBandSWorkOrder(cropTiles, val4, 3));
			}
			if (restTiles.Count > 0)
			{
				order.AddRange(TileUtil.BuildPathWorkOrder(restTiles, (order.Count > 0) ? order[order.Count - 1] : val4, location, SealPlacedTiles));
			}
			source = order;
		}
		else if (val3 is Hoe || val3 is WateringCan)
		{
			// 锄地 / 浇水：按行段 S 扫，不再用「最近优先」（那会绕成同心方环）。
			source = TileUtil.BuildRowSWorkOrder(planned.Keys, val4, interleaveOrphans: true);
		}
		else if (TileUtil.IsMachineLoadItem(val3) || SeedMachineRun)
		{
			// 装机器：和镰刀收机器产物同一条顺序（步行就近 + 挨着上一格继续）。
			// 行段 S 在桶阵里被工作中的桶切成一条条横带加左侧竖条，块间跳来跳去（实机 2026-11-01 截图）。
			source = TileUtil.BuildPathWorkOrder(planned.Keys, val4, location, SealPlacedTiles);
		}
		else
		{
			source = TileUtil.BuildPathWorkOrder(planned.Keys, val4, location, SealPlacedTiles);
		}
		list2 = source.OrderBy(delegate(Vector2 tile)
		{
			TileUtil.TileWork value6;
			return (!planned.TryGetValue(tile, out value6) || value6 == TileUtil.TileWork.Till) ? 1 : 0;
		}).ToList();
		TrailMode = false;
		TrailRoute.Clear();
		TrailExit = null;
		TrailCursor = 0;
		SwingPlan.Clear();
		SwingCursor = 0;
		Farmer player13 = Game1.player;
		if (!TileUtil.IsSowSeed((player13 != null) ? player13.CurrentItem : null))
		{
			Farmer player14 = Game1.player;
			if (!TileUtil.IsSprinklerItem((player14 != null) ? player14.CurrentItem : null))
			{
				Farmer player15 = Game1.player;
				if (!TileUtil.IsFertilizerItem((player15 != null) ? player15.CurrentItem : null))
				{
					Farmer player16 = Game1.player;
					if (!TileUtil.IsPlaceableObjectItem((player16 != null) ? player16.CurrentItem : null))
					{
						goto IL_070f;
					}
				}
			}
		}
		if (location != null)
		{
			TrailRoute.AddRange(TileUtil.BuildTrailRoute(planned.Keys, box, val4, location, out var exit));
			TrailExit = exit;
			if (TrailRoute.Count >= 1)
			{
				TrailMode = true;
				TrailMode = false;
				TrailCursor = 0;
			}
		}
		goto IL_070f;
	}


	public void Stop(string? message = null, bool isError = true)
	{
		// 收尾前先把最后一刀的复查结掉：延迟复查期间队列就结束的话，
		// 漏割的格子会跟着状态一起被清掉，再也不会回来（气泡消失、作物还在地里）。
		if (PendingScytheCovers != null || ScytheVerifyDelay > 0)
		{
			FlushScytheVerify();
		}
		LastStopWasOwnWork = string.IsNullOrEmpty(message);
		LastStopWasStamina = false;
		SeedMachineRun = false;
		Mod.Walk.Release();
		if (Walking && !WalkingByDriver)
		{
			Farmer player = Game1.player;
			if (player != null && ((Character)player).controller != null)
			{
				((Character)player).controller = null;
			}
		}
		WalkingByDriver = false;
		Tiles.Clear();
		InvalidateRemainingCache();
		Planned = new Dictionary<Vector2, TileUtil.TileWork>();
		SelectionBox = null;
		Location = null;
		StartItem = null;
		Plan = null;
		LiveField = default(TileUtil.DistanceField);
		Index = 0;
		TileDone.Clear();
		LastScytheFacing = null;
		LastScytheTarget = Vector2.Zero;
		LastScytheStand = Vector2.Zero;
		ScytheMachineRoute = false;
		ScytheCropRoute = false;
		ScytheCoverageCropsOnly = false;
		ScytheCropMopUpDone = false;
		ActiveSwingCovers = null;
		Walking = false;
		WalkTicks = 0;
		Current = null;
		WalkAttempts = 0;
		WalkFailCooldownTicks = 0;
		ResetGate();
		GatesTried.Clear();
		WalkDestination = null;
		RetriedTiles.Clear();
		UnreachableTiles.Clear();
		SegmentOf.Clear();
		FailedTiles.Clear();
		RescanLogged = false;
		MachineOutcomeTile = null;
		MovementWasDown = false;
		if ((Charging || WaitingSwing) && Game1.player != null)
		{
			RestoreAfterSwing(Game1.player);
		}
		Charging = false;
		ChargeSteps = 0;
		ChargePower = 0;
		ChargeElapsedMs = 0;
		ChargeStepMs = 0;
		ChargeTarget = Vector2.Zero;
		Refilling = false;
		WaitingSwing = false;
		SwingTicks = 0;
		Farmer player2 = Game1.player;
		if (player2 != null)
		{
			((Character)player2).lastClick = Vector2.Zero;
		}
		PreferredFacing = null;
		PreferredStand = null;
		LastSwingFacing = null;
		OrderedRowY = null;
		LockedRowY = null;
		LockedRowFacing = -1;
		SwingTarget = Vector2.Zero;
		SwingAim = null;
		ActiveSwingCovers = null;
		PendingScytheCovers = null;
		PendingScytheBefore.Clear();
		ScytheWorkBefore.Clear();
		ScytheBadStands.Clear();
		ScytheStandRetries.Clear();
		ScytheVerifyDelay = 0;
		ScytheVainSwings = 0;
		Tilled = 0;
		Skipped = 0;
		GrassBefore.Clear();
		PlacedTiles.Clear();
		PlaceSnapFeature = null;
		ScytheGrabsCrops = false;
		PickHits.Clear();
		SplashTilled = 0;
		FailedSwings = 0;
		Refills = 0;
		if (!string.IsNullOrEmpty(message))
		{
			Notify(message, isError);
		}
	}

	public void Tick()
	{
		if (!IsRunning)
		{
			// 队列在这几帧里走空了，但最后一刀还没复查：先结掉。
			// 否则漏割的格子再也不会回到队列——玩家看到的就是气泡消失、作物还在地里。
			if (ScytheVerifyDelay > 0 || PendingScytheCovers != null)
			{
				FlushScytheVerify();
			}
			return;
		}
		Farmer player = Game1.player;
		if (!Context.IsWorldReady || player == null)
		{
			Stop();
			return;
		}

		if (--StaleCheckCountdown <= 0)
		{
			StaleCheckCountdown = 10;
			MarkStaleTilesDone();
		}
		if (WalkFailCooldownTicks > 0)
		{
			WalkFailCooldownTicks--;
		}
		if (ScytheVerifyDelay > 0 && --ScytheVerifyDelay == 0)
		{
			FlushScytheVerify();
		}
		if (Game1.game1 != null && !Game1.game1.IsActiveNoOverlay)
		{
			if (Walking)
			{
				Walking = false;
				WalkingByDriver = false;
				Mod.Walk.Stop();
				if (((Character)player).controller != null)
				{
					((Character)player).controller = null;
					((Character)player).Halt();
				}
			}
			return;
		}
		if (Game1.activeClickableMenu != null)
		{
			if (Walking)
			{
				Walking = false;
				WalkingByDriver = false;
				Mod.Walk.Stop();
			}
			return;
		}
		if (StartedWalking())
		{
			Stop(I18n.T("stop-paused"));
			return;
		}
		if (((Character)player).currentLocation != Location)
		{
			Stop(I18n.T("stop-paused"));
			return;
		}
		Item currentItem = player.CurrentItem;
		if (currentItem == null && TrySwitchToSameItemQuality(player, StartItem))
		{
			// 手里那一叠用完了，包里还有同一种的另一个品质：已经换过去，接着干。
			currentItem = player.CurrentItem;
		}
		if (currentItem == null)
		{
			Stop(I18n.T(TileUtil.IsSaplingItem(StartItem) ? "stop-out-of-saplings" : (TileUtil.IsFertilizerItem(StartItem) ? "stop-out-of-fertilizer" : (TileUtil.IsSowSeed(StartItem) ? "stop-out-of-seeds" : (TileUtil.IsSprinklerItem(StartItem) ? "stop-out-of-sprinklers" : (TileUtil.IsMachineLoadItem(StartItem) ? "stop-out-of-items" : (TileUtil.IsPlaceableObjectItem(StartItem) ? "stop-out-of-items" : "stop-paused")))))));
			return;
		}
		if (currentItem != StartItem)
		{
			Stop(I18n.T("stop-paused"));
			return;
		}
		Tool val = (Tool)((currentItem is Tool) ? currentItem : null);
		if (val != null && !TileUtil.IsScythe(currentItem) && player.Stamina <= 1f)
		{
			Stop(I18n.T("stop-out-of-stamina"));
			LastStopWasStamina = true;
			Mod.OpenBackpackAfterTask();
			return;
		}
		if (TrailMode)
		{
			TickTrail(player);
			return;
		}
		// 开门支路：走路寻不到路、且附近有关着的围栏门时，先去把门推开。
		// 这一段完全独立于作物队列的走/挥状态（Walking 保持 false），走完再重新规划。
		if (GatePhase != 0)
		{
			TickGate(player);
			return;
		}
		if (Walking)
		{
			if (player.UsingTool || player.FarmerSprite.PauseForSingleAnimation)
			{
				Mod.Walk.Stop();
				((Character)player).controller = null;
				Walking = false;
				WalkingByDriver = false;
				WalkTicks = 0;
				return;
			}
			if (((Character)player).Tile != WalkLastTile)
			{
				WalkLastTile = ((Character)player).Tile;
				WalkTicks = 0;
			}
			WalkTicks++;
			WalkTotalTicks++;
			if (WalkingByDriver)
			{
				Point? walkDestination = WalkDestination;
				if (walkDestination.HasValue)
				{
					Point valueOrDefault = walkDestination.GetValueOrDefault();
					if (WalkTicks >= 40 && (Mod.Walk.Stalled || Mod.Walk.InputCallbacks == 0))
					{
						bool flag = TileUtil.MonsterNear(((Character)player).currentLocation, ((Character)player).Tile);
						if (flag || TileUtil.DynamicBlockerNear(((Character)player).currentLocation, ((Character)player).Tile))
						{
							if (WalkTicks >= 160 || WalkTotalTicks >= 240)
							{
								Mod.Walk.Stop();
								WalkingByDriver = false;
								Walking = false;
								if (((Character)player).controller != null)
								{
									((Character)player).controller = null;
									((Character)player).Halt();
								}
								if (!RetryTimedOutTile(WalkTarget, flag ? "monster" : "blocked"))
								{
									GiveUpCurrent(WalkTarget);
								}
							}
							return;
						}
						Mod.Walk.Stop();
						WalkingByDriver = false;
						try
						{
							((Character)player).controller = new PathFindController((Character)player, ((Character)player).currentLocation, valueOrDefault, -1);
							return;
						}
						catch
						{
							((Character)player).controller = null;
							Walking = false;
							GiveUpCurrent(WalkTarget);
							return;
						}
					}
				}
			}
			bool flag2 = !NeedsWalk(player, WalkTarget, currentItem);
			if (TileUtil.IsScythe(currentItem))
			{
				Point? walkDestination2 = WalkDestination;
				if (walkDestination2.HasValue)
				{
					Point valueOrDefault2 = walkDestination2.GetValueOrDefault();
					Vector2 stand = default(Vector2);
					stand = new Vector2((float)valueOrDefault2.X, (float)valueOrDefault2.Y);
					// 这一比每 tick 都要跑，而 CountScytheCoverage 一次要按 4 个朝向 × 6 个动画帧采样
					// （每个采样还要在队列里线性找一遍格子）。两个输入是「脚下格」和「目的地位」，
					// 走路期间只有脚下格会变，所以人没挪格就沿用上次结果；每次起步在 ResetWalkClocks 里清掉，
					// 陈旧最多只可能持续一段走路（几 tick），且只影响「要不要提前停下就地挥」这一个判断。
					Vector2 here = ((Character)player).Tile;
					if (here != CoverageCheckTile || stand != CoverageCheckStand)
					{
						CoverageCheckTile = here;
						CoverageCheckStand = stand;
						CoverageCheckResult = CountScytheCoverage(player, here) >= CountScytheCoverage(player, stand);
					}
					flag2 = ((Character)player).TilePoint == valueOrDefault2 || (flag2 && CoverageCheckResult);
				}
			}
			if (!flag2 && WalkingByDriver && !Mod.Walk.IsAttached)
			{
				// 模拟按键驱动已经脱离（路线走完/被停掉），人却还没到「不用走」的位置：
				// 按当前站位立刻重寻一次，别干等 240 tick 超时
				//（实机：水池边 (29,29) 玩家停在 (30,28)，驱动已脱离，等了 4 秒后整格跳过）。
				if (WalkTarget != DriverDetachTarget)
				{
					DriverDetachTarget = WalkTarget;
					DriverDetachRetries = 0;
				}
				DriverDetachRetries++;
				Mod.Walk.Stop();
				((Character)player).controller = null;
				Walking = false;
				WalkingByDriver = false;
				WalkTicks = 0;
				WalkTotalTicks = 0;
				ControllerGoneTicks = 0;
				if (DriverDetachRetries <= 3)
				{
					return;
				}
				DriverDetachTarget = Vector2.Zero;
				DriverDetachRetries = 0;
				if (!RetryTimedOutTile(WalkTarget, "driver"))
				{
					GiveUpCurrent(WalkTarget);
				}
				return;
			}
			bool flag3 = (WalkingByDriver ? flag2 : ((((Character)player).controller == null) & flag2));
			ControllerGoneTicks = ((!WalkingByDriver && ((Character)player).controller == null) ? (ControllerGoneTicks + 1) : 0);
			bool flag4 = !WalkingByDriver && !flag2 && ControllerGoneTicks > 1;
			bool flag5 = WalkTicks >= 240 || WalkTotalTicks >= 720;
			if (!flag3 && !flag4 && !flag5)
			{
				return;
			}
			Walking = false;
			if (flag3)
			{
				// 到了就把模拟按键停掉：否则旧路线还挂着，蓄力/挥动期间一直在按方向键（心跳日志里
				// 能看到 CanMove=False UsingTool=True 还在按 [左]）。
				Mod.Walk.Stop();
			}
			else
			{
				string text = (flag4 ? "原版寻路自己放弃（controller 空）" : "超时");
				string value = (Refilling ? "去装水的那格不能补，直接收工" : (RetriedTiles.Contains(WalkTarget) ? "补过一次还是走不到，这一格判失败" : "排进补一次"));
				Mod.Walk.Stop();
				if (((Character)player).controller != null)
				{
					((Character)player).controller = null;
					((Character)player).Halt();
				}
				if (!RetryTimedOutTile(WalkTarget, "walktimeout") && GiveUpCurrent(WalkTarget))
				{
					return;
				}
			}
			WalkingByDriver = false;
		}
		if (val != null && TickCharge(player, val))
		{
			return;
		}
		if (WaitingSwing)
		{
			SwingTicks++;
			if (Walking)
			{
				Mod.Walk.Stop();
				((Character)player).controller = null;
				Walking = false;
				WalkingByDriver = false;
				WalkTicks = 0;
			}
			bool flag6 = ((Character)player).lastClick == Vector2.Zero;
			bool flag7 = !player.FarmerSprite.PauseForSingleAnimation;
			// flag6（lastClick 被清零）＝游戏的 useTool 已经跑过，挥动已经落地；
			// 之后再等动画自己清 PauseForSingleAnimation 是不必要的——它有时永远不清，
			// 等满 90 tick 就变成每挥卡 1.5 秒。落地后最多再等 40 tick 就收尾。
			int waitCap = flag6 ? 40 : 90;
			if ((!flag6 || !flag7) && SwingTicks < waitCap)
			{
				return;
			}
			WaitingSwing = false;
			RestoreAfterSwing(player);
			VerifySwing(SwingTarget);
			RequeueMissedCovers();
			WateringCan val2 = (WateringCan)((val is WateringCan) ? val : null);
			if (val2 != null && val2.WaterLeft <= 0 && !player.hasWateringCanEnchantment && TileUtil.IsWaterSource(((Character)player).currentLocation, SwingTarget))
			{
				Stop(I18n.T("stop-refill-failed"));
				return;
			}
		}
		// 游戏正在放挥动/蓄力动画（玩家手动挥、上一挥的尾巴、别的队列在用工具）：这一帧不取新格。
		// 否则「挥动动画未结束」会被调用方记成出发失败，三次就把整格判掉
		// （实机 09:11:55：一次动画期间连跳 (29,29)/(31,30)/(30,30) 三格）。
		if (player.UsingTool || player.FarmerSprite.PauseForSingleAnimation)
		{
			return;
		}
		if (OutOfStamina(player))
		{
			return;
		}
		WateringCan val3 = (WateringCan)((val is WateringCan) ? val : null);
		Vector2? current;
		if (val3 != null && val3.WaterLeft <= 0 && !player.hasWateringCanEnchantment)
		{
			current = Current;
			if (!current.HasValue)
			{
				Vector2? val4 = FindWaterSource(player, val);
				if (!val4.HasValue)
				{
					Stop(I18n.T("stop-can-empty"));
					return;
				}
				Current = val4.Value;
				Refilling = true;
				WalkAttempts = 0;
			}
		}
		current = Current;
		if (!current.HasValue)
		{
			Vector2? current2 = TakeNextTillable(currentItem);
			if (!current2.HasValue)
			{
				if (!RetryRoundUsed && RetryTiles.Count > 0)
				{
					RetryRoundUsed = true;
					List<Vector2> list = TileUtil.BuildPathWorkOrder(RetryTiles, ((Character)player).Tile, Location ?? ((Character)player).currentLocation, SealPlacedTiles);
					RetryTiles.Clear();
					Tiles.AddRange(list);
					return;
				}
				GameLocation location = Location;
				if (location != null)
				{
					List<Vector2> list2 = new List<Vector2>();
					foreach (Vector2 key in Planned.Keys)
					{
						// 混合跑按各格该用的那把工具复检；单工具跑保持原口径（镰刀问开关，锄头问还有没有荒地）。
						bool stillWanted = (Plan != null) ? CanWorkTile(location, key) : (TileUtil.IsScythe(StartItem) ? TileUtil.IsWanted(TileUtil.GetTileWork(location, key, StartItem, Mod.Config), Mod.Config) : TileUtil.WouldTill(location, key));
						if (stillWanted)
						{
							TileAttempts.TryGetValue(key, out var value2);
							if (value2 < 3)
							{
								TileAttempts[key] = value2 + 1;
								list2.Add(key);
							}
						}
					}
					if (list2.Count > 0)
					{
						list2 = TileUtil.BuildPathWorkOrder(list2, ((Character)player).Tile, location, SealPlacedTiles);
						Tiles.AddRange(list2);
						if (!RescanLogged)
						{
							RescanLogged = true;
						}
						return;
					}
				}
				FinishReport();
				return;
			}
			Current = current2;
			WalkAttempts = 0;
			if (Plan != null)
			{
				// 杂物混合跑：取到这一格时可能已经顺手换了工具，这一帧后面的「够不够得着 / 站哪 / 怎么挥」
				// 都得按新工具算，不能再用本 tick 开头读到的那把。
				currentItem = player.CurrentItem;
				if (currentItem == null || currentItem != StartItem)
				{
					Stop(I18n.T("stop-paused"));
					return;
				}
				val = (Tool)((currentItem is Tool) ? currentItem : null);
			}
		}
		Vector2 value3 = Current.Value;
		if (SwingCursor < SwingPlan.Count && SwingPlan[SwingCursor].Target == value3)
		{
			TillPlanner.Swing swing = SwingPlan[SwingCursor];
			PendingPower = swing.Power;
			PreferredFacing = swing.Facing;
			PreferredStand = swing.Stand;
			ActiveSwingCovers = swing.Covers;
			SwingCursor++;
		}
		((Character)player).FacingDirection = PreferredFacing ?? TileUtil.GetFacingDirection(((Character)player).Tile, value3);
		bool flag8 = NeedsWalk(player, value3, currentItem);
		FishPond? targetPond = TileUtil.GetPondWithOutputAt(((Character)player).currentLocation, value3);
		bool flag9 = false;
		// 0.5.77：杂物格（杂草/草/苔藓等）够得着就当场挥，不再为了“多覆盖 2 格”挪站位。
		// 实机 03:29 日志：目标 (54,22) 时人在 (53,23) 和 (55,21) 间往返 2 分钟不挥（剩余数一直 83）。
		// 作物批（作物/花/枯萎作物）保持原行为——IsScytheCropTile 判定走原有收割逻辑，不改变。
		if (!flag8 && Plan == null && TileUtil.IsScythe(currentItem) && !ShouldHandPick(value3) && IsScytheCropTile(value3))
		{
			flag9 = ScytheShouldSeekBetterStand(player, value3);
			flag8 = flag9;
		}
		if (flag8)
		{
			// 冷却中就别再算了：上一次全候选都寻不到路，隔一两帧再跑一遍结果不会变，
			// 但一帧 8 次全图 A* 足以把帧率压到个位数（这就是围栏门前后「卡成几帧」的真凶）。
			if (WalkFailCooldownTicks > 0)
			{
				return;
			}
			if (TryStartWalking(player, currentItem, value3))
			{
				WalkAttempts = 0;
				WalkFailCooldownTicks = 0;
				return;
			}
			if (WalkStartFailure == "挥动动画未结束")
			{
				// 不是走不过去，是游戏这会儿不让走：等动画放完再试，不记失败、不跳过。
				return;
			}
			if (!flag9)
			{
				// 走不过去，但人就贴在旁边、光标又可见（点击会被采纳）：直接挥，别把这格丢掉。
				// 实机：玩家 (4,13) 要挥 (3,12)（切比雪夫 1 格），两次出发失败后整格被跳过，
				// 表现出来就是「卡住、眼前那格不锄」。
				bool adjacentAndClickable = (currentItem is Hoe || currentItem is WateringCan)
					&& Game1.wasMouseVisibleThisFrame
					&& TileUtil.ChebyshevDistance(((Character)player).Tile, SwingAim ?? value3) <= 1f;
				if (!adjacentAndClickable)
				{
					// 门后那片地在门关着时压根搜不到路：先走到门口把门推开，下一 tick 重新规划。
					if (GatePhase == 0 && TryDetourToGate(player))
					{
						return;
					}
					// 8 个候选站位全寻不到路，说明「这片区域现在过不去」（关着的门 / 动物堵在唯一通道），
					// 不是抖一下：第二趟还不行就排到队尾，并且封盘 30 tick，
					// 别留在原地每帧重烧 8 次全图 A*（一帧几十~几百毫秒，就是掉成几帧的原因）。
					if (++WalkAttempts > 1 && !RetryTimedOutTile(value3, "startfail"))
					{
						GiveUpCurrent(value3);
					}
					WalkFailCooldownTicks = WalkFailCooldown;
					return;
				}
			}
		}
		Current = null;
		bool refilling = Refilling;
		Refilling = false;
		if (refilling)
		{
			SkipOrderOnce = true;
		}
		if (!refilling)
		{
			MeleeWeapon val5 = (MeleeWeapon)((currentItem is MeleeWeapon) ? currentItem : null);
			bool flag10;
			if (targetPond != null)
			{
				flag10 = true;
			}
			else if (val5 == null || !TileUtil.IsScythe(currentItem))
			{
				Vector2 reachHere = ((currentItem is Hoe || currentItem is WateringCan) ? (SwingAim ?? value3) : value3);
				flag10 = TileUtil.ChebyshevDistance(((Character)player).Tile, reachHere) <= 1f;
			}
			else
			{
				if (ScytheCanHit(val5, player, value3))
				{
					goto IL_14f5;
				}
				Point? walkDestination3 = WalkDestination;
				if (!walkDestination3.HasValue)
				{
					goto IL_136f;
				}
				Point valueOrDefault3 = walkDestination3.GetValueOrDefault();
				flag10 = ((Character)player).TilePoint == valueOrDefault3;
			}
			if (!flag10)
			{
				goto IL_136f;
			}
		}
		goto IL_14f5;
		IL_14f5:
		if (!refilling && !TileUtil.IsSowSeed(currentItem))
		{
			MeleeWeapon val6 = (MeleeWeapon)((currentItem is MeleeWeapon) ? currentItem : null);
			bool flag11;
			if (val6 != null && TileUtil.IsScythe(currentItem))
			{
				flag11 = TileUtil.CanWork(((Character)player).currentLocation, value3, Mod.Config, currentItem);
				if (!flag11)
				{
					GameLocation currentLocation = ((Character)player).currentLocation;
					if (currentLocation != null)
					{
						int facingDirection = ((Character)player).FacingDirection;
						foreach (Vector2 item in ScytheSweepTiles(val6, player, value3, facingDirection))
						{
							if (TileUtil.CanWork(currentLocation, item, Mod.Config, currentItem))
							{
								flag11 = true;
								break;
							}
						}
					}
				}
			}
			else
			{
				flag11 = ((SwingPlan.Count > 0) ? HasAnyWorkAround(((Character)player).currentLocation, value3, currentItem) : TileUtil.CanWork(((Character)player).currentLocation, value3, Mod.Config, currentItem));
			}
			if (!flag11)
			{
				if (TileUtil.IsScythe(currentItem))
				{
					if (Planned.TryGetValue(value3, out var value4) && TileUtil.IsWanted(value4, Mod.Config))
					{
						GameLocation location2 = Location;
						if (location2 != null && TileUtil.GetTileWork(location2, value3, StartItem, Mod.Config) != TileUtil.TileWork.None)
						{
							goto IL_1938;
						}
					}
					SplashTilled++;
					TileDone.Add(value3);
					return;
				}
				SplashTilled++;
				return;
			}
		}
		goto IL_1938;
		IL_136f:
		// 够不够得着要按「瞄准的那一格」量：锚点可能比队首挪了一格（选框边缘），
		// 人站在计划站位上贴着的是锚点。拿队首量会把已经站好的人判成够不着，来回踱步。
		Vector2 reachTile = ((currentItem is Hoe || currentItem is WateringCan) ? (SwingAim ?? value3) : value3);
		float value6 = TileUtil.ChebyshevDistance(((Character)player).Tile, reachTile);
		// 目标已经在上面被清掉了；这里必须补回来。否则走一步就丢目标、下一 tick 换下一格，
		// 人会一路走一路换格、几乎不挥（实机：距离 2 → 走一步 → 换下一格 → 距离 2 …）。
		Current = value3;
		if (TryStartWalking(player, currentItem, value3))
		{
			WalkAttempts = 0;
			WalkFailCooldownTicks = 0;
			if (Tiles.IndexOf(value3) < Index)
			{
				Tiles.Insert(Math.Min(Index, Tiles.Count), value3);
			}
		}
		else if (GatePhase == 0 && TryDetourToGate(player))
		{
			// 够不着 + 附近有关着的围栏门：门后的地寻不到路，先去把门推开。
			return;
		}
		else if (++WalkAttempts > 2 && !RetryTimedOutTile(value3, "unreachable"))
		{
			GiveUpCurrent(value3);
		}
		return;
		IL_1938:
		if (refilling)
		{
			if (val == null)
			{
				Stop(I18n.T("stop-refill-failed"));
				return;
			}
			Refills++;
			ApplyRefill(player, val, value3);
		}
		else if (Planned.TryGetValue(value3, out TileUtil.TileWork workHere) && workHere == TileUtil.TileWork.MachineLoad)
		{
			// 「既能种、又能装机器」的种子（咖啡豆）：按这一格规划出来的活儿走，不按手持物类别走。
			ApplyMachineLoad(player, currentItem, value3);
			Tilled++;
		}
		else if (TileUtil.IsSowSeed(currentItem))
		{
			ApplyPlaceItem(player, currentItem, value3);
			Tilled++;
		}
		else if (val != null)
		{
			if (TileUtil.IsScythe(currentItem) && ShouldHandPick(value3))
			{
				GameLocation currentLocation2 = ((Character)player).currentLocation;
				if (currentLocation2 != null && IsHandPickInventoryFull(currentLocation2, player, value3))
				{
					Stop(I18n.T("stop-inventory-full"));
					return;
				}
				bool flag12 = currentLocation2 != null && ApplyHandPick(currentLocation2, player, value3);
				if (flag12)
				{
					Tilled++;
					LastScytheTarget = value3;
				LastScytheStand = ((Character)player).Tile;
					return;
				}
				FailedSwings++;
				if (!RetryTimedOutTile(value3, "handpick"))
				{
					GiveUpCurrent(value3);
				}
			}
			else
			{
				ApplyHoe(player, val, value3);
				Tilled++;
			}
		}
		else if (TileUtil.IsSprinklerItem(currentItem))
		{
			if (ApplySprinklerPlace(player, currentItem, value3))
			{
				Tilled++;
			}
		}
		else if (TileUtil.IsFertilizerItem(currentItem))
		{
			ApplyPlaceItem(player, currentItem, value3);
			Tilled++;
		}
		else if (TileUtil.IsPlaceableObjectItem(currentItem))
		{
			ApplyPlaceItem(player, currentItem, value3);
			Tilled++;
		}
		else if (TileUtil.IsMachineLoadItem(currentItem))
		{
			ApplyMachineLoad(player, currentItem, value3);
			Tilled++;
		}
		else
		{
			Stop(I18n.T("stop-unknown-item"));
		}
	}

	private bool StartedWalking()
	{
		bool flag = IsMovementInputDown();
		bool result = flag && !MovementWasDown;
		MovementWasDown = flag;
		return result;
	}

	internal static bool IsMovementInputDown()
	{
		InputState input = Game1.input;
		if (input == null || Game1.options == null)
		{
			return false;
		}
		KeyboardState keyboardState = input.GetKeyboardState();
		if (Game1.isOneOfTheseKeysDown(keyboardState, Game1.options.moveUpButton) || Game1.isOneOfTheseKeysDown(keyboardState, Game1.options.moveDownButton) || Game1.isOneOfTheseKeysDown(keyboardState, Game1.options.moveLeftButton) || Game1.isOneOfTheseKeysDown(keyboardState, Game1.options.moveRightButton))
		{
			return true;
		}
		if (!Game1.options.gamepadControls)
		{
			return false;
		}
		GamePadState gamePadState = input.GetGamePadState();
		GamePadThumbSticks thumbSticks = (gamePadState).ThumbSticks;
		if (!(Math.Abs((thumbSticks).Left.X) > 0.25f))
		{
			thumbSticks = (gamePadState).ThumbSticks;
			if (!(Math.Abs((thumbSticks).Left.Y) > 0.25f) && !(gamePadState).IsButtonDown((Buttons)1) && !(gamePadState).IsButtonDown((Buttons)2) && !(gamePadState).IsButtonDown((Buttons)4))
			{
				return (gamePadState).IsButtonDown((Buttons)8);
			}
		}
		return true;
	}

	private Vector2? TakeNextTillable(Item? item)
	{
		// 杂物混合跑一路只按「离人最近」挑格，不按行序；行重排会把刚挑好的队首又打乱。
		// 镰刀作物批同理：带子 S 的顺序是权威的，行重排按玩家 X 翻方向会把带子拆散。
		if (Plan == null && !(ScytheCropRoute && TileUtil.IsScythe(item)))
		{
			OrderHeadRow(Game1.player);
		}
		// 锄/浇的锚点选择挪到下面循环里、确认这一格真要做的时候再算：
		// 之前是在取格之前按队首算的，而队首可能还是前面那行已经做完的格子。

		// 锄/浇收尾（剩不到 24 格）：残局散在几行里，再守行序就会「眼前有活却跑远」。
		// 这一段改成和斧/镐一样的「按真实步行距离取最近一格」，行序只在大块时用。
		bool tailNearest = (item is Hoe || item is WateringCan) && Tiles.Count - Index <= 24;
		bool skipOrder = SkipOrderOnce;
		SkipOrderOnce = false;
		// 队首离人 ≥8 格、而近处还有活：先做脚边的。行尾漏下的两三列会在「这一行扫完、
		// 转到下一行」时把队首甩到田对面（实机：从 (31,23) 走 29 格回去补 (5,21) 那三格）。
		// 但开局第一格还没取之前不许生效：那一格是「离玩家最近的那条边」的起点，
		// 起手就该先走过去，不能又把人拉回脚边（实机：玩家周围先挖出一块缺口）。
		bool rowNearest = (item is Hoe || item is WateringCan) && !tailNearest && !skipOrder && Location != null
			&& Index < Tiles.Count && Index > 0 && Game1.player != null;
		// 混合跑里作物批排在最前段、顺序由带子 S 定；这段不能被「离人最近」重排，
		// 否则作物批刚排好的 S 会被杂物段的就近贪心一路打散。
		bool cropBatchHead = ScytheCropRoute && Plan != null && Index < Tiles.Count && IsScytheCropTile(Tiles[Index]);
		if ((item is Axe || item is Pickaxe || tailNearest || (Plan != null && !cropBatchHead)) && Location != null && Index < Tiles.Count)
		{
			// 斧/镐路线是一次性按开局地形排的：树/石头/桩/栅栏本身挡路，内圈目标开局可能根本
			// 不可达，只能被排到队尾；外圈清开后它其实已经在脚边。每取一格前按玩家当前站位
			// 重算真实步行距离，把最近的那格换到队首（平局保持原队列顺序）。
			// 杂物混合跑也走这条：整框一条路线，下一个永远是「离人最近的那格」，不管它归哪把工具——
			// 眼前是杂草就用镰刀，最近的是树枝就换斧头，再下一个是石头就换镐头。
			int liveSlot = FindNearestLiveSlot(Location, item);
			if (liveSlot > Index)
			{
				Vector2 liveFront = Tiles[Index];
				Tiles[Index] = Tiles[liveSlot];
				Tiles[liveSlot] = liveFront;
			}
			// 下一格要走很远时把账记下来：直线近 ≠ 走路近。被「不在队列里的树 / 水 / 建筑」挡着的格
			// 只能绕外圈，这种绕路不是挑格挑错，是地形如此——日志里说清楚，别看起来像没按最近走。
			if (LiveField.IsValid)
			{
				int walkSteps = LiveField.Serve(Tiles[Index]);
				if (walkSteps >= 20)
				{
					Farmer walker = Game1.player;
					Vector2 at = ((walker != null) ? ((Character)walker).Tile : Tiles[Index]);
				}
			}
		}
		else if (rowNearest)
		{
			// 队首那几格可能已经做完了（Index 还没跳过），要看「第一个真正的待办格」有多远；
			// 再按格子距离直接扫队列找最近的一格（走距场偶尔返回空/不可达，兜底会退化成守队首）。
			Vector2 playerTile = ((Character)Game1.player).Tile;
			int headIdx = Index;
			while (headIdx < Tiles.Count && !TileUtil.CanWork(Location, Tiles[headIdx], Mod.Config, item))
			{
				headIdx++;
			}
			if (headIdx < Tiles.Count)
			{
				float headDist = TileUtil.ChebyshevDistance(playerTile, Tiles[headIdx]);
				int best = -1;
				float bestDist = headDist;
				if (headDist >= 8f)
				{
					// 「身边有活却要走远」按真实步行步数量：直线 3 格、但隔着一片没清的林子要绕
					// 三十步的那种格不算「身边」（用户 2026-11-01：最近 = 走路的距离，不是直线距离）。
					// 切氏距离只当便宜的门槛，真要比就灌一次裁剪距离场。
					TileUtil.DistanceField walk = TileUtil.BuildWalkFieldIn(Location, playerTile, terrainOnly: true, LiveSlotBounds(playerTile));
					float headWalk = WalkSteps(walk, playerTile, Tiles[headIdx]);
					// 不规则边缘下队首常常在田对面：8 格门槛会让它每挥一刀就跨行跳
					//（实机：d=5/11、d=4/15 这种小收益也跳，玩家看到的就是乱跑）。
					// 只在「队首明显更远（≥12 步）、近处还能省 ≥4 步」时才就近换。
					if (headWalk >= 12f)
					{
						bestDist = headWalk;
						for (int i = headIdx + 1; i < Tiles.Count; i++)
						{
							Vector2 cand = Tiles[i];
							if (TileAttempts.TryGetValue(cand, out int attempts) && attempts >= 3)
							{
								continue;
							}
							if (!TileUtil.CanWork(Location, cand, Mod.Config, item))
							{
								continue;
							}
							float dist = WalkSteps(walk, playerTile, cand);
							if (dist + 4f <= headWalk && dist < bestDist)
							{
								bestDist = dist;
								best = i;
							}
						}
					}
				}
				if (best > headIdx)
				{
					Vector2 was = Tiles[headIdx];
					Tiles[headIdx] = Tiles[best];
					Tiles[best] = was;
				}
			}
		}
		int num3 = Tiles.Count + 1;
		while (Index < Tiles.Count && num3-- > 0)
		{
			// 作物批带子扫完的那一刻（队首第一次变成非作物格），把还活着的作物格就近补进队列。
			// 镰刀弧线不规则，每条带子的顶行常漏一格；不就地补，这些零头会攒成一排稀疏气泡、
			// 等整轮收尾再横穿一趟（实机 0.5.68 截图：上面每收完一片就漏一个）。
			if (ScytheCropRoute && !ScytheCropMopUpDone && !IsScytheCropTile(Tiles[Index]))
			{
				ScytheCropMopUpDone = true;
				List<Vector2> left = new List<Vector2>();
				if (Location != null)
				{
					HashSet<Vector2> seen = new HashSet<Vector2>();
					foreach (Vector2 t in Tiles)
					{
						if (!seen.Add(t) || !IsScytheCropTile(t))
						{
							continue;
						}
						TileAttempts.TryGetValue(t, out var attempts);
						if (attempts < 3 && TileUtil.CanWork(Location, t, Mod.Config, item))
						{
							left.Add(t);
						}
					}
				}
				if (left.Count > 0)
				{
					Farmer mopPlayer = Game1.player;
					Vector2 mopFrom = ((mopPlayer != null) ? ((Character)mopPlayer).Tile : Vector2.Zero);
					List<Vector2> mop = TileUtil.BuildPathWorkOrder(left, mopFrom, Location, SealPlacedTiles);
					Tiles.InsertRange(Index, mop);
				}
				continue;
			}
			// 只有队首这一格「真要做」时才按玩家现在的站位重排它所在的那一行。
			// 之前无条件在循环里排：跳过已做完的格子时也会顺手把后面几行一起排掉，
			// 那几行用的是「同一时刻的站位」，于是 13/12/11/10/9 行全被排成从右端开始，来回横穿。
			if (!skipOrder && (item is Hoe || item is WateringCan) && Location != null && TileUtil.CanWork(Location, Tiles[Index], Mod.Config, item))
			{
				OrderHeadRow(Game1.player);
			}
			Vector2 val4 = Tiles[Index];
			if (TileAttempts.TryGetValue(val4, out var value5) && value5 >= 3)
			{
				Index++;
				continue;
			}
			if (SwingPlan.Count > 0)
			{
				if (!TileUtil.WouldTill(Location, val4))
				{
					Index++;
					SplashTilled++;
					continue;
				}
				Index++;
				return val4;
			}
			// 混合跑：这一格该用哪把工具问分工表；单工具跑就是手里这把。
			Item? toolHere = (Plan != null) ? ToolFor(val4) : item;
			if (TileUtil.CanWork(Location, val4, Mod.Config, toolHere))
			{
				if (TileUtil.IsScythe(toolHere) && Plan == null)
				{
					Index++;
					GameLocation location2 = Location;
					if (location2 != null)
					{
						// 作物批按 S 序严格走，不就近重挑 —— 重挑正是「乱走」的来源；
						// 剩余批（杂草/草/苔藓/采集品/机器/鱼塘）仍是 0.5.59/0.5.65 的就近挑。
						// 顺带把覆盖统计限到作物格，免得找站位时被杂草堆从 S 路线上拽走。
						ScytheCoverageCropsOnly = ScytheCropRoute && IsScytheCropTile(val4);
						return ScytheCoverageCropsOnly ? val4 : PickNextScytheTile(location2, val4);
					}
					return val4;
				}
				if ((toolHere is Hoe || toolHere is WateringCan) && Location != null && Plan == null)
				{
					// 锚点在这里按「真要做的那一格」算：可以在它旁边挪一格对齐 3 宽，朝向/档位也一起定。
					var (slot, facing, stand, power) = PickWidestSwing(Game1.player, Location, (Tool)toolHere);
					if (slot >= Index)
					{
						Index++;
						PreferredFacing = facing;
						PreferredStand = stand;
						PendingPower = power;
						Vector2 aimTile2 = SwingAim ?? val4;
						return val4;
					}
				}
				Index++;
				// 混合跑：这一格该换工具就现在换——先换再走，人是拿着对的工具走过去的。
				if (Plan != null)
				{
					SwitchToolFor(Game1.player, val4, toolHere);
				}
				return val4;
			}
			Index++;
			if (Planned.ContainsKey(val4))
			{
				if (TileUtil.IsScythe(toolHere))
				{
					TileDone.Add(val4);
				}
				SplashTilled++;
			}
			else
			{
				Skipped++;
			}
		}
		return null;
	}

	private int FindNearestLiveSlot(GameLocation location, Item item)
	{
		Farmer player = Game1.player;
		Vector2 from = ((player != null) ? ((Character)player).Tile : Tiles[Index]);
		// 距离场只灌「玩家 + 还没做的格」的外接框：整图灌一次在大地图上是 120KB 级的大对象分配
		// （进 LOH，给 gen2 GC 加压）+ 几万格 BFS，而这个方法每取一格就跑一次。
		// 通行表用「角色当空气」那一套（掩码 249）：挑格只是排序，不是真走——真走由走路驱动现绕。
		// 用带角色的表（251）会把鸡/牛/宠物站的那几格冻成墙 60 tick，脚边的杂草整片读成「不可达」，
		// 人就撇下眼前的活绕去别处（实机 2026-11-01：眼前有杂草却绕一大圈）。
		Rectangle? bounds = LiveSlotBounds(from);
		TileUtil.DistanceField field = TileUtil.BuildWalkFieldIn(location, from, terrainOnly: true, bounds);
		int slot = ScanNearestLiveSlot(location, item, field, from, out var anyUnreachable);
		if (anyUnreachable && bounds.HasValue)
		{
			// 有格子在裁剪范围里读成不可达：可能是真要绕出框才连得通（湖/围栏），
			// 整图重算一次兜底，别把「绕得通」当成「够不着」把最近的格子落下。
			TileUtil.DistanceField whole = TileUtil.BuildWalkFieldIn(location, from, terrainOnly: true, null);
			if (whole.IsValid)
			{
				field = whole;
				slot = ScanNearestLiveSlot(location, item, field, from, out _);
			}
		}
		if (!field.IsValid)
		{
			return Index;
		}
		LiveField = field;
		return slot;
	}

	/// <summary>「玩家 + Index 之后还没做的格」的外接框，四周留余量。
	/// 余量规则和 BuildPathWorkOrder / 动物队列站位框一致：至少 12 格、最多 24 格、或跨度的一半。
	/// 下限是为了别把绕湖绕围栏的路线裁断（裁断会让距离被高估，而这张场从 0.5.73 起不只用来挑最近格，
	/// 镰刀选格和「脚边有活」判定也读它，距离读歪会直接改顺序）；上限是为了大选框时还真省得下来。</summary>
	private Rectangle? LiveSlotBounds(Vector2 from)
	{
		if (Index >= Tiles.Count)
		{
			return null;
		}
		int minX = (int)MathF.Floor(from.X);
		int minY = (int)MathF.Floor(from.Y);
		int maxX = minX;
		int maxY = minY;
		for (int i = Index; i < Tiles.Count; i++)
		{
			Vector2 tile = Tiles[i];
			int x = (int)MathF.Floor(tile.X);
			int y = (int)MathF.Floor(tile.Y);
			minX = Math.Min(minX, x);
			minY = Math.Min(minY, y);
			maxX = Math.Max(maxX, x);
			maxY = Math.Max(maxY, y);
		}
		int margin = Math.Max(12, Math.Min(24, Math.Max(maxX - minX, maxY - minY) / 2));
		return new Rectangle(minX - margin, minY - margin, maxX - minX + 1 + margin * 2, maxY - minY + 1 + margin * 2);
	}

	/// <summary>按距离场挑「离人最近、还真有活」的那一格；顺带报出这次裁剪有没有可能裁掉了真正的最近格。</summary>
	private int ScanNearestLiveSlot(GameLocation location, Item item, TileUtil.DistanceField field, Vector2 from, out bool anyUnreachable)
	{
		anyUnreachable = false;
		LastScanUnreachable = 0;
		int slot = Index;
		int best = int.MaxValue;
		int bestStraight = int.MaxValue;
		int nearestUnreachableStraight = int.MaxValue;
		for (int i = Index; i < Tiles.Count; i++)
		{
			Vector2 candidate = Tiles[i];
			if (TileAttempts.TryGetValue(candidate, out var attempts) && attempts >= 3)
			{
				continue;
			}
			if (!(Plan != null ? CanWorkTile(location, candidate) : TileUtil.CanWork(location, candidate, Mod.Config, item)))
			{
				continue;
			}
			int straight = Math.Abs((int)candidate.X - (int)from.X) + Math.Abs((int)candidate.Y - (int)from.Y);
			int distance = field.Serve(candidate);
			if (distance >= 1000000 && ScytheCanHitFromHere(location, candidate))
			{
				// 镰刀不用贴边站：隔着一层杂草、站在原地一挥就带到的格，步行距离场读成「不可达」
				// （没地方站），可它恰恰是眼前最该挥的那一刀。算 0 步，立刻排到最前。
				distance = 0;
			}
			if (distance >= 1000000)
			{
				LastScanUnreachable++;
				// 只有「直线距离就已经比最终最优更近」的不可达格，才值得整图重灌复核：
				// 真步数一定 ≥ 曼哈顿距离，直线都比不过的格子整图重灌也翻不了盘；
				// 等距（==）也不用兜——平局本来就是先扫到的那格赢，整图口径下挑出来的是同一格。
				// 这样「内圈目标开局本来就走不到」的地图不会每取一格都白灌两遍。
				nearestUnreachableStraight = Math.Min(nearestUnreachableStraight, straight);
				continue;
			}
			if (distance < best || (Plan != null && distance == best && straight < bestStraight))
			{
				best = distance;
				bestStraight = straight;
				slot = i;
			}
		}
		anyUnreachable = nearestUnreachableStraight < best;
		return slot;
	}

	/// <summary>
	/// 挑格用的「步行步数」：有距离场就走距离场——此刻走不过去的格返回无穷大，
	/// 一律不算「近」（直线/曼哈顿近的格，可能隔着一片没清的林子或一道水沟要绕几十步，
	/// 用户 2026-11-01 的原话：那种距离就不算最近距离）。场算不出来时退回曼哈顿，总比没有强。
	/// </summary>
	private static float WalkSteps(TileUtil.DistanceField field, Vector2 from, Vector2 tile)
	{
		if (field.IsValid)
		{
			int steps = field.Serve(tile);
			return (steps >= 1000000) ? float.MaxValue : steps;
		}
		return TileUtil.ManhattanDistance(from, tile);
	}

	/// <summary>
	/// 这一格归镰刀、且站在原地一挥就够得着吗？步行距离场按「能不能站到旁边」算可达，
	/// 对镰刀太严：杂草成片时里层的格四周全是杂草、没地方站，场里读成不可达，
	/// 可镰刀一挥是一片，站在原地就能带到——这种格不该被推迟到绕一大圈之后。
	/// </summary>
	private bool ScytheCanHitFromHere(GameLocation location, Vector2 candidate)
	{
		Item? tool = (Plan != null) ? ToolFor(candidate) : StartItem;
		MeleeWeapon scythe = (MeleeWeapon)(((tool is MeleeWeapon) && TileUtil.IsScythe(tool)) ? tool : null);
		if (scythe == null)
		{
			return false;
		}
		Farmer player = Game1.player;
		return (player != null) && ScytheCanHit(scythe, player, candidate);
	}

	private static bool BatchHasMachineWork(IEnumerable<Vector2> tiles, Dictionary<Vector2, TileUtil.TileWork> planned)
	{
		foreach (Vector2 tile in tiles)
		{
			if (planned.TryGetValue(tile, out var value) && value == TileUtil.TileWork.ScytheMachine)
			{
				return true;
			}
		}
		return false;
	}

	private Vector2 PickNextScytheTile(GameLocation location, Vector2 fallback)
	{
		int num = Index - 1;
		if (num < 0 || num >= Tiles.Count || Tiles[num] != fallback)
		{
			return fallback;
		}
		Vector2 from;
		if (Walking)
		{
			from = WalkTarget;
		}
		else
		{
			Farmer player = Game1.player;
			from = ((player != null) ? ((Character)player).Tile : fallback);
		}
		// 「最近」按真实步行步数：曼哈顿近的格可能隔着一片没清的林子 / 一道水沟，实际要绕几十步——
		// 那种格在这一刀不算近（用户 2026-11-01：「绕超级远的路，不是按距离最近来」）；
		// 此刻走不过去的格不参与挑，等挡路的东西清掉自然会浮上来。
		TileUtil.DistanceField walk = TileUtil.BuildWalkFieldIn(location, from, terrainOnly: true, LiveSlotBounds(from));
		float minDistance = float.MaxValue;
		for (int i = num; i < Tiles.Count; i++)
		{
			if (!IsScytheCandidate(location, Tiles[i]))
			{
				continue;
			}
			float d = WalkSteps(walk, from, Tiles[i]);
			if (d >= float.MaxValue && ScytheCanHitFromHere(location, Tiles[i]))
			{
				d = 0f;
			}
			if (d < minDistance)
			{
				minDistance = d;
			}
		}
		if (minDistance == float.MaxValue)
		{
			return fallback;
		}
		// 先只看「离得最近的一圈」，再在这一圈里按「挨着上一刀 → 一刀砍得多」定夺。
		// 覆盖数要采样 4 个朝向 × 6 帧动画，先用距离筛掉绝大多数候选才不会卡帧。
		// 距离必须是曼哈顿步数而不是切氏格数：农夫四方向走路，斜对角 1 格实际要走 2 步。
		// 切氏把斜向当近路，两个后果（实机 2026-11-01 铱镰刀日志）：
		//   1) 脚边零头输给「看起来近」的斜向目标，一路被推迟成收尾横穿——
		//      (30,25) 时脚边 3 步的 (33,25) 输给 3 步的 (28,24)（切氏 3>2），
		//      最后从 (14,24) 横穿 19 步回捡；曼哈顿下两者打平，挨着上一刀的 (33,25) 当场胜出。
		//   2) 两条作物带之间等距平局，由覆盖数随意翻边 → 站位 y=12,15,12,15 锯齿；
		//      曼哈顿下同行 3 步 < 斜穿 5 步，一行扫完才换行。
		// 0.5.65：曼哈顿就近之上加两条离线模拟验证过的修正
		//（.research/sim_scythe_current.py 的 V6 = 孤儿折扣 + 方向惯性；该脚本的 V0 逐字对齐本函数）：
		//   孤儿折扣：曼哈顿 ≤4 且一刀只能盖 ≤3 格的零头，有效距离 -1 —— 路过就顺手吃掉。
		//     不然它会在同距平局里输给覆盖大的目标，被一路推迟成收尾横穿（0.5.58 日志的老毛病）。
		//   方向惯性：有效距离再减 0.5 ×「候选方向 · 最近一次移动方向」的对齐度，
		//     抑制两条作物带之间等距翻边（站位 y=12,15,12,15 锯齿）。
		// 5 布局（实心田×2 / 隔格田 / 畜栏带墙 / 温室带墙）求和：走路 853→803（-5.9%）、
		// ≥8 格横穿 19→12、收尾路 168→136，逐布局无单点回归。
		// 覆盖数要采样 4 朝向 × 6 帧动画，先用距离预筛（比 0.5.64 多放一圈，让孤儿折扣够得着）。
		Vector2 heading = from - LastScytheStand;
		float headingLen = MathF.Max(MathF.Abs(heading.X), MathF.Abs(heading.Y));
		bool useHeading = LastScytheStand != Vector2.Zero && headingLen > 0f;
		List<Vector2> pool = new List<Vector2>();
		List<int> poolIndex = new List<int>();
		List<float> poolEff = new List<float>();
		List<int> poolCover = new List<int>();
		for (int i = num; i < Tiles.Count; i++)
		{
			Vector2 candidate = Tiles[i];
			if (!IsScytheCandidate(location, candidate))
			{
				continue;
			}
			float distance = WalkSteps(walk, from, candidate);
			if (distance >= float.MaxValue && ScytheCanHitFromHere(location, candidate))
			{
				distance = 0f;
			}
			if (distance > minDistance + 2f)
			{
				continue;
			}
			int cover = (ShouldHandPick(candidate) ? 0 : CountScytheCoverage(Game1.player, candidate));
			float eff = distance;
			if (distance <= 4f && cover <= 3)
			{
				eff -= 1f;
			}
			if (useHeading)
			{
				float vx = candidate.X - from.X;
				float vy = candidate.Y - from.Y;
				float vLen = MathF.Max(MathF.Abs(vx), MathF.Abs(vy));
				if (vLen > 0f)
				{
					eff -= 0.5f * (vx / vLen * (heading.X / headingLen) + vy / vLen * (heading.Y / headingLen));
				}
			}
			pool.Add(candidate);
			poolIndex.Add(i);
			poolEff.Add(eff);
			poolCover.Add(cover);
		}
		Vector2 val = fallback;
		int num2 = num;
		float num3 = float.MaxValue;
		if (pool.Count > 0)
		{
			float effMin = float.MaxValue;
			for (int k = 0; k < poolEff.Count; k++)
			{
				if (poolEff[k] < effMin)
				{
					effMin = poolEff[k];
				}
			}
			for (int k = 0; k < pool.Count; k++)
			{
				if (poolEff[k] > effMin + 1f)
				{
					continue;
				}
				Vector2 candidate2 = pool[k];
				int cover2 = poolCover[k];
				int num5 = ((cover2 < 2) ? 1 : 0);
				float lastLeg = TileUtil.ManhattanDistance(LastScytheTarget, candidate2);
				float score = poolEff[k] * 100000f + MathF.Min(lastLeg, 30f) * 1000f + (float)num5 * 100f + (float)(-cover2) * 10f + candidate2.Y + candidate2.X / 1000f;
				if (score < num3)
				{
					num3 = score;
					val = candidate2;
					num2 = poolIndex[k];
				}
			}
		}
		if (num2 != num)
		{
			Vector2 value2 = Tiles[num2];
			Tiles[num2] = Tiles[num];
			Tiles[num] = value2;
		}
		return val;
	}

	/// <summary>这一格能不能当镰刀的下一刀：没放弃、确实还有活；混着别的活时机器产物先往后放。</summary>
	private bool IsScytheCandidate(GameLocation location, Vector2 candidate)
	{
		if (TileAttempts.TryGetValue(candidate, out var attempts) && attempts >= MaxTileAttempts)
		{
			return false;
		}
		if (!TileUtil.CanWork(location, candidate, Mod.Config, StartItem))
		{
			return false;
		}
		if (ScytheMachineRoute && Planned.TryGetValue(candidate, out var work) && work == TileUtil.TileWork.ScytheMachine)
		{
			return false;
		}
		return true;
	}

	private bool HasAnyWorkAround(GameLocation? location, Vector2 tile, Item? item)
	{
		if (location == null)
		{
			return false;
		}
		Vector2 tile2 = default(Vector2);
		for (int i = -1; i <= 1; i++)
		{
			for (int j = -1; j <= 1; j++)
			{
				tile2 = new Vector2(tile.X + (float)j, tile.Y + (float)i);
				if (TileUtil.WouldTill(location, tile2) || TileUtil.GetTileWork(location, tile2, item, Mod.Config) != TileUtil.TileWork.None)
				{
					return true;
				}
			}
		}
		return false;
	}


	private void RequeueMissedCovers()
	{
		ActiveSwingCovers = null;
		GameLocation location = Location;
		if (location == null)
		{
			return;
		}
		int num = 0;
		List<Vector2> list = new List<Vector2>();
		// Tiles 是 List、Contains 是线性扫，这一圈就成了 O(选框格数 × 队列长)，而它每挥一次就跑一遍
		// （800 格的框 ≈ 64 万次比较）。这里现做一份快照，把整圈降到 O(选框格数)。
		// 不改成常驻的 HashSet 镜像：Tiles 有 15 处变更点，而且允许重复（走过的格子会被重新插回来补做），
		// 镜像漏同步一处就是静默丢格/重复格——那正是这个仓库斗了 50 个版本的那类 bug；
		// 快照只活在这个方法里，用完就扔，没有这个风险。
		HashSet<Vector2> queued = new HashSet<Vector2>(Tiles);
		foreach (Vector2 key in Planned.Keys)
		{
			// 只要这格还「有活」就补回队列：旧版只补荒地（WouldTill），浇水/播种/收割漏掉的
			// 格子不会回来，只能耗到 3 次判定失败——玩家看到的就是「漏一格就标失败」。
			if (TileUtil.CanWork(location, key, Mod.Config, StartItem) && !queued.Contains(key))
			{
				TileAttempts.TryGetValue(key, out var value);
				if (value < 3)
				{
					TileAttempts[key] = value + 1;
					list.Add(key);
				}
			}
		}
		if (list.Count > 0)
		{
			Vector2 playerTile = ((WalkTarget == Vector2.Zero) ? ((Character)Game1.player).Tile : WalkTarget);
			// 镰刀作物批的补漏也按行段 S 排：回插时若重排成就近贪心，S 型当场被打断。
			bool scytheCropRequeue = ScytheCropRoute && TileUtil.IsScythe(StartItem) && list.Any((Vector2 t) => IsScytheCropTile(t));
			// 锄/浇的外围小块也按「路过顺手」插进主路线；镰刀补漏只守行段 S，不插散块。
			bool hoeOrCan = StartItem is Hoe || StartItem is WateringCan;
			// 作物批的主路线用的是 BuildBandSWorkOrder（3 行一条带子），补漏必须用同一套，
			// 否则补回来的格子按「分块就近」插进队首：实机表现就是扫完一条带子又横穿回另一头
			// 补一两格（用户 2026-11-02：有间隔的田里每趟都从右到左、完了再走回右边）。
			if (scytheCropRequeue)
			{
				list = TileUtil.BuildBandSWorkOrder(list, playerTile, 3);
			}
			else
			{
				list = ((hoeOrCan || scytheCropRequeue) ? TileUtil.BuildRowSWorkOrder(list, playerTile, hoeOrCan) : TileUtil.BuildPathWorkOrder(list, playerTile, location, SealPlacedTiles));
			}
			int num2 = Math.Min(Index, Tiles.Count);
			foreach (Vector2 item in list)
			{
				Tiles.Insert(num2, item);
				num2++;
			}
			num = list.Count;
		}
	}

	private (int Index, int Facing, Vector2 Stand, int Power) PickWidestSwing(Farmer player, GameLocation location, Tool tool)
	{
		SwingAim = null;
		// 小片/整块：如果队首所在的那一片待办能被一次蓄力完整盖住，就直接按“让它整片盖住”选锚点，
		// 而不是按距离挑一个角格（3x6 从角上蓄力会丢掉最右列，就是这条路走出来的）。
		if (TryPickChargedFullCover(player, location, tool, out int fullIndex, out int fullFacing, out Vector2 fullStand, out int fullPower))
		{
			if (fullIndex >= 0 && fullIndex < Tiles.Count)
			{
				SwingAim = Tiles[fullIndex];
			}
			return (Index: Index, Facing: fullFacing, Stand: fullStand, Power: fullPower);
		}
		if (Index < 0 || Index >= Tiles.Count)
		{
			return (Index: -1, Facing: -1, Stand: Vector2.Zero, Power: 0);
		}
		Vector2 headTile = Tiles[Index];
		// 只认队首那一格当锚点：锄/浇要「顺着」队列扫，不能因为后面某一格一次盖得更多就跳过去
		// （实机日志：行内从 23 跳到 32，再折回 17 一路到 5，最后才补 34）。朝向和档位仍在这个
		// 锚点上挑覆盖最多的那个——蓄力区域本来就按朝向铺，换朝向不会换格子。
		if (!TileUtil.CanWork(location, headTile, Mod.Config, (Item)tool))
		{
			return (Index: -1, Facing: -1, Stand: Vector2.Zero, Power: 0);
		}
		// 锚点允许在队首格旁边挪一格：蓄力区是 3 宽、以锚点为中心，队首是「最左的待办列」时
		// 直接拿队首当锚点会和上一块重叠一列（3×6 + 2×6，每三块亏一块）；往扫的方向挪一格，
		// 两块 3×6 才正好首尾相接。只挪一格，不会变成跳格。
		int bestCover = 0;
		int bestFacing = -1;
		Vector2 bestAnchor = headTile;
		int bestPower = 0;
		int[] facingBest = new int[4];
		Vector2[] facingAnchor = new Vector2[4];
		int[] facingPower = new int[4];
		// 只朝上/朝下（横跨行）。朝左/朝右是一块 6 宽×3 高、顺着行铺的带子，锚点没法沿行对齐，
		// 下一挥必和上一挥叠一大片（实机：第一挥朝右 (16,17)、第二挥朝右 (15,19)，重叠 5 列 1 行）。
		for (int j = 0; j < 4; j++)
		{
			// 上下：带子 3 宽（铱+范围附魔是 5 宽）、以锚点为中心，沿 X 挪 1~2 格才能让起始列
			// 正好落在队首。左右：带子从锚点那一头往外铺，沿 Y 挪是为了把带子挪进选框。
			// 挪 2 格是给 5×5 那档用的：只挪 1 格时 100×100 要多挥 29%、白盖 24%。
			bool vertical = (j == 0 || j == 2);
			// 队首贴着选框上/下边缘：朝边缘外那一侧铺会有一格伸出框外，PlanAt 的溢出判定把整挥作废；
			// 只能朝框内铺，而锚点压在队首这一行时，3 格高的带子只盖住队首这一行，框内紧挨着的
			// 另一行要等最后单独补一趟（实机：角上 2 行×3 格，第一挥只浇下面 3 格，上面 3 格最后又跑一趟）。
			// 把锚点往框内挪一行、仍然朝框内铺，带子正好压住这两行。
			Rectangle? edgeBox = SelectionBox;
			if (vertical && edgeBox.HasValue)
			{
				Rectangle box = edgeBox.Value;
				int rowShift = 0;
				if (j == 2 && headTile.Y - 2f < (float)box.Top)
				{
					rowShift = -1;
				}
				else if (j == 0 && headTile.Y + 2f > (float)(box.Bottom - 1))
				{
					rowShift = 1;
				}
				if (rowShift != 0)
				{
					for (int k = -2; k <= 2; k++)
					{
						Vector2 edgeAnchor = headTile + new Vector2((float)k, (float)rowShift);
						if (!InSelection(edgeAnchor) || !PendingAhead(edgeAnchor))
						{
							continue;
						}
						if (!TileUtil.IsStandableIgnoringCritters(location, edgeAnchor + StandOffset(j)))
						{
							continue;
						}
						(int Power, int Newly) edgePlan = PlanAt(player, tool, edgeAnchor, j);
						if (edgePlan.Newly <= 0)
						{
							continue;
						}
						if (!BandCovers(tool, edgeAnchor, edgePlan.Power, j, headTile, player))
						{
							continue;
						}
						if (edgePlan.Newly > facingBest[j])
						{
							facingBest[j] = edgePlan.Newly;
							facingAnchor[j] = edgeAnchor;
							facingPower[j] = edgePlan.Power;
						}
						if (edgePlan.Newly > bestCover)
						{
							bestCover = edgePlan.Newly;
							bestFacing = j;
							bestAnchor = edgeAnchor;
							bestPower = edgePlan.Power;
						}
					}
				}
			}
			for (int k = -2; k <= 2; k++)
			{
				// 横向带子原来锚点的 X 只能落在队首那一列：贴到选框左右边缘时，
				// 5 格（铜锄 3 格）宽的带子会伸到框外被整挥作废，于是最右几列一直扫不到
				// （实机：13 宽的地每行最右 x=27 单列留到最后、一根一根去补）。
				// 给横向也加 ±2 列平移，让带子整条落进框里。
				for (int dx = -2; dx <= 2; dx++)
				{
					if (vertical && dx != 0)
					{
						continue;
					}
					Vector2 candidate = vertical ? headTile + new Vector2((float)k, 0f) : headTile + new Vector2((float)dx, (float)k);
					bool moved = vertical ? (k != 0) : (k != 0 || dx != 0);
					if (moved)
					{
						// 上下：锚点必须还是待办格（3 宽以它为中心）。
						// 左右：可以挪到框内相邻一行/列，哪怕已经做过——靠它把带子整条挪进框里
						// （记账/验收仍按队首那格）。
						if (vertical ? !PendingAhead(candidate) : !InSelection(candidate))
						{
							continue;
						}
					}
				// 计划站位必须真能站人。水塘边这一排的下方全是水，站位选到水里时走路永远到不了：
				// 队列会等满 240 tick，再把这一格判失败跳过（实机：水边连续跳过三格）。
				// 只看地形——动物/村民能被推开或绕开，不算挡。
				if (!TileUtil.IsStandableIgnoringCritters(location, candidate + StandOffset(j)))
				{
					continue;
				}
				(int Power, int Newly) plan = PlanAt(player, tool, candidate, j);
				if (plan.Newly <= 0)
				{
					continue;
				}
				// 挪过的锚点必须仍然盖住队首那一格：否则小片地上会退成单格档，只盖瞄准格、
				// 队首永远做不掉（离线仿真 2×2 起就能复现）。
				if (candidate != headTile && !BandCovers(tool, candidate, plan.Power, j, headTile, player))
				{
					continue;
				}
				if (plan.Newly > facingBest[j])
				{
					facingBest[j] = plan.Newly;
					facingAnchor[j] = candidate;
					facingPower[j] = plan.Power;
				}
				if (plan.Newly > bestCover)
				{
					bestCover = plan.Newly;
					bestFacing = j;
					bestAnchor = candidate;
					bestPower = plan.Power;
				}
				}
			}
		}
		// 朝向连续：上一挥的朝向只要不是明显差（不低于最优的 2/3）就继续用。否则一行扫到一半
		// 会翻成横着盖（3 行×6 列），把上下两行的格子提前吃掉，玩家看到的就是左右乱跳。
		int facing = bestFacing;
		Vector2 anchor = bestAnchor;
		int power = bestPower;
		// 横跨行（上下）优先：它是「梳子」扫法，一行一带不会来回跳。
		// 只有顺着行（左右）明显更划算（至少 1.5 倍，比如只剩一两行时 6 格 vs 3 格）才横着扫。
		// 之前一放开左右就在所有形状里都用，锚点又变回 16→22→29→31→24→21 那样来回横跳。
		int verticalBest = Math.Max(facingBest[0], facingBest[2]);
		int alongBest = Math.Max(facingBest[1], facingBest[3]);
		bool allowAlong = alongBest * 2 >= verticalBest * 3;
		// 顺着行铺的带子只要「实际待办覆盖」比上下朝向多，就用横的：
		// 顶上两行这种薄条，3×6 每挥 12 格，上下 5×5 只有 10 格（现场点名要的覆盖优先）。
		// 反过来说，竖铺能多盖（比如薄条有三行、竖铺一盖 15 格）时仍然保持竖铺。
		if (!allowAlong && alongBest > verticalBest)
		{
			allowAlong = true;
		}
		// 队首贴着选框上/下边缘、边缘只剩薄薄两行待办：上下朝向的带子会把三行已经做完的
		// 地一起铺进去（每挥只盖 10 格），顺着行铺的 3×6 同样这两行能盖 12 格。贴边时只要
		// 不比上下差就用横的——顶边两行按 6 列一带扫过去。
		if (!allowAlong && alongBest > 0 && alongBest >= verticalBest)
		{
			Rectangle? edgeSel = SelectionBox;
			if (edgeSel.HasValue && (headTile.Y - 2f < (float)edgeSel.Value.Top || headTile.Y + 2f > (float)(edgeSel.Value.Bottom - 1)))
			{
				allowAlong = true;
				int alongFacing = ((facingBest[1] >= facingBest[3]) ? 1 : 3);
				facing = alongFacing;
				anchor = facingAnchor[alongFacing];
				power = facingPower[alongFacing];
			}
		}
		if (!allowAlong)
		{
			if (LockedRowY.HasValue && MathF.Abs(LockedRowY.Value - headTile.Y) < 0.5f && LockedRowFacing >= 0 && facingBest[LockedRowFacing] > 0)
			{
				// 这一行已经定了朝向：只要它还盖得到新格，本行内就不翻面。
				// （实机：一行到头时把「朝上」判成不如「朝下」，于是最后两列翻了方向。）
				facing = LockedRowFacing;
			}
			else
			{
				facing = ((facingBest[0] >= facingBest[2]) ? 0 : 2);
				if (LastSwingFacing.HasValue && (LastSwingFacing.Value & 1) == 0 && facingBest[LastSwingFacing.Value] == verticalBest)
				{
					facing = LastSwingFacing.Value;
				}
				LockedRowY = headTile.Y;
				LockedRowFacing = facing;
			}
			anchor = facingAnchor[facing];
			power = facingPower[facing];
			if (facingBest[facing] <= 0)
			{
				LockedRowY = null;
				LockedRowFacing = -1;
				return (Index: -1, Facing: -1, Stand: Vector2.Zero, Power: 0);
			}
		}
		else if (LastSwingFacing.HasValue)
		{
			int last = LastSwingFacing.Value;
			if (facingBest[last] > 0 && facingBest[last] == bestCover)
			{
				facing = last;
				anchor = facingAnchor[last];
				power = facingPower[last];
			}
		}
		if (facing < 0)
		{
			return (Index: -1, Facing: -1, Stand: Vector2.Zero, Power: 0);
		}
		SwingAim = anchor;
		return (Index: Index, Facing: facing, Stand: anchor + StandOffset(facing), Power: power);
	}

	/// <summary>已经按玩家站位排过序的那一行（Y）。同一行只排一次，否则人走到行中间会把剩下的格子翻过来。</summary>
	private float? OrderedRowY;


	/// <summary>这一行用没用过「补排一次」（同行里脚边有活、队首却很远）。</summary>
	private bool RowResortUsed;

	/// <summary>这一行锁定的朝向和它的行号：一行内不翻面（只要锁的朝向还盖得到新格）。</summary>
	private int LockedRowFacing = -1;
	private float? LockedRowY;


	/// <summary>
	/// 每次换到新的一行时重排这一行剩下的格子：以玩家现在这一列切两半，先扫离自己近的那半边，
	/// 回头再扫远的半边（玩家站在行的一端时，近半边就是整行＝普通蛇形）。
	/// 不能靠开工时算好的交替：蓄力一带一次吃掉 3 行，下一行实际落在哪、人站在哪一端，开局对不上，
	/// 结果就是锄完一行又横穿整块地跑到另一端去。
	/// </summary>
	private void OrderHeadRow(Farmer? player)
	{
		if (player == null || Index >= Tiles.Count)
		{
			return;
		}
		float rowY = Tiles[Index].Y;
		int end = Index;
		while (end < Tiles.Count && MathF.Abs(Tiles[end].Y - rowY) < 0.5f)
		{
			end++;
		}
		if (OrderedRowY.HasValue && MathF.Abs(OrderedRowY.Value - rowY) < 0.5f)
		{
			// 这一行已经排过。但有两种情况要补排一次（每行最多一次）：
			// 队首离人超过 4 格，而这一行里另有待办格就在脚边 2 格内——
			// 那样会出现「身边有活却跑远」，而且跑过去之后脚边那格还得单独绕回来。
			if (RowResortUsed || TileUtil.ChebyshevDistance(((Character)player).Tile, Tiles[Index]) <= 3f)
			{
				return;
			}
			bool nearby = false;
			for (int i = Index; i < end; i++)
			{
				if (TileUtil.ChebyshevDistance(((Character)player).Tile, Tiles[i]) <= 2f)
				{
					nearby = true;
					break;
				}
			}
			if (!nearby)
			{
				return;
			}
			RowResortUsed = true;
		}
		else
		{
			RowResortUsed = false;
		}
		OrderedRowY = rowY;
		if (end - Index <= 1)
		{
			return;
		}
		float minX = float.MaxValue;
		float maxX = float.MinValue;
		for (int i = Index; i < end; i++)
		{
			minX = MathF.Min(minX, Tiles[i].X);
			maxX = MathF.Max(maxX, Tiles[i].X);
		}
		float px = ((Character)player).Tile.X;
		bool nearLeft = (px - minX) <= (maxX - px);
		List<Vector2> row = new List<Vector2>();
		for (int j = Index; j < end; j++)
		{
			row.Add(Tiles[j]);
		}
		row.Sort(delegate(Vector2 a, Vector2 b)
		{
			return a.X.CompareTo(b.X);
		});
		// 整行只按「离玩家近的那一端」进，一路扫到另一端——不再按玩家那列切成两半
		// （切两半会在行内出现一次「扫一半又跳回中间」）。
		if (!nearLeft)
		{
			row.Reverse();
		}
		List<Vector2> ordered = row;
		for (int k = 0; k < ordered.Count; k++)
		{
			Tiles[Index + k] = ordered[k];
		}
	}

	/// <summary>这个锚点+朝向+档位的蓄力带，是否盖住了 <paramref name="tile"/>。</summary>
	private bool BandCovers(Tool tool, Vector2 anchor, int power, int facing, Vector2 tile, Farmer player)
	{
		int old = ((Character)player).FacingDirection;
		try
		{
			((Character)player).FacingDirection = facing;
			List<Vector2>? affectedTiles = GetAffectedTiles(tool, anchor, power, player);
			return affectedTiles != null && affectedTiles.Contains(tile);
		}
		finally
		{
			((Character)player).FacingDirection = old;
		}
	}

	/// <summary>这一格在选框里（不含框外）。</summary>
	private bool InSelection(Vector2 tile)
	{
		if (!SelectionBox.HasValue)
		{
			return true;
		}
		Rectangle box = SelectionBox.Value;
		return (int)tile.X >= box.Left && (int)tile.X < box.Right && (int)tile.Y >= box.Top && (int)tile.Y < box.Bottom;
	}

	/// <summary>这格还有活、且排在队首后面（没被跳过）：锚点挪格只允许挪到这种格上。</summary>
	private bool PendingAhead(Vector2 tile)
	{
		if (Location == null || !TileUtil.CanWork(Location, tile, Mod.Config, StartItem))
		{
			return false;
		}
		return Tiles.IndexOf(tile) >= Index;
	}

	private static Vector2 StandOffset(int facing)
	{
		return facing switch
		{
			0 => new Vector2(0f, 1f), 
			1 => new Vector2(-1f, 0f), 
			2 => new Vector2(0f, -1f), 
			_ => new Vector2(1f, 0f), 
		};
	}

	/// <summary>
	/// 队首所在的那一片连通待办如果整个装得下一次蓄力，就选一个能把它整片盖住的锚点/朝向/档位，
	/// 并且优先站在待办区外（例如 3x6 的田：站短边的中间、朝田里，而不是站角上）。
	/// 装不下就返回 false，交给原来的贪心（已改成覆盖优先）。
	/// </summary>
	private bool TryPickChargedFullCover(Farmer player, GameLocation location, Tool tool, out int index, out int facing, out Vector2 stand, out int power)
	{
		index = -1;
		facing = -1;
		stand = Vector2.Zero;
		power = 0;
		int maxCharge = MaxCharge(tool);
		if (maxCharge <= 0)
		{
			return false;
		}
		Vector2 front = Vector2.Zero;
		bool hasFront = false;
		HashSet<Vector2> pending = new HashSet<Vector2>();
		for (int i = Index; i < Tiles.Count; i++)
		{
			Vector2 tile = Tiles[i];
			if (!TileUtil.CanWork(location, tile, Mod.Config, (Item)tool))
			{
				continue;
			}
			pending.Add(tile);
			if (!hasFront)
			{
				front = tile;
				hasFront = true;
			}
		}
		if (!hasFront)
		{
			return false;
		}
		// 一次蓄力最多能盖的格数：超过它的连通片肯定装不下，BFS 早停
		List<Vector2>? maxAffected = AffectedTilesFor(tool, front, maxCharge, player, 0);
		if (maxAffected == null)
		{
			return false;
		}
		int maxArea = maxAffected.Count;
		HashSet<Vector2> component = new HashSet<Vector2> { front };
		Queue<Vector2> queue = new Queue<Vector2>();
		queue.Enqueue(front);
		while (queue.Count > 0)
		{
			Vector2 current = queue.Dequeue();
			for (int dx = -1; dx <= 1; dx++)
			{
				for (int dy = -1; dy <= 1; dy++)
				{
					if (dx == 0 && dy == 0)
					{
						continue;
					}
					Vector2 next = new Vector2(current.X + (float)dx, current.Y + (float)dy);
					if (pending.Contains(next) && component.Add(next))
					{
						queue.Enqueue(next);
						if (component.Count > maxArea)
						{
							return false;
						}
					}
				}
			}
		}
		if (component.Count <= 1)
		{
			return false;
		}
		Dictionary<Vector2, int> slots = new Dictionary<Vector2, int>();
		foreach (Vector2 member in component)
		{
			int slot = Tiles.IndexOf(member);
			if (slot >= Index)
			{
				slots[member] = slot;
			}
		}
		if (slots.Count == 0)
		{
			return false;
		}
		int bestPower = int.MaxValue;
		int bestDist = int.MaxValue;
		bool bestOutside = false;
		for (int p = 0; p <= maxCharge; p++)
		{
			for (int f = 0; f < 4; f++)
			{
				foreach (Vector2 anchor in slots.Keys)
				{
					List<Vector2>? affected = AffectedTilesFor(tool, anchor, p, player, f);
					if (affected == null || affected.Count < component.Count)
					{
						continue;
					}
					HashSet<Vector2> area = new HashSet<Vector2>(affected);
					bool covers = true;
					foreach (Vector2 member in component)
					{
						if (!area.Contains(member))
						{
							covers = false;
							break;
						}
					}
					if (!covers)
					{
						continue;
					}
					bool spills = false;
					foreach (Vector2 tile in affected)
					{
						if (!Planned.ContainsKey(tile) && TileUtil.LooksWorkable(location, tile, Mod.Config, (Item)tool))
						{
							spills = true;
							break;
						}
					}
					if (spills)
					{
						continue;
					}
					Vector2 candidateStand = anchor + StandOffset(f);
					if (!TileUtil.IsStandable(location, candidateStand))
					{
						continue;
					}
					Vector2 playerTile = ((Character)player).Tile;
					int distance = (int)(MathF.Abs(candidateStand.X - playerTile.X) + MathF.Abs(candidateStand.Y - playerTile.Y));
					bool outside = !Planned.ContainsKey(candidateStand);
					bool better = p < bestPower || (p == bestPower && outside && !bestOutside) || (p == bestPower && outside == bestOutside && distance < bestDist);
					if (!better)
					{
						continue;
					}
					bestPower = p;
					bestDist = distance;
					bestOutside = outside;
					index = slots[anchor];
					facing = f;
					stand = candidateStand;
					power = p;
				}
			}
			if (index >= 0)
			{
				break;
			}
		}
		if (index < 0)
		{
			return false;
		}
		return true;
	}

	private List<Vector2>? AffectedTilesFor(Tool tool, Vector2 tile, int power, Farmer player, int facing)
	{
		int oldFacing = ((Character)player).FacingDirection;
		try
		{
			((Character)player).FacingDirection = facing;
			return GetAffectedTiles(tool, tile, power, player);
		}
		finally
		{
			((Character)player).FacingDirection = oldFacing;
		}
	}


	private void SkipCurrent(Vector2 tile)
	{
		Skipped++;
		ClearCurrentState(tile);
	}

	private void ClearCurrentState(Vector2 tile)
	{
		Current = null;
		WalkAttempts = 0;
		List<Vector2> activeSwingCovers = ActiveSwingCovers;
		if (activeSwingCovers != null)
		{
			GameLocation location = Location;
			if (location != null)
			{
				ActiveSwingCovers = null;
				foreach (Vector2 item in activeSwingCovers)
				{
					if (!(item == tile) && TileUtil.WouldTill(location, item) && !Tiles.Contains(item))
					{
						Tiles.Insert(Math.Min(Index, Tiles.Count), item);
					}
				}
			}
		}
		PendingPower = null;
		PreferredFacing = null;
		PreferredStand = null;
	}

	private bool RetryTimedOutTile(Vector2 tile, string why = "")
	{
		if (Refilling)
		{
			return false;
		}
		if (!RetriedTiles.Add(tile))
		{
			return false;
		}
		ClearCurrentState(tile);
		int num = Tiles.IndexOf(tile);
		if (num >= 0)
		{
			Tiles.RemoveAt(num);
			if (num < Index)
			{
				Index--;
			}
		}
		int num2 = FindRetrySlot(tile);
		Tiles.Insert(num2, tile);
		return true;
	}

	private int FindRetrySlot(Vector2 tile)
	{
		int count = Tiles.Count;
		int index = Index;
		if (index >= count)
		{
			return count;
		}
		GameLocation val = Location;
		if (val == null)
		{
			Farmer player = Game1.player;
			val = ((player != null) ? ((Character)player).currentLocation : null);
		}
		if (val == null)
		{
			return count;
		}
		int[] array = TileUtil.BuildDistanceField(val, tile, out var width, out var height);
		if (array == null)
		{
			return count;
		}
		int num = -1;
		int num2 = int.MaxValue;
		int num3 = -1;
		for (int i = index; i < count; i++)
		{
			int num4 = TileUtil.ServeDistance(array, width, height, Tiles[i]);
			if (num4 < 1000000)
			{
				if (num4 < num2)
				{
					num2 = num4;
					num = i;
				}
				if (num4 <= 3)
				{
					num3 = i;
				}
			}
		}
		int slot;
		if (num3 >= 0)
		{
			slot = num3 + 1;
		}
		else if (num >= 0)
		{
			slot = num + 1;
		}
		else
		{
			slot = count;
		}
		// 作物批 / 剩余批分段：重试格不能插到另一段里去，
		// 否则 S 序中间会冒出杂草格、剩余批里会冒出作物格。
		if (ScytheCropRoute)
		{
			int boundary = count;
			for (int i = index; i < count; i++)
			{
				if (!IsScytheCropTile(Tiles[i]))
				{
					boundary = i;
					break;
				}
			}
			if (IsScytheCropTile(tile) ? (slot > boundary) : (slot < boundary))
			{
				slot = boundary;
			}
		}
		return slot;
	}

	private int FindOrderSlot(Vector2 tile)
	{
		int count = Tiles.Count;
		if (Index >= count)
		{
			return count;
		}
		GameLocation val = Location;
		if (val == null)
		{
			Farmer player = Game1.player;
			val = ((player != null) ? ((Character)player).currentLocation : null);
		}
		if (val == null || StartAnchor == Vector2.Zero)
		{
			return count;
		}
		int[] array = TileUtil.BuildDistanceField(val, StartAnchor, out var width, out var height);
		if (array == null)
		{
			return count;
		}
		int num = TileUtil.ServeDistance(array, width, height, tile);
		if (num >= 1000000)
		{
			return count;
		}
		for (int i = Index; i < count; i++)
		{
			int num2 = TileUtil.ServeDistance(array, width, height, Tiles[i]);
			if (num2 < 1000000 && num2 > num)
			{
				return i;
			}
		}
		return count;
	}

	private bool GiveUpCurrent(Vector2 tile)
	{
		if (!Refilling)
		{
			UnreachableTiles.Add(tile);
			TileAttempts.TryGetValue(tile, out var value);
			TileAttempts[tile] = value + 1;
			SkipCurrent(tile);
			return false;
		}
		Stop(I18n.T("stop-refill-failed"));
		return true;
	}

	private bool NeedsWalk(Farmer player, Vector2 tile, Item? item)
	{
		GameLocation? location = ((Character)player).currentLocation;
		FishPond? pond = ((location != null) ? TileUtil.GetPondWithOutputAt(location, tile) : null);
		if (pond != null)
		{
			return PondNeedsWalk(player, pond);
		}
		MeleeWeapon val = (MeleeWeapon)((item is MeleeWeapon) ? item : null);
		if (val != null && TileUtil.IsScythe(item))
		{
			if (ShouldHandPick(tile))
			{
				// 手摘不是挥镰刀：必须贴身，不能让 2 格挥击范围把机器/作物隔着实休收掉。
				return TileUtil.ChebyshevDistance(((Character)player).Tile, tile) > 1f;
			}
			// 「站这儿够不够得着」必须按玩家此刻的真实碰撞盒问（ScytheCanHit 的 Farmer 版）。
			// 站位版（ScytheCanHit(stand)）假设玩家像素贴在格左上角(+8)，而 WalkDriver 停稳后
			// 玩家常在格中间(+24)，两者差 16px——正好差出边缘那一格：
			// 实机 13:15 日志：站 (47,44) 判「够得着 (45,44)」→ 不走路 → 实际四朝向都打不到，
			// 原地换站位重试两次（重试也没走，因为这里还是判够得着）→ 放弃，格子留下、气泡消失。
			return !ScytheCanHit(val, player, tile);
		}
		if ((item is Hoe || item is WateringCan) && location != null && Current.HasValue && Current.Value == tile)
		{
			// 游戏是在挥动动画结束那一刻才用 GetToolLocation() 定格子的；鼠标隐没或手柄时
			// 它不采纳我们写的 lastClick，只按朝向取「前方 48px」那格——斜着站就必然挥到
			// 正交那格去（实机日志：256 次挥动里 143 次是对角站位）。所以锄/浇不管这一挥
			// 有没有蓄力方案，都先站到正交相邻格再挥。
			// 瞄准格可能比队首挪了一格（选框边缘把带子挪进框里），站位要围着瞄准格算。
			Vector2 aim = SwingAim ?? tile;
			Vector2 current = ((Character)player).Tile;
			if (current == aim)
			{
				return !(item is WateringCan);
			}
			if (TileUtil.ChebyshevDistance(current, aim) <= 1f && (current.X == aim.X || current.Y == aim.Y))
			{
				// 游戏不采纳点击时，蓄力区域是按「目标格 + 朝向」铺出去的：站在另一侧挥，
				// 整片会偏一格。所以先站到计划的那个正交站位上。
				return PreferredStand.HasValue && !Game1.wasMouseVisibleThisFrame && PreferredStand.Value != current;
			}
			// 太远必须走过去（距离 2 也算太远）；斜着一格时鼠标可见可以不动，光标隐没就得走位。
			return TileUtil.ChebyshevDistance(current, aim) > 1f || !Game1.wasMouseVisibleThisFrame;
		}
		if (((Character)player).Tile == tile)
		{
			if (item is WateringCan)
			{
				return false;
			}
			if (!TileUtil.IsSowSeed(item))
			{
				return true;
			}
			return TileUtil.IsRaisedCropSeed(item, ((Character)player).currentLocation);
		}
		if (item is Axe || item is Pickaxe)
		{
			// 斧/镐斜着一格挥会空：鼠标隐没时游戏按朝向取「前方那一格」，
			// 斜着站必然打到旁边那格（实机反馈：砍树、挖矿时不时原地空挥、卡住）。
			// 先站到正交相邻格再挥；鼠标可见时点击会被采纳，斜着也能打到，不用多走。
			Vector2 axeTile = ((Character)player).Tile;
			if (TileUtil.ChebyshevDistance(axeTile, tile) > 1f)
			{
				return true;
			}
			return axeTile.X != tile.X && axeTile.Y != tile.Y && !Game1.wasMouseVisibleThisFrame;
		}
		return TileUtil.ChebyshevDistance(((Character)player).Tile, tile) > 1f;
	}

	private static bool PondNeedsWalk(Farmer player, FishPond pond)
	{
		Point tile = ((Character)player).TilePoint;
		int left = pond.tileX.Value;
		int top = pond.tileY.Value;
		int right = left + pond.tilesWide.Value - 1;
		int bottom = top + pond.tilesHigh.Value - 1;
		int dx = Math.Max(0, Math.Max(left - tile.X, tile.X - right));
		int dy = Math.Max(0, Math.Max(top - tile.Y, tile.Y - bottom));
		return Math.Max(dx, dy) > 1;
	}

	private static IEnumerable<Point> PondPerimeter(FishPond pond)
	{
		int left = pond.tileX.Value;
		int top = pond.tileY.Value;
		int wide = pond.tilesWide.Value;
		int high = pond.tilesHigh.Value;
		for (int x = left - 1; x <= left + wide; x++)
		{
			yield return new Point(x, top - 1);
			yield return new Point(x, top + high);
		}
		for (int y = top; y < top + high; y++)
		{
			yield return new Point(left - 1, y);
			yield return new Point(left + wide, y);
		}
	}

	private bool ScytheShouldSeekBetterStand(Farmer player, Vector2 tile)
	{
		List<Point> list = FindApproachTiles(player, StartItem, tile);
		if (list.Count == 0)
		{
			return false;
		}
		Vector2 val = default(Vector2);
		val = new Vector2((float)list[0].X, (float)list[0].Y);
		if (val == ((Character)player).Tile || TileUtil.ChebyshevDistance(val, ((Character)player).Tile) > 3f)
		{
			return false;
		}
		return CountScytheCoverage(player, val) >= CountScytheCoverage(player, ((Character)player).Tile) + 2;
	}

	/// <summary>
	/// 这一格是不是「行尾拐角」；是的话，返回人应该站的格子（下一行那一侧）。
	/// 只有在实心摆放（<see cref="SealPlacedTiles"/>）时才算：种子/锄地/浇水不会把自己围死。
	/// 判据：下一格在相邻行（±1 行）且同一列（±1 列），那就是 S 的拐角。
	/// 站在这一侧摆，这一行就成了完整的墙，人被关在外面、下一行在墙里侧 ——
	/// 先穿过空着的那一格站到下一行，再回手补这一格，就不用绕整块地。
	/// </summary>
	private Vector2? GateStandFor(Vector2 tile)
	{
		if (!SealPlacedTiles || Index < 0 || Index + 1 >= Tiles.Count)
		{
			return null;
		}
		if (Tiles[Index] != tile)
		{
			return null;
		}
		Vector2 next = Tiles[Index + 1];
		float dy = next.Y - tile.Y;
		if (MathF.Abs(dy) < 0.5f || MathF.Abs(dy) > 1.5f)
		{
			return null;
		}
		if (MathF.Abs(next.X - tile.X) > 1f)
		{
			return null;
		}
		return new Vector2(tile.X, tile.Y + ((dy > 0f) ? 1f : (-1f)));
	}

	private bool TryStartWalking(Farmer player, Item? item, Vector2 tile)
	{
		// 挥动动画还在播时不许起步：动画被走路的输入打断后 PauseForSingleAnimation 可能再也不会回零，
		// 人就会一直 CanMove=False、方向键推不动（实机：连续不动 155 帧）。
		if (player.UsingTool || player.FarmerSprite.PauseForSingleAnimation)
		{
			WalkStartFailure = "挥动动画未结束";
			return false;
		}
		WalkStartFailure = "";
		List<Point> list = FindApproachTiles(player, item, tile);
		// 行尾留门：这一格是这一行的最后一格、下一格已经在隔壁行时，站位必须选「下一行那一侧」。
		// 站在这一侧摆，等于把这一行封成一堵完整的墙，人被关在外面，下一行在墙里侧 ——
		// 摆完只能绕整块地一圈（实机 2026-11-02：35 格宽的桶行，换行寻路 57 格）。
		// 从空着的那一格先穿过去站好，再把最后一格回手补上。
		Vector2? gateStand = GateStandFor(tile);
		if (gateStand.HasValue)
		{
			Point gatePoint = new Point((int)gateStand.Value.X, (int)gateStand.Value.Y);
			int gateAt = list.IndexOf(gatePoint);
			if (gateAt > 0)
			{
				list.RemoveAt(gateAt);
				list.Insert(0, gatePoint);
			}
		}
		// 有计划站位就先试它：瞄准格可能和队首差一格，FindApproachTiles() 是围着队首算的，
		// 走过去的站位可能挨不着瞄准格，于是 NeedsWalk 一直说“要再走”，来回抽。
		if (PreferredStand.HasValue && !gateStand.HasValue)
		{
			Point preferred = new Point((int)PreferredStand.Value.X, (int)PreferredStand.Value.Y);
			GameLocation? standLocation = ((Character)player).currentLocation;
			if (standLocation != null && preferred != ((Character)player).TilePoint && TileUtil.IsStandable(standLocation, PreferredStand.Value) && Mod.Walk.TryStart(standLocation, player, preferred))
			{
				WalkDestination = preferred;
				Walking = true;
				WalkingByDriver = true;
				WalkTarget = tile;
				WalkStartFailure = "";
				ResetWalkClocks(player);
				return true;
			}
		}
		if (list.Count == 0)
		{
			WalkStartFailure = "没有可站的邻格（FindApproachTiles 为空）";
			return false;
		}
		foreach (Point item2 in list)
		{
			if (!(item2 == ((Character)player).TilePoint))
			{
				if (Mod.Walk.TryStart(((Character)player).currentLocation, player, item2))
				{
					WalkDestination = item2;
					Walking = true;
					WalkingByDriver = true;
					WalkTarget = tile;
					WalkStartFailure = "";
					ResetWalkClocks(player);
					return true;
				}
				WalkStartFailure = Mod.Walk.LastFailReason ?? "模拟走路不可用";
			}
		}
		if (Mod.Walk.IsAvailable)
		{
			WalkStartFailure = (string.IsNullOrEmpty(WalkStartFailure) ? $"有 {list.Count} 个候选站位，但都寻不到路（findPath 返回空）" : $"有 {list.Count} 个候选站位，但都走不通：{WalkStartFailure}");
			return false;
		}
		WalkStartFailure = "反射拿不到 Game1.inputSimulator，退回原版寻路";
		try
		{
			((Character)player).controller = new PathFindController((Character)player, ((Character)player).currentLocation, list[0], -1);
			WalkDestination = list[0];
			Walking = true;
			WalkingByDriver = false;
			WalkTarget = tile;
			ResetWalkClocks(player);
			return true;
		}
		catch
		{
			((Character)player).controller = null;
			return false;
		}
	}

	/// <summary>
	/// 走不到目标时找一扇关着的围栏门去推开。
	/// 原版寻路把关着的门当墙（<see cref="Fence"/> 的 isPassable() 只认 gatePosition >= 88），
	/// 而游戏自己的 PathFindController 是在「移动」阶段顺手开门，我们只借了它的搜索、自己按键走路，
	/// 所以门后的地永远搜不到路 —— 必须人到门口 checkForAction 把门推开。
	/// 门一开 isPassable() 返回 true，寻路立刻能穿过去；toggleGate 还会把该格塞进
	/// TemporaryPassableTiles，人走过去时碰撞也不挡。
	/// </summary>
	private bool TryDetourToGate(Farmer player)
	{
		GameLocation? location = Location ?? ((Character)player).currentLocation;
		if (location == null || GatesTried.Count >= MaxGatesTried)
		{
			return false;
		}
		Vector2 fromTile = ((Character)player).Tile;
		Fence? bestGate = null;
		Vector2 bestGateTile = Vector2.Zero;
		float bestDist = 24f;
		try
		{
			foreach (KeyValuePair<Vector2, Object> pair in location.objects.Pairs)
			{
				if (!(pair.Value is Fence gate) || !gate.isGate.Value || gate.isPassable() || GatesTried.Contains(pair.Key))
				{
					continue;
				}
				// 只认「这一趟干活的范围内、离人不远」的门：满农场的门全去推一遍就成了乱开门。
				Rectangle? box = SelectionBox;
				if (box.HasValue && !box.Value.Contains((int)pair.Key.X, (int)pair.Key.Y) && TileUtil.ChebyshevDistance(fromTile, pair.Key) > 12f)
				{
					continue;
				}
				float dist = TileUtil.ChebyshevDistance(fromTile, pair.Key);
				if (dist < bestDist)
				{
					bestDist = dist;
					bestGate = gate;
					bestGateTile = pair.Key;
				}
			}
		}
		catch
		{
			return false;
		}
		if (bestGate == null)
		{
			return false;
		}
		// 门边站位：地形可站就行（角色/动物站那儿不算「没地方站」，它们能被顶开）。
		int[] dx = { 0, 0, -1, 1, -1, 1, -1, 1 };
		int[] dy = { -1, 1, 0, 0, -1, -1, 1, 1 };
		Point bestStand = Point.Zero;
		float bestStandDist = float.MaxValue;
		for (int i = 0; i < dx.Length; i++)
		{
			Point stand = new Point((int)bestGateTile.X + dx[i], (int)bestGateTile.Y + dy[i]);
			Vector2 standTile = new Vector2(stand.X, stand.Y);
			if (!TileUtil.IsStandableIgnoringCritters(location, standTile))
			{
				continue;
			}
			float d = TileUtil.ChebyshevDistance(fromTile, standTile);
			if (d < bestStandDist)
			{
				bestStandDist = d;
				bestStand = stand;
			}
		}
		if (bestStand == Point.Zero)
		{
			GatesTried.Add(bestGateTile);
			return false;
		}
		bool alreadyThere = bestStand == ((Character)player).TilePoint;
		if (!alreadyThere && !Mod.Walk.TryStart(location, player, bestStand))
		{
			// 门边也走不过去（隔着一堵墙）：这扇门记下、换下一扇，别在这儿反复试。
			GatesTried.Add(bestGateTile);
			return false;
		}
		GateTarget = bestGate;
		GateTile = bestGateTile;
		GateStand = bestStand;
		GateTicks = 0;
		// 人已经站在门口（常见：走过去就被门挡住）：不用再走，直接进入推门阶段。
		GatePhase = (alreadyThere ? 2 : 1);
		((Mod)Mod).Monitor.Log($"走不到，改去开围栏门 ({bestGateTile.X}, {bestGateTile.Y})：门口站位 ({bestStand.X}, {bestStand.Y}){(alreadyThere ? "（已在门口）" : "")}", LogLevel.Trace);
		return true;
	}

	/// <summary>开门状态机：走到门边 → 推门 → 清掉记忆，下一 tick 重新规划。</summary>
	private void TickGate(Farmer player)
	{
		if (GateTarget == null)
		{
			GatePhase = 0;
			return;
		}
		GateTicks++;
		if (GatePhase == 1)
		{
			Vector2 stand = new Vector2(GateStand.X, GateStand.Y);
			float dist = TileUtil.ChebyshevDistance(((Character)player).Tile, stand);
			bool arrived = ((Character)player).TilePoint == GateStand || dist < 0.5f;
			// 走路自己收工了但人已经在门口那一圈（差半格）：也算到了，别为了半格再等 90 tick。
			if (arrived || (!Mod.Walk.IsWalking && dist <= 1.5f))
			{
				Mod.Walk.Stop();
				((Character)player).controller = null;
				GatePhase = 2;
				GateTicks = 0;
				return;
			}
			if (GateTicks > GateWalkTimeoutTicks || !Mod.Walk.IsWalking)
			{
				((Mod)Mod).Monitor.Log($"去开门的路上没走成 ({GateTile.X}, {GateTile.Y})，放弃这扇门", LogLevel.Trace);
				GatesTried.Add(GateTile);
				ResetGate();
				WalkFailCooldownTicks = WalkFailCooldown;
			}
			return;
		}
		// 站在门口别立刻推：走路输入刚松开那几帧方向键还压着，先站稳。
		if (GateTicks < 6)
		{
			return;
		}
		try
		{
			GateTarget.checkForAction(player, false);
		}
		catch
		{
		}
		bool opened = GateTarget.isPassable();
		if (!opened)
		{
			GatesTried.Add(GateTile);
		}
		((Mod)Mod).Monitor.Log(opened ? $"围栏门 ({GateTile.X}, {GateTile.Y}) 已推开，重新规划路线" : $"围栏门 ({GateTile.X}, {GateTile.Y}) 推不开（可能是双格门对不上），换下一扇", LogLevel.Trace);
		ResetGate();
		// 地形通行表缓存里这扇门还是「挡」，必须作废，否则下一步规划还按老地形算。
		TileUtil.InvalidateStandableCache();
		TileUtil.InvalidateTerrainCache();
		// 门开了就等于整片区域重新可达：之前的失败记录和冷却一起清掉，马上重试。
		WalkAttempts = 0;
		WalkFailCooldownTicks = 0;
		PreferredStand = null;
	}

	private void ResetGate()
	{
		GateTarget = null;
		GateTile = Vector2.Zero;
		GateStand = Point.Zero;
		GatePhase = 0;
		GateTicks = 0;
	}

	private void ResetWalkClocks(Farmer player)
	{
		WalkTicks = 0;
		WalkTotalTicks = 0;
		ControllerGoneTicks = 0;
		WalkLastTile = ((Character)player).Tile;
		CoverageCheckTile = new Vector2(float.MinValue, float.MinValue);
		CoverageCheckStand = new Vector2(float.MinValue, float.MinValue);
	}

	private List<Point> FindApproachTiles(Farmer player, Item? item, Vector2 tile)
	{
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation == null)
		{
			return new List<Point>();
		}
		if (Plan == null && TileUtil.IsScythe(item))
		{
			MeleeWeapon val = (MeleeWeapon)((item is MeleeWeapon) ? item : null);
			if (val != null)
			{
				List<(Point, int, float)> list = new List<(Point, int, float)>();
				Vector2 val2 = default(Vector2);
				foreach (Point item2 in FindApproachTilesRaw(player, item, tile))
				{
					val2 = new Vector2((float)item2.X, (float)item2.Y);
					// 这一格在这个站位上已经试过打不到（四朝向都够不着）：别再走过去。
					if (ScytheBadStands.TryGetValue(tile, out HashSet<Vector2>? badStands) && badStands.Contains(val2))
					{
						continue;
					}
					// 站位必须真能打到这一格：覆盖率再高，打不到目标也是白走一趟。
					// 实机 2026-11-02：站 (47,44) 一刀覆盖 14 格，却构不到 (45,45)，
					// 四个朝向都失败后整格被放弃（气泡随之消失，作物还在地里）。
					if (!ScytheCanHit(val, val2, tile))
					{
						continue;
					}
					list.Add((item2, CountScytheCoverage(player, val2), Vector2.DistanceSquared(val2, ((Character)player).Tile)));
				}
				if (list.Count > 0)
				{
					list.Sort(delegate((Point Spot, int Cover, float Distance) a, (Point Spot, int Cover, float Distance) b)
					{
						int num = b.Cover.CompareTo(a.Cover);
						if (num != 0)
						{
							return num;
						}
						int num2 = a.Distance.CompareTo(b.Distance);
						if (num2 != 0)
						{
							return num2;
						}
						int num3 = a.Spot.Y.CompareTo(b.Spot.Y);
						return (num3 == 0) ? a.Spot.X.CompareTo(b.Spot.X) : num3;
					});
					return list.Select<(Point, int, float), Point>(delegate((Point Spot, int Cover, float Distance) s)
					{
						return s.Spot;
					}).ToList();
				}
			}
		}
		return FindApproachTilesRaw(player, item, tile);
	}

	private List<Point> FindApproachTilesRaw(Farmer player, Item? item, Vector2 tile)
	{
		List<Point> list = FindApproachTilesRawCore(player, item, tile);
		GameLocation? location = ((Character)player).currentLocation;
		if ((item is Hoe || item is WateringCan) && PreferredStand.HasValue && location != null && Current.HasValue && Current.Value == tile)
		{
			// 计划好的站位放最前面先试；走不到时还能退回原来的候选格，不会因为站位被占就卡死。
			Vector2 preferred = PreferredStand.Value;
			if (TileUtil.ChebyshevDistance(preferred, tile) <= 1f && (preferred == ((Character)player).Tile || TileUtil.IsStandable(location, preferred)))
			{
				Point point = new Point((int)preferred.X, (int)preferred.Y);
				list.RemoveAll(delegate(Point p)
				{
					return p == point;
				});
				list.Insert(0, point);
			}
		}
		return list;
	}

	private List<Point> FindApproachTilesRawCore(Farmer player, Item? item, Vector2 tile)
	{
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation == null)
		{
			return new List<Point>();
		}
		FishPond? pond = TileUtil.GetPondWithOutputAt(currentLocation, tile);
		if (pond != null)
		{
			return PondPerimeter(pond).Where(delegate(Point p)
			{
				Vector2 spot = new Vector2((float)p.X, (float)p.Y);
				return spot != ((Character)player).Tile && TileUtil.IsStandable(currentLocation, spot);
			}).OrderBy(delegate(Point p)
			{
				return TileUtil.ChebyshevDistance(new Vector2((float)p.X, (float)p.Y), ((Character)player).Tile);
			}).ThenBy(delegate(Point p)
			{
				return p.Y;
			}).ThenBy(delegate(Point p)
			{
				return p.X;
			}).ToList();
		}
		Vector2[] source = (Vector2[])(object)new Vector2[8]
		{
			new Vector2(1f, 0f),
			new Vector2(-1f, 0f),
			new Vector2(0f, 1f),
			new Vector2(0f, -1f),
			new Vector2(1f, 1f),
			new Vector2(-1f, 1f),
			new Vector2(1f, -1f),
			new Vector2(-1f, -1f)
		};
		source = (TileUtil.IsScythe(item) ? source.OrderBy(delegate(Vector2 o)
		{
			return TileUtil.ChebyshevDistance(tile + o, ((Character)player).Tile);
		}).ThenBy(delegate(Vector2 o)
		{
			return tile.Y + o.Y;
		}).ThenBy(delegate(Vector2 o)
		{
			return tile.X + o.X;
		})
			.ToArray() : source.OrderBy(delegate(Vector2 o)
		{
			return TileUtil.ChebyshevDistance(tile + o, ((Character)player).Tile);
		}).ToArray());
		List<(Vector2 Spot, int Gain, float Distance)> list = new List<(Vector2 Spot, int Gain, float Distance)>();
		Vector2[] array = source;
		Vector2[] array2 = array;
		foreach (Vector2 val in array2)
		{
			Vector2 val2 = tile + val;
			if (val2 == ((Character)player).Tile)
			{
				continue;
			}
			// 农场动物能推开（farmerPassesThrough），它站着的那格照样算能站。旧版用 IsStandable
			// （只去掉 Farmers）→ 畜棚里牛占着站位就「放不进去」，只能换格重试，看着像来回乱跑。
			// 实心角色（村民/宠物/马）仍算挡；它们的候选排在最后。
			bool freeStand = TileUtil.IsStandable(currentLocation, val2);
			if (!freeStand && !TileUtil.IsStandableIgnoringCritters(currentLocation, val2))
			{
				continue;
			}
			if (!(item is Tool) && PlacingMode)
			{
				int num2 = 0;
				for (int num3 = Index; num3 < Tiles.Count; num3++)
				{
					if (TileUtil.ChebyshevDistance(Tiles[num3], val2) <= 1f)
					{
						num2++;
					}
				}
				int num4 = int.MaxValue;
				for (int num5 = Index; num5 < Tiles.Count; num5++)
				{
					if (TileUtil.ChebyshevDistance(Tiles[num5], val2) <= 1f && num5 < num4)
					{
						num4 = num5;
					}
				}
				float standDistance = ((num4 == int.MaxValue) ? 100000f : ((float)num4));
				// 摆放时优先站到框内还没摆的格子上，别站到框外的过道：站外过道会把上下两条过道隔开，
				// 这一排摆完就够不到下一排（实机 16×2 的框：上排摆完，下排 15 格 findPath 全空）。
				// 站在未摆的格子上还能继续往框里走，把「从近侧进、从远侧出」的通道留出来。
				if (Planned.ContainsKey(val2) && !TileDone.Contains(val2))
				{
					standDistance -= 1000000f;
				}
				list.Add((val2, num2, standDistance));
			}
			else if (TileUtil.IsScythe(item))
			{
				int item2 = ((!ShouldHandPick(tile)) ? CountScytheCoverage(player, val2) : 0);
				list.Add((val2, item2, Vector2.DistanceSquared(val2, ((Character)player).Tile) + (freeStand ? 0f : 1000000f)));
			}
			else
			{
				Tool val3 = (Tool)((item is Tool) ? item : null);
				int item3 = ((val3 != null && !TileUtil.IsScythe(item)) ? PlanAt(player, val3, tile, TileUtil.GetFacingDirection(val2, tile)).Newly : 0);
				list.Add((val2, item3, Vector2.DistanceSquared(val2, ((Character)player).Tile) + (freeStand ? 0f : 1000000f)));
			}
		}
		if (list.Count == 0)
		{
			return new List<Point>();
		}
		if (!(item is Tool) && PlacingMode)
		{
			(Vector2 Spot, int Gain, float Distance) placePick = (from c in list
				orderby c.Distance, c.Gain descending
				select c).ThenBy<(Vector2 Spot, int Gain, float Distance), float>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return c.Spot.Y;
			}).ThenBy<(Vector2 Spot, int Gain, float Distance), float>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return c.Spot.X;
			}).First();
			List<Point> list2 = new List<Point>
			{
				new Point((int)placePick.Spot.X, (int)placePick.Spot.Y)
			};
			foreach (var item4 in (from c in list.Where<(Vector2 Spot, int Gain, float Distance)>(delegate((Vector2 Spot, int Gain, float Distance) c)
				{
					return c.Spot != placePick.Spot;
				})
				orderby c.Distance, c.Gain descending
				select c).ThenBy<(Vector2 Spot, int Gain, float Distance), float>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return c.Spot.Y;
			}).ThenBy<(Vector2 Spot, int Gain, float Distance), float>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return c.Spot.X;
			}))
			{
				list2.Add(new Point((int)item4.Item1.X, (int)item4.Item1.Y));
			}
			return list2;
		}
		bool flag = TileUtil.IsScythe(item) && ShouldHandPick(tile);
		if (TileUtil.IsScythe(item) && !flag)
		{
			MeleeWeapon approachScythe = (MeleeWeapon)((item is MeleeWeapon) ? item : null);
			if (approachScythe != null)
			{
				List<(Vector2 Spot, int Gain, float Distance)> list3 = list.Where<(Vector2 Spot, int Gain, float Distance)>(delegate((Vector2 Spot, int Gain, float Distance) c)
				{
					return ScytheCanHit(approachScythe, c.Spot, tile);
				}).ToList();
				if (list3.Count > 0)
				{
					list = list3;
				}
			}
		}
		if (Plan != null && TileUtil.IsScythe(item))
		{
			return list.OrderBy(c => c.Distance).ThenBy(c => c.Spot.Y).ThenBy(c => c.Spot.X).Select(c => new Point((int)c.Spot.X, (int)c.Spot.Y)).ToList();
		}
		float minDistance = list.Min<(Vector2 Spot, int Gain, float Distance)>(((Vector2 Spot, int Gain, float Distance) c) => c.Distance);
		if (TileUtil.IsScythe(item))
		{
			float minCheb = list.Min<(Vector2 Spot, int Gain, float Distance)>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return TileUtil.ChebyshevDistance(c.Spot, ((Character)player).Tile);
			});
			int nearBest = list.Where<(Vector2 Spot, int Gain, float Distance)>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return TileUtil.ChebyshevDistance(c.Spot, ((Character)player).Tile) <= minCheb;
			}).Max<(Vector2 Spot, int Gain, float Distance)>(((Vector2 Spot, int Gain, float Distance) c) => c.Gain);
			(Vector2 Spot, int Gain, float Distance) nearest = (from c in list.Where<(Vector2 Spot, int Gain, float Distance)>(delegate((Vector2 Spot, int Gain, float Distance) c)
				{
					return TileUtil.ChebyshevDistance(c.Spot, ((Character)player).Tile) <= minCheb || c.Gain >= nearBest + 2;
				})
				orderby c.Gain descending, c.Distance
				select c).ThenBy<(Vector2 Spot, int Gain, float Distance), float>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return c.Spot.Y;
			}).ThenBy<(Vector2 Spot, int Gain, float Distance), float>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return c.Spot.X;
			}).First();
			List<Point> list4 = new List<Point>
			{
				new Point((int)nearest.Spot.X, (int)nearest.Spot.Y)
			};
			foreach (var item5 in (from c in list.Where<(Vector2 Spot, int Gain, float Distance)>(delegate((Vector2 Spot, int Gain, float Distance) c)
				{
					return c.Spot != nearest.Spot;
				})
				orderby c.Distance
				select c).ThenBy<(Vector2 Spot, int Gain, float Distance), float>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return c.Spot.Y;
			}).ThenBy<(Vector2 Spot, int Gain, float Distance), float>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return c.Spot.X;
			}))
			{
				list4.Add(new Point((int)item5.Item1.X, (int)item5.Item1.Y));
			}
			return list4;
		}
		(Vector2 Spot, int Gain, float Distance) pick = (from c in list
			where c.Distance <= minDistance + 1f
			orderby c.Gain descending, c.Distance
			select c).First();
		List<Point> list5 = new List<Point>
		{
			new Point((int)pick.Spot.X, (int)pick.Spot.Y)
		};
		foreach (var item6 in list.Where<(Vector2 Spot, int Gain, float Distance)>(delegate((Vector2 Spot, int Gain, float Distance) c)
		{
			return c.Spot != pick.Spot;
		}).OrderBy<(Vector2 Spot, int Gain, float Distance), int>(delegate((Vector2 Spot, int Gain, float Distance) c)
		{
			return (c.Spot.X != tile.X && c.Spot.Y != tile.Y) ? 1 : 0;
		}).ThenBy<(Vector2 Spot, int Gain, float Distance), float>(((Vector2 Spot, int Gain, float Distance) c) => c.Distance)
			.ThenBy<(Vector2 Spot, int Gain, float Distance), float>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return c.Spot.X;
			})
			.ThenBy<(Vector2 Spot, int Gain, float Distance), float>(delegate((Vector2 Spot, int Gain, float Distance) c)
			{
				return c.Spot.Y;
			}))
		{
			list5.Add(new Point((int)item6.Item1.X, (int)item6.Item1.Y));
		}
		return list5;
	}

	private Point? FindApproachTile(Farmer player, Item? item, Vector2 tile)
	{
		List<Point> list = FindApproachTiles(player, item, tile);
		if (list.Count != 0)
		{
			return list[0];
		}
		return null;
	}

	private static List<Vector2> ScytheSweepTiles(MeleeWeapon scythe, Vector2 stand, int facing)
	{
		return ScytheSweepTiles(scythe, stand, facing, 8);
	}

	/// <param name="pixelOffsetX">玩家碰撞盒相对格子左边的像素偏移：刚拐进格时贴 8，
	/// 站稳后居中 24（Farmer.GetBoundingBox 是 Position+8，宽 48）。</param>
	private static List<Vector2> ScytheSweepTiles(MeleeWeapon scythe, Vector2 stand, int facing, int pixelOffsetX)
	{
		Rectangle bbox = default(Rectangle);
		bbox = new Rectangle((int)stand.X * 64 + pixelOffsetX, (int)stand.Y * 64, 48, 32);
		Vector2 toolLocation = ToolLocationFromBbox(bbox, facing, targetOnSelfTile: false);
		return SweepSampleTiles(scythe, bbox, toolLocation, facing);
	}

	private static List<Vector2> ScytheSweepTiles(MeleeWeapon scythe, Farmer who, Vector2 target, int facing)
	{
		Rectangle boundingBox = ((Character)who).GetBoundingBox();
		bool targetOnSelfTile = (int)target.X == ((Character)who).TilePoint.X && (int)target.Y == ((Character)who).TilePoint.Y;
		Vector2 toolLocation = ToolLocationFromBbox(boundingBox, facing, targetOnSelfTile);
		return SweepSampleTiles(scythe, boundingBox, toolLocation, facing);
	}

	private static Vector2 ToolLocationFromBbox(Rectangle bbox, int facing, bool targetOnSelfTile)
	{
		int num = (targetOnSelfTile ? 64 : 48);
		Vector2 result = (Vector2)(facing switch
		{
			0 => new Vector2((float)(bbox.X + bbox.Width / 2), (float)(bbox.Y - num)), 
			1 => new Vector2((float)(bbox.X + bbox.Width + num), (float)(bbox.Y + bbox.Height / 2)), 
			2 => new Vector2((float)(bbox.X + bbox.Width / 2), (float)(bbox.Y + bbox.Height + num)), 
			_ => new Vector2((float)(bbox.X - num), (float)(bbox.Y + bbox.Height / 2)), 
		});
		return result;
	}

	private static List<Vector2> SweepSampleTiles(MeleeWeapon scythe, Rectangle bbox, Vector2 toolLocation, int facing)
	{
		List<Vector2> list = new List<Vector2>(16);
		for (int i = 0; i < 6; i++)
		{
			Vector2 zero = Vector2.Zero;
			Vector2 zero2 = Vector2.Zero;
			Rectangle areaOfEffect = scythe.getAreaOfEffect((int)toolLocation.X, (int)toolLocation.Y, facing, ref zero, ref zero2, bbox, i);
			foreach (Vector2 item in Utility.getListOfTileLocationsForBordersOfNonTileRectangle(areaOfEffect))
			{
				if (!list.Contains(item))
				{
					list.Add(item);
				}
			}
		}
		return list;
	}

	/// <summary>还没走过去时的估计：玩家停稳后像素位置介于「刚拐进格(+8)」和「居中(+24)」之间，
	/// 两种姿态任一能打到就算能打到。只按 +8 判会把边缘目标误判成够得着（真到那儿就打不到），
	/// 只按 +24 判又会把能打的站位筛掉、让人没地方站。</summary>
	private static bool ScytheCanHit(MeleeWeapon scythe, Vector2 stand, Vector2 target)
	{
		for (int pixelOffset = 8; pixelOffset <= 24; pixelOffset += 16)
		{
			for (int facing = 0; facing < 4; facing++)
			{
				if (ScytheSweepTiles(scythe, stand, facing, pixelOffset).Contains(target))
				{
					return true;
				}
			}
		}
		return false;
	}

	private static bool ScytheCanHit(MeleeWeapon scythe, Farmer who, Vector2 target)
	{
		for (int i = 0; i < 4; i++)
		{
			if (ScytheSweepTiles(scythe, who, target, i).Contains(target))
			{
				return true;
			}
		}
		return false;
	}

	private int CountScytheCoverage(Farmer player, Vector2 stand)
	{
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation != null)
		{
			Item startItem = StartItem;
			MeleeWeapon val = (MeleeWeapon)((startItem is MeleeWeapon) ? startItem : null);
			if (val != null)
			{
				HashSet<Vector2> hashSet = new HashSet<Vector2>();
				for (int i = 0; i < 4; i++)
				{
					foreach (Vector2 item in ScytheSweepTiles(val, stand, i))
					{
						// 作物批执行期间只数作物格：杂草/草也能挥，但把它们算进覆盖数
						// 会让找站位的逻辑偏离 S 路线去够杂草堆。
						if (IsSwingCollectibleWork(currentLocation, item) && (!ScytheCoverageCropsOnly || IsScytheCropTile(item)))
						{
							hashSet.Add(item);
						}
					}
				}
				return hashSet.Count;
			}
		}
		return 0;
	}

	private bool IsPendingScytheWork(GameLocation location, Vector2 cell)
	{
		if (TileUtil.GetTileWork(location, cell, StartItem, Mod.Config) == TileUtil.TileWork.None)
		{
			return false;
		}
		if (Tiles.Contains(cell) && !TileDone.Contains(cell))
		{
			if (TileAttempts.TryGetValue(cell, out var value))
			{
				return value < 3;
			}
			return true;
		}
		return false;
	}

	private bool IsSwingCollectibleWork(GameLocation location, Vector2 cell)
	{
		if (IsPendingScytheWork(location, cell))
		{
			return !ShouldHandPick(cell);
		}
		return false;
	}

	private int PickScytheDirection(Farmer player, Vector2 stand, Vector2 tile)
	{
		int facingDirection = TileUtil.GetFacingDirection(stand, tile);
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation == null)
		{
			return facingDirection;
		}
		List<int> list = new List<int>(4);
		int? lastScytheFacing = LastScytheFacing;
		if (lastScytheFacing.HasValue)
		{
			int valueOrDefault = lastScytheFacing.GetValueOrDefault();
			list.Add(valueOrDefault);
		}
		list.Add(facingDirection);
		int[] array = new int[4] { 0, 1, 2, 3 };
		int[] array2 = array;
		foreach (int item in array2)
		{
			if (!list.Contains(item))
			{
				list.Add(item);
			}
		}
		int result = facingDirection;
		int num = -1;
		Item startItem = StartItem;
		MeleeWeapon val = (MeleeWeapon)((startItem is MeleeWeapon) ? startItem : null);
		if (val == null)
		{
			return facingDirection;
		}
		foreach (int item2 in list)
		{
			HashSet<Vector2> hashSet = new HashSet<Vector2>();
			List<Vector2> list2 = ScytheSweepTiles(val, player, tile, item2);
			if ((TileUtil.CanWork(currentLocation, tile, Mod.Config, StartItem) || IsPendingScytheWork(currentLocation, tile)) && !list2.Contains(tile))
			{
				continue;
			}
			foreach (Vector2 item3 in list2)
			{
				if (IsSwingCollectibleWork(currentLocation, item3))
				{
					hashSet.Add(item3);
				}
			}
			if (hashSet.Count > num)
			{
				num = hashSet.Count;
				result = item2;
			}
		}
		return result;
	}

	private Vector2? FindWaterSource(Farmer player, Tool tool)
	{
		Rectangle? selectionBox = SelectionBox;
		if (!selectionBox.HasValue)
		{
			return null;
		}
		Rectangle valueOrDefault = selectionBox.GetValueOrDefault();
		(valueOrDefault).Inflate(10, 10);
		GameLocation location = Location;
		if (location == null)
		{
			return null;
		}
		Vector2 val = default(Vector2);
		int[] array = TileUtil.BuildDistanceField(location, ((Character)player).Tile, out var width, out var height);
		if (array != null)
		{
			Vector2? result = null;
			int num = int.MaxValue;
			for (int i = (valueOrDefault).Left; i < (valueOrDefault).Right; i++)
			{
				for (int j = (valueOrDefault).Top; j < (valueOrDefault).Bottom; j++)
				{
					val = new Vector2((float)i, (float)j);
					if (TileUtil.IsWaterSource(location, val))
					{
						int num2 = TileUtil.ServeDistance(array, width, height, val);
						if (num2 < num && num2 < 1000000)
						{
							num = num2;
							result = val;
						}
					}
				}
			}
			return result;
		}
		Vector2? result2 = null;
		float num3 = float.MaxValue;
		for (int k = (valueOrDefault).Left; k < (valueOrDefault).Right; k++)
		{
			for (int l = (valueOrDefault).Top; l < (valueOrDefault).Bottom; l++)
			{
				val = new Vector2((float)k, (float)l);
				if (TileUtil.IsWaterSource(location, val))
				{
					float num4 = Vector2.DistanceSquared(((Character)player).Tile, val);
					if (!(num4 >= num3) && (!NeedsWalk(player, val, (Item)tool) || FindApproachTile(player, (Item)tool, val).HasValue))
					{
						num3 = num4;
						result2 = val;
					}
				}
			}
		}
		return result2;
	}

	private void ApplyHoe(Farmer player, Tool tool, Vector2 tile)
	{
		int num = ResolveToolPower(player, tool, tile);
		TerrainFeature val = null;
		if (TileUtil.IsScythe((Item)tool) && Location != null && ((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)Location.terrainFeatures).TryGetValue(tile, out val))
		{
			Grass val2 = (Grass)((val is Grass) ? val : null);
			if (val2 != null)
			{
				GrassBefore[tile] = ((NetFieldBase<int, NetInt>)(object)val2.numberOfWeeds).Value;
			}
		}
		MeleeWeapon val3 = (MeleeWeapon)((tool is MeleeWeapon) ? tool : null);
		if (val3 != null && ((Tool)val3).isScythe())
		{
			BeginScytheSwing(player, val3, tile);
		}
		else
		{
			BeginToolUse(player, tool, tile, num);
		}
	}

	private void BeginScytheSwing(Farmer player, MeleeWeapon scythe, Vector2 tile)
	{
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation == null)
		{
			FailedSwings++;
			return;
		}
		int num = (((Character)player).FacingDirection = PickScytheDirection(player, ((Character)player).Tile, tile));
		int num3 = num;
		LastScytheFacing = num3;
		LastScytheTarget = tile;
		LastScytheStand = ((Character)player).Tile;
		((Character)player).lastClick = TileUtil.GetTileCenterPixel(tile);
		ActiveSwingCovers = ScytheSweepTiles(scythe, player, tile, num3);
		if (!ActiveSwingCovers.Contains(tile))
		{
			int? num4 = null;
			for (int i = 0; i < 4; i++)
			{
				if (ScytheSweepTiles(scythe, player, tile, i).Contains(tile))
				{
					num4 = i;
					break;
				}
			}
			if (!num4.HasValue)
			{
				Vector2 badStand = ((Character)player).Tile;
				if (!ScytheBadStands.TryGetValue(tile, out HashSet<Vector2>? tried))
				{
					tried = new HashSet<Vector2>();
					ScytheBadStands[tile] = tried;
				}
				tried.Add(badStand);
				ScytheStandRetries.TryGetValue(tile, out int standTries);
				if (standTries < 2 && Tiles.Contains(tile))
				{
					// 别直接判死：站错地方够不着，换个站位再试。这一格在 (badStand) 上
					// 挥不到，FindApproachTiles 下次会跳过它（实机 2026-11-02：站 (47,44)
					// 够不到 (45,45)/(45,44)，两格被放弃 → 气泡消失、作物留在地里）。
					ScytheStandRetries[tile] = standTries + 1;
					Tiles.Insert(Math.Min(Index, Tiles.Count), tile);
					((Character)player).lastClick = Vector2.Zero;
					return;
				}
				((Character)player).lastClick = Vector2.Zero;
				FailedSwings++;
				FailedTiles.Add(tile);
				return;
			}
			int valueOrDefault = num4.GetValueOrDefault();
			num = (((Character)player).FacingDirection = valueOrDefault);
			num3 = num;
			LastScytheFacing = num3;
			ActiveSwingCovers = ScytheSweepTiles(scythe, player, tile, num3);
		}
		bool flag = false;
		if (currentLocation != null)
		{
			GameLocation location = currentLocation;
			foreach (Vector2 activeSwingCover in ActiveSwingCovers)
			{
				if (TileUtil.CanWork(location, activeSwingCover, Mod.Config, StartItem))
				{
					flag = true;
					break;
				}
			}
		}
		if (!flag)
		{
			TileDone.Add(tile);
			((Character)player).lastClick = Vector2.Zero;
			Tilled++;
			return;
		}
		// 挥刀前记下这一刀范围内「此刻真有活」的格。挥完之后这些格若一个都没消掉，
		// 说明是站位/朝向没对准（白挥），不是「这格做不成」——不能让它们耗掉 3 次机会。
		// 只算镰刀真能收的格：要手摘的作物本来就不吃这一刀，算进去会让「白挥」永远成立。
		ScytheWorkBefore.Clear();
		if (currentLocation != null)
		{
			foreach (Vector2 cover in ActiveSwingCovers)
			{
				if (!ShouldHandPick(cover) && IsSwingCollectibleWork(currentLocation, cover))
				{
					ScytheWorkBefore.Add(cover);
				}
			}
		}
		((Tool)scythe).lastUser = player;
		((Character)player).Halt();
		player.UsingTool = true;
		player.CanMove = false;
		player.canReleaseTool = true;
		((NetFieldBase<int, NetInt>)(object)player.toolPower).Value = 0;
		player.FarmerSprite.PauseForSingleAnimation = false;
		try
		{
			((Tool)scythe).beginUsing(currentLocation, (int)((Character)player).lastClick.X, (int)((Character)player).lastClick.Y, player);
		}
		catch (Exception value)
		{
			((Mod)Mod).Monitor.Log($"挥镰刀（{((Item)scythe).Name}）时出错：{value}", (LogLevel)4);
			((Character)player).lastClick = Vector2.Zero;
			RestoreAfterSwing(player);
			FailedSwings++;
			return;
		}
		((Character)player).lastClick = Vector2.Zero;
		WaitingSwing = true;
		SwingTicks = 0;
		SwingTarget = tile;
	}


	/// <summary>作物批：长在土里的那些（枯萎作物 / 作物 / 花），不论这把镰刀能不能成片割。
	/// 它们走 S 型；能不能挥刀由 ShouldHandPick 在执行时再判，不影响排序。</summary>
	private static bool IsScytheCropWork(TileUtil.TileWork work)
	{
		return work == TileUtil.TileWork.ScytheDeadCrop || work == TileUtil.TileWork.ScytheCrop || work == TileUtil.TileWork.ScytheFlower;
	}

	private bool IsScytheCropTile(Vector2 tile)
	{
		return Planned != null && Planned.TryGetValue(tile, out var work) && IsScytheCropWork(work);
	}

	private bool ShouldHandPick(Vector2 tile)
	{
		if (!Planned.TryGetValue(tile, out var value))
		{
			return false;
		}
		bool flag;
		switch (value)
		{
		case TileUtil.TileWork.ScytheForage:
			return !TileUtil.CanScythePickForage(StartItem, Location, tile);
		case TileUtil.TileWork.ScytheMachine:
			return true;
		case TileUtil.TileWork.ScythePond:
			return true;
		case TileUtil.TileWork.ShakeBush:
			// 灌木丛镰刀砍不动（原版要靠右键/动作键摇），必须走手摇
			return true;
		case TileUtil.TileWork.HarvestFruit:
			// 果树也是右键摇下来的，镰刀挥它只会白挥
			return true;
		case TileUtil.TileWork.ScytheCrop:
		case TileUtil.TileWork.ScytheFlower:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			return false;
		}
		if (TileUtil.IsIridiumScythe(StartItem))
		{
			return false;
		}
		if (TileUtil.IsScytheHarvestCrop(Location, tile))
		{
			return false;
		}
		return !ScytheGrabsCrops;
	}


	/// <summary>
	/// 出发前预检：这一趟所有「手摘进背包」的格子里，是不是一个能装下的都没有。
	/// 有一个装得下就照常开工（装不下的那格走到再停，和原行为一致）；
	/// 一个都装不下就没必要开走，直接在起点停。
	/// </summary>
	private bool WholeRunHandPickBlockedByFullBag(GameLocation location, Farmer player)
	{
		int num = 0;
		foreach (KeyValuePair<Vector2, TileUtil.TileWork> item in Planned)
		{
			if (!ShouldHandPick(item.Key))
			{
				continue;
			}
			num++;
			if (!IsHandPickInventoryFull(location, player, item.Key))
			{
				return false;
			}
		}
		return num > 0;
	}

	private bool IsHandPickInventoryFull(GameLocation location, Farmer player, Vector2 tile)
	{
		// 先查容量：原版 checkForAction 在背包满时会走失败分支，触发本队列的补收和重扫。
		Item output = null;
		Bush bush = TileUtil.BushAt(location, tile);
		if (bush != null && TileUtil.IsBerryBushReady(bush))
		{
			// 灌木浆果没有「产物对象」可查，只能拿原版那件掉落物探一下背包放不放得下。
			// 不在这里拦住的话，摇不动会被当成「这格没活」白耗三次重试，日志还写错原因。
			string shakeOff = bush.GetShakeOffItem();
			if (shakeOff != null)
			{
				return !player.couldInventoryAcceptThisItem(ItemRegistry.Create(shakeOff));
			}
		}
		FruitTree fruitTree = (FruitTree)((TileUtil.GetTree(location, tile) is FruitTree tree) ? tree : null);
		if (TileUtil.IsFruitTreeReady(fruitTree))
		{
			// 果树的水果本来就摆在树上，直接拿它探容量
			return !player.couldInventoryAcceptThisItem(fruitTree.fruit[0]);
		}
		FishPond pond = TileUtil.GetPondWithOutputAt(location, tile);
		if (pond != null)
		{
			output = ((NetFieldBase<Item, NetRef<Item>>)(object)pond.output).Value;
		}
		else
		{
			Object obj = null;
			if (location.objects.TryGetValue(tile, out obj) && obj != null)
			{
				if (((NetFieldBase<bool, NetBool>)(object)obj.readyForHarvest).Value)
				{
					output = ((NetFieldBase<Object, NetRef<Object>>)(object)obj.heldObject).Value;
				}
				else if (obj.isForage())
				{
					// 采集品也查容量：放不下的话走到那格只会静默重试三次再放弃，
					// 人白走一趟、日志里还只有「手摘没收到」。和机器产物一样按背包满停。
					return !player.couldInventoryAcceptThisItem((Item)(object)obj);
				}
			}
		}
		return output != null && !player.couldInventoryAcceptThisItem(output);
	}

	private void ApplyHandPick(Farmer player, Vector2 tile)
	{
		GameLocation location = Location;
		if (location != null)
		{
			ApplyHandPick(location, player, tile);
		}
	}

	private bool ApplyHandPick(GameLocation location, Farmer player, Vector2 tile)
	{
		Bush bush = TileUtil.BushAt(location, tile);
		if (bush != null && TileUtil.IsBerryBushReady(bush))
		{
			// 地里的灌木和花盆里的茶树都是同一套「摇」：花盆那棵也走 bush.performUseAction
			// （IndoorPot.checkForAction 最后一行就是它），果子和音效全由原版负责。
			return ApplyBushShake(location, player, tile, bush);
		}
		FruitTree fruitTree = (FruitTree)((TileUtil.GetTree(location, tile) is FruitTree fruitTreeHit) ? fruitTreeHit : null);
		if (TileUtil.IsFruitTreeReady(fruitTree))
		{
			return ApplyFruitShake(player, tile, fruitTree);
		}
		FishPond pond = TileUtil.GetPondWithOutputAt(location, tile);
		if (pond != null)
		{
			Item output = ((NetFieldBase<Item, NetRef<Item>>)(object)pond.output).Value;
			((NetFieldBase<Item, NetRef<Item>>)(object)pond.output).Value = null;
			if (!player.addItemToInventoryBool(output, false))
			{
				((NetFieldBase<Item, NetRef<Item>>)(object)pond.output).Value = output;
				return false;
			}
			try
			{
				Game1.playSound("coin", (int?)null);
				int xp = 10;
				Object asObject = (Object)((output is Object) ? output : null);
				if (asObject != null)
				{
					xp += (int)((float)((Item)asObject).sellToStorePrice(-1L) * 0.04f);
				}
				player.gainExperience(1, xp);
			}
			catch
			{
			}
			return true;
		}
		Object val = null;
		if (location.objects.TryGetValue(tile, out val))
		{
			if (((NetFieldBase<bool, NetBool>)(object)val.readyForHarvest).Value && ((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value != null)
			{
				val.checkForAction(player, false);
				if (((NetFieldBase<bool, NetBool>)(object)val.readyForHarvest).Value)
				{
					return ((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value == null;
				}
				return true;
			}
			if (val.isForage())
			{
				ApplyForagePick(location, player, tile);
				return !location.objects.ContainsKey(tile);
			}
		}
		// 地上的土和花盆里的土都要认：盆栽作物（手摘类）只能从这里够到，
		// 只查 terrainFeatures 的话温室/屋里那一片盆栽永远「手摘没动手」。
		HoeDirt val3 = location.GetHoeDirtAtTile(tile);
		if (val3 != null)
		{
			Crop crop = val3.crop;
			if (crop == null || !val3.readyForHarvest())
			{
				return false;
			}
			((TerrainFeature)val3).performUseAction(tile);
			if (val3.crop != null)
			{
				return !val3.readyForHarvest();
			}
			return true;
		}
		return false;
	}

	private void ApplyForagePick(GameLocation location, Farmer player, Vector2 tile)
	{
		Object val = null;
		if (!location.objects.TryGetValue(tile, out val) || !val.isForage())
		{
			return;
		}
		// 和原版 GameLocation.checkAction 的「地面采集品」分支同一套口径：
		// 品质和「采集者」那个 20% 双倍判定共用同一个按格种子的 random —— 原版同一天同一格结果固定，
		// 用 Game1.random 每次重试都重掷，等于把双倍变成了「多试几次总会有」。
		Random random = Utility.CreateDaySaveRandom((double)tile.X, (double)(tile.Y * 777f), 0.0);
		int qualityBefore = ((Item)val).Quality;
		((Item)val).Quality = location.GetHarvestSpawnedObjectQuality(player, val.isForage(), val.TileLocation, random);
		// 任务物品还没接那个任务时原版不许捡（捡了任务就断），这里跟着拦，并把品质还原
		if (((NetFieldBase<bool, NetBool>)(object)val.questItem).Value && val.questId.Value != null && val.questId.Value != "0" && !player.hasQuest(val.questId.Value))
		{
			((Item)val).Quality = qualityBefore;
			return;
		}
		if (!player.couldInventoryAcceptThisItem((Item)val))
		{
			((Item)val).Quality = qualityBefore;
			return;
		}
		location.localSound("pickUpItem", (Vector2?)null, (int?)null, (SoundContext)0);
		DelayedAction.playSoundAfterDelay("coin", 300, (GameLocation)null, (Vector2?)null, -1, false);
		player.animateOnce(279 + ((Character)player).FacingDirection);
		if (!location.isFarmBuildingInterior())
		{
			if (val.isForage())
			{
				location.OnHarvestedForage(player, val);
			}
		}
		else
		{
			player.gainExperience(0, 5);
		}
		player.addItemToInventoryBool(((Item)val).getOne(), false);
		Stats stats = Game1.stats;
		uint itemsForaged = stats.ItemsForaged;
		stats.ItemsForaged = itemsForaged + 1;
		if (((NetHashSet<int>)(object)player.professions).Contains(13) && random.NextDouble() < 0.2 && !((NetFieldBase<bool, NetBool>)(object)val.questItem).Value && player.couldInventoryAcceptThisItem((Item)val) && !location.isFarmBuildingInterior())
		{
			player.addItemToInventoryBool(((Item)val).getOne(), false);
			player.gainExperience(2, 7);
		}
		location.objects.Remove(tile);
	}

	/// <summary>
	/// 摇灌木丛（春季大树莓 / 秋季黑莓 / 茶叶）。
	/// 原版动作是「对着灌木按一下」：<c>Bush.performUseAction</c> → <c>shake()</c> —— 灌木抖一下、
	/// 果子被打成 debris 碎片，落定约 0.6 秒后 <c>Debris.chunksMoveTowardPlayer</c> 按玩家的磁力半径
	/// 把它们吸过去（带一声 coin）。所以这里**不自己发物品、也不做捡东西的动作**，只是把原版这条链
	/// 放出来，掉落数量 / 植物学家铱星 / 每颗采集经验全交给原版算。
	/// </summary>
	private bool ApplyBushShake(GameLocation location, Farmer player, Vector2 tile, Bush bush)
	{
		// 人朝着灌木（原版是玩家右键面前的灌木，朝向本来就对着它）
		player.faceDirection(TileUtil.GetFacingDirection(((Character)player).Tile, tile));
		bush.performUseAction(tile);
		if (bush.readyForHarvest())
		{
			// 原版 performUseAction 内部走 shake(doEvenIfStillShaking: false)：灌木还在抖的时候
			// （maxShake != 0）这一下直接空转。贴着连摇两丛时会撞上，这里补一记强制摇，
			// 果子照样打下来，不会白跑一趟。
			bush.shake(tile, true);
		}
		return !bush.readyForHarvest();
	}

	/// <summary>
	/// 摇果摘水果。原版动作同样是「对着树按一下」：<c>FruitTree.performUseAction</c> → <c>shake()</c>
	/// —— 树抖一下，树上的 <c>fruit</c> 被做成 Debris（目标点就是玩家站立点，品质取树的品质），
	/// 落地后自动吸附进背包。别按「砍树」那套处理：砍树会连树一起放倒。
	/// </summary>
	private bool ApplyFruitShake(Farmer player, Vector2 treeTile, FruitTree tree)
	{
		// shake() 里会核对 Location.terrainFeatures[tile] 确实就是这棵树，否则直接 return 不落果。
		// 这里用队列自己那格（GetTree 就是在这格找到它的），不要依赖 tree.Tile 有没有被正确回填。
		player.faceDirection(TileUtil.GetFacingDirection(((Character)player).Tile, treeTile));
		tree.performUseAction(treeTile);
		if (TileUtil.IsFruitTreeReady(tree))
		{
			// 和灌木一样：树还在抖的时候原版 shake 会空转，这里补一记强制摇
			tree.shake(treeTile, true);
		}
		return !TileUtil.IsFruitTreeReady(tree);
	}

	/// <summary>把手上这叠洒水器放一个到目标格。</summary>
	/// <remarks>
	/// 0.5.2 修：本函数曾经整段漏掉放置调用（只剩 lastClick 归零 + VerifySwing 验收），
	/// 于是规划、排队、走位全都正常，农夫走到每一格旁边却什么都没放，
	/// 玩家看到的就是「框完选之后只移动、一颗都不放」。
	/// 成不成的口径跟别处一致 —— 看结果：这一格上真的出现了洒水器。
	/// </remarks>
	private bool ApplySprinklerPlace(Farmer player, Item item, Vector2 tile)
	{
		GameLocation currentLocation = ((Character)player).currentLocation;
		Object val = (Object)((item is Object) ? item : null);
		if (currentLocation == null || val == null)
		{
			FailedSwings++;
			return false;
		}
		if (Mod.Config.DebugLog)
		{
			((Mod)Mod).Monitor.Log($"放置洒水器 ({tile.X}, {tile.Y})  剩余 {Remaining}", LogLevel.Trace);
		}
		// 花盆/实心物那套「先把自己从格子上让开」的逻辑在 TryPlaceHeldItem 里，洒水器也算实心物。
		TryPlaceHeldItem(currentLocation, player, val, tile);
		Object placed = null;
		bool landed = currentLocation.objects.TryGetValue(tile, out placed) && placed != null && placed.GetBaseRadiusForSprinkler() >= 0;
		if (landed)
		{
			PlacedTiles.Add(tile);
			// 洒水器是实心的：这一格从此站不住。通行表在 TryPlaceHeldItem 里已经作废，
			// 但「哪格能站」那张表也得跟着补，不然后面挑站位还会挑到刚放下的这一格上。
			TileUtil.InvalidateStandableCache(currentLocation, tile);
		}
		else if (Mod.Config.DebugLog)
		{
			((Mod)Mod).Monitor.Log($"洒水器放不下 ({tile.X}, {tile.Y}) —— 这一格此刻有东西挡着", LogLevel.Trace);
		}
		((Character)player).lastClick = Vector2.Zero;
		VerifySwing(tile);
		return landed;
	}

	private void ApplyMachineLoad(Farmer player, Item item, Vector2 tile)
	{
		MachineOutcomeTile = null;
		GameLocation location = Location;
		if (location == null)
		{
			((Mod)Mod).Monitor.Log($"⚠ 机器格 ({tile.X}, {tile.Y}) 没干活：this.Location 不是 GameLocation｜手持={item.Name}({item.QualifiedItemId}) Stack={item.Stack}", (LogLevel)3);
			return;
		}
		Object val = null;
		if (!location.objects.TryGetValue(tile, out val) || val == null)
		{
			((Mod)Mod).Monitor.Log($"⚠ 机器格 ({tile.X}, {tile.Y}) 没干活：这一格上没有机器（{location.Name}）｜手持={item.Name}({item.QualifiedItemId}) Stack={item.Stack}", (LogLevel)3);
			return;
		}
		try
		{
			bool value = ((NetFieldBase<bool, NetBool>)(object)val.readyForHarvest).Value;
			Object value2 = ((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value;
			int minutesUntilReady = val.MinutesUntilReady;
			int stack = item.Stack;
			if (value && value2 != null)
			{
				val.checkForAction(player, false);
			}
			bool value3 = ((NetFieldBase<bool, NetBool>)(object)val.readyForHarvest).Value;
			Object value4 = ((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value;
			((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value = null;
			bool flag = val.performObjectDropInAction(item, true, player, false);
			((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value = value4;
			bool flag2 = val.performObjectDropInAction(item, false, player, true);
			bool ignoreItemConsumptionThisFrame = player.ignoreItemConsumptionThisFrame;
			if (!ignoreItemConsumptionThisFrame & flag2)
			{
				player.reduceActiveItemByOne();
			}
			// returnFalseIfItemConsumed=true 的语义是「这次我已经自己扣过原料了，你别再扣一遍」——
			// 一次吃多个的配方（咖啡豆 5 个进小桶）就走这条路：函数返回 false，机器却实实在在地装上了。
			// 旧版拿返回值当成败，于是每一次成功的装料都被记成「没干成」（实机 2026-11-02：
			// 137 格小桶全报失败，其实咖啡早就灌进去了）。成败只看结果：机器里有了东西，或手上真少了东西。
			Object heldNow = ((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value;
			bool placed = (heldNow != null && !object.ReferenceEquals(heldNow, value4)) || item.Stack < stack;
			bool flag3 = value && !((NetFieldBase<bool, NetBool>)(object)val.readyForHarvest).Value;
			bool flag4 = flag && !placed && !ignoreItemConsumptionThisFrame;
			bool flag5 = (((NetFieldBase<bool, NetBool>)(object)val.readyForHarvest).Value | flag4) || (!flag3 && !placed && !ignoreItemConsumptionThisFrame);
			if (flag5)
			{
				int num = (int)((Character)player).Tile.X;
				int num2 = (int)((Character)player).Tile.Y;
				int value5 = Math.Max(Math.Abs(num - (int)tile.X), Math.Abs(num2 - (int)tile.Y));
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(298, 28);
				defaultInterpolatedStringHandler.AppendLiteral("⚠ 机器格 (");
				defaultInterpolatedStringHandler.AppendFormatted(tile.X);
				defaultInterpolatedStringHandler.AppendLiteral(", ");
				defaultInterpolatedStringHandler.AppendFormatted(tile.Y);
				defaultInterpolatedStringHandler.AppendLiteral(") ");
				defaultInterpolatedStringHandler.AppendFormatted(flag5 ? "没干成" : "只收了产物、没放进去");
				defaultInterpolatedStringHandler.AppendLiteral("｜机器=");
				defaultInterpolatedStringHandler.AppendFormatted(((Item)val).Name);
				defaultInterpolatedStringHandler.AppendLiteral("(");
				defaultInterpolatedStringHandler.AppendFormatted(((Item)val).QualifiedItemId);
				defaultInterpolatedStringHandler.AppendLiteral(")");
				defaultInterpolatedStringHandler.AppendLiteral("｜进函数时 readyForHarvest=");
				defaultInterpolatedStringHandler.AppendFormatted(value);
				defaultInterpolatedStringHandler.AppendLiteral("，heldObject=");
				defaultInterpolatedStringHandler.AppendFormatted(((value2 != null) ? ((Item)value2).Name : null) ?? "<空>");
				defaultInterpolatedStringHandler.AppendLiteral("，MinutesUntilReady=");
				defaultInterpolatedStringHandler.AppendFormatted(minutesUntilReady);
				defaultInterpolatedStringHandler.AppendLiteral("｜手持=");
				defaultInterpolatedStringHandler.AppendFormatted(item.Name);
				defaultInterpolatedStringHandler.AppendLiteral("(");
				defaultInterpolatedStringHandler.AppendFormatted(item.QualifiedItemId);
				defaultInterpolatedStringHandler.AppendLiteral(")，数量 ");
				defaultInterpolatedStringHandler.AppendFormatted(stack);
				defaultInterpolatedStringHandler.AppendLiteral("→");
				defaultInterpolatedStringHandler.AppendFormatted(item.Stack);
				defaultInterpolatedStringHandler.AppendLiteral("｜ignoreItemConsumptionThisFrame=");
				defaultInterpolatedStringHandler.AppendFormatted(ignoreItemConsumptionThisFrame);
				defaultInterpolatedStringHandler.AppendLiteral("｜checkForAction：readyForHarvest ");
				defaultInterpolatedStringHandler.AppendFormatted(value);
				defaultInterpolatedStringHandler.AppendLiteral("→");
				defaultInterpolatedStringHandler.AppendFormatted(value3);
				defaultInterpolatedStringHandler.AppendLiteral("（harvested=");
				defaultInterpolatedStringHandler.AppendFormatted(flag3);
				defaultInterpolatedStringHandler.AppendLiteral("）");
				defaultInterpolatedStringHandler.AppendLiteral("｜probe=");
				defaultInterpolatedStringHandler.AppendFormatted(flag);
				defaultInterpolatedStringHandler.AppendLiteral("｜placed=");
				defaultInterpolatedStringHandler.AppendFormatted(flag2);
				defaultInterpolatedStringHandler.AppendLiteral("｜装入=");
				defaultInterpolatedStringHandler.AppendFormatted(placed);
				defaultInterpolatedStringHandler.AppendLiteral("｜玩家格=(");
				defaultInterpolatedStringHandler.AppendFormatted(num);
				defaultInterpolatedStringHandler.AppendLiteral(", ");
				defaultInterpolatedStringHandler.AppendFormatted(num2);
				defaultInterpolatedStringHandler.AppendLiteral(")，切比雪夫距离=");
				defaultInterpolatedStringHandler.AppendFormatted(value5);
				defaultInterpolatedStringHandler.AppendLiteral("，UsingTool=");
				defaultInterpolatedStringHandler.AppendFormatted(player.UsingTool);
				defaultInterpolatedStringHandler.AppendLiteral("，PauseForSingleAnimation=");
				defaultInterpolatedStringHandler.AppendFormatted(player.FarmerSprite.PauseForSingleAnimation);
				defaultInterpolatedStringHandler.AppendLiteral("｜结束时 readyForHarvest=");
				defaultInterpolatedStringHandler.AppendFormatted(((NetFieldBase<bool, NetBool>)(object)val.readyForHarvest).Value);
				defaultInterpolatedStringHandler.AppendLiteral("，heldObject=");
				Object value6 = ((NetFieldBase<Object, NetRef<Object>>)(object)val.heldObject).Value;
				defaultInterpolatedStringHandler.AppendFormatted(((value6 != null) ? ((Item)value6).Name : null) ?? "<空>");
				defaultInterpolatedStringHandler.AppendLiteral("，MinutesUntilReady=");
				defaultInterpolatedStringHandler.AppendFormatted(val.MinutesUntilReady);
			}
			MachineOutcomeTile = tile;
			MachineOutcomeProbed = flag;
			MachineOutcomePlaced = placed;
			MachineOutcomeConsumed = item.Stack < stack;
		}
		catch (Exception value7)
		{
			((Mod)Mod).Monitor.Log($"往 ({tile.X}, {tile.Y}) 的机器里放「{item.Name}」时出错：{value7}", (LogLevel)4);
			FailedSwings++;
			return;
		}
		((Character)player).lastClick = Vector2.Zero;
		WaitingSwing = true;
		SwingTicks = 0;
		SwingTarget = tile;
	}

	private void ApplyRefill(Farmer player, Tool tool, Vector2 water)
	{
		BeginToolUse(player, tool, water, 0);
	}

	private bool ApplyPlaceItem(Farmer player, Item item, Vector2 tile)
	{
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation != null)
		{
			Object val = (Object)((item is Object) ? item : null);
			if (val != null)
			{
				TerrainFeature val2 = null;
				PlaceSnapFeature = (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)currentLocation.terrainFeatures).TryGetValue(tile, out val2) ? val2 : null);
				PlaceSnapHadObject = currentLocation.objects.ContainsKey(tile);
				bool flag = TryPlaceHeldItem(currentLocation, player, val, tile);
				if (flag && TileUtil.IsSolidPlaceItem((Item)val) && TileUtil.NeedsPlacement(currentLocation, tile, (Item)val))
				{
					flag = false;
				}
				if (flag && TileUtil.IsSowSeed((Item)val) && TileUtil.CanSowHere(currentLocation, tile, val))
				{
					flag = false;
				}
				if (TileUtil.IsPlaceableObjectItem((Item)val) && PlacementLanded(currentLocation, tile))
				{
					flag = true;
					PlacedTiles.Add(tile);
				}
				if (!flag)
				{
					Vector2 tile2 = ((Character)player).Tile;
					float num = Vector2.Distance(tile2, tile);
					bool flag2 = false;
					bool flag3 = false;
					try
					{
						flag2 = ((Item)val).canBePlacedHere(currentLocation, tile, (CollisionMask)255, false);
					}
					catch
					{
					}
					try
					{
						flag3 = ((Item)val).canBePlacedHere(currentLocation, tile, (CollisionMask)251, false);
					}
					catch
					{
					}
					string value = ((tile2 == tile) ? "人正站在这一格上（原版禁止站在格上放实心物件）" : ((num > 1.5f) ? $"人不在旁边（玩家 {tile2.X},{tile2.Y}，距离 {num:0.0} 格）" : (flag2 ? "canBePlacedHere = true 但 tryToPlaceItem 仍失败（可达性/禁止放置区/其他 mod 拦截）" : ((!flag3) ? "canBePlacedHere = false（这一格本身放不下：有物件/地形不符/被占据）" : "只有『农夫包围盒』挡着 —— 放之前已摆正过一次，还失败说明这次没摆正（坐骑/别的角色/坐标被改回）"))));
					TerrainFeature val3 = null;
					string text;
					if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)currentLocation.terrainFeatures).TryGetValue(tile, out val3))
					{
						text = ((object)val3).GetType().Name;
						HoeDirt val4 = (HoeDirt)((val3 is HoeDirt) ? val3 : null);
						if (val4 != null)
						{
							string value2 = ((val4.crop == null) ? "无作物" : ("有作物(" + ((NetFieldBase<string, NetString>)(object)val4.crop.indexOfHarvest).Value + ")"));
							text += $"[state={((NetFieldBase<int, NetInt>)(object)val4.state).Value}, {value2}, 肥料={val4.HasFertilizer()}]";
						}
					}
					else
					{
						text = "无地形特征（这一格根本没翻地）";
					}
					Rectangle val5 = default(Rectangle);
					val5 = new Rectangle((int)tile.X * 64, (int)tile.Y * 64, 64, 64);
					Rectangle boundingBox = ((Character)player).GetBoundingBox();
					bool value3 = (boundingBox).Intersects(val5);
					bool flag4;
					try
					{
						flag4 = currentLocation.CanItemBePlacedHere(tile, true, (CollisionMask)0, (CollisionMask)223, false, false);
					}
					catch
					{
						flag4 = false;
					}
					bool value4 = currentLocation.objects.ContainsKey(tile);
					bool value5 = currentLocation.CanItemBePlacedHere(tile, false, (CollisionMask)255, (CollisionMask)223, false, false);
					string value6 = currentLocation.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back", false);
					string value7 = currentLocation.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Type", "Back", false);
				}
				((Character)player).lastClick = Vector2.Zero;
				VerifySwing(tile);
				return flag;
			}
		}
		FailedSwings++;
		return false;
	}

	private bool PlacementLanded(GameLocation location, Vector2 tile)
	{
		bool flag = location.objects.ContainsKey(tile);
		if (flag != PlaceSnapHadObject)
		{
			return true;
		}
		TerrainFeature val = null;
		((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val);
		return val != PlaceSnapFeature;
	}

	private bool TryPlaceHeldItem(GameLocation location, Farmer player, Object item, Vector2 tile)
	{
		// 花盆下种/施肥不能交给 tryToPlaceItem：原版 Object.placementAction 的种子和肥料分支都只读 terrainFeatures，
		// 对花盆返回 false，可 tryToPlaceItem 照样返回 true —— 东西没消耗、盆里也没动，我们却会当成干完了。
		// 原版往花盆里放种子/肥料走的是 IndoorPot.performObjectDropInAction（GameLocation.checkAction 就这么做）。
		IndoorPot? pot = TileUtil.PotAt(location, tile);
		if (pot != null && (TileUtil.IsSowSeed((Item)item) || TileUtil.IsFertilizerItem((Item)item)))
		{
			return DropIntoPot(pot, item, player);
		}
		Vector2 tileCenterPixel = TileUtil.GetTileCenterPixel(tile);
		Vector2 original = ((Character)player).Position;
		bool flag = (TileUtil.IsSolidPlaceItem((Item)item) || TileUtil.IsRaisedCropSeed((Item)item, location) || TileUtil.IsSowSeed((Item)item) || TileUtil.IsFertilizerItem((Item)item) || TileUtil.IsSaplingItem((Item)item)) && TileUtil.TryClearOfTile(player, location, tile, out original);
		bool flag2 = false;
		try
		{
			flag2 = Utility.tryToPlaceItem(location, item, (int)tileCenterPixel.X, (int)tileCenterPixel.Y);
		}
		finally
		{
			if (flag && !flag2)
			{
				((Character)player).Position = original;
				if (TileUtil.FindSqueezingObjects(location, player).Count > 0)
				{
					TileUtil.TryClearOfTile(player, location, tile, out var _);
				}
			}
		}
		if (flag2)
		{
			// 放下去的东西（洒水器/树苗/花盆/实心物）会改「哪格能走」：作废地形通行表，
			// 别让缓存里那张旧表把刚放下的东西当成空气，后面的寻路会从它身上穿过去。
			TileUtil.InvalidateTerrainCache();
		}
		return flag2;
	}

	/// <summary>往花盆里放种子或肥料。照原版 GameLocation.checkAction 的做法：放成了才扣一个。</summary>
	private static bool DropIntoPot(IndoorPot pot, Object item, Farmer player)
	{
		if (!pot.performObjectDropInAction((Item)item, false, player, true))
		{
			return false;
		}
		if (!player.ignoreItemConsumptionThisFrame)
		{
			player.reduceActiveItemByOne();
		}
		return true;
	}

	private void BeginToolUse(Farmer player, Tool tool, Vector2 tile, int power)
	{
		// 对着的格子可能比队首挪了一格（选框边缘把带子挪进框里），朝向也按那一格算。
		Vector2 aimTile = SwingAim ?? tile;
		((Character)player).FacingDirection = PreferredFacing ?? TileUtil.GetFacingDirection(((Character)player).Tile, aimTile);
		LastSwingFacing = ((Character)player).FacingDirection;
		PreferredFacing = null;
		PreferredStand = null;
		SwingAim = null;
		((Character)player).lastClick = TileUtil.GetTileCenterPixel(aimTile);
		if (((Character)player).lastClick == Vector2.Zero)
		{
			((Character)player).lastClick = TileUtil.GetTileCenterPixel(aimTile);
		}
		tool.lastUser = player;
		player.UsingTool = true;
		player.CanMove = false;
		player.canReleaseTool = true;
		((NetFieldBase<int, NetInt>)(object)player.toolPower).Value = 0;
		((Character)player).Halt();
		player.FarmerSprite.PauseForSingleAnimation = false;
		player.FarmerSprite.setCurrentFrame(SwingAnimationId(tool, ((Character)player).FacingDirection));
		tool.Update(((Character)player).FacingDirection, 0, player);
		ChargeStepMs = ResolveChargeStepMs(tool);
		if (power <= 0 || ChargeStepMs <= 0)
		{
			((NetFieldBase<int, NetInt>)(object)player.toolPower).Value = power;
			StartSwing(player, tool, tile);
			return;
		}
		Charging = true;
		ChargePower = power;
		ChargeSteps = 0;
		ChargeElapsedMs = 0;
		ChargeTarget = tile;
	}

	private int ResolveChargeStepMs(Tool tool)
	{
		if (Mod.Config.SkipChargeAnimation)
		{
			return 0;
		}
		float chargeWindupSpeed = Mod.Config.ChargeWindupSpeed;
		if (chargeWindupSpeed <= 0f)
		{
			return 0;
		}
		float val = 600f * tool.AnimationSpeedModifier / chargeWindupSpeed;
		return (int)Math.Round(Math.Max(1f, val));
	}

	private bool TickCharge(Farmer player, Tool tool)
	{
		if (!Charging)
		{
			return false;
		}
		if (!player.UsingTool)
		{
			// 工具状态被游戏清掉（不是我们挥完的）：这一蓄作废，目标格留在队首下一帧重来。
			// 以前这里只在 UsingTool 时等动画、不在时静默收尾，结果是这一格既没落地也没人管，
			// 要靠后面的 ⚠ 验收才发现。
			Charging = false;
			RestoreAfterSwing(player);
			if (ChargeTarget == ChargeCutTile)
			{
				ChargeCutCount++;
			}
			else
			{
				ChargeCutTile = ChargeTarget;
				ChargeCutCount = 1;
			}
			if (ChargeCutCount <= 2)
			{
				if (!Tiles.Contains(ChargeTarget))
				{
					Tiles.Insert(Index, ChargeTarget);
				}
			}
			else
			{
			}
			return true;
		}
		// PauseForSingleAnimation 只是原版动画标志：蓄力是这里按时间自己推进的，它变 true 时
		// 不能当成「蓄力结束」——否则工具会按当前那点小档挥出去、目标格漏掉
		// （实机 21:28:18：计划 5 档只蓄到 1 档就收尾，随后 ⚠ 没干成）。
		ChargeElapsedMs += Game1.currentGameTime.ElapsedGameTime.Milliseconds;
		if (ChargeElapsedMs < ChargeStepMs)
		{
			return true;
		}
		ChargeElapsedMs = 0;
		player.toolPowerIncrease();
		ChargeSteps++;
		if (ChargeSteps >= ChargePower)
		{
			((NetFieldBase<int, NetInt>)(object)player.toolPower).Value = ChargePower;
			Charging = false;
			StartSwing(player, tool, ChargeTarget);
		}
		return true;
	}

	private void StartSwing(Farmer player, Tool tool, Vector2 tile)
	{
		tool.endUsing(((Character)player).currentLocation, player);
		WaitingSwing = true;
		SwingTicks = 0;
		SwingTarget = tile;
	}

	private static void RestoreAfterSwing(Farmer player)
	{
		player.stopJittering();
		player.UsingTool = false;
		if (!Game1.eventUp)
		{
			player.CanMove = true;
		}
		try
		{
			if (player.FarmerSprite.PauseForSingleAnimation)
			{
				player.FarmerSprite.PauseForSingleAnimation = false;
				((AnimatedSprite)player.FarmerSprite).StopAnimation();
			}
		}
		catch
		{
		}
	}

	private static int SwingAnimationId(Tool tool, int facingDirection)
	{
		int result;
		if (tool is WateringCan)
		{
			result = facingDirection switch
			{
				0 => 180, 
				1 => 172, 
				2 => 164, 
				_ => 188, 
			};
			return result;
		}
		result = facingDirection switch
		{
			0 => 176, 
			1 => 168, 
			2 => 160, 
			_ => 184, 
		};
		return result;
	}

	private static int MaxCharge(Tool tool)
	{
		return tool.UpgradeLevel + (tool.hasEnchantmentOfType<ReachingToolEnchantment>() ? 1 : 0);
	}

	private int ResolveToolPower(Farmer player, Tool tool, Vector2 tile)
	{
		if (TileUtil.IsScythe((Item)tool))
		{
			return 0;
		}
		if (tool is Pickaxe)
		{
			return 0;
		}
		if (tool is Axe)
		{
			return 0;
		}
		if (PendingPower.HasValue)
		{
			// 取格时选锚点已经算好的档位/朝向，挥的时候就用它；否则会按另一套目标重算，
			// 把“覆盖最全”的锚点换成一个小范围档，留下最右列那一类缺口。
			int plannedPower = PendingPower.Value;
			PendingPower = null;
			return plannedPower;
		}
		WateringCan val = (WateringCan)((tool is WateringCan) ? tool : null);
		if (val != null)
		{
			return ResolveCanPower(val, tile, player);
		}
		int? pendingPower = PendingPower;
		if (pendingPower.HasValue)
		{
			int valueOrDefault = pendingPower.GetValueOrDefault();
			PendingPower = null;
			return valueOrDefault;
		}
		int num = PreferredFacing ?? ((Character)player).FacingDirection;
		(int, int) tuple = PlanAt(player, tool, tile, num);
		int num2 = tuple.Item1;
		int num3 = tuple.Item2;
		pendingPower = PreferredFacing;
		if (!pendingPower.HasValue)
		{
			for (int i = 0; i < 4; i++)
			{
				if (i != num)
				{
					var (num4, num5) = PlanAt(player, tool, tile, i);
					if (num5 > num3 || (num5 == num3 && num5 > 0 && num4 > num2))
					{
						num2 = num4;
						num3 = num5;
						num = i;
						PreferredFacing = i;
					}
				}
			}
		}
		return num2;
	}

	private int ResolveCanPower(WateringCan can, Vector2 tile, Farmer player)
	{
		int num = MaxCharge((Tool)can);
		if (num <= 0)
		{
			return 0;
		}
		HashSet<Vector2> hashSet = new HashSet<Vector2> { tile };
		for (int i = Index; i < Tiles.Count; i++)
		{
			hashSet.Add(Tiles[i]);
		}
		GameLocation val = Location ?? ((Character)player).currentLocation;
		int facingDirection = ((Character)player).FacingDirection;
		int num2 = PreferredFacing ?? TileUtil.GetFacingDirection(((Character)player).Tile, tile);
		int[] array = new int[num + 1];
		int[] array2 = new int[num + 1];
		for (int j = 0; j <= num; j++)
		{
			array[j] = -1;
			array2[j] = num2;
		}
		try
		{
			for (int k = 0; k < 4; k++)
			{
				int num3 = (((Character)player).FacingDirection = ((k == 0) ? num2 : ((num2 + k) % 4)));
				int num5 = num3;
				for (int l = 0; l <= num; l++)
				{
					List<Vector2> affectedTiles = GetAffectedTiles((Tool)can, tile, l, player);
					if (affectedTiles == null)
					{
						return 0;
					}
					int num6 = 0;
					bool flag = true;
					foreach (Vector2 item in affectedTiles)
					{
						if (val != null && TileUtil.LooksWorkable(val, item, Mod.Config, (Item)can))
						{
							if (!hashSet.Contains(item))
							{
								flag = false;
								break;
							}
							num6++;
						}
					}
					if (flag && num6 > array[l])
					{
						array[l] = num6;
						array2[l] = num5;
					}
				}
			}
		}
		finally
		{
			((Character)player).FacingDirection = facingDirection;
		}
		int num7 = 0;
		int num8 = array[0];
		for (int m = 1; m <= num; m++)
		{
			int num9 = array[m] - m;
			if (num9 > num8)
			{
				num8 = num9;
				num7 = m;
			}
		}
		PreferredFacing = array2[num7];
		return num7;
	}

	private (int Power, int Newly) PlanAt(Farmer player, Tool tool, Vector2 tile, int facing)
	{
		int num = MaxCharge(tool);
		if (num <= 0)
		{
			return (Power: 0, Newly: 0);
		}
		GameLocation location = Location;
		if (location != null)
		{
			Rectangle? selectionBox = SelectionBox;
			if (selectionBox.HasValue)
			{
				Rectangle valueOrDefault = selectionBox.GetValueOrDefault();
				int facingDirection = ((Character)player).FacingDirection;
				((Character)player).FacingDirection = facing;
				int item = 0;
				int num2 = -1;
				try
				{
					for (int i = 0; i <= num; i++)
					{
						List<Vector2> affectedTiles = GetAffectedTiles(tool, tile, i, player);
						if (affectedTiles == null)
						{
							break;
						}
						int num3 = 0;
						bool flag = false;
						foreach (Vector2 item2 in affectedTiles)
						{
							if (TileUtil.LooksWorkable(location, item2, Mod.Config, (Item)tool))
							{
								if (Mod.Config.TillSprinkleredOnly && !TileUtil.IsSprinkleredTile(location, item2))
								{
									flag = true;
									break;
								}
								if (!Planned.ContainsKey(item2))
								{
									flag = true;
									break;
								}
								num3++;
							}
						}
						if (!flag && num3 > num2)
						{
							num2 = num3;
							item = i;
						}
					}
				}
				finally
				{
					((Character)player).FacingDirection = facingDirection;
				}
				return (Power: item, Newly: Math.Max(0, num2));
			}
		}
		return (Power: 0, Newly: 0);
	}

	private List<Vector2>? GetAffectedTiles(Tool tool, Vector2 tile, int power, Farmer who)
	{
		if ((object)AffectedTilesMethod == null)
		{
			WarnChargePredictionBroken("找不到 Tool.tilesAffected（这个游戏版本的内部方法名变了？）");
			return null;
		}
		try
		{
			return AffectedTilesMethod.Invoke(tool, new object[3] { tile, power, who }) as List<Vector2>;
		}
		catch (Exception ex)
		{
			WarnChargePredictionBroken("调用 Tool.tilesAffected 抛异常：" + ex.Message);
			return null;
		}
	}

	private void WarnChargePredictionBroken(string reason)
	{
		if (!AffectedTilesBroken)
		{
			AffectedTilesBroken = true;
			((Mod)Mod).Monitor.Log(reason + "；已退回「一次只翻一格」（锄地本身不受影响）", (LogLevel)3);
		}
	}

	private bool OutOfStamina(Farmer player)
	{
		if (TileUtil.IsScythe(StartItem))
		{
			return false;
		}
		if (TileUtil.IsSowSeed(StartItem))
		{
			return false;
		}
		if (TileUtil.IsMachineLoadItem(StartItem))
		{
			return false;
		}
		if (TileUtil.IsSprinklerItem(StartItem))
		{
			return false;
		}
		if (TileUtil.IsFertilizerItem(StartItem))
		{
			return false;
		}
		float num = Mod.Config.StopBelowStamina;
		if (num <= 0f || player.Stamina > num)
		{
			return false;
		}
		int remaining = Remaining;
		Stop(I18n.T("stop-out-of-stamina"));
		LastStopWasStamina = true;
		// 体力不支属于预期内的收工：和干完一样弹背包自动暂停（杂物链还挂着时改由 Finalize 弹）。
		Mod.OpenBackpackAfterTask();
		return true;
	}


	private void VerifySwing(Vector2 tile)
	{
		GameLocation location = Location;
		if (location == null)
		{
			return;
		}
		Item startItem = StartItem;
		WateringCan val = (WateringCan)((startItem is WateringCan) ? startItem : null);
		if (val != null && TileUtil.IsWaterSource(location, tile))
		{
			if (val.WaterLeft <= 0)
			{
				FailedSwings++;
				if (FailedSwings <= 3)
				{
					((Mod)Mod).Monitor.Log($"⚠ 朝 ({tile.X}, {tile.Y}) 装水没成功（水还是 {val.WaterLeft}）。" + "最可能是那一格其实不是水源（水域被别的 mod 改过），或者挥的时候人已经不在旁边了。", (LogLevel)3);
				}
			}
			return;
		}
		MarkSplashDone(tile);
		TileUtil.TileWork tileWork = ((!Planned.TryGetValue(tile, out var value)) ? TileUtil.TileWork.Till : value);
		if (TileUtil.IsScythe(StartItem))
		{
			ScheduleScytheVerify(tile);
		}
		int num7;
		int num5;
		int num3;
		int num8;
		int num10;
		int num6;
		int num;
		int num2;
		int num9;
		int num4;
		bool flag;
		switch (tileWork)
		{
		case TileUtil.TileWork.Weed:
		case TileUtil.TileWork.ArtifactSpot:
		case TileUtil.TileWork.SeedSpot:
			flag = !location.objects.ContainsKey(tile);
			break;
		case TileUtil.TileWork.WaterDirt:
		case TileUtil.TileWork.WaterCrop:
		{
			// pot 版查找：花盆里的土不在 terrainFeatures 里，只看那个会把浇好的花盆判成没浇上，白重试一轮。
			HoeDirt val17 = location.GetHoeDirtAtTile(tile);
			num7 = (((val17 != null && val17.state.Value == 1) ? 1 : 0));
			goto IL_0887;
		}
		case TileUtil.TileWork.Sow:
		{
			HoeDirt val7 = location.GetHoeDirtAtTile(tile);
			num5 = (((val7 != null && val7.crop != null) ? 1 : 0));
			goto IL_08b3;
		}
		case TileUtil.TileWork.ScytheCrop:
		case TileUtil.TileWork.ScytheFlower:
		{
			TerrainFeature val18 = null;
			if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val18))
			{
				HoeDirt val19 = (HoeDirt)((val18 is HoeDirt) ? val18 : null);
				if (val19 != null)
				{
					num3 = ((val19.crop == null || !val19.readyForHarvest()) ? 1 : 0);
					goto IL_08be;
				}
			}
			num3 = 0;
			goto IL_08be;
		}
		case TileUtil.TileWork.ScytheDeadCrop:
		{
			TerrainFeature val23 = null;
			if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val23))
			{
				HoeDirt val24 = (HoeDirt)((val23 is HoeDirt) ? val23 : null);
				if (val24 != null)
				{
					num8 = ((val24.crop == null) ? 1 : 0);
					goto IL_08df;
				}
			}
			num8 = 0;
			goto IL_08df;
		}
		case TileUtil.TileWork.ScytheForage:
		case TileUtil.TileWork.ScytheWeed:
			flag = !location.objects.ContainsKey(tile);
			break;
		case TileUtil.TileWork.ScytheMachine:
		{
			Object val20 = null;
			flag = !location.objects.TryGetValue(tile, out val20) || val20 == null || !((NetFieldBase<bool, NetBool>)(object)val20.readyForHarvest).Value || ((NetFieldBase<Object, NetRef<Object>>)(object)val20.heldObject).Value == null;
			break;
		}
		case TileUtil.TileWork.ScythePond:
			flag = TileUtil.GetPondWithOutputAt(location, tile) == null;
			break;
		case TileUtil.TileWork.ShakeBush:
			flag = !TileUtil.IsBerryBushReady(TileUtil.BushAt(location, tile));
			break;
		case TileUtil.TileWork.HarvestFruit:
			flag = !TileUtil.IsFruitTreeReady((FruitTree)((TileUtil.GetTree(location, tile) is FruitTree ft) ? ft : null));
			break;
		case TileUtil.TileWork.Fertilize:
		{
			// pot 版查找：花盆里的肥料不在 terrainFeatures 里，只看那个会把放好的花盆判成没放上。
			// 树肥同理：施在野树上时结果在 Tree.fertilized，HoeDirt 永远是空的。
			HoeDirt val22 = location.GetHoeDirtAtTile(tile);
			num10 = (((val22 != null && val22.HasFertilizer()) || TileUtil.IsTreeFertilized(location, tile)) ? 1 : 0);
			goto IL_087c;
		}
		case TileUtil.TileWork.SaplingPlace:
			flag = TileUtil.GetTree(location, tile) != null || TileUtil.FindBush(location, tile) != null;
			break;
		case TileUtil.TileWork.PlaceObject:
			flag = location.objects.ContainsKey(tile) || PlacedTiles.Contains(tile);
			break;
		case TileUtil.TileWork.ScytheMoss:
		{
			TerrainFeature val14 = null;
			if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val14))
			{
				Tree val15 = (Tree)((val14 is Tree) ? val14 : null);
				if (val15 != null)
				{
					num6 = ((!((NetFieldBase<bool, NetBool>)(object)val15.hasMoss).Value) ? 1 : 0);
					goto IL_0892;
				}
			}
			num6 = 0;
			goto IL_0892;
		}
		case TileUtil.TileWork.ScytheBluegrass:
		case TileUtil.TileWork.ScytheGrass:
			if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).ContainsKey(tile))
			{
				TerrainFeature val3 = null;
				if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val3))
				{
					Grass val4 = (Grass)((val3 is Grass) ? val3 : null);
					if (val4 != null && GrassBefore.TryGetValue(tile, out var value2))
					{
						num = ((((NetFieldBase<int, NetInt>)(object)val4.numberOfWeeds).Value < value2) ? 1 : 0);
						goto IL_08d4;
					}
				}
				num = 0;
			}
			else
			{
				num = 1;
			}
			goto IL_08d4;
		case TileUtil.TileWork.PickDeadCrop:
		{
			TerrainFeature val9 = null;
			if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val9))
			{
				HoeDirt val10 = (HoeDirt)((val9 is HoeDirt) ? val9 : null);
				if (val10 != null && val10.crop != null)
				{
					num2 = ((!((NetFieldBase<bool, NetBool>)(object)val10.crop.dead).Value) ? 1 : 0);
					goto IL_089d;
				}
			}
			num2 = 1;
			goto IL_089d;
		}
		case TileUtil.TileWork.PickEmptyDirt:
		case TileUtil.TileWork.PickFertilizedDirt:
		{
			TerrainFeature val8 = null;
			flag = !((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val8) || !(val8 is HoeDirt);
			break;
		}
		case TileUtil.TileWork.PickFloor:
		{
			TerrainFeature val11 = null;
			flag = !((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val11) || !(val11 is Flooring);
			break;
		}
		case TileUtil.TileWork.PickWeed:
		case TileUtil.TileWork.PickObject:
		case TileUtil.TileWork.PickStone:
		case TileUtil.TileWork.PickBoulder:
			flag = !location.objects.ContainsKey(tile);
			break;
		case TileUtil.TileWork.PickClump:
			flag = TileUtil.FindResourceClump(location, tile) == null;
			break;
		case TileUtil.TileWork.PickFence:
			flag = !location.objects.ContainsKey(tile);
			break;
		case TileUtil.TileWork.AxeFruitTreeMature:
		case TileUtil.TileWork.AxeTreeMature:
			flag = !IsTreeStillStanding(location, tile);
			break;
		case TileUtil.TileWork.AxeTapper:
			// 采集器有没有被敲下来：原版里斧头砍在采集器上，它就掉了（掉出来的采集器和树液是 Debris，
			// 落在脚边走过去就捡到）。这一格没采集器了就算干成；树还在不在不管。
			flag = !TileUtil.HasTapperAt(location, tile);
			break;
		case TileUtil.TileWork.AxeStump:
		{
			TerrainFeature tree = TileUtil.GetTree(location, tile);
			flag = (tree == null || !TileUtil.IsStump(tree)) && TileUtil.FindAxeClump(location, tile) == null;
			break;
		}
		case TileUtil.TileWork.AxeFruitTreeSeed:
		case TileUtil.TileWork.AxeFruitTreeSapling:
		case TileUtil.TileWork.AxeTreeSeed:
		case TileUtil.TileWork.AxeTreeSapling:
			flag = TileUtil.GetTree(location, tile) == null;
			break;
		case TileUtil.TileWork.AxeGiantCrop:
			flag = TileUtil.FindResourceClump(location, tile) == null;
			break;
		case TileUtil.TileWork.AxeBush:
			// 灌木是被 Axe.DoFunction 里的 RemoveWhere 当场摘掉的（不是延迟到下一 tick），
			// 所以挥完立刻查这一格还有没有灌木就是准的。
			flag = TileUtil.FindBush(location, tile) == null;
			break;
		case TileUtil.TileWork.AxeDeadCrop:
		case TileUtil.TileWork.AxeLiveCrop:
		{
			TerrainFeature val12 = null;
			if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(tile, out val12))
			{
				HoeDirt val13 = (HoeDirt)((val12 is HoeDirt) ? val12 : null);
				if (val13 != null)
				{
					num9 = ((val13.crop == null) ? 1 : 0);
					goto IL_08c9;
				}
			}
			num9 = 1;
			goto IL_08c9;
		}
		case TileUtil.TileWork.AxeTwig:
		case TileUtil.TileWork.AxeWeed:
		case TileUtil.TileWork.AxeFence:
			flag = !location.objects.ContainsKey(tile);
			break;
		case TileUtil.TileWork.MachineLoad:
		{
			Object val5 = null;
			if (location.objects.TryGetValue(tile, out val5) && val5 != null && !((NetFieldBase<bool, NetBool>)(object)val5.readyForHarvest).Value)
			{
				if (((NetFieldBase<Object, NetRef<Object>>)(object)val5.heldObject).Value == null && val5.MinutesUntilReady <= 0)
				{
					Vector2? machineOutcomeTile = MachineOutcomeTile;
					if (machineOutcomeTile.HasValue && !(machineOutcomeTile.GetValueOrDefault() != tile) && MachineOutcomeProbed && !MachineOutcomePlaced)
					{
						num4 = (MachineOutcomeConsumed ? 1 : 0);
						goto IL_08a8;
					}
				}
				num4 = 1;
			}
			else
			{
				num4 = 0;
			}
			goto IL_08a8;
		}
		case TileUtil.TileWork.SprinklerPlace:
		{
			Object val2 = null;
			flag = location.objects.TryGetValue(tile, out val2) && val2 != null && val2.GetBaseRadiusForSprinkler() >= 0;
			break;
		}
		default:
			{
				flag = ((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).ContainsKey(tile);
				break;
			}
			IL_089d:
			flag = (byte)num2 != 0;
			break;
			IL_08be:
			flag = (byte)num3 != 0;
			break;
			IL_0892:
			flag = (byte)num6 != 0;
			break;
			IL_08b3:
			flag = (byte)num5 != 0;
			break;
			IL_0887:
			flag = (byte)num7 != 0;
			break;
			IL_08df:
			flag = (byte)num8 != 0;
			break;
			IL_08a8:
			flag = (byte)num4 != 0;
			break;
			IL_08d4:
			flag = (byte)num != 0;
			break;
			IL_087c:
			flag = (byte)num10 != 0;
			break;
			IL_08c9:
			flag = (byte)num9 != 0;
			break;
		}
		if (flag)
		{
			PickHits.Remove(tile);
			if (tileWork == TileUtil.TileWork.AxeFruitTreeSapling || tileWork == TileUtil.TileWork.AxeFruitTreeMature || tileWork == TileUtil.TileWork.AxeTreeSapling || tileWork == TileUtil.TileWork.AxeTreeMature || tileWork == TileUtil.TileWork.AxeStump || tileWork == TileUtil.TileWork.AxeGiantCrop || tileWork == TileUtil.TileWork.AxeFence || tileWork == TileUtil.TileWork.AxeBush || tileWork == TileUtil.TileWork.PickStone || tileWork == TileUtil.TileWork.PickBoulder || tileWork == TileUtil.TileWork.PickClump || tileWork == TileUtil.TileWork.PickFence || tileWork == TileUtil.TileWork.PickObject)
			{
				// 站立的树/树桩/巨大作物/木栅栏、石头/巨岩/矿块/拆掉的实物也都挡路：
				// 清掉这一刻地形变了，站位缓存必须作废，下一格才能按新地形算最近目标。
				// 只作废这一格周围那一小片（整图作废会让大地图上每清一个障碍都重扫一遍全图）。
				TileUtil.InvalidateStandableCache(location, tile);
			}
			if ((tileWork != TileUtil.TileWork.AxeFruitTreeMature && tileWork != TileUtil.TileWork.AxeTreeMature) || !Mod.Config.AxeStumps)
			{
				return;
			}
			TerrainFeature tree2 = TileUtil.GetTree(location, tile);
			if (tree2 != null && TileUtil.IsStump(tree2) && !TileUtil.IsDead(tree2))
			{
				int num11 = (PickHits.TryGetValue(tile, out var value3) ? value3 : 0) + 1;
				PickHits[tile] = num11;
				if (num11 <= 16)
				{
					Current = tile;
				}
				else
				{
					PickHits.Remove(tile);
				}
			}
			return;
		}
		if (IsRepeatWork(tileWork))
		{
			int num12 = (PickHits.TryGetValue(tile, out var value4) ? value4 : 0) + 1;
			PickHits[tile] = num12;
			int num13 = ((tileWork == TileUtil.TileWork.PickWeed) ? 3 : 16);
			if (num12 <= num13)
			{
				Current = tile;
				return;
			}
			PickHits.Remove(tile);
		}
		if ((tileWork == TileUtil.TileWork.ScytheCrop || tileWork == TileUtil.TileWork.ScytheFlower) && !TileUtil.IsIridiumScythe(StartItem))
		{
			Farmer player = Game1.player;
			if (player != null && ApplyHandPick(location, player, tile))
			{
				return;
			}
		}
		if (tileWork == TileUtil.TileWork.MachineLoad)
		{
			Object readyOut = TileUtil.GetReadyMachineOutput(location, tile);
			if (readyOut != null && TileUtil.ReadyMachineBlockedByFullBag((Item)(object)readyOut))
			{
				// 走到跟前才发现背包收不下产物：这格本轮做不了，记完成跳过，
				// 不要甩队尾再失败一轮（旧行为：整列熟机器被静默弹走，玩家只看到"路过没干活"）。
				TileDone.Add(tile);
				return;
			}
		}
		bool flag2 = !RetryRoundUsed;
		bool flag3 = flag2;
		if (flag3)
		{
			flag = (((uint)(tileWork - 40) <= 2u || tileWork == TileUtil.TileWork.PlaceObject) ? true : false);
			flag3 = flag;
		}
		if ((!flag3 || !RetryTimedOutTile(tile, "placefail")) && (tileWork != TileUtil.TileWork.MachineLoad || !RetryTimedOutTile(tile, "machine")))
		{
			FailedSwings++;
		}
	}

	/// <summary>把这一刀的复查推迟几帧：镰刀的实际收割常常比 beginUsing 晚落地
	/// （原版逐帧判定 + 镰刀收割 mod 的补丁），挥完立刻复查会把「其实割到了」当成没割到。
	/// 延迟期间又挥了一刀时，先把上一刀立刻结掉，格子不会丢。</summary>
	private void ScheduleScytheVerify(Vector2 target)
	{
		List<Vector2>? covers = ActiveSwingCovers;
		ActiveSwingCovers = null;
		if (PendingScytheCovers != null)
		{
			FlushScytheVerify();
		}
		if (covers == null)
		{
			ScytheWorkBefore.Clear();
			return;
		}
		PendingScytheTarget = target;
		PendingScytheCovers = covers;
		PendingScytheBefore.Clear();
		PendingScytheBefore.UnionWith(ScytheWorkBefore);
		ScytheWorkBefore.Clear();
		ScytheVerifyDelay = 8;
	}

	private void FlushScytheVerify()
	{
		List<Vector2>? covers = PendingScytheCovers;
		PendingScytheCovers = null;
		ScytheVerifyDelay = 0;
		if (covers == null)
		{
			PendingScytheBefore.Clear();
			return;
		}
		GameLocation location = Location;
		if (location == null)
		{
			PendingScytheBefore.Clear();
			return;
		}
		RequeueScytheMisses(location, PendingScytheTarget, covers, PendingScytheBefore);
		PendingScytheBefore.Clear();
	}

	private void RequeueScytheMisses(GameLocation location, Vector2 target, List<Vector2> activeSwingCovers, HashSet<Vector2> workBefore)
	{
		if (activeSwingCovers == null)
		{
			workBefore.Clear();
			return;
		}
		// 整刀白挥：挥前有活的格，挥完一格都没消掉。多半是站位/朝向没对准（站在原地、
		// 半格偏移、被走路推了一把），不是这一格做不成。第一次白挥不记失败次数，直接补回重挥；
		// 连白两次才按普通失败算，免得「明明能割到」的格子被 3 次机会耗死、气泡消失作物还在。
		bool vainSwing = false;
		if (workBefore.Count > 0)
		{
			int stillAlive = 0;
			foreach (Vector2 before in workBefore)
			{
				TileUtil.TileWork workNow = TileUtil.GetTileWork(location, before, StartItem, Mod.Config);
				if (workNow != TileUtil.TileWork.None && TileUtil.IsWanted(workNow, Mod.Config))
				{
					stillAlive++;
				}
			}
			vainSwing = stillAlive == workBefore.Count;
		}
		workBefore.Clear();
		bool forgive = vainSwing && ScytheVainSwings < 2;
		if (vainSwing)
		{
			ScytheVainSwings++;
			if (forgive)
			{
				Vector2 at = ((Game1.player != null) ? ((Character)Game1.player).Tile : target);
			}
		}
		else
		{
			ScytheVainSwings = 0;
		}
		Rectangle? selectionBox = SelectionBox;
		List<Vector2> list = new List<Vector2>();
		foreach (Vector2 item in activeSwingCovers)
		{
			if (selectionBox.HasValue)
			{
				Rectangle valueOrDefault = selectionBox.GetValueOrDefault();
				if (item.X < (float)(valueOrDefault).Left || item.X >= (float)(valueOrDefault).Right || item.Y < (float)(valueOrDefault).Top || item.Y >= (float)(valueOrDefault).Bottom)
				{
					continue;
				}
			}
			if (!Planned.ContainsKey(item) && !Tiles.Contains(item))
			{
				continue;
			}
			TileUtil.TileWork tileWork = TileUtil.GetTileWork(location, item, StartItem, Mod.Config);
			if (tileWork == TileUtil.TileWork.None || !TileUtil.IsWanted(tileWork, Mod.Config))
			{
				TileDone.Add(item);
				continue;
			}
			TileDone.Remove(item);
			if (!TileDone.Contains(item) && Tiles.IndexOf(item) < Index)
			{
				TileAttempts.TryGetValue(item, out var value);
				if (value >= 3 && !forgive)
				{
					FailedTiles.Add(item);
					Skipped++;
				}
				else
				{
					if (!forgive)
					{
						TileAttempts[item] = value + 1;
					}
					list.Add(item);
				}
			}
		}
		if (list.Count == 0)
		{
			return;
		}
		// 作物批的主路线是带子 S（BuildBandSWorkOrder），补回来的格也必须按同一套排；
		// 按「离这一刀的落点最近」排会把它们塞成另一条逆向路线，实机表现就是补漏时来回横穿。
		if (ScytheCropRoute && TileUtil.IsScythe(StartItem) && list.All((Vector2 t) => IsScytheCropTile(t)))
		{
			Vector2 fromTile = ((Game1.player != null) ? ((Character)Game1.player).Tile : target);
			list = TileUtil.BuildBandSWorkOrder(list, fromTile, 3);
		}
		else
		{
			list.Sort(delegate(Vector2 a, Vector2 b)
			{
				int num2 = Math.Abs((int)a.Y - (int)target.Y).CompareTo(Math.Abs((int)b.Y - (int)target.Y));
				return (num2 == 0) ? Math.Abs((int)a.X - (int)target.X).CompareTo(Math.Abs((int)b.X - (int)target.X)) : num2;
			});
		}
		int num = Math.Min(Index, Tiles.Count);
		foreach (Vector2 item2 in list)
		{
			Tiles.Insert(num, item2);
			num++;
		}
	}

	private static bool IsTreeStillStanding(GameLocation location, Vector2 tile)
	{
		TerrainFeature tree = TileUtil.GetTree(location, tile);
		if (tree != null)
		{
			return !TileUtil.IsStump(tree);
		}
		return false;
	}

	private static string DescribeWork(TileUtil.TileWork work)
	{
		string result = work switch
		{
			TileUtil.TileWork.Till => "翻地", 
			TileUtil.TileWork.Weed => "清杂草", 
			TileUtil.TileWork.ArtifactSpot => "挖遗迹斑点", 
			TileUtil.TileWork.SeedSpot => "挖种子斑点", 
			TileUtil.TileWork.WaterDirt => "浇空地", 
			TileUtil.TileWork.WaterCrop => "浇作物", 
			TileUtil.TileWork.Sow => "下种", 
			TileUtil.TileWork.ScytheCrop => "收割作物", 
			TileUtil.TileWork.ScytheFlower => "收花", 
			TileUtil.TileWork.ScytheForage => "拾取采集品", 
			TileUtil.TileWork.ScytheMoss => "收苔藓", 
			TileUtil.TileWork.ScytheBluegrass => "割蓝草", 
			TileUtil.TileWork.ScytheGrass => "割草", 
			TileUtil.TileWork.ScytheDeadCrop => "清枯萎作物", 
			TileUtil.TileWork.ScytheWeed => "清杂草", 
			TileUtil.TileWork.ScytheMachine => "收机器产物",
			TileUtil.TileWork.ScythePond => "收鱼塘产物", 
			TileUtil.TileWork.ShakeBush => "摇浆果丛", 
			TileUtil.TileWork.HarvestFruit => "摇果树", 
			TileUtil.TileWork.Fertilize => "施肥", 
			TileUtil.TileWork.SaplingPlace => "种树苗", 
			TileUtil.TileWork.PickDeadCrop => "清枯萎作物", 
			TileUtil.TileWork.PickEmptyDirt => "填平耕地", 
			TileUtil.TileWork.PickFertilizedDirt => "填平施肥耕地", 
			TileUtil.TileWork.PickWeed => "清杂草", 
			TileUtil.TileWork.PickFloor => "清地板", 
			TileUtil.TileWork.PickClump => "敲巨石", 
			TileUtil.TileWork.PickObject => "收回实物", 
			TileUtil.TileWork.PickStone => "挖矿", 
			TileUtil.TileWork.PickBoulder => "敲巨石", 
			TileUtil.TileWork.PickFence => "拆栅栏", 
			TileUtil.TileWork.AxeFruitTreeSeed => "清果树种子", 
			TileUtil.TileWork.AxeFruitTreeSapling => "清果树苗", 
			TileUtil.TileWork.AxeFruitTreeMature => "砍果树", 
			TileUtil.TileWork.AxeTreeSeed => "清树种", 
			TileUtil.TileWork.AxeTreeSapling => "清树苗", 
			TileUtil.TileWork.AxeTreeMature => "砍树",
			TileUtil.TileWork.AxeTapper => "摘采集器",
			TileUtil.TileWork.AxeStump => "清树桩", 
			TileUtil.TileWork.AxeDeadCrop => "清枯萎作物", 
			TileUtil.TileWork.AxeLiveCrop => "清活作物", 
			TileUtil.TileWork.AxeGiantCrop => "收巨大作物", 
			TileUtil.TileWork.AxeTwig => "清树枝", 
			TileUtil.TileWork.AxeWeed => "清杂草", 
			TileUtil.TileWork.AxeFence => "拆木栅栏", 
			TileUtil.TileWork.AxeBush => "清除灌木", 
			TileUtil.TileWork.MachineLoad => "装载", 
			TileUtil.TileWork.SprinklerPlace => "放洒水器", 
			TileUtil.TileWork.PlaceObject => "摆实物", 
			_ => "无", 
		};
		return result;
	}

	private void FinishReport()
	{
		GameLocation location = Location;
		// Stop() 会把分工表清掉，混合跑的账要先记下来。
		bool mixedRun = Plan != null;
		int toolSwitches = ToolSwitches;
		bool flag = StartItem is WateringCan;
		bool flag2 = TileUtil.IsSowSeed(StartItem);
		bool flag3 = TileUtil.IsScythe(StartItem);
		bool flag4 = TileUtil.IsPickaxe(StartItem);
		bool flag5 = TileUtil.IsAxe(StartItem);
		bool flag6 = TileUtil.IsMachineLoadItem(StartItem);
		bool flag7 = TileUtil.IsSprinklerItem(StartItem);
		bool flag8 = TileUtil.IsFertilizerItem(StartItem);
		bool flag9 = TileUtil.IsPlaceableObjectItem(StartItem);
		int tilled = Tilled;
		int splashTilled = SplashTilled;
		// 结账的三个数一律按「收工那一刻的真实世界状态」重算，不用累加计数器：
		// Skipped / FailedTiles / UnreachableTiles 只增不减，补漏、收工前重扫、镰刀复查
		// 把格子真做完了它们也不知道，于是「处理 40 格 / 跳过 3 格」加起来会超过计划总数
		// （实机 2026-11-02：补查补上了，提示里还是照报跳过）。
		int skipped;
		int count;
		int failedSwings = FailedSwings;
		int count2;
		int refills = Refills;
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		// 扫 Planned 时攒：到最后还没做成的格数，以及其中记过原因的（无法到达 / 落空）。
		int pending = 0;
		int pendingUnreachable = 0;
		int pendingFailed = 0;
		if (location != null)
		{
			TerrainFeature val2 = null;
			TerrainFeature val3 = null;
			Object val4 = null;
			Object val5 = null;
			Object val6 = null;
			TerrainFeature val8 = null;
			TerrainFeature val10 = null;
			TerrainFeature val11 = null;
			foreach (KeyValuePair<Vector2, TileUtil.TileWork> item in Planned)
			{
				Vector2 key = item.Key;
				bool flag10 = ((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).ContainsKey(key);
				bool flag11 = !location.objects.ContainsKey(key);
				// pot 版查找：花盆里的土不在 terrainFeatures 里，只看那个会把浇好的花盆算成没浇上。
				HoeDirt wateredDirt = location.GetHoeDirtAtTile(key);
				int num4 = ((wateredDirt != null && wateredDirt.state.Value == 1) ? 1 : 0);
				goto IL_06df;
				IL_0235:
				int num5;
				bool flag12 = (byte)num5 != 0;
				bool flag13 = ((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(key, out val2) && val2 is Flooring;
				TerrainFeature tree = TileUtil.GetTree(location, key);
				bool flag14 = tree != null && !TileUtil.IsStump(tree);
				bool flag15 = (tree != null && TileUtil.IsStump(tree)) || TileUtil.FindAxeClump(location, key) != null;
				int num6;
				if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(key, out val3))
				{
					HoeDirt val13 = (HoeDirt)((val3 is HoeDirt) ? val3 : null);
					if (val13 != null)
					{
						num6 = ((val13.crop == null) ? 1 : 0);
						goto IL_02f0;
					}
				}
				num6 = 1;
				goto IL_02f0;
				IL_07d5:
				int num7;
				bool flag16 = (byte)num7 != 0;
				int num8;
				if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(key, out val11))
				{
					Tree val14 = (Tree)((val11 is Tree) ? val11 : null);
					if (val14 != null)
					{
						num8 = ((!((NetFieldBase<bool, NetBool>)(object)val14.hasMoss).Value) ? 1 : 0);
						goto IL_0741;
					}
				}
				num8 = 0;
				goto IL_0741;
				IL_0741:
				bool flag17 = (byte)num8 != 0;
				bool flag18 = !((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).ContainsKey(key);
				if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(key, out val10))
				{
					HoeDirt val15 = (HoeDirt)((val10 is HoeDirt) ? val10 : null);
					if (val15 != null && val15.crop != null)
					{
						num5 = ((!((NetFieldBase<bool, NetBool>)(object)val15.crop.dead).Value) ? 1 : 0);
						goto IL_0235;
					}
				}
				num5 = 1;
				goto IL_0235;
				IL_0674:
				int num9;
				bool flag19 = (byte)num9 != 0;
				if (((NetDictionary<Vector2, TerrainFeature, NetRef<TerrainFeature>, SerializableDictionary<Vector2, TerrainFeature>, NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>>)(object)location.terrainFeatures).TryGetValue(key, out val8))
				{
					HoeDirt val16 = (HoeDirt)((val8 is HoeDirt) ? val8 : null);
					if (val16 != null)
					{
						num7 = ((val16.crop == null || !val16.readyForHarvest()) ? 1 : 0);
						goto IL_07d5;
					}
				}
				num7 = 0;
				goto IL_07d5;
				IL_06df:
				bool flag20 = (byte)num4 != 0;
				HoeDirt sownDirt = location.GetHoeDirtAtTile(key);
				num9 = ((sownDirt != null && sownDirt.crop != null) ? 1 : 0);
				goto IL_0674;
				IL_02f0:
				bool flag21 = (byte)num6 != 0;
				int num10;
				if (location.objects.TryGetValue(key, out val4) && val4 != null && !((NetFieldBase<bool, NetBool>)(object)val4.readyForHarvest).Value)
				{
					Vector2? machineOutcomeTile = MachineOutcomeTile;
					Vector2 val18 = key;
					num10 = ((!machineOutcomeTile.HasValue || machineOutcomeTile.GetValueOrDefault() != val18 || !MachineOutcomeProbed || MachineOutcomePlaced || MachineOutcomeConsumed) ? 1 : 0);
				}
				else
				{
					num10 = 0;
				}
				bool flag22 = (byte)num10 != 0;
				bool flag23 = location.objects.TryGetValue(key, out val5) && val5 != null && val5.GetBaseRadiusForSprinkler() >= 0;
				bool flag24 = !location.objects.TryGetValue(key, out val6) || val6 == null || !((NetFieldBase<bool, NetBool>)(object)val6.readyForHarvest).Value || ((NetFieldBase<Object, NetRef<Object>>)(object)val6.heldObject).Value == null;
				// pot 版查找：花盆里的肥料不在 terrainFeatures 里，只看那个会把放好的花盆算成没放上。
				// 树肥同理：结果在 Tree.fertilized 上。
				HoeDirt fertDirt = location.GetHoeDirtAtTile(key);
				int num11 = (((fertDirt != null && fertDirt.HasFertilizer()) || TileUtil.IsTreeFertilized(location, key)) ? 1 : 0);
				goto IL_043a;
				IL_043a:
				bool flag25 = (byte)num11 != 0;
				bool flag26 = TileUtil.GetTree(location, key) != null || TileUtil.FindBush(location, key) != null;
				bool flag27 = location.objects.ContainsKey(key) || PlacedTiles.Contains(key);
				bool flag28;
				switch (item.Value)
				{
				case TileUtil.TileWork.Weed:
				case TileUtil.TileWork.ArtifactSpot:
				case TileUtil.TileWork.SeedSpot:
					flag28 = flag11;
					break;
				case TileUtil.TileWork.WaterDirt:
				case TileUtil.TileWork.WaterCrop:
					flag28 = flag20;
					break;
				case TileUtil.TileWork.Sow:
					flag28 = flag19;
					break;
				case TileUtil.TileWork.ScytheCrop:
				case TileUtil.TileWork.ScytheFlower:
				case TileUtil.TileWork.ScytheDeadCrop:
					flag28 = flag16;
					break;
				case TileUtil.TileWork.ScytheForage:
				case TileUtil.TileWork.ScytheWeed:
					flag28 = flag11;
					break;
				case TileUtil.TileWork.ScytheMoss:
					flag28 = flag17;
					break;
				case TileUtil.TileWork.ScytheBluegrass:
				case TileUtil.TileWork.ScytheGrass:
					flag28 = flag18;
					break;
				case TileUtil.TileWork.ScytheMachine:
					flag28 = flag24;
					break;
				case TileUtil.TileWork.ScythePond:
					flag28 = TileUtil.GetPondWithOutputAt(location, key) == null;
					break;
				case TileUtil.TileWork.ShakeBush:
					flag28 = !TileUtil.IsBerryBushReady(TileUtil.BushAt(location, key));
					break;
				case TileUtil.TileWork.HarvestFruit:
					flag28 = !TileUtil.IsFruitTreeReady((FruitTree)((TileUtil.GetTree(location, key) is FruitTree ftk) ? ftk : null));
					break;
				case TileUtil.TileWork.Fertilize:
					flag28 = flag25;
					break;
				case TileUtil.TileWork.SaplingPlace:
					flag28 = flag26;
					break;
				case TileUtil.TileWork.PlaceObject:
					flag28 = flag27;
					break;
				case TileUtil.TileWork.PickDeadCrop:
					flag28 = flag12;
					break;
				case TileUtil.TileWork.PickEmptyDirt:
				case TileUtil.TileWork.PickFertilizedDirt:
					flag28 = !flag10;
					break;
				case TileUtil.TileWork.PickFloor:
					flag28 = !flag13;
					break;
				case TileUtil.TileWork.PickWeed:
				case TileUtil.TileWork.PickObject:
				case TileUtil.TileWork.PickStone:
				case TileUtil.TileWork.PickBoulder:
					flag28 = flag11;
					break;
				case TileUtil.TileWork.PickClump:
					flag28 = TileUtil.FindResourceClump(location, key) == null;
					break;
				case TileUtil.TileWork.PickFence:
					flag28 = flag11;
					break;
				case TileUtil.TileWork.AxeFruitTreeMature:
				case TileUtil.TileWork.AxeTreeMature:
					flag28 = !flag14;
					break;
				case TileUtil.TileWork.AxeStump:
					flag28 = !flag15;
					break;
				case TileUtil.TileWork.AxeFruitTreeSeed:
				case TileUtil.TileWork.AxeFruitTreeSapling:
				case TileUtil.TileWork.AxeTreeSeed:
				case TileUtil.TileWork.AxeTreeSapling:
					flag28 = tree == null;
					break;
				case TileUtil.TileWork.AxeGiantCrop:
					flag28 = TileUtil.FindResourceClump(location, key) == null;
					break;
				case TileUtil.TileWork.AxeDeadCrop:
				case TileUtil.TileWork.AxeLiveCrop:
					flag28 = flag21;
					break;
				case TileUtil.TileWork.AxeTwig:
				case TileUtil.TileWork.AxeWeed:
				case TileUtil.TileWork.AxeFence:
					flag28 = flag11;
					break;
				case TileUtil.TileWork.AxeBush:
					flag28 = TileUtil.FindBush(location, key) == null;
					break;
				case TileUtil.TileWork.MachineLoad:
					flag28 = flag22;
					break;
				case TileUtil.TileWork.SprinklerPlace:
					flag28 = flag23;
					break;
				default:
					flag28 = flag10;
					break;
				}
				if (!flag28)
				{
					// 这一格到最后还是没做成：它才是真正的「跳过」。
					// 无法到达 / 落空各自单列，三个数加起来正好等于没做成的格数。
					pending++;
					if (UnreachableTiles.Contains(key))
					{
						pendingUnreachable++;
					}
					else if (FailedTiles.Contains(key))
					{
						pendingFailed++;
					}
					continue;
				}
				num++;
				// 这一格最终做成了（很可能是在补漏 / 收工前重扫 / 镰刀复查里补上的）：
				// 之前记下的「走不到」「挥空」都要撤回来，否则做完了还照样往报告里算。
				UnreachableTiles.Remove(key);
				FailedTiles.Remove(key);
				if (!(flag3 | flag4 | flag5 | flag6 | flag7 | flag8 | flag9))
				{
					if (flag10)
					{
						num2++;
					}
					else
					{
						num3++;
					}
				}
			}
		}
		skipped = pending - pendingUnreachable - pendingFailed;
		if (skipped < 0)
		{
			skipped = 0;
		}
		count = pendingFailed;
		count2 = pendingUnreachable;
		Stop();
		string reportVerb = ReportVerb;
		Notify(I18n.T("report-done", new
		{
			verb = reportVerb,
			count = num
		}));
		if (count2 > 0)
		{
			Notify(I18n.T("report-unreachable", new
			{
				count = count2
			}));
		}
		if (count > 0)
		{
			Notify(I18n.T("report-failed", new { count }));
		}
		Mod.OpenBackpackAfterTask();
	}

	/// <summary>框里一格活都没有时，说清楚是「哪一条」卡住的：取格数最多的那个原因。
	/// 只说原因，不带「请先清背包 / 请打开某个开关」这类操作建议。</summary>
	private static string DescribeNoWorkReason(Item? item, Dictionary<TileUtil.TillBlock, int> blocks)
	{
		TileUtil.TillBlock top = TileUtil.TillBlock.None;
		int topCount = 0;
		foreach (KeyValuePair<TileUtil.TillBlock, int> block in blocks)
		{
			if (block.Value > topCount)
			{
				top = block.Key;
				topCount = block.Value;
			}
		}
		if (top != TileUtil.TillBlock.None)
		{
			return I18n.T(NoWorkReasonKey(top), new { count = topCount });
		}
		return I18n.T(NoWorkReasonKeyForTool(item));
	}

	private static string NoWorkReasonKey(TileUtil.TillBlock block)
	{
		switch (block)
		{
		case TileUtil.TillBlock.NoMap:
			return "nowork-no-map";
		case TileUtil.TillBlock.OutOfBounds:
			return "nowork-out-of-bounds";
		case TileUtil.TillBlock.Occupied:
			return "nowork-occupied";
		case TileUtil.TillBlock.ResourceClump:
			return "nowork-resource-clump";
		case TileUtil.TillBlock.NotDiggable:
			return "nowork-not-diggable";
		case TileUtil.TillBlock.NotReachable:
			return "nowork-not-reachable";
		case TileUtil.TillBlock.NotSelected:
			return "nowork-not-selected";
		case TileUtil.TillBlock.NotSprinklered:
			return "nowork-not-sprinklered";
		case TileUtil.TillBlock.BagFull:
			return "nowork-bag-full";
		default:
			return "nowork-wrong-tool";
		}
	}

	/// <summary>没有格子级原因可报时（比如这一片本来就没有活儿），按手里的东西说清楚它没什么可做。</summary>
	private static string NoWorkReasonKeyForTool(Item? item)
	{
		if (item is WateringCan)
		{
			return "nowork-no-water";
		}
		if (TileUtil.IsSowSeed(item))
		{
			return "nowork-no-sow";
		}
		if (TileUtil.IsScythe(item))
		{
			return "nowork-no-harvest";
		}
		if (item is Axe)
		{
			return "nowork-no-chop";
		}
		if (item is Pickaxe)
		{
			return "nowork-no-mine";
		}
		if (TileUtil.IsFertilizerItem(item))
		{
			return "nowork-no-fertilize";
		}
		if (TileUtil.IsSprinklerItem(item))
		{
			return "nowork-no-sprinkler";
		}
		if (TileUtil.IsPlaceableObjectItem(item))
		{
			return "nowork-no-place";
		}
		if (TileUtil.IsMachineLoadItem(item))
		{
			return "nowork-no-load";
		}
		return "nowork-nothing";
	}

	private static string DoneVerbKey(Item? item)
	{
		if (!TileUtil.IsSaplingItem(item))
		{
			if (!TileUtil.IsFertilizerItem(item))
			{
				if (!TileUtil.IsPlaceableObjectItem(item))
				{
					if (!TileUtil.IsSowSeed(item))
					{
						if (!(item is WateringCan))
						{
							if (!TileUtil.IsScythe(item))
							{
								if (!TileUtil.IsPickaxe(item))
								{
									if (!TileUtil.IsAxe(item))
									{
										if (!TileUtil.IsSprinklerItem(item))
										{
											if (!TileUtil.IsMachineLoadItem(item))
											{
												return "verb-done-till";
											}
											return "verb-done-load";
										}
										return "verb-done-sprinkler";
									}
									return "verb-done-chop";
								}
								return "verb-done-mine";
							}
							return "verb-done-harvest";
						}
						return "verb-done-water";
					}
					return "verb-done-sow";
				}
				return "verb-done-place";
			}
			return "verb-done-fertilize";
		}
		return "verb-done-plant";
	}


	/// <summary>
	/// 队列的日常提示（已暂停 / 体力不够 / 该区域无法工作 / 种子用完 / 本轮结束汇总…）。
	/// 只在游戏里飘一条 HUD，不再往 SMAPI 日志写 WARN —— 那些是正常收工而不是警告，
	/// 一晚上能刷几十行。要排查时把配置里的 DebugLog 打开，会以 TRACE 记下来。
	/// </summary>
	private void Notify(string message, bool isError = false)
	{
		try
		{
			Game1.addHUDMessage(new HUDMessage(message, isError ? 3 : 0));
		}
		catch
		{
		}
		if (Mod.Config.DebugLog)
		{
			((Mod)Mod).Monitor.Log(message, LogLevel.Trace);
		}
	}

	private void TickTrail(Farmer player)
	{
		Item currentItem = player.CurrentItem;
		if (currentItem == null)
		{
			Stop(I18n.T("stop-nothing-in-hand"));
			return;
		}
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation == null)
		{
			Stop(I18n.T("stop-paused"));
			return;
		}
		if (Walking)
		{
			WalkTicks++;
			Point? walkDestination = WalkDestination;
			if (walkDestination.HasValue)
			{
				Point valueOrDefault = walkDestination.GetValueOrDefault();
				if (IsSettledOn(player, new Vector2((float)valueOrDefault.X, (float)valueOrDefault.Y)))
				{
					Mod.Walk.Stop();
					((Character)player).controller = null;
					Walking = false;
					WalkingByDriver = false;
					WalkTicks = 0;
					WalkUnstuckTried = false;
					goto IL_04eb;
				}
			}
			if (WalkingByDriver)
			{
				walkDestination = WalkDestination;
				if (walkDestination.HasValue)
				{
					Point valueOrDefault2 = walkDestination.GetValueOrDefault();
					if (WalkTicks >= 40 && (Mod.Walk.Stalled || Mod.Walk.InputCallbacks == 0))
					{
						if (TryRecoverFromSqueeze(player, currentLocation))
						{
							return;
						}
						if (TileUtil.DynamicBlockerNear(currentLocation, ((Character)player).Tile))
						{
							if (WalkTicks > 300)
							{
								AbandonTrailTile(player, "被动物/角色堵死");
							}
							return;
						}
						Mod.Walk.Stop();
						WalkingByDriver = false;
						WalkTicks = 0;
						try
						{
							((Character)player).controller = new PathFindController((Character)player, currentLocation, valueOrDefault2, -1);
							return;
						}
						catch
						{
							AbandonTrailTile(player, "原版寻路也构造失败");
							return;
						}
					}
				}
			}
			if (!WalkingByDriver && ((Character)player).controller == null && WalkTicks > 30)
			{
				if (!TryRecoverFromSqueeze(player, currentLocation))
				{
					AbandonTrailTile(player, "原版寻路走不到");
				}
			}
			else if (WalkTicks > 300 && !TryRecoverFromSqueeze(player, currentLocation))
			{
				AbandonTrailTile(player, "超时");
			}
			return;
		}
		goto IL_04eb;
		IL_04eb:
		while (TrailCursor < TrailRoute.Count)
		{
			Vector2 val = TrailRoute[TrailCursor];
			bool num;
			if (!TileUtil.IsSprinklerItem(currentItem) && !TileUtil.IsPlaceableObjectItem(currentItem))
			{
				if (TileUtil.IsSowSeed(currentItem))
				{
					goto IL_038f;
				}
				num = TileUtil.GetTileWork(currentLocation, val, currentItem, Mod.Config) != TileUtil.TileWork.None;
			}
			else
			{
				num = TileUtil.NeedsPlacement(currentLocation, val, currentItem);
			}
			if (!num)
			{
				TrailCursor++;
				continue;
			}
			goto IL_038f;
			IL_038f:
			Vector2? val2 = PickTrailStand(player, currentLocation, val);
			if (!val2.HasValue)
			{
				RetryTiles.Add(val);
				TrailCursor++;
				continue;
			}
			if (!IsSettledOn(player, val2.Value))
			{
				StartTrailWalk(player, currentLocation, val2.Value);
				return;
			}
			Mod.Walk.Stop();
			((Character)player).controller = null;
			Walking = false;
			WalkingByDriver = false;
			WalkTicks = 0;
			((Character)player).FacingDirection = TileUtil.GetFacingDirection(((Character)player).Tile, val);
			if (ApplyPlaceItem(player, currentItem, val))
			{
				Tilled++;
			}
			else
			{
				FailedSwings++;
				RetryTiles.Add(val);
			}
			TrailCursor++;
			return;
		}
		if (RetryTiles.Count > 0 && !RetryRoundUsed)
		{
			RetryRoundUsed = true;
			List<Vector2> collection = TileUtil.BuildPathWorkOrder(RetryTiles, ((Character)player).Tile, Location ?? ((Character)player).currentLocation, SealPlacedTiles);
			RetryTiles.Clear();
			TrailRoute.Clear();
			TrailRoute.AddRange(collection);
			TrailCursor = 0;
		}
		else
		{
			FinishReport();
		}
	}

	private Vector2? PickTrailStand(Farmer player, GameLocation location, Vector2 target)
	{
		Vector2? val = TrailSideStand(player, location, target);
		if (val.HasValue)
		{
			return val.GetValueOrDefault();
		}
		if (TrailCursor + 1 < TrailRoute.Count)
		{
			Vector2 val2 = TrailRoute[TrailCursor + 1];
			if (TileUtil.ChebyshevDistance(val2, target) <= 1f && TileUtil.IsStandable(location, val2))
			{
				return val2;
			}
		}
		else
		{
			val = TrailExit;
			if (val.HasValue)
			{
				Vector2 valueOrDefault = val.GetValueOrDefault();
				if (TileUtil.ChebyshevDistance(valueOrDefault, target) <= 1f && TileUtil.IsStandable(location, valueOrDefault))
				{
					return valueOrDefault;
				}
			}
		}
		if (((Character)player).Tile != target && TileUtil.ChebyshevDistance(((Character)player).Tile, target) <= 1f)
		{
			return ((Character)player).Tile;
		}
		Vector2? val3 = TileUtil.FindFreeNeighbor(location, target, new HashSet<Vector2>(TrailRoute), ((Character)player).Tile);
		if (val3.HasValue)
		{
			return val3.GetValueOrDefault();
		}
		Vector2 val4 = default(Vector2);
		for (int i = -1; i <= 1; i++)
		{
			for (int j = -1; j <= 1; j++)
			{
				if (j != 0 || i != 0)
				{
					val4 = new Vector2(target.X + (float)j, target.Y + (float)i);
					if (!TrailRoute.Contains(val4) && TileUtil.IsStandable(location, val4))
					{
						return val4;
					}
				}
			}
		}
		return null;
	}

	private Vector2? TrailSideStand(Farmer player, GameLocation location, Vector2 target)
	{
		if (!TileUtil.IsSingleLine(TrailRoute, out var horizontal))
		{
			return null;
		}
		int num = (horizontal ? ((!(((Character)player).Tile.Y < target.Y)) ? 1 : (-1)) : ((!(((Character)player).Tile.X < target.X)) ? 1 : (-1)));
		int[] array = new int[2]
		{
			num,
			-num
		};
		int[] array2 = array;
		foreach (int num2 in array2)
		{
			Vector2 val = (horizontal ? new Vector2(target.X, target.Y + (float)num2) : new Vector2(target.X + (float)num2, target.Y));
			if (((Character)player).Tile == val)
			{
				return val;
			}
			if (!TrailRoute.Contains(val) && TileUtil.IsStandable(location, val))
			{
				return val;
			}
		}
		return null;
	}

	private void StartTrailWalk(Farmer player, GameLocation location, Vector2 stand)
	{
		Point val = default(Point);
		val = new Point((int)stand.X, (int)stand.Y);
		WalkDestination = val;
		WalkTicks = 0;
		if (Mod.Walk.TryStart(location, player, val))
		{
			Walking = true;
			WalkingByDriver = true;
			return;
		}
		try
		{
			((Character)player).controller = new PathFindController((Character)player, location, val, -1);
			Walking = true;
			WalkingByDriver = false;
		}
		catch
		{
			Walking = false;
		}
	}

	private void AbandonTrailTile(Farmer player, string reason)
	{
		string value = "";
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation != null)
		{
			List<Vector2> list = TileUtil.FindSqueezingObjects(currentLocation, player);
			if (list.Count > 0)
			{
				value = "；被实体压住：" + DescribeTiles(list) + "（已临时放行）";
			}
			FreeSqueezingTiles(player, list);
		}
		Mod.Walk.Stop();
		((Character)player).controller = null;
		Walking = false;
		WalkingByDriver = false;
		WalkTicks = 0;
		WalkUnstuckTried = false;
		if (TrailCursor < TrailRoute.Count)
		{
			RetryTiles.Add(TrailRoute[TrailCursor]);
			TrailCursor++;
		}
	}

	private bool TryRecoverFromSqueeze(Farmer player, GameLocation location)
	{
		if (WalkUnstuckTried)
		{
			return false;
		}
		List<Vector2> list = TileUtil.FindSqueezingObjects(location, player);
		if (list.Count == 0)
		{
			return false;
		}
		WalkUnstuckTried = true;
		((Mod)Mod).Monitor.Log("被自己放下的东西压住了：" + DescribeTiles(list) + " —— 临时放行后重走这一段", (LogLevel)3);
		FreeSqueezingTiles(player, list);
		Mod.Walk.Stop();
		((Character)player).controller = null;
		Walking = false;
		WalkingByDriver = false;
		Point? walkDestination = WalkDestination;
		if (walkDestination.HasValue)
		{
			Point valueOrDefault = walkDestination.GetValueOrDefault();
			StartTrailWalk(player, location, new Vector2((float)valueOrDefault.X, (float)valueOrDefault.Y));
		}
		return true;
	}

	private void FreeSqueezingTiles(Farmer player, List<Vector2> tiles)
	{
		foreach (Vector2 tile in tiles)
		{
			player.TemporaryPassableTiles.Add(new Rectangle((int)tile.X * 64, (int)tile.Y * 64, 64, 64));
		}
	}

	private static string DescribeTiles(List<Vector2> tiles)
	{
		List<string> list = new List<string>(tiles.Count);
		foreach (Vector2 tile in tiles)
		{
			list.Add($"({tile.X},{tile.Y})");
		}
		return string.Join(" ", list);
	}

	private bool IsSettledOn(Farmer player, Vector2 tile)
	{
		return ((Character)player).Tile == tile;
	}


}
