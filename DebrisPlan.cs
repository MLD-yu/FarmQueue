using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Tools;

namespace FarmQueue;

/// <summary>
/// 杂物混合跑的工具分工表：某一格该由哪把工具来干、对那把工具算是什么活。
///
/// 为什么要这张表：0.5.61 的自动换工具是「一轮一把工具」——先把整框的杂草扫完，再换斧头把整框的树砍完，
/// 再换镐头把整框的石头敲完，人在同一片地里来回横穿三趟。玩家要的是**一条路线**：
/// 眼前是杂草就用镰刀，下一个最近的是树枝就换斧头，再下一个是石头就换镐头。
/// 所以这里按「格子」分工，队列按「离人最近」挑下一格，走到哪格就用那格该用的工具。
///
/// 分工规则：
///   · 只认身上真带着的工具（镰刀 / 斧头 / 镐头），没带的那把当不存在——框选标红标绿也按这个判；
///   · 杂草三把都能清，按优先级归一把：镰刀 → 斧头 → 镐头，取第一个「清杂草开关开着且身上真带着」的；
///   · 框选时手里那把负责它自己那一摊的全部活（镰刀顺带收作物 / 收机器产物，镐头顺带填平耕地等）；
///   · 另外两把只管杂物：斧头 = 树 / 果树 / 树桩 / 树枝，镐头 = 石头 / 巨石 / clump；
///   · 各把工具具体清什么，仍然由通用配置里那些开关决定（GetTileWork 已经读过开关）。
/// </summary>
internal sealed class DebrisPlan
{
	public const int Scythe = 0;

	public const int Axe = 1;

	public const int Pickaxe = 2;

	private readonly Item?[] tools = new Item?[3];

	private readonly ModConfig Config;

	/// <summary>这张表认哪张地图（框选和这一趟作业都在同一张图上）。</summary>
	public GameLocation Location { get; }

	/// <summary>框选时手里那把（0=镰刀 1=斧头 2=镐头）。</summary>
	public int HeldKind { get; }

	/// <summary>杂草归谁清：镰刀 → 斧头 → 镐头，第一个「开关开着且身上真带着」的；三把都不合格 = -1。</summary>
	public int WeedTool { get; }

	private DebrisPlan(GameLocation location, Farmer player, int heldKind, ModConfig config)
	{
		Location = location;
		Config = config;
		HeldKind = heldKind;
		tools[Scythe] = FindTool(player, Scythe);
		tools[Axe] = FindTool(player, Axe);
		tools[Pickaxe] = FindTool(player, Pickaxe);
		// 手里那把必须就是手里那一个实例：带了两把镰刀（普通 + 铱制）时，按种类搜会搜到另一把，
		// 跑起来就把手里的铱制镰刀换成普通镰刀了。
		tools[heldKind] = player.CurrentItem;
		// IsWanted 对三种杂草活的判据正好就是这三个开关，直接读开关省一次 GetTileWork。
		if (tools[Scythe] != null && config.ScytheClearWeeds)
		{
			WeedTool = Scythe;
		}
		else if (tools[Axe] != null && config.AxeWeeds)
		{
			WeedTool = Axe;
		}
		else if (tools[Pickaxe] != null && config.PickWeeds)
		{
			WeedTool = Pickaxe;
		}
		else
		{
			WeedTool = -1;
		}
	}

	/// <summary>
	/// 手里不是镰刀 / 斧头 / 镐头，或者「杂物自动换工具」关着 → null（调用方走原来的单工具队列）。
	/// </summary>
	public static DebrisPlan? Build(GameLocation? location, Farmer? player, ModConfig config)
	{
		if (location == null || player == null || config == null || !config.AutoSwitchDebrisTools)
		{
			return null;
		}
		int heldKind = KindOf(player.CurrentItem);
		if (heldKind < 0)
		{
			return null;
		}
		return new DebrisPlan(location, player, heldKind, config);
	}

	/// <summary>0=镰刀 1=斧头 2=镐头，别的物品 -1。</summary>
	public static int KindOf(Item? item)
	{
		if (item == null)
		{
			return -1;
		}
		if (TileUtil.IsScythe(item))
		{
			return Scythe;
		}
		if (item is Axe)
		{
			return Axe;
		}
		if (item is Pickaxe)
		{
			return Pickaxe;
		}
		return -1;
	}

	/// <summary>身上真带着的那一把；背包里没有就等于没这把工具，不拿它去分工。</summary>
	public static Item? FindTool(Farmer? player, int kind)
	{
		if (player == null || kind < 0)
		{
			return null;
		}
		foreach (Item item in player.Items)
		{
			if (item != null && KindOf(item) == kind)
			{
				return item;
			}
		}
		return null;
	}

	public Item? Tool(int kind)
	{
		return (kind >= 0 && kind < 3) ? tools[kind] : null;
	}

	public bool Carries(int kind)
	{
		return Tool(kind) != null;
	}

	/// <summary>身上带了几把（含手里那把）。</summary>

	public static string KindName(int kind)
	{
		return I18n.T(kind == Scythe ? "tool-scythe" : (kind == Axe ? "tool-axe" : "tool-pickaxe"));
	}

	public static string NamesOf(IEnumerable<int> kinds)
	{
		return string.Join(" / ", kinds.Select(new System.Func<int, string>(KindName)));
	}

	/// <summary>这一格对指定那把工具是什么活（没带这把工具 = 没活）。</summary>
	public TileUtil.TileWork WorkOf(int kind, Vector2 tile)
	{
		Item? tool = Tool(kind);
		return (tool != null) ? TileUtil.GetTileWork(Location, tile, tool, Config) : TileUtil.TileWork.None;
	}

