using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Mods;
using StardewValley.Tools;

namespace FarmQueue;

public sealed class ModEntry : Mod
{
	private bool? scytheHarvestsCrops;

	private int PhysicalReleaseTicks;


	private bool ClickPending;

	private Vector2 ClickStartTile;

	private double LastClickMs;

	private Vector2 LastClickTile;


	/// <summary>
	/// 同屏双人时 SMAPI 只建一份 mod 实例，但 UpdateTicked / ButtonReleased / RenderedHud 会按屏幕各来一遍，
	/// 而 2P 那半的「光标」根本不是鼠标：原版对非主屏一律返回模拟光标，鼠标键恒为松开（InputState.GetMouseState），
	/// 那个模拟光标又停在 2P 的工具位置上（Game1.cs:11616）。不认屏幕就会：2P 那半的 tick 把选框改写成
	/// 「从起点连到 2P」并跟着 2P 走，再因为读不到鼠标左键而两帧后提前收工、把队列挂到 2P 身上。
	/// </summary>
	private int? OwnerScreenId;

	private bool IsOwnerScreen => OwnerScreenId == null || Context.ScreenId == OwnerScreenId || !Context.HasScreenId(OwnerScreenId.Value);

	internal static ModEntry? Instance { get; private set; }

	internal ModConfig Config { get; private set; }

	internal SelectionController Selection { get; private set; }

	internal DebrisChain Chain { get; private set; }

	internal TillQueue Queue { get; private set; }

	internal AnimalQueue Animals { get; private set; }

	internal WalkDriver Walk { get; private set; }

	internal bool ScytheHarvestsCrops
	{
		get
		{
			bool valueOrDefault = scytheHarvestsCrops == true;
			if (!scytheHarvestsCrops.HasValue)
			{
				valueOrDefault = DetectScytheHarvestMod();
				scytheHarvestsCrops = valueOrDefault;
			}
			return scytheHarvestsCrops.Value;
		}
	}

	internal static bool ScytheHarvestModLoaded => Instance?.ScytheHarvestsCrops ?? false;

	/// <summary>
	/// 装了 Destroyable Bushes（Esca）吗。只用来判「0 级斧头砍不砍得动」：
	/// 那个 mod 把原版 `axe.upgradeLevel >= 1` 换成了可配置项（默认 0 级也能砍），
	/// 而原版自己 0 级斧头砍灌木只摇不扣血。灌木本身能不能砍仍然问游戏（Bush.isDestroyable）。
	/// </summary>
	internal static bool DestroyableBushesLoaded { get; private set; }

	private bool DetectScytheHarvestMod()
	{
		if (((Mod)this).Helper.ModRegistry.IsLoaded("bcmpinc.HarvestWithScythe"))
		{
			return true;
		}
		foreach (IModInfo item in ((Mod)this).Helper.ModRegistry.GetAll())
		{
			string text = (item.Manifest.UniqueID + " " + item.Manifest.Name).ToLowerInvariant();
			if (text.Contains("scythe") && text.Contains("harvest"))
			{
				return ((Mod)this).Helper.ModRegistry.IsLoaded(item.Manifest.UniqueID);
			}
		}
		return false;
	}

