using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Network;
using StardewValley.Pathfinding;
using StardewValley.Tools;
using Object = StardewValley.Object;

namespace FarmQueue;

internal sealed class AnimalQueue
{
	private enum Mode
	{
		None,
		Pet,
		Shear,
		Milk,
		Collect
	}

	private sealed class Target
	{
		public Character? Critter;

		public Object? GroundItem;

		public Vector2 GroundTile;

		public long Id;

		public int Retries;

		public int Deferrals;

		/// <summary>曾经因为够不着被放弃过；补试成功后要把“无法到达”的计数退回去。</summary>
		public bool GivenUp;

		/// <summary>曾经被计进“跳过”过；补试真做成之后要把这一笔退回来（同一只只计一次）。</summary>
		public bool SkippedOnce;

		public Vector2 Tile
		{
			get
			{
				if (Critter != null)
				{
					return Critter.Tile;
				}
				return GroundTile;
			}
		}

		public string Name
		{
			get
			{
				if (Critter != null)
				{
					return Critter.displayName ?? Critter.Name ?? "?";
				}
				return GroundItem?.displayName ?? "产物";
			}
		}
	}

	private const int WalkTimeoutTicks = 240;


	// 挤奶/剪毛动画是 4 帧×400ms（帧表写死，beginUsing 传的 50f 是废参数；体力≤0 时翻倍到 3200ms），
	// 产出由末帧的 Farmer.useTool 回调交付——超时闸必须留在那一帧之后，否则动画照放、产出永不进包。
	private const int ActionTimeoutTicks = 240;


	private const int MaxWalkAttempts = 8;


	/// <summary>执行期间多久扫一遍范围，把刚走出畜棚/鸡舍的动物补进队列（30 tick ≈ 0.5 秒）。</summary>
	private const int MidScanIntervalTicks = 30;


	private readonly ModEntry Mod;

	private readonly List<Target> Pending = new List<Target>();

	private readonly List<Target> FailedTargets = new List<Target>();

	/// <summary>本次运行已经排进过队列的 Id（含已跳过/已放弃）：收工复查只补真正没排过的新来者。</summary>
	private readonly HashSet<long> QueuedIds = new HashSet<long>();

	/// <summary>
	/// 这一趟已经判定「围栏/建筑把路隔断」的目标 Id：不再补试、收工复查也不再捞回来。
	/// 否则每只动物都要走「初次 + 2 次补试 + 5 轮复查」≈ 8 遍整图规划，
	/// 一栏十几只就是几十上百次必然失败的寻路 —— 玩家看到的就是「卡在栏外 + 疯狂掉帧」。
	/// 门被推开（TickGate 成功）时清空：路通了，这些目标要重新有机会。
	/// </summary>
	private readonly HashSet<long> CutOffIds = new HashSet<long>();

	private Mode mode;

	private GameLocation? Location;

	private Item? StartItem;

	private Item? ExpectedItem;

	private int OriginalToolIndex = -1;

	private Target? Current;

	private bool Walking;

	private bool WalkingByDriver;

	private bool AimWalk;

	private Point WalkDestination;

	private int WalkTicks;

	private int WalkTotalTicks;

	private int ChaseTicks;

	private int LastReplanTick;

	private int Replans;

	private Vector2 WalkLastTile;

	private int ControllerGoneTicks;

	private int WalkAttempts;

	/// <summary>挥动前顶住的 Game1.wasMouseVisibleThisFrame 原值；null = 当前没顶着。</summary>
	private bool? SavedMouseVisible;

	/// <summary>本次挥动要收的那只；等手里的奶桶/剪刀真正开始动画后改回它。</summary>
	private FarmAnimal? PendingHarvestTarget;

	private bool WaitingAction;

	private int ActionTicks;

	private int ActionTicksTotal;

	private int CooldownTicks;

	private int Done;

	private int PetsAlongside;

	private int ItemsCollected;

	private int Skipped;

	private int Sleeping;

	private int Unreachable;

	private int GiveUps;

	private int NoToolSkips;

	private int Deferred;

	private int DeferCooldownTicks;

	private string LastActionVerb = "";

	private int RecheckRounds;

	private int RecheckWaitTicks;

	private int MidScanTicks;

	private bool Finished = true;

	private bool Reported;

	private readonly List<Point> TriedStandTiles = new List<Point>();

	/// <summary>本轮已经去开过的门（每扇门每轮只试一次，免得在两扇门之间来回跑）。</summary>
	private readonly HashSet<Vector2> GatesTried = new HashSet<Vector2>();

	private Fence? GateTarget;

	private Vector2 GateTile;

	private int GatePhase;

	private int GateTicks;

	private int WalkFailCooldownTicks;

	/// <summary>贴身位「地形能站，但被别的动物/村民堵着」。</summary>
	private bool AllApproachBlockedByCritters;

	/// <summary>贴身位被围栏/建筑/水隔断（等多久都不会变）。</summary>
	private bool LastApproachCutOff;

	private bool LastApproachNoCandidates;

	/// <summary>上一次找站位时，「原版 findPath 返回空」已经出现两次以上＝这整片连通区到不了目标
	/// （不是站位挑得不好，而是根本走不过去：关门、围栏围死、隔着水）。</summary>
	private bool LastWalkSawEmptyPath;

	/// <summary>静默暂停（窗口不在前台/有菜单）的上一次记录，免得被当成卡死。</summary>
	private int PauseWatchTick;

	private string? PauseWatchReason;

	private int WatchTick;

	private long WatchSignature = long.MinValue;

	/// <summary>上一轮「贴身位被角色堵死」时，挡在附近的是谁（写进放回日志，便于定位）。</summary>
	private string LastCritterBlocker = "无";

	private Rectangle CollectBounds;

	private bool MovementWasDown;

	public bool IsRunning => !Finished && (Pending.Count > 0 || Current != null || WaitingAction || FailedTargets.Count > 0 || RecheckRounds < 5 || !Reported);

	public int Remaining => Pending.Count + ((Current != null) ? 1 : 0);

	public string ActiveVerb
	{
		get
		{
			Target current = Current;
			if (current != null)
			{
				return VerbFor(current);
			}
			if (!string.IsNullOrEmpty(LastActionVerb))
			{
				return LastActionVerb;
			}
			Mode mode = this.mode;
			string key = mode switch
			{
				Mode.Milk => "verb-milk", 
				Mode.Shear => "verb-shear", 
				Mode.Collect => "verb-collect", 
				_ => "verb-pet", 
			};
			return I18n.T(key);
		}
	}

	public IEnumerable<Character> PendingCritters()
	{
		if (Current?.Critter != null)
		{
			yield return Current.Critter;
		}
		foreach (Target pending in Pending)
		{
			if (pending.Critter != null)
			{
				yield return pending.Critter;
			}
		}
	}

	public static bool IsAnimalModeItem(Item? item)
	{
		if (item == null)
		{
			return true;
		}
		if (item is Shears || item is MilkPail)
		{
			return true;
		}
		if (item is Tool)
		{
			return false;
		}
		if (TileUtil.IsSowSeed(item) || TileUtil.IsScythe(item) || TileUtil.IsSprinklerItem(item) || TileUtil.IsFertilizerItem(item) || TileUtil.IsSaplingItem(item) || TileUtil.IsPlaceableObjectItem(item))
		{
			return false;
		}
		return true;
	}

	public static bool IsPureAnimalItem(Item? item)
	{
		if (item == null)
		{
			return true;
		}
		return IsHarvestTool(item);
	}

