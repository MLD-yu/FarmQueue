using System.Diagnostics;
using System.Runtime.CompilerServices;
using StardewModdingAPI;

namespace FarmQueue;

internal sealed class ModConfig
{
	[CompilerGenerated]
	[DebuggerBrowsable(DebuggerBrowsableState.Never)]
	private SButton _003CModifierKey_003Ek__BackingField;

	[CompilerGenerated]
	[DebuggerBrowsable(DebuggerBrowsableState.Never)]
	private SButton _003CMouseButton_003Ek__BackingField;

	public bool TillDirt { get; set; }

	public bool ClearWeeds { get; set; }

	public bool TillSprinkleredOnly { get; set; }

	public bool SowSprinkleredOnly { get; set; }

	public bool WaterCropsOnly { get; set; }

	public bool SkipTilesOccupiedByObjects { get; set; }

	public bool HarvestCrops { get; set; }

	public bool HarvestFlowers { get; set; }

	public bool CollectForage { get; set; }

	public bool HarvestMoss { get; set; }

	public bool HarvestBluegrass { get; set; }

	public bool HarvestOtherGrass { get; set; }

	public bool ClearDeadCrops { get; set; }

	public bool ScytheClearWeeds { get; set; }

	public bool HarvestMachines { get; set; }

	public bool PickDeadCrops { get; set; }

	public bool PickEmptyDirt { get; set; }

	public bool PickUnsprinkleredDirt { get; set; }

	public bool PickFertilizedDirt { get; set; }

	public bool PickWeeds { get; set; }

	public bool PickFloor { get; set; }

	public bool PickResourceClumps { get; set; }

	public bool PickObjects { get; set; }

	public bool PickStones { get; set; }

	public bool PickFences { get; set; }

	public bool PickWorkingObjects { get; set; }

	public bool AxeFruitTreeSeeds { get; set; }

	public bool AxeFruitTreeSaplings { get; set; }

	public bool AxeFruitTreeMature { get; set; }

	public bool AxeTreeSeeds { get; set; }

	public bool AxeTreeSaplings { get; set; }

	public bool AxeTreeMature { get; set; }

	/// <summary>
	/// 把树上挂着的采集器摘下来（树不砍）。默认关。
	/// 「清除树（成熟）」+「收获机器产物」都开着时，砍树本来就会顺带摘下采集器；
	/// 这一项管的是「不砍树、只把采集器取回来」。
	/// </summary>
	public bool AxeTappers { get; set; }

	public bool AxeStumps { get; set; }

	public bool AxeDeadCrops { get; set; }

	public bool AxeLiveCrops { get; set; }

	public bool AxeGiantCrops { get; set; }

	public bool AxeTwigs { get; set; }

	public bool AxeWeeds { get; set; }

	public bool AxeFences { get; set; }

	/// <summary>
	/// 清除灌木丛（默认开）。能不能砍由游戏自己说了算：原版只有茶树和农场图边缘那几丛，
	/// 装了 Destroyable Bushes（Esca.DestroyableBushes）之后普通灌木才可砍。
	/// 茶树永远不砍 —— 那是产茶叶的作物。
	/// </summary>
	public bool AxeBushes { get; set; }

	public bool LoadMachines { get; set; }

	/// <summary>
	/// 手里那一叠用完之后，自动换到背包里同一种物品的另一个品质接着干（默认开）。
	/// 例：普通品质的玉米装完了，包里还有银星/金星的那几叠——自动接上，不用手动切换。
	/// </summary>
	public bool AutoSwitchItemQuality { get; set; }

	public bool PlaceSprinklers { get; set; }

	public int SaplingGapX { get; set; }

	public int SaplingGapY { get; set; }

	public string SprinklerLayout { get; set; }

	public int SprinklerGap { get; set; }

	public SButton ModifierKey
	{
		[CompilerGenerated]
		get
		{
			return _003CModifierKey_003Ek__BackingField;
		}
		[CompilerGenerated]
		set
		{
			_003CModifierKey_003Ek__BackingField = value;
		}
	}

	public SButton MouseButton
	{
		[CompilerGenerated]
		get
		{
			return _003CMouseButton_003Ek__BackingField;
		}
		[CompilerGenerated]
		set
		{
			_003CMouseButton_003Ek__BackingField = value;
		}
	}

	public bool SkipChargeAnimation { get; set; }

	public float ChargeWindupSpeed { get; set; }

	public int StopBelowStamina { get; set; }

	public bool ShowStatusText { get; set; }

	public bool ShowTaskBubbles { get; set; }

	public bool OpenBackpackOnFinish { get; set; }

	public bool AutoSwitchDebrisTools { get; set; }

	public bool DebugLog { get; set; }

	public ModConfig()
	{
		TillDirt = true;
		ClearWeeds = true;
		SkipTilesOccupiedByObjects = true;
		HarvestCrops = true;
		CollectForage = true;
		HarvestMoss = true;
		ClearDeadCrops = true;
		ScytheClearWeeds = true;
		HarvestMachines = true;
		PickFloor = true;
		PickResourceClumps = true;
		PickObjects = true;
		PickStones = true;
		AxeTreeMature = true;
		AxeStumps = true;
		AxeTwigs = true;
		AxeBushes = true;
		LoadMachines = true;
		AutoSwitchItemQuality = true;
		PlaceSprinklers = true;
		SprinklerLayout = "default";
		ModifierKey = (SButton)162;
		MouseButton = (SButton)1000;
		ChargeWindupSpeed = 1f;
		StopBelowStamina = 15;
		ShowStatusText = true;
		ShowTaskBubbles = true;
		OpenBackpackOnFinish = true;
		AutoSwitchDebrisTools = true;
	}
}