	public override void Entry(IModHelper helper)
	{
		I18n.Init(((Mod)this).Helper.Translation);
		try
		{
			Config = helper.ReadConfig<ModConfig>();
		}
		catch (Exception ex)
		{
			Config = new ModConfig();
			((Mod)this).Monitor.Log("config.json 读取失败，已回退到默认值：" + ex.Message, (LogLevel)3);
		}
		DestroyableBushesLoaded = helper.ModRegistry.IsLoaded("Esca.DestroyableBushes");
		Selection = new SelectionController(this);
		Chain = new DebrisChain(this);
		Walk = new WalkDriver(this);
		Queue = new TillQueue(this);
		Animals = new AnimalQueue(this);
		Instance = this;
		ApplyPatches();
		helper.Events.Input.ButtonPressed += OnButtonPressed;
		helper.Events.Input.ButtonReleased += OnButtonReleased;
		helper.Events.Display.RenderedWorld += OnRenderedWorld;
		helper.Events.Display.RenderingStep += OnRenderingStep;
		helper.Events.Display.RenderedStep += OnRenderedStep;
		helper.Events.Display.RenderedHud += OnRenderedHud;
		helper.Events.GameLoop.GameLaunched += OnGameLaunched;
		helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
		helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
		MigrateLegacyConfig();
		((Mod)this).Monitor.Log(T("startup-log", new
		{
			modifier = Config.ModifierKey,
			button = Config.MouseButton
		}) + " " + (Config.SkipChargeAnimation ? T("startup-charge-skipped") : T("startup-charge-speed", new
		{
			speed = Config.ChargeWindupSpeed.ToString("0.##")
		})) + " " + ((Config.StopBelowStamina > 0) ? T("startup-stamina", new
		{
			stamina = Config.StopBelowStamina
		}) : "") + " " + T("startup-keys") + " " + (Walk.IsAvailable ? T("startup-walk-sim") : T("startup-walk-vanilla")) + " " + T("startup-hoe", new
		{
			work = DescribeWork()
		}) + " " + (Config.WaterCropsOnly ? T("startup-can-crops") : T("startup-can-all")) + " " + ((Config.StopBelowStamina > 0) ? T("startup-can-refill") + " " : "") + T("startup-can-empty") + " " + T("startup-seeds") + " " + T("startup-animals"), (LogLevel)0);
	}

	private void MigrateLegacyConfig()
	{
		string layout = Config.SprinklerLayout;
		if (layout == "default" || layout == "trellis")
		{
			return;
		}
		Config.SprinklerLayout = ((layout == "架子作物") ? "trellis" : "default");
		try
		{
			((Mod)this).Helper.WriteConfig<ModConfig>(Config);
		}
		catch (Exception ex)
		{
			((Mod)this).Monitor.Log("config.json 写回失败（不影响本次运行）：" + ex.Message, (LogLevel)2);
		}
	}

	private string DescribeWork()
	{
		List<string> list = new List<string> { T("work-artifact-spots"), T("work-seed-spots") };
		if (Config.ClearWeeds)
		{
			list.Insert(0, T("work-weeds"));
		}
		if (Config.TillDirt)
		{
			list.Insert(0, T("work-till"));
		}
		return string.Join(", ", list);
	}