	/// <summary>这一格该由哪把工具干；-1 = 身上带的工具谁都干不了（框选时这一格就标红）。</summary>
	public int KindAt(Vector2 tile)
	{
		return KindAt(tile, out var _);
	}

	public int KindAt(Vector2 tile, out TileUtil.TileWork work)
	{
		work = TileUtil.TileWork.None;
		TileUtil.TileWork heldWork = WorkOf(HeldKind, tile);
		bool heldOk = heldWork != TileUtil.TileWork.None && TileUtil.IsWanted(heldWork, Config);
		// 杂草三把都能清，按优先级归一把：
		//   · 手里这把也想清杂草、但优先级更高的另一把（身上带着、开关也开着）在 → 让给它
		//     （手里是斧头、镰刀也开着清杂草时，杂草归镰刀，斧头路过不理）；
		//   · 手里这把根本不清杂草（开关关着）、可这格就是杂草 → 同样交给定下来的那把。
		if (WeedTool >= 0 && WeedTool != HeldKind && (IsWeedWork(heldWork) || (heldWork == TileUtil.TileWork.None && IsWeedObject(tile))))
		{
			TileUtil.TileWork weedWork = WorkOf(WeedTool, tile);
			if (IsWeedWork(weedWork) && TileUtil.IsWanted(weedWork, Config))
			{
				work = weedWork;
				return WeedTool;
			}
		}
		// 手里那把：它想干的全部活都归它（镰刀顺带收作物 / 收机器产物，镐头顺带填平耕地等）。
		// 例外：挂着采集器的成熟树。镰刀在那一格只看得见「收树液」，可斧头那边挂着
		// 「清除树液采集器」时，这一格该整格交给斧头 —— 收完树液、把采集器摘回背包，
		// （「清除树（成熟）」也开着的话）再把树砍了。只让镰刀收一半就走过，
		// 等于「清除树液采集器」对这一格没生效。
		// 开关判定不用在这里重复：GetAxeWork 对挂着采集器的树本来就要求「清除树液采集器」开着。
		if (heldWork == TileUtil.TileWork.ScytheMachine && tools[Axe] != null)
		{
			TileUtil.TileWork axeWork = WorkOf(Axe, tile);
			if (IsTapperWork(axeWork) && TileUtil.IsWanted(axeWork, Config))
			{
				work = axeWork;
				return Axe;
			}
		}
		if (heldOk)
		{
			work = heldWork;
			return HeldKind;
		}
		// 另外两把：只认杂物。
		for (int kind = 0; kind < 3; kind++)
		{
			if (kind == HeldKind || tools[kind] == null)
			{
				continue;
			}
			TileUtil.TileWork other = WorkOf(kind, tile);
			if (other != TileUtil.TileWork.None && TileUtil.IsWanted(other, Config) && IsDebrisWork(kind, other))
			{
				work = other;
				return kind;
			}
		}
		return -1;
	}

	/// <summary>这一格该用的那把工具；这一格没活时给手里那把（调用方只是要个非 null 的判据）。</summary>
	public Item? ToolFor(Vector2 tile)
	{
		int kind = KindAt(tile);
		return (kind >= 0) ? tools[kind] : tools[HeldKind];
	}

	/// <summary>这一格的活（按分工后的那把工具算）；没人能干 = None。</summary>
	public TileUtil.TileWork WorkAt(Vector2 tile)
	{
		KindAt(tile, out var work);
		return work;
	}

	/// <summary>
	/// 扫一遍这些格子，数出各把工具分别要干几格。
	/// 收框时用它决定「要不要混合跑」（只有一把工具用得上就照旧走单工具队列），日志里也报这个数。
	/// </summary>
	public Dictionary<int, int> CountKinds(IEnumerable<Vector2> tiles, out int total)
	{
		Dictionary<int, int> counts = new Dictionary<int, int>();
		total = 0;
		foreach (Vector2 tile in tiles)
		{
			int kind = KindAt(tile);
			if (kind < 0)
			{
				continue;
			}
			counts.TryGetValue(kind, out var value);
			counts[kind] = value + 1;
			total++;
		}
		return counts;
	}

	/// <summary>斧头眼里的「树上的采集器」这类活：砍树（顺带摘采集器）或只摘采集器。</summary>
	public static bool IsTapperWork(TileUtil.TileWork work)
	{
		return work == TileUtil.TileWork.AxeTreeMature || work == TileUtil.TileWork.AxeTapper;
	}

	/// <summary>三种「清杂草」的活：优先级就在这三者之间排。</summary>
	public static bool IsWeedWork(TileUtil.TileWork work)	{
		switch (work)
		{
		case TileUtil.TileWork.ScytheWeed:
		case TileUtil.TileWork.AxeWeed:
		case TileUtil.TileWork.PickWeed:
			return true;
		default:
			return false;
		}
	}

	/// <summary>这一把工具眼里的「杂物」：镰刀=杂草，斧头=树/果树/树桩/树枝，镐头=石头/巨石/clump。</summary>
	public static bool IsDebrisWork(int kind, TileUtil.TileWork work)
	{
		switch (kind)
		{
		case Scythe:
			return TileUtil.IsScytheDebrisWork(work);
		case Axe:
			return TileUtil.IsAxeDebrisWork(work);
		default:
			return TileUtil.IsPickDebrisWork(work);
		}
	}

	private bool IsWeedObject(Vector2 tile)
	{
		try
		{
			StardewValley.Object obj = null;
			return Location.objects.TryGetValue(tile, out obj) && obj != null && obj.IsWeeds();
		}
		catch
		{
			return false;
		}
	}
}
