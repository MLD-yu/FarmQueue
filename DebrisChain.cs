using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using Microsoft.Xna.Framework;

namespace FarmQueue;

/// <summary>
/// 杂物自动换工具：用镰刀 / 斧头 / 镐头框选时，若框里的活一把工具干不完，就把整框交给
/// <see cref="DebrisPlan"/> 按格分工，队列一路「就近挑下一格」走下去，走到哪格就换成那格该用的工具
/// （眼前是杂草用镰刀、下一个最近的是树枝就换斧头、再下一个是石头就换镐头），收工后换回框选时拿的工具。
///
/// 0.5.61 是「一轮一把工具」：先把整框的杂草扫完再换斧头砍整框的树，同一片地来回横穿三趟；
/// 现在整框只有一趟、一条路线，工具沿途换。本类只负责建分工表、记住原槽位、收工时换回去。
/// </summary>
internal sealed class DebrisChain
{
	private readonly ModEntry Mod;

	private bool active;

	private DebrisPlan plan;

	private GameLocation location;

	private int originalToolIndex = -1;

	public bool Active => active;

	/// <summary>这一趟沿途换了几次工具（收工的日志里报）。</summary>
	public int ToolSwitches => Mod.Queue.MixedToolSwitches;

	public DebrisChain(ModEntry mod)
	{
		Mod = mod;
	}

	/// <summary>0=镰刀 1=斧头 2=镐头，其它工具 -1。</summary>
	internal static int KindOf(Item item)
	{
		return DebrisPlan.KindOf(item);
	}

	private void Notify(string message)
	{
		try
		{
			Game1.addHUDMessage(new HUDMessage(message, 0));
		}
		catch
		{
		}
	}

	/// <summary>
	/// 框选收框时调用：算出这一框要不要按格分工混合跑。
	/// 返回 true 表示链已接管（队列已经开跑），调用方不要再自己 Start。
	/// </summary>
	public bool TryStart(List<Vector2> workTiles, GameLocation loc, Rectangle selBox, Farmer player, Item held)
	{
		active = false;
		plan = null;
		location = null;
		if (!Mod.Config.AutoSwitchDebrisTools || player == null || loc == null || workTiles == null || workTiles.Count == 0)
		{
			return false;
		}
		if (KindOf(held) < 0)
		{
			return false;
		}
		DebrisPlan built = DebrisPlan.Build(loc, player, Mod.Config);
		if (built == null)
		{
			return false;
		}
		Dictionary<int, int> counts = built.CountKinds(workTiles, out var total);
		// 手里这把就能把整框干完（或者整框没活）：不接管，照旧走原来的单工具队列，行为一模一样。
		// 要接管的是「得用两把以上」和「框里的活全归别的工具」两种：
		// 后者比如手里拿着斧头、框里只有杂草和石头而「斧头清杂草」关着——不接管就只能报「该区域无法工作」，
		// 可框选时那些格明明标了绿（身上带着镐头能清）。
		if (total == 0 || (counts.Count < 2 && counts.ContainsKey(built.HeldKind)))
		{
			return false;
		}
		plan = built;
		location = loc;
		originalToolIndex = player.CurrentToolIndex;
		active = true;
		// 收框时先数一句：有几格此刻走不过去（挡路的树 / 水 / 建筑不在队列里，或开关关着没排进活）。
		// 这种格只能等外圈清完或绕外圈——跑起来看到长走路是地形如此，不是没按最近挑。
		// 整图灌一次距离场只在收框这一刻付一次，不在每格挑格的热路径上。
		int walled = 0;
		try
		{
			Vector2 at = ((Character)player).Tile;
			// 角色当空气（掩码 249）：鸡/牛/宠物站在哪不该让那一格读成「走不过去」，它们会走开、也推得开。
			TileUtil.DistanceField field = TileUtil.BuildWalkFieldIn(loc, at, terrainOnly: true, null);
			if (field.IsValid)
			{
				foreach (Vector2 tile in workTiles)
				{
					if (built.KindAt(tile) < 0)
					{
						continue;
					}
					if (field.Serve(tile) >= 1000000)
					{
						walled++;
					}
				}
			}
		}
		catch
		{
		}
		string tools = DebrisPlan.NamesOf(counts.Keys);
		Notify(I18n.T("debris-chain-start", new { tools }));
		Mod.Queue.Start(workTiles, loc, selBox, built);
		if (!Mod.Queue.IsRunning)
		{
			// 队列自己判了「这地方没活」：别把链挂着，工具直接换回去。
			Finalize(player, interrupted: true);
		}
		return true;
	}

	/// <summary>每帧由 ModEntry 在队列 Tick 之后调用：这一趟收工（或被打断）就把工具换回去。</summary>
	public void Tick()
	{
		if (!active)
		{
			return;
		}
		if (!Context.IsWorldReady)
		{
			Abort();
			return;
		}
		Farmer player = Game1.player;
		if (player == null)
		{
			Abort();
			return;
		}
		if (Mod.Queue.IsRunning || Mod.Animals.IsRunning || Mod.Selection.IsActive)
		{
			return;
		}
		if (Game1.activeClickableMenu != null || Game1.eventUp || Game1.dialogueUp || Game1.currentMinigame != null)
		{
			return;
		}
		// 被打断（自己走动 / 手动换工具 / 换地图 / 到点）不算收工：不弹背包。
		// 例外是体力不支：算打断，但属于预期内的收工，Finalize 里照常弹背包。
		bool ownWork = Mod.Queue.LastStopWasOwnWork && player.currentLocation == location;
		Finalize(player, interrupted: !ownWork);
	}

	private void Finalize(Farmer player, bool interrupted)
	{
		active = false;
		int kind = KindOf(player.CurrentItem);
		// 只有手里还是「我们这几把」之一（或者空手）才把槽位换回去：
		// 玩家自己切去水壶了，就别把他的选择顶掉。
		bool stillOurs = player.CurrentItem == null || (kind >= 0 && plan != null && plan.Carries(kind));
		if (stillOurs && originalToolIndex >= 0 && originalToolIndex < player.Items.Count && player.CurrentToolIndex != originalToolIndex)
		{
			player.CurrentToolIndex = originalToolIndex;
		}
		plan = null;
		location = null;
		originalToolIndex = -1;
		// 体力不支虽然走「打断」收摊（LastStopWasOwnWork=false），但要照常弹背包自动暂停。
		if (!interrupted || Mod.Queue.LastStopWasStamina)
		{
			Mod.OpenBackpackAfterTask();
		}
	}

	/// <summary>
	/// 开新选框时调用：旧链收摊并把工具换回去。
	/// 不收的话，取消新选框后旧链会以为「上一趟干完了」接着弹背包。
	/// </summary>
	public void Cancel()
	{
		if (!active)
		{
			return;
		}
		Farmer player = Game1.player;
		if (player == null)
		{
			Abort();
			return;
		}
		Finalize(player, interrupted: true);
	}

	/// <summary>回标题 / 存档卸载这类场合：不作恢复，直接把链撤了。</summary>
	public void Abort()
	{
		active = false;
		plan = null;
		location = null;
		originalToolIndex = -1;
	}
}