	public static bool BoxHasAnimal(GameLocation? location, Rectangle bounds)
	{
		if (location == null || bounds.Width <= 0 || bounds.Height <= 0)
		{
			return false;
		}
		try
		{
			var enumerator = ((NetDictionary<long, FarmAnimal, NetRef<FarmAnimal>, SerializableDictionary<long, FarmAnimal>, NetLongDictionary<FarmAnimal, NetRef<FarmAnimal>>>)(object)location.animals).Values.GetEnumerator();
			try
			{
				while (enumerator.MoveNext())
				{
					FarmAnimal current = enumerator.Current;
					if (current != null && (bounds).Contains(((Character)current).TilePoint))
					{
						return true;
					}
				}
			}
			finally
			{
				((IDisposable)enumerator/*cast due to constrained. prefix*/).Dispose();
			}
			foreach (NPC character in location.characters)
			{
				if (character is Pet && (bounds).Contains(((Character)character).TilePoint))
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	public static bool IsGroundProduce(Object obj)
	{
		if (obj == null || !((NetFieldBase<bool, NetBool>)(object)obj.isSpawnedObject).Value)
		{
			return false;
		}
		string qualifiedItemId = ((Item)obj).QualifiedItemId;
		return !(qualifiedItemId == "(O)590") && !(qualifiedItemId == "(O)SeedSpot");
	}

	/// <summary>
	/// 地面产物（例如猪松露）入包前算品质。植物学家（职业 16）的采集品固定铱星；
	/// 其他玩家仍走原版采集品质。非采集类地面产物保持物品自身品质，不做重算。
	/// </summary>
	private static void ApplyGroundItemQuality(GameLocation location, Farmer player, Object groundItem, Vector2 tile)
	{
		if (location == null || player == null || groundItem == null)
		{
			return;
		}
		bool isForage = groundItem.isForage();
		if (!isForage)
		{
			return;
		}
		if (((NetHashSet<int>)(object)player.professions).Contains(16))
		{
			((Item)groundItem).Quality = 4;
			return;
		}
		((Item)groundItem).Quality = location.GetHarvestSpawnedObjectQuality(player, isForage, tile, Utility.CreateDaySaveRandom((double)tile.X, (double)(tile.Y * 777f), 0.0));
	}

	public static bool BoxHasProduce(GameLocation? location, Rectangle bounds)
	{
		if (location == null || bounds.Width <= 0 || bounds.Height <= 0)
		{
			return false;
		}
		try
		{
			foreach (KeyValuePair<Vector2, Object> pair in location.objects.Pairs)
			{
				if ((bounds).Contains((int)pair.Key.X, (int)pair.Key.Y) && IsGroundProduce(pair.Value))
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	public static bool IsHarvestTool(Item? item)
	{
		if (!(item is Shears))
		{
			return item is MilkPail;
		}
		return true;
	}

	public static bool ReadyForTool(FarmAnimal animal, Tool tool)
	{
		return ((NetFieldBase<string, NetString>)(object)animal.currentProduce).Value != null && animal.isAdult() && animal.CanGetProduceWithTool(tool);
	}

	public static bool BagToolReadyFor(FarmAnimal animal, out Tool? tool)
	{
		Farmer player = Game1.player;
		tool = null;
		if (player == null)
		{
			return false;
		}
		foreach (Item item in player.Items)
		{
			Tool val = (Tool)(object)((item is Tool) ? item : null);
			if (val != null && ReadyForTool(animal, val))
			{
				tool = val;
				return true;
			}
		}
		return false;
	}

	private Tool? HarvestToolFor(FarmAnimal animal)
	{
		Farmer player = Game1.player;
		if (player == null)
		{
			return null;
		}
		foreach (Item item in player.Items)
		{
			Tool val = (Tool)(object)((item is Tool) ? item : null);
			if (val != null && ReadyForTool(animal, val))
			{
				return val;
			}
		}
		return null;
	}

	private bool SelfSwitchToTool(Farmer player, Tool tool)
	{
		if ((object)player.CurrentItem == tool)
		{
			return true;
		}
		for (int i = 0; i < player.Items.Count; i++)
		{
			if ((object)player.Items[i] == tool)
			{
				player.CurrentToolIndex = i;
				ExpectedItem = (Item?)(object)tool;
				return true;
			}
		}
		return false;
	}

	public static bool HasHarvestToolInInventory()
	{
		Farmer player = Game1.player;
		if (player == null)
		{
			return false;
		}
		foreach (Item item in player.Items)
		{
			if (item is Shears || item is MilkPail)
			{
				return true;
			}
		}
		return false;
	}

	internal static bool PetToday(Pet pet)
	{
		try
		{
			Farmer player = Game1.player;
			int num = default(int);
			return player != null && ((NetDictionary<long, int, NetInt, SerializableDictionary<long, int>, NetLongDictionary<int, NetInt>>)(object)pet.lastPetDay).TryGetValue(player.UniqueMultiplayerID, out num) && num == Game1.Date.TotalDays;
		}
		catch
		{
			return false;
		}
	}

	public AnimalQueue(ModEntry mod)
	{
		Mod = mod;
	}

	public void Start(Rectangle bounds, GameLocation location, Item? heldItem)
	{
		Stop();
		if (heldItem == null)
		{
			mode = Mode.Pet;
		}
		else if (heldItem is Shears)
		{
			mode = Mode.Shear;
		}
		else if (heldItem is MilkPail)
		{
			mode = Mode.Milk;
		}
		else if (TileUtil.IsScythe(heldItem))
		{
			mode = Mode.Collect;
		}
		else
		{
			mode = Mode.Pet;
		}
		bool flag = mode == Mode.Collect;
		Rectangle collectBounds = bounds;
		(collectBounds).Inflate(10, 10);
		CollectBounds = collectBounds;
		if (bounds.Width <= 0 || bounds.Height <= 0 || location == null)
		{
			mode = Mode.None;
			return;
		}
		Farmer player = Game1.player;
		if (player == null)
		{
			return;
		}
		Vector2 playerTile = ((Character)player).Tile;
		bool flag2 = ((heldItem != null) ? heldItem.QualifiedItemId : null) == "(O)ButterflyPowder";
		List<Target> list = new List<Target>();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num5 = 0;
		try
		{
			var enumerator = ((NetDictionary<long, FarmAnimal, NetRef<FarmAnimal>, SerializableDictionary<long, FarmAnimal>, NetLongDictionary<FarmAnimal, NetRef<FarmAnimal>>>)(object)location.animals).Values.GetEnumerator();
			try
			{
				while (enumerator.MoveNext())
				{
					FarmAnimal current = enumerator.Current;
					num2++;
					if (current != null && (collectBounds).Contains(((Character)current).TilePoint) && !flag)
					{
						// 与选框标记同判据：今天摸过、又没有产出可收的，不再进队列。
						// 以前只有剪刀/奶桶模式排除，抚摸模式会把已摸过的也排进去，
						// 于是「补选漏掉的那一只」会连已完成的 20 只一起数进“还剩”。
						if (HarvestToolFor(current) == null && ((NetFieldBase<bool, NetBool>)(object)current.wasPet).Value)
						{
							num++;
							continue;
						}
						Target item = new Target
						{
							Critter = (Character)current,
							Id = ((NetFieldBase<long, NetLong>)(object)current.myID).Value
						};
						list.Add(item);
					}
				}
			}
			finally
			{
				((IDisposable)enumerator/*cast due to constrained. prefix*/).Dispose();
			}
			foreach (NPC character in location.characters)
			{
				Pet val = (Pet)(object)((character is Pet) ? character : null);
				if (val == null || !(collectBounds).Contains(((Character)character).TilePoint))
				{
					continue;
				}
				num3++;
				if (!flag)
				{
					if (flag2)
					{
						num++;
						continue;
					}
					if (PetToday(val))
					{
						num++;
						continue;
					}
					Target item2 = new Target
					{
						Critter = (Character?)(object)character,
						Id = ((NetFieldBase<Guid, NetGuid>)(object)val.petId).Value.GetHashCode()
					};
					list.Add(item2);
				}
			}
			foreach (KeyValuePair<Vector2, Object> pair in location.objects.Pairs)
			{
				if ((bounds).Contains((int)pair.Key.X, (int)pair.Key.Y) && IsGroundProduce(pair.Value))
				{
					num5++;
					Target item4 = new Target
					{
						GroundItem = pair.Value,
						GroundTile = pair.Key,
						Id = (long)(pair.Key.X * 4096f + pair.Key.Y)
					};
					list.Add(item4);
				}
			}
		}
		catch (Exception ex)
		{
			((Mod)Mod).Monitor.Log("收集框内动物失败：" + ex.Message, (LogLevel)3);
			return;
		}
		if (list.Count == 0)
		{
			if (num > 0)
			{
				Notify(I18n.T("animal-not-ready", new
				{
					count = num
				}), isError: true);
			}
			else if (num2 + num3 + num5 > 0)
			{
				Notify(I18n.T("animal-box-missed", new
				{
					count = num2 + num3 + num5
				}), isError: true);
			}
			else
			{
				Notify(I18n.T("animal-no-targets"), isError: true);
			}
			mode = Mode.None;
			return;
		}
		// 按「实际要走几步」排，不按直线距离：隔着一道栅栏的那只看上去最近，实际要绕一整圈。
		// 农场动物当空气（推得开），村民/宠物/马照挡；直线距离只在距离场算不出来时兜底。
		int[] orderField = TileUtil.BuildWalkField(location, playerTile, terrainOnly: false, out int orderWidth, out int orderHeight);
		list.Sort(delegate(Target a, Target b)
		{
			int num7 = OrderCost(orderField, orderWidth, orderHeight, a.Tile, playerTile).CompareTo(OrderCost(orderField, orderWidth, orderHeight, b.Tile, playerTile));
			return (num7 != 0) ? num7 : a.Id.CompareTo(b.Id);
		});
		Pending.AddRange(list);
		QueuedIds.Clear();
		CutOffIds.Clear();
		foreach (Target queued in list)
		{
			QueuedIds.Add(queued.Id);
		}
		Location = location;
		StartItem = heldItem;
		ExpectedItem = heldItem;
		try
		{
			Farmer player2 = Game1.player;
			if (player2 != null)
			{
				OriginalToolIndex = player2.CurrentToolIndex;
			}
		}
		catch
		{
			OriginalToolIndex = -1;
		}
		Done = 0;
		PetsAlongside = 0;
		ItemsCollected = 0;
		Skipped = 0;
		Sleeping = 0;
		Unreachable = 0;
		GiveUps = 0;
		Deferred = 0;
		NoToolSkips = 0;
		DeferCooldownTicks = 0;
		FailedTargets.Clear();
		RecheckRounds = 0;
		RecheckWaitTicks = 0;
		MidScanTicks = 0;
		Finished = false;
		Reported = false;
		TriedStandTiles.Clear();
		GatesTried.Clear();
		GateTarget = null;
		GatePhase = 0;
		GateTicks = 0;
		WalkFailCooldownTicks = 0;
		AllApproachBlockedByCritters = false;
		LastApproachCutOff = false;
		LastApproachNoCandidates = false;
		LastWalkSawEmptyPath = false;
		Current = null;
		Walking = false;
		WalkingByDriver = false;
		AimWalk = false;
		WalkDestination = Point.Zero;
		WalkAttempts = 0;
		ChaseTicks = 0;
		Replans = 0;
		LastReplanTick = 0;
		WaitingAction = false;
		ActionTicks = 0;
		ActionTicksTotal = 0;
		CooldownTicks = 0;
		LastActionVerb = "";
		MovementWasDown = TillQueue.IsMovementInputDown();
	}

	public bool ToggleTargetAt(Vector2 tile)
	{
		if (Location == null)
		{
			return false;
		}
		Farmer player = Game1.player;
		if (Current != null && TargetCoversTile(Current, tile))
		{
			TraceTarget(Current, "TOGGLE-OFF");
			FailedTargets.Remove(Current);
			if (Walking && player != null)
			{
				StopWalking(player);
			}
			Current = null;
			CooldownTicks = 2;
			return true;
		}
		for (int i = 0; i < Pending.Count; i++)
		{
			if (TargetCoversTile(Pending[i], tile))
			{
				TraceTarget(Pending[i], "TOGGLE-OFF");
				FailedTargets.Remove(Pending[i]);
				Pending.RemoveAt(i);
				return true;
			}
		}
		Target target = FindTargetAt(tile);
		if (target != null)
		{
			Pending.Insert(0, target);
			TraceTarget(target, "TOGGLE-ON");
			return true;
		}
		return false;
	}

	public int FilterSameKind(Vector2 tile)
	{
		Target target = FindTargetAt(tile);
		string key = ((target != null) ? KindKeyOf(target) : null) ?? ((Current != null && TargetCoversTile(Current, tile)) ? KindKeyOf(Current) : null);
		if (key == null)
		{
			return -1;
		}
		Farmer player = Game1.player;
		int num = 0;
		if (Current != null && KindKeyOf(Current) != key)
		{
			FailedTargets.Remove(Current);
			if (Walking && player != null)
			{
				StopWalking(player);
			}
			Current = null;
			CooldownTicks = 2;
			num++;
		}
		for (int num2 = Pending.Count - 1; num2 >= 0; num2--)
		{
			if (KindKeyOf(Pending[num2]) != key)
			{
				FailedTargets.Remove(Pending[num2]);
				Pending.RemoveAt(num2);
				num++;
			}
		}
		FailedTargets.RemoveAll((Target t) => KindKeyOf(t) != key);
		return num;
	}

	private static bool TargetCoversTile(Target target, Vector2 tile)
	{
		if (target.GroundItem != null)
		{
			return target.GroundTile == tile;
		}
		Character critter = target.Critter;
		if (critter != null)
		{
			return critter.Tile == tile || critter.TilePoint == new Point((int)tile.X, (int)tile.Y);
		}
		return false;
	}

	private Target? FindTargetAt(Vector2 tile)
	{
		GameLocation location = Location;
		if (location == null)
		{
			return null;
		}
		try
		{
			var enumerator = ((NetDictionary<long, FarmAnimal, NetRef<FarmAnimal>, SerializableDictionary<long, FarmAnimal>, NetLongDictionary<FarmAnimal, NetRef<FarmAnimal>>>)(object)location.animals).Values.GetEnumerator();
			try
			{
				while (enumerator.MoveNext())
				{
					FarmAnimal current = enumerator.Current;
					if (current != null && ((Character)current).Tile == tile)
					{
						return new Target
						{
							Critter = (Character)current,
							Id = ((NetFieldBase<long, NetLong>)(object)current.myID).Value
						};
					}
				}
			}
			finally
			{
				((IDisposable)enumerator/*cast due to constrained. prefix*/).Dispose();
			}
			foreach (NPC character in location.characters)
			{
				Pet val = (Pet)(object)((character is Pet) ? character : null);
				if (val != null && ((Character)character).Tile == tile)
				{
					return new Target
					{
						Critter = (Character?)(object)character,
						Id = ((NetFieldBase<Guid, NetGuid>)(object)val.petId).Value.GetHashCode()
					};
				}
			}
			Object val2 = default(Object);
			if (location.objects.TryGetValue(tile, out val2) && IsGroundProduce(val2))
			{
				return new Target
				{
					GroundItem = val2,
					GroundTile = tile,
					Id = (long)(tile.X * 4096f + tile.Y)
				};
			}
		}
		catch
		{
		}
		return null;
	}

	private static string KindKeyOf(Target target)
	{
		if (target.GroundItem != null)
		{
			return "Item:" + ((Item)target.GroundItem).QualifiedItemId;
		}
		Character? critter = target.Critter;
		Pet val = (Pet)(object)((critter is Pet) ? critter : null);
		if (val != null)
		{
			return "Pet:" + ((NetFieldBase<string, NetString>)(object)val.petType).Value;
		}
		Character? critter2 = target.Critter;
		FarmAnimal val2 = (FarmAnimal)(object)((critter2 is FarmAnimal) ? critter2 : null);
		if (val2 != null)
		{
			return "Animal:" + val2.displayType;
		}
		return "?";
	}

	public void Stop(string? message = null, bool isError = true)
	{
		RestoreMouseVisible();
		PendingHarvestTarget = null;
		Mod.Walk.Release();
		if (Walking && !WalkingByDriver)
		{
			Farmer player = Game1.player;
			if (player != null && ((Character)player).controller != null)
			{
				((Character)player).controller = null;
			}
		}
		if (WaitingAction && Game1.player != null && !Game1.player.passedOut)
		{
			Game1.player.completelyStopAnimatingOrDoingAction();
			Game1.player.canMove = !Game1.eventUp;
		}
		if (OriginalToolIndex >= 0 && Game1.player != null)
		{
			try
			{
				Game1.player.CurrentToolIndex = OriginalToolIndex;
			}
			catch
			{
			}
		}
		WalkingByDriver = false;
		AimWalk = false;
		WalkDestination = Point.Zero;
		Pending.Clear();
		FailedTargets.Clear();
		QueuedIds.Clear();
		CutOffIds.Clear();
		RecheckRounds = 0;
		RecheckWaitTicks = 0;
		DeferCooldownTicks = 0;
		Finished = true;
		TriedStandTiles.Clear();
		GatesTried.Clear();
		GateTarget = null;
		GatePhase = 0;
		GateTicks = 0;
		WalkFailCooldownTicks = 0;
		AllApproachBlockedByCritters = false;
		LastApproachCutOff = false;
		LastApproachNoCandidates = false;
		LastWalkSawEmptyPath = false;
		CollectBounds = Rectangle.Empty;
		Current = null;
		Walking = false;
		WalkTicks = 0;
		Location = null;
		StartItem = null;
		ExpectedItem = null;
		OriginalToolIndex = -1;
		mode = Mode.None;
		WaitingAction = false;
		ActionTicks = 0;
		CooldownTicks = 0;
		if (!string.IsNullOrEmpty(message))
		{
			Notify(message, isError);
		}
	}

	public void Tick()
	{
		RestoreMouseVisible();
		if (!IsRunning)
		{
			return;
		}
		Farmer player = Game1.player;
		if (!Context.IsWorldReady || player == null)
		{
			Stop();
			return;
		}
		// 窗口不在前台不再暂停：双屏时人常盯着画面/聊天，游戏世界照跑，队列该继续干活。
		// （真被游戏自己暂停时 Game1.ticks 不走，这里也就什么都不做。）
		if (Game1.activeClickableMenu != null)
		{
			StopWaitingOnPause(player);
			PauseWatch("打开了菜单/对话框");
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
		if (currentItem != ExpectedItem)
		{
			Stop(I18n.T("stop-paused"));
			return;
		}
		// 执行期间每 0.5 秒扫一遍：刚走出畜棚/鸡舍、或刚走进范围的动物直接补进待办，
		// 不再等收工复查——那时它们已经错过整趟了。
		if (++MidScanTicks >= MidScanIntervalTicks)
		{
			MidScanTicks = 0;
			int fresh = CollectNearbyArrivals();
		}
		if (WaitingAction)
		{
			TickWaitingAction(player);
			return;
		}
		if (CooldownTicks > 0)
		{
			CooldownTicks--;
			return;
		}
		if (DeferCooldownTicks > 0)
		{
			DeferCooldownTicks--;
			return;
		}
		if (GatePhase != 0 && TickGate(player))
		{
			return;
		}
		if (Walking)
		{
			TickWalking(player);
			return;
		}
		Target current = Current;
		if (current == null)
		{
			if (!TakeNext(player))
			{
				return;
			}
			current = Current;
			if (current == null)
			{
				return;
			}
		}
		if (!StillInLocation(current))
		{
			SkipCurrent("离开当前地图");
			return;
		}
		if (!HasStandableSpot(current))
		{
			if (!BlockedByCritters(current) || !DeferCurrent("被别的动物挤住"))
			{
				GiveUpCurrent(player, "在水里够不着");
			}
			return;
		}
		string text = ClassifySkip(current);
		if (text != null)
		{
			if (text == "背包满")
			{
				if (mode == Mode.Collect)
				{
					Stop(I18n.T("animal-stop-inventory-full"));
				}
				else
				{
					TraceTarget(current, "SKIP 背包满（不捡）");
					Current = null;
					CooldownTicks = 2;
				}
			}
			else if (text == "体力不足")
			{
				Stop(I18n.T("stop-out-of-stamina"));
				// 体力不支属于预期内的收工：和干完一样弹背包自动暂停。
				Mod.OpenBackpackAfterTask();
			}
			else
			{
				SkipCurrent(text);
			}
		}
		else
		{
			if (player.UsingTool || player.FarmerSprite.PauseForSingleAnimation)
			{
				return;
			}
			if (NeedsWalk(player, current))
			{
				// 规划失败别每 tick 重来一遍：一次失败会把所有候选站位重算、再逐个试寻路。
				if (WalkFailCooldownTicks > 0)
				{
					WalkFailCooldownTicks--;
					return;
				}
				if (TryStartWalking(player, current))
				{
					WalkAttempts = 0;
					WalkFailCooldownTicks = 0;
					return;
				}
				WalkFailCooldownTicks = Math.Min(6 + WalkAttempts * 4, 30);
				// 一个候选都不剩、且是围栏/建筑隔断（不是动物挡路）：等多久也不会变。
				// 先试最近的一扇关着的门；没有门就直接记够不着，别原地耗。
				// 2026-11-04 实机（玩家反馈）：抚摸动物时栅栏门关着，人卡在栏外、帧率掉到几帧。
				// 那条路上「候选站位」其实是有的 —— 动物贴身那几格都在栏里侧、地形完全能站，
				// 只是原版 findPath 一片空白，于是 LastApproachNoCandidates = false，
				// 开门分支从来没进过，剩下的候选逐个把整图 A* 跑干净，一帧几十毫秒。
				bool cutOff = (LastApproachNoCandidates && LastApproachCutOff && !AllApproachBlockedByCritters)
					|| (LastWalkSawEmptyPath && !AllApproachBlockedByCritters);
				if (cutOff)
				{
					if (TryWalkToGate(player))
					{
						return;
					}
					GiveUpCurrent(player, "够不着（围栏/建筑把路隔断）");
					return;
				}
				// 地形能站、只是动物/村民挤着：放回队尾等它们挪窝，不算走不过去。
				if (AllApproachBlockedByCritters && DeferCurrent("贴身位全被动物/村民堵死"))
				{
					return;
				}
				if (++WalkAttempts > MaxWalkAttempts)
				{
					GiveUpCurrent(player, "走不过去");
				}
			}
			else
			{
				Act(player, current);
			}
		}
	}

	/// <summary>窗口不在前台/有菜单时的静默暂停：几秒才报一次，免得被当成「卡住」。</summary>
	private void PauseWatch(string reason)
	{
		if (Game1.ticks - PauseWatchTick < 180)
		{
			return;
		}
		PauseWatchTick = Game1.ticks;
		if (PauseWatchReason != reason)
		{
			PauseWatchReason = reason;
		}
	}

	/// <summary>队列在跑却 2.5 秒没有任何推进时，把卡在哪一步打出来（每 2.5 秒一次）。</summary>
	public void Watchdog()
	{
		if (!IsRunning)
		{
			WatchTick = 0;
			WatchSignature = long.MinValue;
			PauseWatchReason = null;
			return;
		}
		if (Game1.ticks - WatchTick < 150)
		{
			return;
		}
		WatchTick = Game1.ticks;
		Farmer player = Game1.player;
		if (player == null)
		{
			return;
		}
		// 玩家格也算进「有没有在动」：长途走过去时目标不变，但人在动，不能算卡住。
		long signature = Done * 1000003L + Skipped * 10007L + GiveUps * 101L + Deferred * 17L + ((Current != null) ? (Current.Id * 7L) : (-1L)) + (Walking ? 3 : 0) + (WaitingAction ? 5 : 0) + Pending.Count * 31L + player.TilePoint.X * 37L + player.TilePoint.Y * 53L + WalkDestination.X * 11L + WalkDestination.Y * 13L;
		if (signature == WatchSignature)
		{
			Target current = Current;
			string who = ((current != null) ? $"{current.Name}({current.Tile.X:0},{current.Tile.Y:0})" : "无");
			bool standable = current != null && HasStandableSpot(current);
			bool needWalk = current != null && NeedsWalk(player, current);
		}
		WatchSignature = signature;
	}

	private void StopWaitingOnPause(Farmer player)
	{
		if (WaitingAction && Walking)
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
	}

	private int CollectNearbyArrivals()
	{
		GameLocation location = Location;
		if (location == null || mode == Mode.Collect)
		{
			return 0;
		}
		Rectangle collectBounds = CollectBounds;
		Item? startItem = StartItem;
		bool flag = ((startItem != null) ? startItem.QualifiedItemId : null) == "(O)ButterflyPowder";
		HashSet<long> hashSet = new HashSet<long>(QueuedIds);
		foreach (Target pending in Pending)
		{
			hashSet.Add(pending.Id);
		}
		if (Current != null)
		{
			hashSet.Add(Current.Id);
		}
		int num = 0;
		try
		{
			var enumerator2 = ((NetDictionary<long, FarmAnimal, NetRef<FarmAnimal>, SerializableDictionary<long, FarmAnimal>, NetLongDictionary<FarmAnimal, NetRef<FarmAnimal>>>)(object)location.animals).Values.GetEnumerator();
			try
			{
				while (enumerator2.MoveNext())
				{
					FarmAnimal current2 = enumerator2.Current;
					if (current2 != null && (collectBounds).Contains(((Character)current2).TilePoint))
					{
						long animalId = ((NetFieldBase<long, NetLong>)(object)current2.myID).Value;
						// 只补“还有活得干”的：没摸过，或有产出且包里带着对应工具。
						// 以前抚摸模式把范围内所有动物都当新来的加回来，收工后整栏被重排好几轮。
						if (!hashSet.Contains(animalId) && !CutOffIds.Contains(animalId) && (!((NetFieldBase<bool, NetBool>)(object)current2.wasPet).Value || HarvestToolFor(current2) != null))
						{
							Pending.Add(new Target
							{
								Critter = (Character)current2,
								Id = animalId
							});
							QueuedIds.Add(animalId);
							num++;
						}
					}
				}
			}
			finally
			{
				((IDisposable)enumerator2/*cast due to constrained. prefix*/).Dispose();
			}
			if (!flag)
			{
				foreach (NPC character in location.characters)
				{
					Pet val = (Pet)(object)((character is Pet) ? character : null);
					if (val != null && (collectBounds).Contains(((Character)character).TilePoint) && !PetToday(val))
					{
						int petKey = ((NetFieldBase<Guid, NetGuid>)(object)val.petId).Value.GetHashCode();
						if (!hashSet.Contains(petKey) && !CutOffIds.Contains(petKey))
						{
							Pending.Add(new Target
							{
								Critter = (Character?)(object)character,
								Id = petKey
							});
							QueuedIds.Add(petKey);
							num++;
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			((Mod)Mod).Monitor.Log("范围补扫失败：" + ex.Message, (LogLevel)3);
		}
		return num;
	}

	private bool StartedWalking()
	{
		bool flag = TillQueue.IsMovementInputDown();
		bool result = flag && !MovementWasDown;
		MovementWasDown = flag;
		return result;
	}

	private bool TakeNext(Farmer player)
	{
		if (Pending.Count == 0)
		{
			if (FailedTargets.Count > 0)
			{
				GameLocation location = Location;
				if (location != null)
				{
					List<Target> list = new List<Target>();
					foreach (Target failedTarget in FailedTargets)
					{
						if (failedTarget.Retries < 2 && StillInLocation(failedTarget) && !CutOffIds.Contains(failedTarget.Id))
						{
							failedTarget.Retries++;
							list.Add(failedTarget);
						}
					}
					FailedTargets.Clear();
					if (list.Count > 0)
					{
						Pending.AddRange(list);
						return true;
					}
				}
				FailedTargets.Clear();
			}
			if (RecheckRounds < 5)
			{
				if (RecheckWaitTicks > 0)
				{
					RecheckWaitTicks--;
					return false;
				}
				RecheckRounds++;
				int num = CollectNearbyArrivals();
				RecheckWaitTicks = 90;
				if (num > 0)
				{
					return true;
				}
				// 扫一遍没活就直接收工：不再空等剩下的轮次（以前 5 轮 × 1.5 秒，
				// 期间 HUD 一直「还剩 0」、动物还在发光）。
			}
			FinishReport();
			return false;
		}
		Vector2 tile = ((Character)player).Tile;
		int[] pickField = null;
		int pickWidth = 0;
		int pickHeight = 0;
		GameLocation pickLocation = Location;
		if (pickLocation != null)
		{
			pickField = TileUtil.BuildWalkField(pickLocation, tile, terrainOnly: false, out pickWidth, out pickHeight);
		}
		// 换目标时按当前位置的实际步数挑最近的一只：隔栅栏的「直线近」不算近。
		Target target = Pending[0];
		int num2 = int.MaxValue;
		foreach (Target item in Pending)
		{
			int num3 = OrderCost(pickField, pickWidth, pickHeight, item.Tile, tile);
			if (num3 < num2)
			{
				num2 = num3;
				target = item;
			}
		}
		Pending.Remove(target);
		Current = target;
		WalkAttempts = 0;
		ChaseTicks = 0;
		Replans = 0;
		WalkDestination = Point.Zero;
		TriedStandTiles.Clear();
		AimWalk = false;
		return true;
	}

	private string? ClassifySkip(Target target)
	{
		if (target.GroundItem != null)
		{
			GameLocation location = Location;
			Object groundItem = target.GroundItem;
			Object val = default(Object);
			if (location == null || !location.objects.TryGetValue(target.GroundTile, out val) || val != groundItem)
			{
				return "已被捡走";
			}
			Farmer player2 = Game1.player;
			if (player2 != null)
			{
				ApplyGroundItemQuality(location, player2, groundItem, target.GroundTile);
				if (!player2.couldInventoryAcceptThisItem((Item)groundItem))
				{
					return "背包满";
				}
			}
			return null;
		}
		Character? critter = target.Critter;
		Pet val2 = (Pet)(object)((critter is Pet) ? critter : null);
		if (val2 != null)
		{
			Item? startItem = StartItem;
			if (((startItem != null) ? startItem.QualifiedItemId : null) == "(O)ButterflyPowder")
			{
				return "手持蝴蝶粉";
			}
			if (PetToday(val2))
			{
				return "已经抚摸过";
			}
			return null;
		}
		Character? critter2 = target.Critter;
		FarmAnimal val3 = (FarmAnimal)(object)((critter2 is FarmAnimal) ? critter2 : null);
		if (val3 != null)
		{
			if (mode == Mode.Pet)
			{
				if (((NetFieldBase<bool, NetBool>)(object)val3.wasPet).Value)
				{
					return "已经抚摸过";
				}
				if (Game1.timeOfDay >= 1900 && !((Character)val3).isMoving())
				{
					return "睡着了";
				}
				return null;
			}
			Tool val4 = HarvestToolFor(val3);
			if (val4 != null)
			{
				Farmer player3 = Game1.player;
				if (player3 != null && player3.Stamina <= Math.Max(1f, Mod.Config.StopBelowStamina))
				{
					return "体力不足";
				}
				return null;
			}
			bool flag = ((NetFieldBase<string, NetString>)(object)val3.currentProduce).Value != null && val3.isAdult();
			if (((NetFieldBase<bool, NetBool>)(object)val3.wasPet).Value)
			{
				if (flag)
				{
					return "有产出但没带工具";
				}
				return "没产出也摸过了";
			}
			if (Game1.timeOfDay >= 1900 && !((Character)val3).isMoving())
			{
				return "睡着了";
			}
			return null;
		}
		return "不认识的目标";
	}

	private bool StillInLocation(Target target)
	{
		GameLocation location = Location;
		if (location == null)
		{
			return false;
		}
		try
		{
			Object val = default(Object);
			if (target.GroundItem != null)
			{
				return location.objects.TryGetValue(target.GroundTile, out val) && val == target.GroundItem;
			}
			Character critter = target.Critter;
			if (critter == null)
			{
				return false;
			}
			FarmAnimal val2 = (FarmAnimal)(object)((critter is FarmAnimal) ? critter : null);
			if (val2 != null)
			{
				if (critter.currentLocation == location)
				{
					return true;
				}
				return ((NetDictionary<long, FarmAnimal, NetRef<FarmAnimal>, SerializableDictionary<long, FarmAnimal>, NetLongDictionary<FarmAnimal, NetRef<FarmAnimal>>>)(object)location.animals).ContainsKey(((NetFieldBase<long, NetLong>)(object)val2.myID).Value);
			}
			return critter.currentLocation == location || ((IEnumerable<Character>)location.characters).Contains(critter);
		}
		catch
		{
			return false;
		}
	}

	private bool HasStandableSpot(Target target)
	{
		GameLocation val = Location;
		if (target.Critter != null)
		{
			val = target.Critter.currentLocation;
		}
		if (val == null)
		{
			return false;
		}
		Point val2 = (Point)((Game1.player != null) ? ((Character)Game1.player).TilePoint : new Point(-1, -1));
		Vector2 tile = default(Vector2);
		foreach (Point item in NeighborSpots(target))
		{
			if (item == val2)
			{
				return true;
			}
			tile = new Vector2((float)item.X, (float)item.Y);
			if (TileUtil.IsStandable(val, tile))
			{
				return true;
			}
		}
		return false;
	}

	private bool BlockedByCritters(Target target)
	{
		GameLocation location = Location;
		if (target.Critter == null)
		{
			return false;
		}
		location = target.Critter.currentLocation;
		if (location == null)
		{
			return false;
		}
		Vector2 tile = default(Vector2);
		foreach (Point item in NeighborSpots(target))
		{
			tile = new Vector2((float)item.X, (float)item.Y);
			if (!TileUtil.IsStandable(location, tile) || !TileUtil.IsStandableIgnoringCharacters(location, tile))
			{
				continue;
			}
			return true;
		}
		return false;
	}

	/// <summary>列出站位附近能挡路的角色：实心=玩家推不过去（宠物/拖拉机/村民），可穿=农场动物。</summary>
	private static string DescribeCritterBlockers(GameLocation location, Vector2 tile)
	{
		List<string> list = new List<string>();
		try
		{
			foreach (NPC npc in location.characters)
			{
				if (npc == null || npc.IsInvisible)
				{
					continue;
				}
				Character character = (Character)npc;
				if (TileUtil.ChebyshevDistance(character.Tile, tile) > 2.5f)
				{
					continue;
				}
				list.Add($"{npc.Name}({character.Tile.X:0},{character.Tile.Y:0}){(npc.farmerPassesThrough ? "可穿" : "实心")}");
				if (list.Count >= 4)
				{
					break;
				}
			}
		}
		catch
		{
		}
		return ((list.Count == 0) ? "无" : string.Join("、", list));
	}

	private static IEnumerable<Point> NeighborSpots(Target target)
	{
		Vector2 tile = ((target.Critter != null) ? target.Critter.Tile : target.GroundTile);
		for (int k = -1; k <= 1; k++)
		{
			for (int l = -1; l <= 1; l++)
			{
				if (k != 0 || l != 0)
				{
					yield return new Point((int)tile.X + k, (int)tile.Y + l);
				}
			}
		}
	}

	private static bool NeedsWalk(Farmer player, Target target)
	{
		return TileUtil.ChebyshevDistance(((Character)player).Tile, target.Tile) > 1f;
	}

	private void TickWalking(Farmer player)
	{
		Target current = Current;
		if (current == null)
		{
			StopWalking(player);
			return;
		}
		if (player.UsingTool || player.FarmerSprite.PauseForSingleAnimation)
		{
			StopWalking(player);
			return;
		}
		if (!NeedsWalk(player, current) && !AimWalk)
		{
			StopWalking(player);
			return;
		}
		if (((Character)player).Tile != WalkLastTile)
		{
			WalkLastTile = ((Character)player).Tile;
			WalkTicks = 0;
		}
		WalkTicks++;
		WalkTotalTicks++;
		ChaseTicks++;
		if (WalkingByDriver && WalkTicks >= 40 && WalkDestination != Point.Zero && (Mod.Walk.Stalled || Mod.Walk.InputCallbacks == 0))
		{
			bool flag = TileUtil.MonsterNear(((Character)player).currentLocation, ((Character)player).Tile);
			if (flag || TileUtil.DynamicBlockerNear(((Character)player).currentLocation, ((Character)player).Tile))
			{
				// 挡路的是实心角色（宠物/马/拖拉机/村民）才值得等；农场动物能推开：
				// 不去绕（绕＝围着羊群转圈），停下来重规划一次，下一轮直接按地形路线挤过去。
				bool solid = TileUtil.SolidCharacterNear(((Character)player).currentLocation, ((Character)player).Tile);
				if (!flag && !solid)
				{
					if (WalkTicks >= 900 || ChaseTicks >= 3600)
					{
						GiveUpCurrent(player, "blocked");
					}
					else if (Mod.Walk.Stalled)
					{
						Mod.Walk.Stop();
						Walking = false;
						WalkingByDriver = false;
						WalkTicks = 0;
					}
					return;
				}
				if (WalkTicks >= 300 || ChaseTicks >= 1800)
				{
					GiveUpCurrent(player, flag ? "monster" : "blocked");
				}
				return;
			}
			Mod.Walk.Stop();
			WalkingByDriver = false;
			try
			{
				((Character)player).controller = new PathFindController((Character)player, ((Character)player).currentLocation, WalkDestination, -1);
				return;
			}
			catch
			{
				((Character)player).controller = null;
				GiveUpCurrent(player, "pathfind-fail");
				return;
			}
		}
		if (AimWalk && ((Character)player).TilePoint == WalkDestination)
		{
			StopWalking(player);
			return;
		}
		bool flag2;
		if (WalkingByDriver)
		{
			flag2 = !Mod.Walk.IsWalking;
		}
		else
		{
			if (((Character)player).controller == null)
			{
				ControllerGoneTicks++;
			}
			else
			{
				ControllerGoneTicks = 0;
			}
			flag2 = ControllerGoneTicks > 1;
		}
		if (flag2 && Game1.ticks - LastReplanTick >= 15)
		{
			LastReplanTick = Game1.ticks;
			StopWalking(player);
			if (++Replans > 24)
			{
				GiveUpCurrent(player, "绕圈");
			}
		}
		else if (WalkTicks >= 240 || ChaseTicks >= 1800)
		{
			GiveUpCurrent(player, (WalkTicks >= 240) ? "timeout" : "chase");
		}
	}

	private void StopWalking(Farmer player)
	{
		Walking = false;
		WalkingByDriver = false;
		AimWalk = false;
		WalkTicks = 0;
		Mod.Walk.Stop();
		if (((Character)player).controller != null)
		{
			((Character)player).controller = null;
			((Character)player).Halt();
		}
	}

	private bool TryStartWalking(Farmer player, Target target)
	{
		Stopwatch watch = Stopwatch.StartNew();
		List<Point> list = FindApproachTiles(player, target);
		if (list.Count == 0)
		{
			return false;
		}
		Point tilePoint = ((Character)player).TilePoint;
		LastWalkSawEmptyPath = false;
		if (WalkDestination != Point.Zero && list.Contains(WalkDestination) && WalkDestination != tilePoint && !TriedStandTiles.Contains(WalkDestination) && Mod.Walk.TryStart(((Character)player).currentLocation, player, WalkDestination))
		{
			Walking = true;
			WalkingByDriver = true;
			ResetWalkClocks(player);
			return true;
		}
		int emptyPathFailures = 0;
		foreach (Point item in list)
		{
			if (item == tilePoint || TriedStandTiles.Contains(item))
			{
				continue;
			}
			if (Mod.Walk.TryStart(((Character)player).currentLocation, player, item))
			{
				WalkDestination = item;
				TriedStandTiles.Add(item);
				Walking = true;
				WalkingByDriver = true;
				ResetWalkClocks(player);
				return true;
			}
			// 「findPath 返回空」＝这整片连通区里根本没有到那一格的路（典型：门关着、围栏围死）。
			// 这种失败一次要把整片区域展开到 10000 节点上限，8 个候选就是一帧几十~几百毫秒；
			// 连着两次就认定是隔断，别再拿剩下的候选烧 A*，交给下面的穿动物场和开门逻辑。
			if (Mod.Walk.LastFailWasNoPath)
			{
				LastWalkSawEmptyPath = true;
				if (++emptyPathFailures >= 2)
				{
					break;
				}
			}
		}
		// 原版寻路把动物的包围盒当墙：明明地形能走到（玩家还能推着动物挤过去），findPath 却全返回空。
		// 这时按「只看地形」的路线走过去，路上的动物会被顶开。
		if (Mod.Walk.IsAvailable)
		{
			// 距离场只建一次：原来每个候选都重扫一遍整图（8 个候选 = 8 遍全图扫描）。
			int[] throughField = null;
			int throughWidth = 0;
			int throughHeight = 0;
			foreach (Point item in list)
			{
				if (item == tilePoint)
				{
					continue;
				}
				if (throughField == null)
				{
					throughField = TileUtil.BuildWalkField(((Character)player).currentLocation, ((Character)player).Tile, terrainOnly: false, out throughWidth, out throughHeight);
				}
				if (!Mod.Walk.TryStartThroughCritters(((Character)player).currentLocation, player, item, throughField, throughWidth, throughHeight))
				{
					if (Mod.Walk.LastFailWasNoPath)
					{
						LastWalkSawEmptyPath = true;
					}
					continue;
				}
				WalkDestination = item;
				TriedStandTiles.Add(item);
				Walking = true;
				WalkingByDriver = true;
				ResetWalkClocks(player);
				return true;
			}
			return false;
		}
		try
		{
			((Character)player).controller = new PathFindController((Character)player, ((Character)player).currentLocation, list[0], -1);
			WalkDestination = list[0];
			TriedStandTiles.Add(list[0]);
			Walking = true;
			WalkingByDriver = false;
			ResetWalkClocks(player);
			return true;
		}
		catch
		{
			((Character)player).controller = null;
			return false;
		}
	}


	private void ResetWalkClocks(Farmer player)
	{
		WalkTicks = 0;
		WalkTotalTicks = 0;
		ControllerGoneTicks = 0;
		WalkLastTile = ((Character)player).Tile;
	}

	/// <summary>距离场的裁剪框：把玩家和这只动物（含它周围 2 格的站位）框起来，四周留余量。
	/// 余量给得比较宽（至少 12 格，或两点跨度的一半）：框太紧的话，绕湖绕围栏那种路线会被切断、
	/// 距离被高估，站位排序就和整图口径不一致了（宁可多灌一点，也不要悄悄改了顺序）。</summary>
	private static Rectangle ApproachBounds(Vector2 playerTile, Target target)
	{
		int tx = (int)MathF.Floor(target.Tile.X);
		int ty = (int)MathF.Floor(target.Tile.Y);
		int px = (int)MathF.Floor(playerTile.X);
		int py = (int)MathF.Floor(playerTile.Y);
		int minX = Math.Min(px, tx - 2);
		int maxX = Math.Max(px, tx + 2);
		int minY = Math.Min(py, ty - 2);
		int maxY = Math.Max(py, ty + 2);
		int margin = Math.Max(12, Math.Min(24, Math.Max(maxX - minX, maxY - minY) / 2));
		return new Rectangle(minX - margin, minY - margin, maxX - minX + 1 + margin * 2, maxY - minY + 1 + margin * 2);
	}

	private List<Point> FindApproachTiles(Farmer player, Target target)
	{
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation == null)
		{
			return new List<Point>();
		}
		Vector2 tile = ((Character)player).Tile;
		Point tilePoint = ((Character)player).TilePoint;
		AllApproachBlockedByCritters = false;
		LastApproachCutOff = false;
		LastApproachNoCandidates = false;
		// 站位按「从玩家这里实际走几步」排，而不是直线距离——隔着一道栅栏的那格直线最近、
		// 实际根本走不过去。农场动物当空气（推得开），村民/宠物/马照挡。
		// 距离场只灌「玩家 + 这只动物」那一片（ApproachBounds）：整图灌一次在大地图上是几万格 BFS
		// 加一个 120KB 级的大对象分配（超过 85000 字节就进 LOH，直接给 gen2 GC 加压，
		// 表现出来是没规律的卡顿），而这个方法每试一次站位就跑一遍、一个 tick 里最多能连跑到 17 次。
		// 裁剪的代价：要绕出这片才连得通的站位会读成不可达，而「不可达」在这里等于
		// 「被围栏隔断 ⇒ 整只放弃」。所以只要有站位在两个口径下都读成不可达，就把框去掉整图重灌一次重来。
		Rectangle? bounds = ApproachBounds(tile, target);
		List<(Point, int)> list = new List<(Point, int)>();
		for (int attempt = 0; attempt < 2; attempt++)
		{
			TileUtil.DistanceField field = TileUtil.BuildWalkFieldIn(currentLocation, tile, terrainOnly: false, bounds);
			TileUtil.DistanceField staticField = default(TileUtil.DistanceField);
			bool needWider = false;
			list.Clear();
			AllApproachBlockedByCritters = false;
			LastApproachCutOff = false;
			foreach (Point item in NeighborSpots(target))
			{
				Vector2 spot = new Vector2((float)item.X, (float)item.Y);
				if (spot == tile)
				{
					continue;
				}
				// 只按地形筛候选（墙里/水里/栅栏上的格直接不要）；动物站着的那格留给距离场判，
				// 免得「目标自己或同伴站在唯一空位上」时候选被清空。
				if (!TileUtil.IsStandableIgnoringCritters(currentLocation, spot))
				{
					continue;
				}
				int cost;
				if (field.IsValid && item.X >= field.OriginX && item.Y >= field.OriginY && item.X < field.OriginX + field.Width && item.Y < field.OriginY + field.Height)
				{
					cost = field.At(item.X, item.Y);
					if (cost < 0)
					{
						if (!staticField.IsValid)
						{
							staticField = TileUtil.BuildWalkFieldIn(currentLocation, tile, terrainOnly: true, bounds);
						}
						bool reachableWithoutCritters = staticField.IsValid && staticField.At(item.X, item.Y) >= 0;
						if (!reachableWithoutCritters && bounds.HasValue)
						{
							// 两个口径都不可达：可能真被隔断，也可能只是被裁剪框切断了。
							// 这一轮不算数，去掉框整图重灌一次再判（第二次进来 bounds 为 null，不会再兜）。
							needWider = true;
							break;
						}
						if (reachableWithoutCritters)
						{
							// 只是被实心角色堵着：地形上能走，推够时间原版也会放行 ⇒ 排在最后照样试，
							// 别整只丢掉（以前直接 continue，结果只能「放回队尾」干等）。
							AllApproachBlockedByCritters = true;
							LastCritterBlocker = DescribeCritterBlockers(currentLocation, spot);
							list.Add((item, 1000000));
							continue;
						}
						LastApproachCutOff = true;
						continue;
					}
				}
				else
				{
					cost = Math.Abs(item.X - tilePoint.X) + Math.Abs(item.Y - tilePoint.Y);
				}
				list.Add((item, cost));
			}
			if (!needWider)
			{
				break;
			}
			bounds = null;
		}
		if (list.Count > 0)
		{
			// 还有能站的候选：这一轮不算「全被堵死」，真走不通要走普通的尝试计数。
			AllApproachBlockedByCritters = false;
		}
		LastApproachNoCandidates = list.Count == 0;
		list.Sort(delegate((Point Spot, int Cost) a, (Point Spot, int Cost) b)
		{
			int num = a.Cost.CompareTo(b.Cost);
			if (num != 0)
			{
				return num;
			}
			num = a.Spot.Y.CompareTo(b.Spot.Y);
			return (num != 0) ? num : a.Spot.X.CompareTo(b.Spot.X);
		});
		return list.Select<(Point, int), Point>(delegate((Point Spot, int Cost) c)
		{
			return c.Spot;
		}).ToList();
	}

	/// <summary>排队用的代价：走到目标身边要几步。走不通时退回直线距离并加大数，保证排在可达目标之后。</summary>
	private static int OrderCost(int[] field, int width, int height, Vector2 tile, Vector2 from)
	{
		int fallback = (int)MathF.Ceiling(TileUtil.ChebyshevDistance(tile, from));
		if (field == null)
		{
			return fallback;
		}
		int best = TileUtil.ServeDistance(field, width, height, tile);
		return ((best >= 1000000) ? (1000000 + fallback) : best);
	}

	/// <summary>够不着时找最近一扇关着的围栏门走过去：游戏寻路把关着的门当墙，但人走到门边能把门打开。</summary>
	private bool TryWalkToGate(Farmer player)
	{
		GameLocation location = Location;
		if (location == null)
		{
			return false;
		}
		Vector2 fromTile = ((Character)player).Tile;
		Fence bestGate = null;
		Vector2 bestTile = Vector2.Zero;
		float bestDist = 40f;
		try
		{
			foreach (KeyValuePair<Vector2, Object> pair in location.objects.Pairs)
			{
				Fence gate = pair.Value as Fence;
				if (gate == null || !gate.isGate.Value || gate.isPassable() || GatesTried.Contains(pair.Key))
				{
					continue;
				}
				float dist = TileUtil.ChebyshevDistance(fromTile, pair.Key);
				if (dist < bestDist)
				{
					bestDist = dist;
					bestGate = gate;
					bestTile = pair.Key;
				}
			}
		}
		catch
		{
			return false;
		}
		if (bestGate == null)
		{
			((Mod)Mod).Monitor.Log("走不到目标，附近也没有关着的围栏门可开（可能是围栏没有门／门在 40 格外）", LogLevel.Trace);
			return false;
		}
		GatesTried.Add(bestTile);
		int[] dx = { 0, 0, -1, 1, -1, 1, -1, 1 };
		int[] dy = { -1, 1, 0, 0, -1, -1, 1, 1 };
		// 站位按离人远近排：同一侧的门口格先试，少跑几次必然失败的 findPath（每次都是整片展开）。
		List<Point> stands = new List<Point>();
		for (int i = 0; i < dx.Length; i++)
		{
			Point stand = new Point((int)bestTile.X + dx[i], (int)bestTile.Y + dy[i]);
			// 门边站位只按地形筛：动物站在门边也能推开，不能因为它就判「没地方站」。
			if (TileUtil.IsStandableIgnoringCritters(location, new Vector2((float)stand.X, (float)stand.Y)))
			{
				stands.Add(stand);
			}
		}
		stands.Sort(delegate(Point a, Point b)
		{
			return TileUtil.ChebyshevDistance(fromTile, new Vector2((float)a.X, (float)a.Y))
				.CompareTo(TileUtil.ChebyshevDistance(fromTile, new Vector2((float)b.X, (float)b.Y)));
		});
		foreach (Point stand in stands)
		{
			// 人已经站在门边（常见：动物堵在门口）：不用再走，直接进入开门阶段。
			if (stand == ((Character)player).TilePoint)
			{
				GateTarget = bestGate;
				GateTile = bestTile;
				GatePhase = 2;
				GateTicks = 0;
				Walking = false;
				WalkingByDriver = false;
				WalkDestination = stand;
				((Mod)Mod).Monitor.Log($"走不到目标，改去开围栏门 ({bestTile.X}, {bestTile.Y})：人已经在门口", LogLevel.Trace);
				return true;
			}
			if (!Mod.Walk.TryStart(location, player, stand))
			{
				continue;
			}
			GateTarget = bestGate;
			GateTile = bestTile;
			GatePhase = 1;
			GateTicks = 0;
			Walking = true;
			WalkingByDriver = true;
			WalkDestination = stand;
			LastWalkSawEmptyPath = false;
			ResetWalkClocks(player);
			((Mod)Mod).Monitor.Log($"走不到目标，改去开围栏门 ({bestTile.X}, {bestTile.Y})：门口站位 ({stand.X}, {stand.Y})", LogLevel.Trace);
			return true;
		}
		return false;
	}

	/// <summary>开门状态机：走到门边 → 开门 → 清掉站位记忆，下一 tick 重新规划。</summary>
	private bool TickGate(Farmer player)
	{
		if (GateTarget == null)
		{
			GatePhase = 0;
			return false;
		}
		if (GatePhase == 1)
		{
			if (Walking)
			{
				TickWalking(player);
				return true;
			}
			GatePhase = 2;
			GateTicks = 0;
		}
		GateTicks++;
		if (TileUtil.ChebyshevDistance(((Character)player).Tile, GateTile) > 1.5f)
		{
			if (GateTicks > 60)
			{
				GateTarget = null;
				GatePhase = 0;
			}
			return true;
		}
		if (GateTicks >= 6)
		{
			bool opened = false;
			try
			{
				GateTarget.checkForAction(player, false);
				opened = GateTarget.isPassable();
			}
			catch
			{
			}
			// 门开了地形的「能走/能站」两张表就变了，可它们带 60 tick 的缓存（TerrainCacheTtl）。
			// 不作废的话，接下来一整秒里规划仍然把这扇门当墙 —— 人已经站在门口、门也开着，
			// 队列却继续判「够不着」，一只只动物全给放弃掉（玩家看到的就是「门开了也进不去」+ 卡）。
			TileUtil.InvalidateStandableCache();
			((Mod)Mod).Monitor.Log(opened
				? $"围栏门 ({GateTile.X}, {GateTile.Y}) 已推开，重新规划路线"
				: $"围栏门 ({GateTile.X}, {GateTile.Y}) 推不开（可能被对齐的双开门挡住），换下一扇", LogLevel.Trace);
			GateTarget = null;
			GatePhase = 0;
			TriedStandTiles.Clear();
			WalkDestination = Point.Zero;
			WalkAttempts = 0;
			WalkFailCooldownTicks = 0;
			LastWalkSawEmptyPath = false;
			// 路通了：之前因为「围着栏进不去」被放弃的目标重新给机会，收工复查会把它们捞回来。
			if (opened)
			{
				CutOffIds.Clear();
			}
		}
		return true;
	}

	private string VerbFor(Target target)
	{
		if (target.GroundItem != null)
		{
			return I18n.T("verb-collect");
		}
		if (target.Critter is Pet)
		{
			return I18n.T("verb-pet");
		}
		Character? critter = target.Critter;
		FarmAnimal val = (FarmAnimal)(object)((critter is FarmAnimal) ? critter : null);
		if (val != null)
		{
			Tool val2 = HarvestToolFor(val);
			if (val2 != null)
			{
				return I18n.T((val2 is MilkPail) ? "verb-milk" : "verb-shear");
			}
		}
		return I18n.T("verb-pet");
	}

	private static bool CanCollectAnimalProduce(Farmer player, FarmAnimal animal)
	{
		if (player == null)
		{
			return false;
		}
		string produce = ((NetFieldBase<string, NetString>)(object)animal.currentProduce).Value;
		if (produce == null)
		{
			return false;
		}
		int amount = ((NetFieldBase<bool, NetBool>)(object)animal.hasEatenAnimalCracker).Value ? 2 : 1;
		Item item = ItemRegistry.Create("(O)" + produce, amount);
		item.Quality = animal.produceQuality.Value;
		return player.couldInventoryAcceptThisItem(item);
	}

	private void Act(Farmer player, Target target)
	{
		if (target.GroundItem != null)
		{
			ActGroundItem(player, target);
			return;
		}
		if (target.Critter is Pet)
		{
			ActHousePet(player, target);
			return;
		}
		Character? critter = target.Critter;
		FarmAnimal val = (FarmAnimal)(object)((critter is FarmAnimal) ? critter : null);
		if (val == null)
		{
			return;
		}
		Tool val2 = HarvestToolFor(val);
		if (mode != Mode.Collect && val2 != null && CanCollectAnimalProduce(player, val))
		{
			if (!SelfSwitchToTool(player, val2))
			{
				SkipCurrent("找不到工具");
			}
			else
			{
				ActHarvest(player, val);
			}
		}
		else
		{
			ActPet(player, val);
		}
	}

	private void ActGroundItem(Farmer player, Target target)
	{
		GameLocation location = Location;
		Object groundItem = target.GroundItem;
		Object val = default(Object);
		if (location != null && groundItem != null && location.objects.TryGetValue(target.GroundTile, out val) && val == groundItem)
		{
			ApplyGroundItemQuality(location, player, groundItem, target.GroundTile);
		}
		if (location == null || groundItem == null || !location.objects.TryGetValue(target.GroundTile, out val) || val != groundItem)
		{
			SkipCurrent("已被捡走");
		}
		else if (player.addItemToInventoryBool((Item)(object)groundItem, false))
		{
			location.objects.Remove(target.GroundTile);
			// 与原版地面拾取同一套动作 + 结算：弯腰拾取动画 + 延迟金币音效，
			// 然后再补采集经验 / ItemsForaged / 采集者双倍（见 ApplyGroundPickupVanilla）。
			// animateOnce 会把 CanMove/PauseForSingleAnimation 置好，Tick 会等动画结束再继续。
			player.animateOnce(279 + ((Character)player).FacingDirection);
			try
			{
				Game1.playSound("pickUpItem", (int?)null);
				DelayedAction.playSoundAfterDelay("coin", 300, (GameLocation)null, (Vector2?)null, -1, false);
			}
			catch
			{
			}
			ApplyGroundPickupVanilla(location, player, groundItem, target.GroundTile);
			ItemsCollected++;
			LastActionVerb = VerbFor(target);
			TraceTarget(target, "GRAB-OK");
			ClearGiveUps(target);
			// 原版把拾取物放进背包里第一个空格；若那正是选中的空手格，手持物当场就变了。
			// 这是我们自己捡的，不是玩家换物，基线跟着挪，否则下一 tick 会误判成「已暂停」。
			ExpectedItem = player.CurrentItem;
			Current = null;
			CooldownTicks = 3;
		}
		else
		{
			if (mode == Mode.Collect)
			{
				Stop(I18n.T("animal-stop-inventory-full"));
				return;
			}
			TraceTarget(target, "SKIP 背包满（不捡）");
			Current = null;
			CooldownTicks = 2;
		}
	}

	/// <summary>
	/// 地面拾取的原版结算部分（<c>GameLocation.checkAction</c> 里捡 spawned object 那一段）：
	/// 不在农舍内部时，采集品走 <c>OnHarvestedForage</c>（里面给 7 点采集经验）；
	/// 在农舍内部则统一给 5 点耕种经验；<c>ItemsForaged</c> 每次都 +1；
	/// 采集者（职业 13）另有 20% 概率再掉一个，判定用按格种子的 random（原版同一天同一格结果固定）。
	/// 以前这里只做了动画和音效，捡松露一点采集经验都不给，和原版对不上。
	/// </summary>
	private static void ApplyGroundPickupVanilla(GameLocation location, Farmer player, Object groundItem, Vector2 tile)
	{
		bool interior = location.isFarmBuildingInterior();
		if (!interior)
		{
			if (groundItem.isForage())
			{
				location.OnHarvestedForage(player, groundItem);
			}
		}
		else
		{
			player.gainExperience(0, 5);
		}
		Stats stats = Game1.stats;
		uint itemsForaged = stats.ItemsForaged;
		stats.ItemsForaged = itemsForaged + 1;
		if (!((NetHashSet<int>)(object)player.professions).Contains(13))
		{
			return;
		}
		Random random = Utility.CreateDaySaveRandom((double)tile.X, (double)(tile.Y * 777f), 0.0);
		if (random.NextDouble() < 0.2 && !((NetFieldBase<bool, NetBool>)(object)groundItem.questItem).Value && player.couldInventoryAcceptThisItem((Item)(object)groundItem) && !interior)
		{
			player.addItemToInventoryBool(((Item)(object)groundItem).getOne(), false);
			player.gainExperience(2, 7);
		}
	}

	private void ActHousePet(Farmer player, Target target)
	{
		Character? critter = target.Critter;
		Pet val = (Pet)(object)((critter is Pet) ? critter : null);
		if (val == null)
		{
			SkipCurrent("目标丢了");
			return;
		}
		if (!PetToday(val))
		{
			Item? startItem = StartItem;
			if (!(((startItem != null) ? startItem.QualifiedItemId : null) == "(O)ButterflyPowder"))
			{
				((Character)player).FacingDirection = TileUtil.GetFacingDirection(((Character)player).Tile, ((Character)val).Tile);
				bool flag;
				try
				{
					flag = ((NPC)val).checkAction(player, ((Character)player).currentLocation);
				}
				catch
				{
					flag = false;
				}
				if (flag)
				{
					LastActionVerb = VerbFor(target);
					if (mode == Mode.Pet)
					{
						Done++;
					}
					else
					{
						PetsAlongside++;
					}
					ClearGiveUps(target);
					TraceTarget(target, "PETCAT-OK");
				}
				else
				{
					CountSkip(target);
					EnqueueRetry(target);
					TraceTarget(target, "PETCAT-FAIL");
				}
				Current = null;
				CooldownTicks = 8;
				return;
			}
		}
		SkipCurrent("已经抚摸过");
	}

	private void ActPet(Farmer player, FarmAnimal animal)
	{
		if (((NetFieldBase<bool, NetBool>)(object)animal.wasPet).Value)
		{
			Current = null;
			CooldownTicks = 8;
			return;
		}
		if (Game1.timeOfDay >= 1900 && !((Character)animal).isMoving())
		{
			SkipCurrent("睡着了");
			return;
		}
		try
		{
			animal.pet(player, false);
		}
		catch
		{
		}
		bool flag = ((NetFieldBase<bool, NetBool>)(object)animal.wasPet).Value || ((NetFieldBase<bool, NetBool>)(object)animal.hasEatenAnimalCracker).Value;
		if (flag)
		{
			LastActionVerb = VerbFor(Current);
			if (mode == Mode.Pet)
			{
				Done++;
			}
			else
			{
				PetsAlongside++;
			}
			ClearGiveUps(Current);
		}
		else
		{
			CountSkip(Current);
			EnqueueRetry(Current);
		}
		TraceTarget(Current, flag ? "PET" : "PET-FAIL");
		Current = null;
		CooldownTicks = 8;
	}

	/// <summary>
	/// 原版 MilkPail / Shears 的 beginUsing 不看传进去的点击坐标，而是用 GetToolLocation()
	/// 重算 64×64 判定框；那个函数在光标隐没（手柄、光标淡出）时会忽略 player.lastClick、
	/// 按朝向取点——瞄收获框边角就白瞄了。挥动前把 wasMouseVisibleThisFrame 顶成真，
	/// 下一 tick 再还原（beginUsing 在 BeginUsingTool() 里触发，事件即使推迟一帧也覆盖得到）。
	/// </summary>
	private void ForceClickAim()
	{
		if (!SavedMouseVisible.HasValue)
		{
			SavedMouseVisible = Game1.wasMouseVisibleThisFrame;
		}
		Game1.wasMouseVisibleThisFrame = true;
	}

	private void RestoreMouseVisible()
	{
		if (SavedMouseVisible.HasValue)
		{
			Game1.wasMouseVisibleThisFrame = SavedMouseVisible.Value;
			SavedMouseVisible = null;
		}
	}

	private void ActHarvest(Farmer player, FarmAnimal animal)
	{
		if (!((NetFieldBase<bool, NetBool>)(object)animal.wasPet).Value && (Game1.timeOfDay < 1900 || ((Character)animal).isMoving()))
		{
			try
			{
				animal.pet(player, false);
			}
			catch
			{
			}
			if (((NetFieldBase<bool, NetBool>)(object)animal.wasPet).Value)
			{
				PetsAlongside++;
				TraceAct(animal, "PET-ALONGSIDE");
			}
		}
		Tool currentTool = player.CurrentTool;
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentTool == null || currentLocation == null)
		{
			SkipCurrent("工具没了");
			return;
		}
		if (!TryAimAt(player, currentLocation, animal, currentTool, out var aim))
		{
			TraceAct(animal, "AIM-BLOCK 四个朝向都打不到本只");
			if (++WalkAttempts > MaxWalkAttempts || !TryStartAimWalk(player, animal))
			{
				GiveUpCurrent(player, "挥不到这只");
			}
			return;
		}
		((Character)player).lastClick = aim;
		TraceAct(animal, $"USE tool={animal.GetAnimalData()?.HarvestTool ?? "?"} aim=({aim.X:0},{aim.Y:0})");
		ForceClickAim();
		player.BeginUsingTool();
		// beginUsing 取的是它自己那个判定框里的第一只（邻居有产出就抢走），事件被推迟一帧时
		// 强制取点也没赶上——两种都改成「动画结束时收的就是本队列这只」。
		PendingHarvestTarget = animal;
		ApplyHarvestTarget(player);
		WaitingAction = true;
		ActionTicks = 0;
		ActionTicksTotal = 0;
	}

	private void ApplyHarvestTarget(Farmer player)
	{
		FarmAnimal? target = PendingHarvestTarget;
		if (target == null || !player.UsingTool)
		{
			return;
		}
		PendingHarvestTarget = null;
		Tool? tool = player.CurrentTool;
		if (tool is MilkPail pail)
		{
			if (pail.animal != target)
			{
				pail.animal = target;
				TraceAct(target, "RETARGET 邻居抢框，改回本只");
			}
		}
		else if (tool is Shears shears && shears.animal != target)
		{
			shears.animal = target;
			TraceAct(target, "RETARGET 邻居抢框，改回本只");
		}
	}

	private static bool TryAimAt(Farmer player, GameLocation location, FarmAnimal animal, Tool tool, out Vector2 aim)
	{
		Rectangle harvestBoundingBox = animal.GetHarvestBoundingBox();
		Point standingPixel = ((Character)player).StandingPixel;
		List<Vector2> aimPoints = AimPoints(harvestBoundingBox, standingPixel);
		int facingDirection = TileUtil.GetFacingDirection(((Character)player).Tile, ((Character)animal).Tile);
		int facingDirection2 = ((Character)player).FacingDirection;
		aim = aimPoints[0];
		for (int i = 0; i < 4; i++)
		{
			int facingDirection3 = (facingDirection + i) % 4;
			((Character)player).FacingDirection = facingDirection3;
			for (int j = 0; j < aimPoints.Count; j++)
			{
				((Character)player).lastClick = aimPoints[j];
				if (ToolHitsAnimal(player, location, animal, tool))
				{
					aim = aimPoints[j];
					return true;
				}
			}
		}
		((Character)player).FacingDirection = facingDirection2;
		((Character)player).lastClick = aimPoints[1];
		return false;
	}

	/// <summary>
	/// 瞄点：靠玩家这一侧的框边、框中心、收获框四角（内缩 1px，<see cref="Rectangle.Intersects(Rectangle)"/>
	/// 不含右/下边界）。畜棚里两只挨着时，换一个瞄点常能把目标重新从邻居的判定框里孤立出来。
	/// </summary>
	private static List<Vector2> AimPoints(Rectangle box, Point from)
	{
		List<Vector2> points = new List<Vector2>(6)
		{
			PickClosestPointInBox(box, from),
			new Vector2((float)(box).Center.X, (float)(box).Center.Y)
		};
		Vector2 origin = new Vector2((float)from.X, (float)from.Y);
		Vector2[] corners = new Vector2[4]
		{
			new Vector2((float)(box.Left + 1), (float)(box.Top + 1)),
			new Vector2((float)(box.Right - 2), (float)(box.Top + 1)),
			new Vector2((float)(box.Left + 1), (float)(box.Bottom - 2)),
			new Vector2((float)(box.Right - 2), (float)(box.Bottom - 2))
		};
		Array.Sort(corners, (Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, origin).CompareTo(Vector2.DistanceSquared(b, origin)));
		points.AddRange(corners);
		return points;
	}

	/// <summary>按"点击点会被采纳"的口径取工具位置：和强制挥动时游戏自己算出来的那个点一致。</summary>
	private static Vector2 ToolLocationWithClick(Farmer player)
	{
		bool saved = Game1.wasMouseVisibleThisFrame;
		try
		{
			Game1.wasMouseVisibleThisFrame = true;
			return ((Character)player).GetToolLocation();
		}
		finally
		{
			Game1.wasMouseVisibleThisFrame = saved;
		}
	}

	private static bool ToolHitsAnimal(Farmer player, GameLocation location, FarmAnimal animal, Tool tool)
	{
		Vector2 toolLocation = ToolLocationWithClick(player);
		Rectangle val = default(Rectangle);
		val = new Rectangle((int)toolLocation.X - 32, (int)toolLocation.Y - 32, 64, 64);
		Rectangle harvestBoundingBox = animal.GetHarvestBoundingBox();
		if (!(harvestBoundingBox).Intersects(val))
		{
			return false;
		}
		return Utility.GetBestHarvestableFarmAnimal((IEnumerable<FarmAnimal>)(object)((NetDictionary<long, FarmAnimal, NetRef<FarmAnimal>, SerializableDictionary<long, FarmAnimal>, NetLongDictionary<FarmAnimal, NetRef<FarmAnimal>>>)(object)location.animals).Values, tool, val) == animal;
	}

	private static Vector2 PickClosestPointInBox(Rectangle box, Point pixel)
	{
		int num = Math.Clamp(pixel.X, (box).Left + 2, (box).Right - 2);
		int num2 = Math.Clamp(pixel.Y, (box).Top + 2, (box).Bottom - 2);
		return new Vector2((float)num, (float)num2);
	}

	private bool TryStartAimWalk(Farmer player, FarmAnimal animal)
	{
		Target current = Current;
		if (current == null)
		{
			return false;
		}
		Point tilePoint = ((Character)player).TilePoint;
		if (!TriedStandTiles.Contains(tilePoint))
		{
			TriedStandTiles.Add(tilePoint);
		}
		if (!TryStartWalking(player, current))
		{
			return false;
		}
		AimWalk = true;
		TraceAct(animal, $"AIM-WALK 换角度 → 站位({WalkDestination.X},{WalkDestination.Y})");
		return true;
	}

	private void TickWaitingAction(Farmer player)
	{
		ApplyHarvestTarget(player);
		Character? obj = Current?.Critter;
		FarmAnimal val = (FarmAnimal)(object)((obj is FarmAnimal) ? obj : null);
		ActionTicks++;
		ActionTicksTotal++;
		bool flag = player.UsingTool || player.FarmerSprite.PauseForSingleAnimation;
		if (ActionTicks < 12)
		{
			if (Walking)
			{
				Walking = false;
				WalkingByDriver = false;
				Mod.Walk.Stop();
				if (((Character)player).controller != null)
				{
					((Character)player).controller = null;
				}
			}
		}
		else
		{
			if (flag && ActionTicks < ActionTimeoutTicks)
			{
				return;
			}
			WaitingAction = false;
			ActionTicks = 0;
			if (flag)
			{
				player.stopJittering();
				player.UsingTool = false;
				player.CanMove = true;
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
			if (val == null)
			{
				Current = null;
				CooldownTicks = 4;
				return;
			}
			if (((NetFieldBase<string, NetString>)(object)val.currentProduce).Value == null)
			{
				Done++;
				LastActionVerb = VerbFor(Current);
				ClearGiveUps(Current);
				Current = null;
				CooldownTicks = 4;
				TraceAct(val, "HARVEST-OK");
				return;
			}
			if (!CanCollectAnimalProduce(player, val))
			{
				TraceAct(val, "HARVEST-SKIP 背包放不下");
				ActPet(player, val);
				return;
			}
			if (++WalkAttempts <= MaxWalkAttempts && TryStartAimWalk(player, val))
			{
				TraceAct(val, "HARVEST-MISS 换站位再挥");
				CooldownTicks = 2;
				return;
			}
			CountSkip(Current);
			EnqueueRetry(Current);
			TraceAct(val, "HARVEST-FAIL");
			Current = null;
			CooldownTicks = 4;
		}
	}

	private void SkipCurrent(string reason)
	{
		if (Current != null)
		{
			if (reason == "睡着了")
			{
				// 睡觉单独计数：收工报告要说“睡着了”，不能说“跳过 N 只”。
				Sleeping++;
			}
			else
			{
				CountSkip(Current);
				if (reason == "有产出但没带工具")
				{
					NoToolSkips++;
				}
			}
			TraceTarget(Current, "SKIP " + reason);
		}
		Current = null;
		CooldownTicks = 2;
	}

	private bool DeferCurrent(string reason)
	{
		Target current = Current;
		if (current == null)
		{
			return false;
		}
		if (current.Deferrals >= 6)
		{
			return false;
		}
		current.Deferrals++;
		Deferred++;
		Pending.Add(current);
		Current = null;
		WalkAttempts = 0;
		ChaseTicks = 0;
		Replans = 0;
		WalkDestination = Point.Zero;
		TriedStandTiles.Clear();
		AimWalk = false;
		DeferCooldownTicks = 45;
		CooldownTicks = 2;
		TraceTarget(current, $"DEFER {reason}（第 {current.Deferrals} 次）");
		return true;
	}

	private void EnqueueRetry(Target? target)
	{
		if (target != null && !FailedTargets.Contains(target))
		{
			FailedTargets.Add(target);
		}
	}
	/// <summary>记一笔“跳过”。同一只只记一次 —— 补试真做成时由 ClearGiveUps 退回来，
	/// 否则“抚摸完成 12 只，跳过 2 只”里的 2 只是补上了的（实机 2026-11-02）。</summary>
	private void CountSkip(Target? target)
	{
		if (target == null || target.SkippedOnce)
		{
			return;
		}
		target.SkippedOnce = true;
		Skipped++;
	}

	/// <summary>补试/复查里真做成了：这只从“无法到达”和“跳过”里都撤回来。</summary>
	private void ClearGiveUps(Target? target)
	{
		ClearUnreachable(target);
		if (target != null && target.SkippedOnce)
		{
			target.SkippedOnce = false;
			if (Skipped > 0)
			{
				Skipped--;
			}
		}
	}

	/// <summary>补试/复查里真做成了：把这次目标从“无法到达”里撤回来（同一只只计一次）。</summary>
	private void ClearUnreachable(Target? target)
	{
		if (target != null && target.GivenUp)
		{
			target.GivenUp = false;
			if (Unreachable > 0)
			{
				Unreachable--;
			}
		}
	}

	private void GiveUpCurrent(Farmer player, string reason)
	{
		Target current = Current;
		if (Walking)
		{
			StopWalking(player);
		}
		if (current != null)
		{
			if (!current.GivenUp)
			{
				current.GivenUp = true;
				Unreachable++;
			}
			GiveUps++;
			EnqueueRetry(current);
			// 围栏/建筑隔断的目标别再补试：等多久都不会变，补试只会把整图寻路再烧几遍。
			// 门一推开（TickGate 成功）这张表会清空，到时候复查会把它们捞回来。
			if (reason != null && reason.StartsWith("够不着"))
			{
				CutOffIds.Add(current.Id);
			}
			TraceTarget(current, "GIVEUP " + reason);
		}
		Current = null;
		WalkAttempts = 0;
		WalkDestination = Point.Zero;
		TriedStandTiles.Clear();
		AimWalk = false;
		CooldownTicks = 2;
	}

	private void FinishReport()
	{
		if (Reported)
		{
			Stop(null, isError: false);
			return;
		}
		Reported = true;
		string text;
		if (Done == 0 && PetsAlongside > 0)
		{
			// 这一趟没挤出/剪出东西，实际做的就是抚摸，直接按抚摸报。
			text = I18n.T("animal-report", new
			{
				verb = I18n.T("verb-pet"),
				count = PetsAlongside
			});
			if (Sleeping > 0)
			{
				text += I18n.T("report-animal-sleeping", new
				{
					count = Sleeping
				});
			}
		}
		else if (Done == 0 && Sleeping > 0)
		{
			// 一只都没摸到、全是睡着：直接说睡觉，别报“完成 0 只，跳过 N 只”。
			text = I18n.T("animal-report-asleep", new
			{
				count = Sleeping
			});
		}
		else
		{
			text = I18n.T("animal-report", new
			{
				verb = (string.IsNullOrEmpty(LastActionVerb) ? ActiveVerb : LastActionVerb),
				count = Done
			});
			if (Sleeping > 0)
			{
				text += I18n.T("report-animal-sleeping", new
				{
					count = Sleeping
				});
			}
		}
		if (ItemsCollected > 0)
		{
			text += I18n.T("report-animal-items", new
			{
				count = ItemsCollected
			});
		}
		if (Skipped > 0)
		{
			text += I18n.T("report-animal-skipped", new
			{
				count = Skipped
			});
		}
		if (Unreachable > 0)
		{
			text += I18n.T("report-animal-unreachable", new
			{
				count = Unreachable
			});
		}
		if (NoToolSkips > 0)
		{
			text += I18n.T("report-animal-notool", new
			{
				count = NoToolSkips
			});
		}
		Stop(text, isError: false);
		Mod.OpenBackpackAfterTask();
	}


	private void TraceAct(FarmAnimal animal, string tag)
	{
	}

	private void TraceTarget(Target target, string tag)
	{
	}

	/// <summary>同 TillQueue.Notify：只飘 HUD，不再刷 WARN 日志（DebugLog 打开时记 TRACE）。</summary>
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
}