	private void ApplyPatches()
	{
		Harmony val;
		try
		{
			val = new Harmony(((Mod)this).ModManifest.UniqueID);
		}
		catch (Exception ex)
		{
			((Mod)this).Monitor.Log("Harmony 初始化失败：" + ex.Message + "（锄地功能不受影响，只是自动走路时步态会一顿一顿的）", (LogLevel)3);
			return;
		}
		Type[] types = typeof(ModEntry).Assembly.GetTypes();
		Type[] array = types;
		foreach (Type type in array)
		{
			if (((MemberInfo)type).GetCustomAttribute<HarmonyPatch>() != null)
			{
				try
				{
					val.CreateClassProcessor(type).Patch();
				}
				catch
				{
				}
			}
		}
		MethodInfo[] methods = typeof(SpriteBatch).GetMethods();
		MethodInfo[] array2 = methods;
		foreach (MethodInfo methodInfo in array2)
		{
			if (!(methodInfo.Name != "Draw"))
			{
				try
				{
					val.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(GlowDrawPrefix), "Prefix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				catch
				{
				}
			}
		}
		MethodInfo methodInfo2 = AccessTools.DeclaredMethod(typeof(FarmerSprite), "StopAnimation", (Type[])null, (Type[])null);
		Patches val2 = (((object)methodInfo2 == null) ? null : Harmony.GetPatchInfo((MethodBase)methodInfo2));
		int num4;
		if (val2 != null)
		{
			ReadOnlyCollection<Patch> prefixes = val2.Prefixes;
			if (prefixes != null)
			{
				num4 = ((prefixes.Count > 0) ? 1 : 0);
				goto IL_0275;
			}
		}
		num4 = 0;
		goto IL_0275;
		IL_0275:
		bool flag = (byte)num4 != 0;
		((Mod)this).Monitor.Log($"走路动画补丁：{(flag ? "已生效" : "没生效")}（目标 {(((object)methodInfo2 != null) ? GeneralExtensions.FullDescription((MethodBase)methodInfo2) : null) ?? "找不到方法"}）", (LogLevel)((!flag) ? 3 : 0));
	}

	private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
	{
		RegisterConfigMenu();
	}

	private void RegisterConfigMenu()
	{
		IGenericModConfigMenuApi api = ((Mod)this).Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
		if (api == null)
		{
			((Mod)this).Monitor.Log("没检测到 Generic Mod Config Menu —— 游戏内不会有配置界面；想在里面改的话装一个即可（本 mod 已经适配，装上就自动出现）。", (LogLevel)0);
			return;
		}
		try
		{
			api.Register(((Mod)this).ModManifest, delegate
			{
				Config = new ModConfig();
			}, delegate
			{
				((Mod)this).Helper.WriteConfig<ModConfig>(Config);
			});
			try
			{
				api.AddSectionTitle(((Mod)this).ModManifest, () => T("section-general"));
			}
			catch
			{
			}
			api.AddKeybind(((Mod)this).ModManifest, delegate
			{
				return Config.ModifierKey;
			}, delegate(SButton value)
			{
				Config.ModifierKey = value;
			}, () => T("modifier-key"), () => T("modifier-key-tooltip"));
			api.AddKeybind(((Mod)this).ModManifest, delegate
			{
				return Config.MouseButton;
			}, delegate(SButton value)
			{
				Config.MouseButton = value;
			}, () => T("selection-button"), () => T("selection-button-tooltip"));
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.SkipChargeAnimation, delegate(bool value)
			{
				Config.SkipChargeAnimation = value;
			}, () => T("skip-charge-animation"), null);
			api.AddNumberOption(((Mod)this).ModManifest, () => Config.ChargeWindupSpeed, delegate(float value)
			{
				Config.ChargeWindupSpeed = value;
			}, () => T("charge-animation-speed"), null, 1f, 10f, 0.25f, (float value) => $"{value:0.##}x");
			api.AddNumberOption(((Mod)this).ModManifest, () => Config.StopBelowStamina, delegate(float value)
			{
				Config.StopBelowStamina = (int)value;
			}, () => T("stop-below-stamina"), () => T("stop-below-stamina-tooltip"), 0f, 500f, 5f, (float value) => $"{value:0}");
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.ShowStatusText, delegate(bool value)
			{
				Config.ShowStatusText = value;
			}, () => T("show-status-text-near-cursor"), () => T("show-status-text-near-cursor-tooltip"));
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.ShowTaskBubbles, delegate(bool value)
			{
				Config.ShowTaskBubbles = value;
			}, () => T("task-bubbles"), () => T("task-bubbles-tooltip"));
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.OpenBackpackOnFinish, delegate(bool value)
			{
				Config.OpenBackpackOnFinish = value;
			}, () => T("open-backpack-on-finish"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AutoSwitchDebrisTools, delegate(bool value)
			{
				Config.AutoSwitchDebrisTools = value;
			}, () => T("auto-switch-debris-tools"), () => T("auto-switch-debris-tools-tooltip"));
			try
			{
				api.AddSectionTitle(((Mod)this).ModManifest, () => T("section-hoe"));
			}
			catch
			{
			}
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.TillDirt, delegate(bool value)
			{
				Config.TillDirt = value;
			}, () => T("till-soil"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.ClearWeeds, delegate(bool value)
			{
				Config.ClearWeeds = value;
			}, () => T("clear-weeds"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.TillSprinkleredOnly, delegate(bool value)
			{
				Config.TillSprinkleredOnly = value;
			}, () => T("only-till-sprinkler-covered-soil"), null);
			try
			{
				api.AddSectionTitle(((Mod)this).ModManifest, () => T("section-watering-can"));
			}
			catch
			{
			}
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.WaterCropsOnly, delegate(bool value)
			{
				Config.WaterCropsOnly = value;
			}, () => T("only-water-planted-crops"), null);
			try
			{
				api.AddSectionTitle(((Mod)this).ModManifest, () => T("section-seeds"));
			}
			catch
			{
			}
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.SowSprinkleredOnly, delegate(bool value)
			{
				Config.SowSprinkleredOnly = value;
			}, () => T("only-sow-inside-sprinkler-coverage"), null);
			try
			{
				api.AddSectionTitle(((Mod)this).ModManifest, () => T("section-scythe"));
			}
			catch
			{
			}
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.HarvestCrops, delegate(bool value)
			{
				Config.HarvestCrops = value;
			}, () => T("harvest-crops"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.HarvestFlowers, delegate(bool value)
			{
				Config.HarvestFlowers = value;
			}, () => T("harvest-flowers"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.CollectForage, delegate(bool value)
			{
				Config.CollectForage = value;
			}, () => T("collect-forage"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.HarvestMoss, delegate(bool value)
			{
				Config.HarvestMoss = value;
			}, () => T("harvest-moss"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.HarvestBluegrass, delegate(bool value)
			{
				Config.HarvestBluegrass = value;
			}, () => T("harvest-blue-grass"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.HarvestOtherGrass, delegate(bool value)
			{
				Config.HarvestOtherGrass = value;
			}, () => T("harvest-normal-grass"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.ClearDeadCrops, delegate(bool value)
			{
				Config.ClearDeadCrops = value;
			}, () => T("clear-dead-crops"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.ScytheClearWeeds, delegate(bool value)
			{
				Config.ScytheClearWeeds = value;
			}, () => T("clear-weeds-scythe"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.HarvestMachines, delegate(bool value)
			{
				Config.HarvestMachines = value;
			}, () => T("collect-machine-output"), null);
			try
			{
				api.AddSectionTitle(((Mod)this).ModManifest, () => T("section-pickaxe"));
			}
			catch
			{
			}
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.PickEmptyDirt, delegate(bool value)
			{
				Config.PickEmptyDirt = value;
			}, () => T("flatten-tilled-soil"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.PickUnsprinkleredDirt, delegate(bool value)
			{
				Config.PickUnsprinkleredDirt = value;
			}, () => T("only-flatten-unwatered-tilled-soil"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.PickFertilizedDirt, delegate(bool value)
			{
				Config.PickFertilizedDirt = value;
			}, () => T("flatten-fertilized-soil"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.PickFloor, delegate(bool value)
			{
				Config.PickFloor = value;
			}, () => T("remove-flooring"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.PickResourceClumps, delegate(bool value)
			{
				Config.PickResourceClumps = value;
			}, () => T("break-boulders-and-meteorites"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.PickObjects, delegate(bool value)
			{
				Config.PickObjects = value;
			}, () => T("remove-placed-objects"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.PickStones, delegate(bool value)
			{
				Config.PickStones = value;
			}, () => T("mine"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.PickFences, delegate(bool value)
			{
				Config.PickFences = value;
			}, () => T("remove-fences"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.PickWorkingObjects, delegate(bool value)
			{
				Config.PickWorkingObjects = value;
			}, () => T("remove-busy-objects"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.PickWeeds, delegate(bool value)
			{
				Config.PickWeeds = value;
			}, () => T("clear-weeds-pickaxe"), null);
			try
			{
				api.AddSectionTitle(((Mod)this).ModManifest, () => T("section-axe"));
			}
			catch
			{
			}
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeFruitTreeSeeds, delegate(bool value)
			{
				Config.AxeFruitTreeSeeds = value;
			}, () => T("remove-fruit-trees-seeds"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeFruitTreeSaplings, delegate(bool value)
			{
				Config.AxeFruitTreeSaplings = value;
			}, () => T("remove-fruit-trees-saplings"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeFruitTreeMature, delegate(bool value)
			{
				Config.AxeFruitTreeMature = value;
			}, () => T("remove-fruit-trees-mature"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeTreeSeeds, delegate(bool value)
			{
				Config.AxeTreeSeeds = value;
			}, () => T("remove-trees-seeds"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeTreeSaplings, delegate(bool value)
			{
				Config.AxeTreeSaplings = value;
			}, () => T("remove-trees-saplings"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeTreeMature, delegate(bool value)
			{
				Config.AxeTreeMature = value;
			}, () => T("remove-trees-mature"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeTappers, delegate(bool value)
			{
				Config.AxeTappers = value;
			}, () => T("remove-tappers"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeStumps, delegate(bool value)
			{
				Config.AxeStumps = value;
			}, () => T("remove-stumps"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeLiveCrops, delegate(bool value)
			{
				Config.AxeLiveCrops = value;
			}, () => T("clear-living-crops"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeGiantCrops, delegate(bool value)
			{
				Config.AxeGiantCrops = value;
			}, () => T("harvest-giant-crops"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeTwigs, delegate(bool value)
			{
				Config.AxeTwigs = value;
			}, () => T("clear-twigs"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeBushes, delegate(bool value)
			{
				Config.AxeBushes = value;
			}, () => T("chop-bushes"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeFences, delegate(bool value)
			{
				Config.AxeFences = value;
			}, () => T("remove-wooden-fences"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AxeWeeds, delegate(bool value)
			{
				Config.AxeWeeds = value;
			}, () => T("clear-weeds-axe"), null);
			try
			{
				api.AddSectionTitle(((Mod)this).ModManifest, () => T("section-machine"));
			}
			catch
			{
			}
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.LoadMachines, delegate(bool value)
			{
				Config.LoadMachines = value;
			}, () => T("load-machines"), null);
			api.AddBoolOption(((Mod)this).ModManifest, () => Config.AutoSwitchItemQuality, delegate(bool value)
			{
				Config.AutoSwitchItemQuality = value;
			}, () => T("auto-switch-item-quality"), null);
			try
			{
				api.AddSectionTitle(((Mod)this).ModManifest, () => T("section-layout"));
			}
			catch
			{
			}
			api.AddNumberOption(((Mod)this).ModManifest, () => Config.SaplingGapX, delegate(float value)
			{
				Config.SaplingGapX = (int)MathF.Round(value);
			}, () => T("sapling-horizontal-gap"), null, 0f, 10f, 1f, (float value) => T("unit-tiles", new
			{
				count = (int)MathF.Round(value)
			}));
			api.AddNumberOption(((Mod)this).ModManifest, () => Config.SaplingGapY, delegate(float value)
			{
				Config.SaplingGapY = (int)MathF.Round(value);
			}, () => T("sapling-vertical-gap"), null, 0f, 10f, 1f, (float value) => T("unit-tiles", new
			{
				count = (int)MathF.Round(value)
			}));
			api.AddTextOption(((Mod)this).ModManifest, () => Config.SprinklerLayout, delegate(string value)
			{
				Config.SprinklerLayout = value;
			}, () => T("basic-sprinkler-layout"), () => T("basic-sprinkler-layout-tooltip"), new string[2] { "default", "trellis" }, (string value) => (value == "trellis") ? T("layout-trellis") : T("layout-default"));
			api.AddNumberOption(((Mod)this).ModManifest, () => Config.SprinklerGap, delegate(float value)
			{
				Config.SprinklerGap = (int)MathF.Round(value);
			}, () => T("sprinkler-spacing"), () => T("sprinkler-spacing-tooltip"), 0f, 5f, 1f, (float value) => T("unit-tiles", new
			{
				count = (int)MathF.Round(value)
			}));
		}
		catch (Exception ex)
		{
			((Mod)this).Monitor.Log("注册配置菜单失败（不影响功能，配置仍可改 config.json）：" + ex.Message, (LogLevel)3);
		}
	}

	private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
	{
		if (!Context.IsWorldReady || e.Button != Config.MouseButton || Game1.activeClickableMenu != null || (int)Config.ModifierKey == 0 || !((Mod)this).Helper.Input.IsDown(Config.ModifierKey))
		{
			return;
		}
		OwnerScreenId = Context.ScreenId;
		Farmer player = Game1.player;
		if (player == null || Selection.IsActive || ClickPending)
		{
			return;
		}
		Item currentItem = player.CurrentItem;
		bool flag = ((currentItem is Hoe || currentItem is WateringCan || currentItem is Pickaxe || currentItem is Axe) ? true : false);
		bool flag2 = AnimalQueue.IsAnimalModeItem(currentItem);
		bool flag3 = flag2 && !AnimalQueue.IsHarvestTool(currentItem);
		if (((flag | flag2) || TileUtil.IsSowSeed(player.CurrentItem) || TileUtil.IsScythe(player.CurrentItem) || TileUtil.IsSprinklerItem(player.CurrentItem) || TileUtil.IsFertilizerItem(player.CurrentItem) || TileUtil.IsPlaceableObjectItem(player.CurrentItem) || TileUtil.IsMachineLoadItem(player.CurrentItem)) && (flag3 || !(player.Stamina <= 1f) || TileUtil.IsSowSeed(player.CurrentItem)))
		{
			if (Queue.IsRunning || Animals.IsRunning)
			{
				ClickPending = true;
				ClickStartTile = e.Cursor.Tile;
				((Mod)this).Helper.Input.Suppress(e.Button);
			}
			else
			{
				Queue.Stop();
				Animals.Stop();
				Selection.Begin(e.Cursor.Tile);
				PhysicalReleaseTicks = 0;
				((Mod)this).Helper.Input.Suppress(e.Button);
			}
		}
	}

	private void OnButtonReleased(object? sender, ButtonReleasedEventArgs e)
	{
		if (IsOwnerScreen && Selection.IsActive && e.Button == Config.MouseButton && !((Mod)this).Helper.Input.IsSuppressed(e.Button))
		{
			FinishSelection(e.Cursor.Tile);
		}
	}

	private void FinishSelection(Vector2 cursorTile)
	{
		List<Vector2> list = Selection.Finish(cursorTile);
		Rectangle currentBounds = Selection.CurrentBounds;
		Farmer player = Game1.player;
		if (player != null)
		{
			GameLocation here = ((Character)player).currentLocation;
			if (TileUtil.IsScythe(player.CurrentItem))
			{
				// 与框选标记同判据：真有格活就走格队列；只有「没格活、但有产物/鱼塘」才进收取模式。
				// 旧条件只判「框里有产物」——擦到有产出的鱼塘就把整队劫去收取，作物/锄地全不收。
				if (here != null && TileUtil.BoxHasTileWork(here, currentBounds, player.CurrentItem, Config))
				{
					if (list != null && list.Count > 0)
					{
						StartQueueOrChain(list, here, currentBounds, player);
					}
					return;
				}
				if (here != null && AnimalQueue.BoxHasProduce(here, currentBounds))
				{
					Animals.Start(currentBounds, here, player.CurrentItem);
					return;
				}
				if (list != null && list.Count > 0)
				{
					StartQueueOrChain(list, here, currentBounds, player);
				}
				return;
			}
			if (AnimalQueue.IsAnimalModeItem(player.CurrentItem) && (AnimalQueue.IsPureAnimalItem(player.CurrentItem) || AnimalQueue.BoxHasAnimal(here, currentBounds) || AnimalQueue.BoxHasProduce(here, currentBounds)))
			{
				Animals.Start(currentBounds, here, player.CurrentItem);
			}
			else if (list != null && list.Count > 0)
			{
				StartQueueOrChain(list, here, currentBounds, player);
			}
		}
	}

	/// <summary>
	/// 收框后开队列的统一入口：先问杂物换工具链要不要接管（框里还有手里工具清不掉的杂物时，
	/// 它会自己换工具一轮轮清），不接管才走原来的单工具队列。
	/// </summary>
	private void StartQueueOrChain(List<Vector2> list, GameLocation here, Rectangle box, Farmer player)
	{
		if (list == null || list.Count == 0)
		{
			return;
		}
		if (Chain.TryStart(list, here, box, player, player.CurrentItem))
		{
			return;
		}
		Queue.Start(list, here, box);
	}

	/// <summary>
	/// 原版到 2:00（或体力 -15）才让人当场晕倒，而且要 canMove、!UsingTool、freezePause&lt;=0、
	/// controller 空、没菜单没事件一整排条件同时成立（Game1.UpdateOther）。队列作业时正是我们把这几项顶住，
	/// 时间就卡在 2:00 不晕。到点先自己收工，再用原版自己的清理动作把状态交还干净。
	/// </summary>
	private void HandleEndOfNight()
	{
		Farmer player = Game1.player;
		if (player == null || player.passedOut)
		{
			return;
		}
		bool late = Game1.timeOfDay >= 2600;
		if (!late && player.stamina > -15f)
		{
			return;
		}
		if (!Queue.IsRunning && !Animals.IsRunning && !Walk.IsAttached)
		{
			return;
		}
		Queue.Stop(I18n.T(late ? "stop-late-night" : "stop-exhausted"), isError: false);
		Animals.Stop(null, isError: false);
		Walk.Release();
		if (((Character)player).controller != null)
		{
			((Character)player).controller = null;
		}
		player.completelyStopAnimatingOrDoingAction();
		player.canMove = !Game1.eventUp;
		if (!Game1.eventUp && Game1.activeClickableMenu == null)
		{
			player.freezePause = 0;
			player.noMovementPause = 0;
		}
	}

	private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
	{
		if (!Context.IsWorldReady || !IsOwnerScreen)
		{
			return;
		}
		HandleEndOfNight();
		if (ClickPending)
		{
			if (Game1.activeClickableMenu != null)
			{
				ClickPending = false;
			}
			else if (!((Mod)this).Helper.Input.IsDown(Config.MouseButton) && !((Mod)this).Helper.Input.IsSuppressed(Config.MouseButton))
			{
				ClickPending = false;
				HandleWorkClick(((Mod)this).Helper.Input.GetCursorPosition().Tile);
			}
			else
			{
				Vector2 tile = ((Mod)this).Helper.Input.GetCursorPosition().Tile;
				if (TileUtil.ChebyshevDistance(tile, ClickStartTile) > 1.5f)
				{
					ClickPending = false;
					Queue.Stop();
					Animals.Stop();
					Selection.Begin(ClickStartTile);
					PhysicalReleaseTicks = 0;
				}
			}
		}
		if (Selection.IsActive)
		{
			if (Game1.activeClickableMenu != null)
			{
				Selection.Cancel();
				PhysicalReleaseTicks = 0;
			}
			else
			{
				ICursorPosition cursorPosition = ((Mod)this).Helper.Input.GetCursorPosition();
				Selection.UpdateCursor(cursorPosition.Tile);
				if (((Mod)this).Helper.Input.IsDown(Config.MouseButton) || ((Mod)this).Helper.Input.IsSuppressed(Config.MouseButton))
				{
					PhysicalReleaseTicks = 0;
				}
				else if (++PhysicalReleaseTicks >= 2)
				{
					PhysicalReleaseTicks = 0;
					FinishSelection(cursorPosition.Tile);
				}
			}
		}
		Queue.Tick();
		Animals.Tick();
		Animals.Watchdog();
		Chain.Tick();
		WorkHighlight.UpdateFrom(Queue);
		WorkHighlight.UpdateFrom(Animals);
	}

	private void HandleWorkClick(Vector2 tile)
	{
		double num = ((Game1.currentGameTime != null) ? Game1.currentGameTime.TotalGameTime.TotalMilliseconds : 0.0);
		bool flag = num - LastClickMs < 350.0 && !(LastClickTile == Vector2.Zero) && TileUtil.ChebyshevDistance(tile, LastClickTile) <= 1f;
		if (!Queue.IsRunning && Animals.IsRunning)
		{
			if (flag)
			{
				Animals.FilterSameKind(tile);
				LastClickMs = 0.0;
				LastClickTile = Vector2.Zero;
			}
			else
			{
				Animals.ToggleTargetAt(tile);
				LastClickMs = num;
				LastClickTile = tile;
			}
		}
		else if (flag)
		{
			Queue.FilterSameWork(tile);
			LastClickMs = 0.0;
			LastClickTile = Vector2.Zero;
		}
		else
		{
			Queue.ToggleTileAt(tile);
			LastClickMs = num;
			LastClickTile = tile;
		}
	}

	private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
	{
		if (Context.IsWorldReady)
		{
			// 选框只在发起它的那半画；绿网格是同一批世界格，两边都该看得见，不过滤。
			if (IsOwnerScreen)
			{
				Selection.Draw(e.SpriteBatch);
			}
			// 土地作业（锄/种/浇/摆放…）的待办格：地上铺绿色网格
			WorkHighlight.DrawGridTiles(e.SpriteBatch);
			if (!Game1.drawLighting)
			{
				WorkHighlight.DrawTaskBubbles(e.SpriteBatch);
			}
		}
	}

	private void OnRenderingStep(object? sender, RenderingStepEventArgs e)
	{
		if (Context.IsWorldReady && e.Step == RenderSteps.World_DrawLightmapOnScreen)
		{
			WorkHighlight.CaptureNightGlow(e.SpriteBatch);
		}
	}

	private void OnRenderedStep(object? sender, RenderedStepEventArgs e)
	{
		if (Context.IsWorldReady && e.Step == RenderSteps.World_DrawLightmapOnScreen)
		{
			WorkHighlight.DrawAfterLighting(e.SpriteBatch);
		}
	}

	private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
	{
		if (!Context.IsWorldReady || !IsOwnerScreen || !Config.ShowStatusText || Game1.smallFont == null)
		{
			return;
		}
		string text = null;
		if (Selection.IsActive)
		{
			Rectangle currentBounds = Selection.CurrentBounds;
			int total = currentBounds.Width * currentBounds.Height;
			int? tillableCount = Selection.TillableCount;
			int? blockedCount = Selection.BlockedCount;
			text = T("hud-selected", new
			{
				width = currentBounds.Width,
				height = currentBounds.Height,
				total = total
			});
			if (tillableCount.HasValue && blockedCount.HasValue && blockedCount.GetValueOrDefault() > 0)
			{
				text += T("hud-selected-detail", new
				{
					green = tillableCount,
					red = blockedCount
				});
			}
		}
		else if (Queue.IsRunning)
		{
			string activeVerb = Queue.ActiveVerb;
			text = T("hud-working", new
			{
				verb = activeVerb,
				count = Queue.Remaining
			});
		}
		else if (Animals.IsRunning)
		{
			text = T("hud-working-animals", new
			{
				verb = Animals.ActiveVerb,
				count = Animals.Remaining
			});
		}
		if (text != null)
		{
			Vector2 val = ((Mod)this).Helper.Input.GetCursorPosition().ScreenPixels + new Vector2(28f, 16f);
			e.SpriteBatch.DrawString(Game1.smallFont, text, val + new Vector2(2f, 2f), Color.Black * 0.75f);
			e.SpriteBatch.DrawString(Game1.smallFont, text, val, Color.White);
		}
	}

	private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
	{
		Selection.Cancel();
		Queue.Stop();
		Animals.Stop();
		Walk.Stop();
		Chain.Abort();
		PhysicalReleaseTicks = 0;
		ClickPending = false;
		LastClickMs = 0.0;
		LastClickTile = Vector2.Zero;
		OwnerScreenId = null;
	}

	private string T(string key)
	{
		return I18n.T(key);
	}

	/// <summary>
	/// 队列自己收工时按配置打开背包。单人下打开菜单即暂停时间，联机不会暂停。
	/// 只认「自己干完」：被打断、没种子、没体力而停下时不弹，免得盖掉玩家当时正开着的界面。
	/// </summary>
	internal void OpenBackpackAfterTask()
	{
		if (!Config.OpenBackpackOnFinish || !Context.IsWorldReady)
		{
			return;
		}
		if (Chain.Active)
		{
			return;
		}
		if (Queue.IsRunning || Animals.IsRunning || Selection.IsActive)
		{
			return;
		}
		if (Game1.activeClickableMenu != null || Game1.eventUp || Game1.dialogueUp || Game1.currentMinigame != null || Game1.game1 == null || !Game1.game1.IsActiveNoOverlay)
		{
			return;
		}
		Game1.activeClickableMenu = new GameMenu(GameMenu.inventoryTab);
	}

	private string T(string key, object tokens)
	{
		return I18n.T(key, tokens);
	}
}
