using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.Util;

namespace FarmQueue;

internal sealed class WalkDriver : IInputSimulator
{
	private static readonly FieldInfo? SimulatorField = typeof(Game1).GetField("inputSimulator", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

	private static readonly FieldInfo? PathFindCounterField = typeof(PathFindController).GetField("_counter", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

	private readonly ModEntry Mod;

	/// <summary>这一步走路里被 NPC（村民、宠物、拖拉机）占住的格子。原版寻路 pathfinding:true 时不查 NPC 碰撞，
	/// 路线会直接穿过它们，人走到跟前推不动；这里自己补一份占位表给压线段用。</summary>
	private static HashSet<Point> CharacterTiles = new HashSet<Point>();

	/// <summary>本次路线的阻挡图，和 TileBlocked 同一口径（角色占位 + 地形不可站），只盖住路线外接框那一片。
	/// 建一次图，压线段那 O(路线长²) 次判定就全是数组读，不再每格调一次
	/// isCollidingPosition(pathfinding:false)——那个变体每格都要遍历 animals/buildings/resourceClumps/
	/// furniture/largeTerrainFeatures 再加一遍 characters 全表，大地图 + 有障碍时一次起步能吃掉几十毫秒。
	/// Cells 为 null 时（出异常，或外接框太大又拿不到整图地形表）判定自动退回 TileBlocked。</summary>
	private TileUtil.BlockedGrid BlockedGrid;

	/// <summary>压线段时先逐格问游戏，探针数超过这个预算才建阻挡图。
	/// 256 次逐格判定约 0.6ms（isCollidingPosition(pathfinding:false) 口径），
	/// 而一张局部图最贵也就 4096 格 × 0.63us ≈ 2.6ms（整图地形表还有缓存兜着），
	/// 预算再大就得不偿失；空旷地/短路线则永远走不到建图那一步。</summary>
	private const int GridProbeBudget = 256;

	private int ProbeBudget;

	/// <summary>正在压的那条路线（建图要按它的外接框来）；不在压线段时为 null。</summary>
	private List<Point>? GridRoute;

	private List<Point> Route = new List<Point>();

	private int Step;

	private int InputFrames;

	private int NoKeyFrames;

	private Vector2 DiagLastPos;

	private int DiagFrame;

	private int StuckFrames;

	private int SideStepFrames;

	private Vector2 SideStepDir;

	private int SideStepCooldown;


	private int IdleFrames;

	// 队列会每隔十几 tick 重新发一次同一个目的地；如果每次都把「连续不动」清零，
	// 卡住时永远到不了触发侧移/摆正的阈值（实测 0.4.40 里 (18,25) 一秒重发 4 次）。
	private Point LastDestination = new Point(int.MinValue, int.MinValue);

	private Point LastPlayerTile = new Point(int.MinValue, int.MinValue);

	private int CarriedStuckFrames;

	private int CarriedLegIdleFrames;

	private float BestLegDist;

	private int LegIdleFrames;

	private int ProgressStep;

	private int ReroutesThisWalk;

	private int RerouteCooldown;


	public bool IsAvailable => (object)SimulatorField != null;

	public bool IsWalking => Step < Route.Count;

	public bool Stalled
	{
		get
		{
			if (IsWalking)
			{
				return StuckFrames >= 40;
			}
			return false;
		}
	}

	public int InputCallbacks => InputFrames;

	public bool IsAttached => SimulatorField?.GetValue(null) == this;


	public string? LastFailReason { get; private set; }

	/// <summary>上一次失败是不是「这整片连通区里根本搜不到路」（而不是「站位挑得不对」）。
	/// 两者代价差一个量级：搜不到路要把整片区域展开到 10000 节点上限才返回，
	/// 真的走不过去时连试 8 个候选就是一帧几十~几百毫秒（掉到几帧的元凶）。</summary>
	public bool LastFailWasNoPath { get; private set; }

	public WalkDriver(ModEntry mod)
	{
		Mod = mod;
	}

	public bool TryStart(GameLocation location, Farmer player, Point destination)
	{
		BeginAttempt(player, destination);
		if ((object)SimulatorField == null)
		{
			LastFailReason = "模拟按键不可用（反射拿不到 inputSimulator）";
			return false;
		}
		CharacterTiles = CollectCharacterTiles(location);
		// 阻挡图不在这里建：它要盖住「最终那条路线」的外接框，而下面还可能换成穿动物/避让的两条路线之一。
		BlockedGrid = default(TileUtil.BlockedGrid);
		Stack<Point> stack;
		try
		{
			stack = FindPath(location, player, destination);
		}
		catch (Exception ex)
		{
			LastFailReason = "findPath 抛异常：" + ex.GetType().Name + " " + ex.Message;
			return false;
		}
		if (stack == null || stack.Count == 0)
		{
			LastFailReason = "findPath 返回空";
			LastFailWasNoPath = true;
			return false;
		}
		// 原版寻路把农场动物的包围盒当墙：门口站一只时它会绕整圈围栏（实测 3 格的目标走了 22 格）。
		// 用「动物可穿过、实心角色照挡」的距离场再算一条，明显更短就走它——路上的动物会被顶开。
		int chebyshev = (int)TileUtil.ChebyshevDistance(((Character)player).Tile, new Vector2((float)destination.X, (float)destination.Y));
		// 附近没有农场动物时不建这张场：原版寻路比这张场多挡的只有动物（NPC 它 pathfinding:true 时看不见），
		// 一只动物都不在路线附近就不可能有更短的直接路线，白扫一遍整图（大地图上几十毫秒一次）。
		if (stack.Count > chebyshev + 2 && TileUtil.FarmAnimalNearTiles(location, stack, 3))
		{
			int[] walkField = TileUtil.BuildWalkField(location, ((Character)player).Tile, terrainOnly: false, out int walkWidth, out int walkHeight);
			List<Point> direct = TileUtil.TracePath(walkField, walkWidth, walkHeight, ((Character)player).TilePoint, destination);
			if (direct != null && direct.Count >= 2 && direct.Count + 3 <= stack.Count)
			{
				int vanillaCount = stack.Count;
				Stack<Point> rebuilt = new Stack<Point>();
				for (int i = direct.Count - 1; i >= 0; i--)
				{
					rebuilt.Push(direct[i]);
				}
				stack = rebuilt;
			}
		}
		// 原版寻路在 pathfinding:true 时看不见 NPC，路线会从它们身上穿过去；人走到跟前推不动，
		// 侧移兜底也只是左右蹭（就是「围着洒水器来回走」）。这里按真实走路的口径逐格复核，
		// 发现路线踩进走不进去的格就换项目自己的避让寻路（四邻 BFS，通行表本来就排除了角色）。
		HashSet<Point> blockedTiles = new HashSet<Point>(CharacterTiles);
		bool blockedPath = false;
		bool first = true;
		foreach (Point node in stack)
		{
			if (first)
			{
				first = false;
				continue;
			}
			if (TileBlocked(location, player, node.X, node.Y))
			{
				blockedTiles.Add(node);
				blockedPath = true;
			}
		}
		if (blockedPath)
		{
			List<Point> avoiding = TileUtil.FindPathAvoiding(location, ((Character)player).TilePoint, destination, blockedTiles);
			if (avoiding != null && avoiding.Count >= 2)
			{
				Stack<Point> rebuilt = new Stack<Point>();
				for (int i = avoiding.Count - 1; i >= 0; i--)
				{
					rebuilt.Push(avoiding[i]);
				}
				stack = rebuilt;
			}
		}
		return BeginRoute(location, player, stack, destination);
	}

	/// <summary>
	/// 原版寻路把农场动物的包围盒当墙（findPath 返回空），但动物推得开（连撞 60 帧就原地让路）。
	/// 这里按「动物当空气、实心角色照挡」的距离场自己铺一条路线；村民/宠物/马依然算墙。
	/// </summary>
	public bool TryStartThroughCritters(GameLocation location, Farmer player, Point destination)
	{
		int[]? field = TileUtil.BuildWalkField(location, ((Character)player).Tile, terrainOnly: false, out int width, out int height);
		return TryStartThroughCritters(location, player, destination, field, width, height);
	}

	/// <summary>
	/// 同上，但距离场由调用方建好复用。动物队列会拿最多 8 个候选站位逐个试，
	/// 每个都重扫一遍整图（标准农场 5.77ms、大地图 20~40ms）就是「一帧只剩几帧」的来源。
	/// </summary>
	public bool TryStartThroughCritters(GameLocation location, Farmer player, Point destination, int[]? field, int width, int height)
	{
		BeginAttempt(player, destination);
		if ((object)SimulatorField == null)
		{
			LastFailReason = "模拟按键不可用（反射拿不到 inputSimulator）";
			return false;
		}
		List<Point>? path = TileUtil.TracePath(field, width, height, ((Character)player).TilePoint, destination);
		if (path == null || path.Count < 2)
		{
			LastFailReason = "地形距离场找不到路";
			LastFailWasNoPath = true;
			return false;
		}
		CharacterTiles = CollectCharacterTiles(location);
		Stack<Point> stack = new Stack<Point>();
		for (int i = path.Count - 1; i >= 0; i--)
		{
			stack.Push(path[i]);
		}
		return BeginRoute(location, player, stack, destination);
	}

	private void BeginAttempt(Farmer player, Point destination)
	{
		LastFailReason = null;
		LastFailWasNoPath = false;
		IdleFrames = 0;
		InputFrames = 0;
		NoKeyFrames = 0;
		// 同一个人、同一个目的地反复重发：把上一次的「推不动」计数接上，否则卡住时永远触发不了兜底。
		bool sameAttempt = destination == LastDestination && ((Character)player).TilePoint == LastPlayerTile;
		StuckFrames = (sameAttempt ? CarriedStuckFrames : 0);
		LastDestination = destination;
		LastPlayerTile = ((Character)player).TilePoint;
		SideStepFrames = 0;
		SideStepCooldown = 0;
		DiagFrame = 0;
		DiagLastPos = ((Character)player).Position;
		BestLegDist = float.MaxValue;
		LegIdleFrames = (sameAttempt ? CarriedLegIdleFrames : 0);
		ProgressStep = -1;
		ReroutesThisWalk = 0;
		RerouteCooldown = 0;
	}

	private bool BeginRoute(GameLocation location, Farmer player, Stack<Point> stack, Point destination)
	{
		// 阻挡图不在这里建：Simplify 会先用「逐格问游戏」，探针数超预算才建（见 TileBlockedAt）。
		Route = Simplify(location, player, stack);
		Step = 0;
		IdleFrames = 0;
		while (IsWalking && Route[Step] == ((Character)player).TilePoint)
		{
			Step++;
		}
		if (!IsWalking)
		{
			LastFailReason = "压缩后没有可走的路段";
			Stop();
			return false;
		}
		Attach();
		return true;
	}

	public void Stop()
	{
		Route = new List<Point>();
		Step = 0;
		IdleFrames = 0;
		BlockedGrid = default(TileUtil.BlockedGrid);
		if (SimulatorField?.GetValue(null) == this)
		{
			Detach();
		}
	}

	public void Release()
	{
		Stop();
	}

	public void SimulateInput(ref bool actionButtonPressed, ref bool switchToolButtonPressed, ref bool useToolButtonPressed, ref bool useToolButtonReleased, ref bool addItemToInventoryButtonPressed, ref bool cancelButtonPressed, ref bool moveUpPressed, ref bool moveRightPressed, ref bool moveLeftPressed, ref bool moveDownPressed, ref bool moveUpReleased, ref bool moveRightReleased, ref bool moveLeftReleased, ref bool moveDownReleased, ref bool moveUpHeld, ref bool moveRightHeld, ref bool moveLeftHeld, ref bool moveDownHeld)
	{
		InputFrames++;
		if (!IsWalking)
		{
			if (++IdleFrames > 8)
			{
				Detach();
			}
			return;
		}
		IdleFrames = 0;
		Farmer player = Game1.player;
		if (player == null)
		{
			Stop();
			return;
		}
		while (IsWalking && (Arrived(player, Route[Step]) || ((Character)player).TilePoint == Route[Step]))
		{
			Step++;
		}
		if (!IsWalking)
		{
			if (++IdleFrames > 8)
			{
				Detach();
			}
			return;
		}
		Point leg = Route[Step];
		Point legFrom = ((Step > 0) ? Route[Step - 1] : ((Character)player).TilePoint);
		TrackLegProgress(player, leg);
		if (SideStepCooldown > 0)
		{
			SideStepCooldown--;
		}
		if (RerouteCooldown > 0)
		{
			RerouteCooldown--;
		}
		if (SideStepFrames == 0 && SideStepCooldown == 0 && player.CanMove && !player.UsingTool && (StuckFrames >= 28 || LegIdleFrames >= 50) && ((Character)player).currentLocation != null)
		{
			// 侧移兜底不能只认动物/角色：贴着石头/墙角走时碰撞框会卡住，DynamicBlockerNear 是空的，
			// 只等队列的 12 秒硬超时就会像卡死。重算路线仍只在有动态阻挡时做（没有阻挡重算没有意义）。
			bool dynamicBlocker = TileUtil.DynamicBlockerNear(((Character)player).currentLocation, ((Character)player).Tile);
			// 只有农场动物挡着时不绕行：它们能推开，绕来绕去就是「原地转圈」。
			bool solidBlocker = TileUtil.SolidCharacterNear(((Character)player).currentLocation, ((Character)player).Tile);
			Vector2 perp;
			if (dynamicBlocker && solidBlocker && TryRerouteAroundBlockers(player, ref leg, ref legFrom))
			{
				SideStepCooldown = 12;
			}
			else if (TileUtil.TryCenterInTile(player, out _))
			{
				// 侧移只是左右蹭。贴边停下时碰撞框会压在旁边那格上（洒水器/棚架就在邻格），
				// 格子判定都说能走、人却推不动——先把人摆正到格心，再按原方向推一次。
				SideStepCooldown = 8;
				StuckFrames = 0;
				LegIdleFrames = 0;
			}
			else if (TryPickSideStep(player, leg, legFrom, out perp))
			{
				SideStepDir = perp;
				SideStepFrames = 18;
				SideStepCooldown = 45;
			}
			else
			{
				SideStepCooldown = 30;
			}
		}
		int num = leg.X - (int)((Character)player).Tile.X;
		int num2 = leg.Y - (int)((Character)player).Tile.Y;
		if (num == 0 && num2 == 0)
		{
			num = leg.X - legFrom.X;
			num2 = leg.Y - legFrom.Y;
		}
		bool flag = false;
		bool flag2 = false;
		bool flag3 = false;
		bool flag4 = false;
		if (num > 0)
		{
			flag2 = true;
		}
		else if (num < 0)
		{
			flag3 = true;
		}
		if (num2 > 0)
		{
			flag4 = true;
		}
		else if (num2 < 0)
		{
			flag = true;
		}
		if (!flag && !flag2 && !flag3 && !flag4)
		{
			NoKeyFrames++;
		}
		if (SideStepFrames > 0)
		{
			SideStepFrames--;
			flag = SideStepDir.Y < 0f;
			flag4 = SideStepDir.Y > 0f;
			flag3 = SideStepDir.X < 0f;
			flag2 = SideStepDir.X > 0f;
		}
		if (flag)
		{
			moveUpHeld = true;
		}
		if (flag2)
		{
			moveRightHeld = true;
		}
		if (flag3)
		{
			moveLeftHeld = true;
		}
		if (flag4)
		{
			moveDownHeld = true;
		}
		Vector2 posNow = ((Character)player).Position;
		StuckFrames = (((posNow - DiagLastPos).LengthSquared() < 0.01f) ? (StuckFrames + 1) : 0);
		DiagLastPos = posNow;
		CarriedStuckFrames = StuckFrames;
		CarriedLegIdleFrames = LegIdleFrames;
		if (InputFrames - DiagFrame >= 60)
		{
			DiagFrame = InputFrames;
		}
	}

	private static bool TryPickSideStep(Farmer player, Point next, Point from, out Vector2 perp)
	{
		perp = Vector2.Zero;
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation == null)
		{
			return false;
		}
		int num = next.X - (int)((Character)player).Tile.X;
		int num2 = next.Y - (int)((Character)player).Tile.Y;
		if (num != 0 && num2 != 0)
		{
			if (next.X == from.X)
			{
				num = 0;
			}
			else
			{
				num2 = 0;
			}
		}
		if (num == 0 && num2 == 0)
		{
			num = next.X - from.X;
			num2 = next.Y - from.Y;
		}
		Vector2[] array = (Vector2[])((num != 0) ? new Vector2[2]
		{
			new Vector2(0f, 1f),
			new Vector2(0f, -1f)
		} : ((num2 == 0) ? ((Array)Array.Empty<Vector2>()) : ((Array)new Vector2[2]
		{
			new Vector2(1f, 0f),
			new Vector2(-1f, 0f)
		})));
		Vector2[] array2 = array;
		Vector2[] array3 = array2;
		foreach (Vector2 val in array3)
		{
			Vector2 tile = ((Character)player).Tile + val;
			if (TileUtil.IsWalkableThroughAnimals(currentLocation, tile) && !TileUtil.MonsterNear(currentLocation, tile, 0))
			{
				perp = val;
				return true;
			}
		}
		return false;
	}

	private void TrackLegProgress(Farmer player, Point leg)
	{
		if (ProgressStep != Step)
		{
			ProgressStep = Step;
			BestLegDist = float.MaxValue;
			LegIdleFrames = 0;
			return;
		}
		// 像素距离：到段终点格心的平方距离（格坐标→像素 ×64+32），避免开方
		float dx = leg.X * 64f + 32f - ((Character)player).Position.X;
		float dy = leg.Y * 64f + 32f - ((Character)player).Position.Y;
		float num = dx * dx + dy * dy;
		if (num < BestLegDist)
		{
			BestLegDist = num;
			LegIdleFrames = 0;
		}
		else
		{
			LegIdleFrames++;
		}
	}

	private bool TryRerouteAroundBlockers(Farmer player, ref Point leg, ref Point legFrom)
	{
		GameLocation currentLocation = ((Character)player).currentLocation;
		if (currentLocation == null || Route.Count == 0 || ReroutesThisWalk >= 4 || RerouteCooldown > 0)
		{
			return false;
		}
		Point tilePoint = ((Character)player).TilePoint;
		Point val = Route[Route.Count - 1];
		if (tilePoint == val)
		{
			return false;
		}
		ReroutesThisWalk++;
		RerouteCooldown = 45;
		HashSet<Point> hashSet = TileUtil.CollectBlockerTiles(currentLocation, ((Character)player).Tile, 3, includeAnimals: false);
		hashSet.Remove(tilePoint);
		List<Point> list = TileUtil.FindPathAvoiding(currentLocation, tilePoint, val, hashSet);
		if (list == null || list.Count < 2)
		{
			return false;
		}
		Stack<Point> stack = new Stack<Point>();
		for (int num = list.Count - 1; num >= 0; num--)
		{
			stack.Push(list[num]);
		}
		// 角色一直在动：绕行前重新收一遍占位，别拿开走时的旧表压线段。
		CharacterTiles = CollectCharacterTiles(currentLocation);
		RebuildBlockedGrid(currentLocation, player, stack);
		List<Point> list2 = Simplify(currentLocation, player, stack);
		if (list2.Count == 0)
		{
			return false;
		}
		Route = list2;
		Step = 0;
		while (IsWalking && Route[Step] == ((Character)player).TilePoint)
		{
			Step++;
		}
		if (!IsWalking)
		{
			return false;
		}
		StuckFrames = 0;
		SideStepFrames = 0;
		leg = Route[Step];
		legFrom = tilePoint;
		return true;
	}

	private static string DescribeTiles(HashSet<Point> tiles)
	{
		int num = 0;
		string text = "";
		foreach (Point tile in tiles)
		{
			if (num++ > 0)
			{
				text += " ";
			}
			text += $"({tile.X},{tile.Y})";
			if (num >= 6)
			{
				text += "…";
				break;
			}
		}
		return text;
	}


	private Stack<Point>? FindPath(GameLocation location, Farmer player, Point destination)
	{
		try
		{
			return InvokeFindPath(location, player, destination);
		}
		catch (Exception) when (ResetPathFindCounter())
		{
			return InvokeFindPath(location, player, destination);
		}
	}

	private static Stack<Point>? InvokeFindPath(GameLocation location, Farmer player, Point destination)
	{
		Point tilePoint = ((Character)player).TilePoint;
		return PathFindController.findPath(tilePoint, destination, PathFindController.isAtEndPoint, location, (Character)(object)player, 10000);
	}

	private bool ResetPathFindCounter()
	{
		try
		{
			if ((object)PathFindCounterField == null)
			{
				return false;
			}
			if (!(PathFindCounterField.GetValue(null) is int num) || num == 0)
			{
				return false;
			}
			PathFindCounterField.SetValue(null, 0);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void Attach()
	{
		try
		{
			SimulatorField?.SetValue(null, this);
		}
		catch (Exception ex)
		{
			((Mod)Mod).Monitor.Log("模拟走路：挂不上 inputSimulator（" + ex.Message + "）", (LogLevel)3);
		}
	}

	private void Detach()
	{
		try
		{
			if (SimulatorField?.GetValue(null) == this)
			{
				SimulatorField.SetValue(null, null);
			}
		}
		catch
		{
		}
	}

	private static bool Arrived(Farmer player, Point node)
	{
		Rectangle val = default(Rectangle);
		val = new Rectangle(node.X * 64, node.Y * 64, 64, 64);
		(val).Inflate(-2, 0);
		Rectangle boundingBox = ((Character)player).GetBoundingBox();
		if ((val).Contains(boundingBox) || (boundingBox.Width > val.Width && (val).Contains((boundingBox).Center)))
		{
			return (val).Bottom - (boundingBox).Bottom >= 2;
		}
		return false;
	}

	private List<Point> Simplify(GameLocation location, Farmer player, Stack<Point> nodes)
	{
		// 贪心压线段：外层锚点，内层从最后一个节点往前试。有障碍时内层会一路试下来，
		// 判定次数是 O(路线长²)（每次 ClearStraight 再按段长走一遍）；空旷地里第一次探针就成功，
		// 只有 O(路线长) 次。所以判定先走「逐格问游戏」，探针数超预算才建阻挡图切成数组读——
		// 建图本身要按格问一遍地形，短路线/空旷地建图反而亏。
		// 预算按「建这张图要花多少格判定」折算（逐格问一次 ≈ 建图一格的 4 倍贵）：局部小图几百次探针后就该建；
		// 要用整图地形表、而表还是冷的，就等到几千次探针再建（长距离空旷转移根本用不到图）；
		// 表已经在缓存里时建图几乎白拿，按下限 256 次就建。
		GridRoute = nodes.ToList();
		ProbeBudget = Math.Max(GridProbeBudget, TileUtil.BlockedGridCost(location, TileUtil.BoundsOf(GridRoute, 1)) / 4);
		List<Point> list = GridRoute;
		List<Point> list2 = new List<Point>();
		int num = 0;
		while (num < list.Count - 1)
		{
			int num2 = list.Count - 1;
			while (num2 > num + 1 && !ClearStraight(location, player, list[num], list[num2]))
			{
				num2--;
			}
			list2.Add(list[num2]);
			num = num2;
		}
		GridRoute = null;
		return list2;
	}

	private bool ClearStraight(GameLocation location, Farmer player, Point a, Point b)
	{
		int num = Math.Sign(b.X - a.X);
		int num2 = Math.Sign(b.Y - a.Y);
		int num3 = Math.Min(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
		int num4 = a.X;
		int num5 = a.Y;
		for (int i = 0; i < num3; i++)
		{
			int x = num4;
			int y = num5;
			num4 += num;
			num5 += num2;
			if (TileBlockedAt(location, player, num4, num5) || (TileBlockedAt(location, player, num4, y) && TileBlockedAt(location, player, x, num5)))
			{
				return false;
			}
		}
		while (num4 != b.X)
		{
			num4 += num;
			if (TileBlockedAt(location, player, num4, num5))
			{
				return false;
			}
		}
		while (num5 != b.Y)
		{
			num5 += num2;
			if (TileBlockedAt(location, player, num4, num5))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// 按「这条路线的外接框 + 1 格余量」建阻挡图：小区域现问现建（每格一次 IsStandableIgnoringCritters，
	/// 总共几十格），区域太大才改用缓存的整图地形表——两三格的短路线不该为整张地图付费
	/// （实测那样每次起步反而多花 4ms）。
	/// </summary>
	private void RebuildBlockedGrid(GameLocation location, Farmer player, IEnumerable<Point> route)
	{
		BlockedGrid = TileUtil.BuildBlockedGrid(location, CharacterTiles, TileUtil.BoundsOf(route, 1));
	}

	/// <summary>「这一格走不走得进去」：有阻挡图就读数组（和 TileBlocked 同口径），
	/// 没有就逐格问游戏；问满预算还没压完（说明障碍多、探针是 O(路线长²)）就当场建图切过去。</summary>
	private bool TileBlockedAt(GameLocation location, Farmer player, int x, int y)
	{
		if (BlockedGrid.Cells != null)
		{
			return BlockedGrid.IsBlocked(x, y);
		}
		if (GridRoute != null && --ProbeBudget <= 0)
		{
			RebuildBlockedGrid(location, player, GridRoute);
			GridRoute = null;
			if (BlockedGrid.Cells != null)
			{
				return BlockedGrid.IsBlocked(x, y);
			}
		}
		return TileBlocked(location, player, x, y);
	}

	/// <summary>逐格问游戏的版本（没有阻挡图时的兜底，例如地图超过 65536 格）。</summary>
	private static bool TileBlocked(GameLocation location, Farmer player, int x, int y)
	{
		if (CharacterTiles.Contains(new Point(x, y)))
		{
			return true;
		}
		Rectangle val = default(Rectangle);
		val = new Rectangle(x * 64 + 1, y * 64 + 1, 62, 62);
		// pathfinding:false = 和农夫真实移动同一口径：NPC/村民/拖拉机也算碰撞（原版寻路会跳过它们）。
		// skipCollisionEffects:true 只关掉碰动物时的推挤副作用，判定本身不变。
		if (!location.isCollidingPosition(val, Game1.viewport, true, 0, false, (Character)player, false, false, false, true))
		{
			return false;
		}
		// isCollidingPosition 对农场动物没有豁免（按包围盒撞，连撞 60 帧才临时放行），但动物推得开：
		// 只被动物挡着不算挡。村民/宠物/马这些实心角色由 IsWalkableThroughAnimals 补回来。
		return !TileUtil.IsWalkableThroughAnimals(location, new Vector2((float)x, (float)y));
	}

	private static HashSet<Point> CollectCharacterTiles(GameLocation location)
	{
		HashSet<Point> set = new HashSet<Point>();
		try
		{
			foreach (NPC npc in location.characters)
			{
				if (npc == null || npc.IsInvisible)
				{
					continue;
				}
				Rectangle box = npc.GetBoundingBox();
				int x0 = box.Left / 64;
				int x1 = (box.Right - 1) / 64;
				int y0 = box.Top / 64;
				int y1 = (box.Bottom - 1) / 64;
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

}
