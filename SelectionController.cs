using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Network;
using StardewValley.Tools;
using Object = StardewValley.Object;

namespace FarmQueue;

internal sealed class SelectionController
{

	private const TileUtil.TillBlock NoMarker = (TileUtil.TillBlock)99;

	private readonly ModEntry Mod;

	private Texture2D? Pixel;

	private bool PixelBroken;

	private bool Active;

	private Vector2 StartTile;

	private Vector2 CurrentTile;

	private Rectangle Bounds;

	private TileUtil.TillBlock[]? MarkerOverlay;

	private Rectangle? MarkerBounds;


	public bool IsActive => Active;

	public int? BlockedCount { get; private set; }

	public int? TillableCount { get; private set; }

	public Rectangle CurrentBounds
	{
		get
		{
			return Bounds;
		}
	}

	public SelectionController(ModEntry mod)
	{
		Mod = mod;
	}

	public void Begin(Vector2 tile)
	{
		Active = true;
		StartTile = tile;
		// 开新选框 = 旧的一轮作废：杂物换工具链收摊并把工具换回去，免得取消选框后旧链自己接着跑。
		Mod.Chain.Cancel();
		CurrentTile = tile;
		Bounds = TileUtil.ToRect(tile, tile);
		RefreshMarkerOverlay();
	}

	public void UpdateCursor(Vector2 tile)
	{
		if (Active && !(tile == CurrentTile))
		{
			CurrentTile = tile;
			Bounds = TileUtil.ToRect(StartTile, tile);
			RefreshMarkerOverlay();
		}
	}

	public List<Vector2>? Finish(Vector2 tile)
	{
		if (!Active)
		{
			return null;
		}
		Active = false;
		Bounds = TileUtil.ToRect(StartTile, tile);
		ClearMarkerOverlay();
		Farmer player = Game1.player;
		if (player == null || Bounds.Width <= 0 || Bounds.Height <= 0)
		{
			return null;
		}
		List<Vector2> list = TileUtil.BuildWorkOrder(Bounds, ((Character)player).Tile);
		return list;
	}

	public void Cancel()
	{
		Active = false;
		Bounds = Rectangle.Empty;
		ClearMarkerOverlay();
	}

	private void ClearMarkerOverlay()
	{
		MarkerOverlay = null;
		MarkerBounds = null;
		TillableCount = null;
		BlockedCount = null;
	}

	private void RefreshMarkerOverlay()
	{
		ClearMarkerOverlay();
		int width = Bounds.Width;
		int height = Bounds.Height;
		if (width <= 0 || height <= 0 || width * height > 2000)
		{
			return;
		}
		Farmer player = Game1.player;
		GameLocation val = ((player != null) ? ((Character)player).currentLocation : null);
		if (val == null)
		{
			return;
		}
		Farmer player2 = Game1.player;
		Item val2 = ((player2 != null) ? player2.CurrentItem : null);
		bool flag = TileUtil.IsScythe(val2);
		// 镰刀：框里真有格活就画逐格网格；只有「没格活、但有产物/鱼塘」才整框切产物标记。
		// 旧条件只判「框里有产物」——擦到一个有产出的鱼塘，整框格标就全消失（用户实测）。
		bool scytheGridWork = flag && TileUtil.BoxHasTileWork(val, Bounds, val2, Mod.Config);
		if ((flag && !scytheGridWork && AnimalQueue.BoxHasProduce(val, Bounds)) || (AnimalQueue.IsAnimalModeItem(val2) && (AnimalQueue.IsPureAnimalItem(val2) || AnimalQueue.BoxHasAnimal(val, Bounds) || AnimalQueue.BoxHasProduce(val, Bounds))))
		{
			Tool val3 = (Tool)(object)((val2 is Tool) ? val2 : null);
			Rectangle bounds = Bounds;
			(bounds).Inflate(10, 10);
			if ((long)bounds.Width * (long)bounds.Height > 6000)
			{
				bounds = Bounds;
			}
			MarkerBounds = bounds;
			int width2 = bounds.Width;
			TileUtil.TillBlock[] array = new TileUtil.TillBlock[bounds.Width * bounds.Height];
			int num = 0;
			int num2 = 0;
			for (int i = 0; i < array.Length; i++)
			{
				array[i] = (TileUtil.TillBlock)99;
			}
			try
			{
				var enumerator = ((NetDictionary<long, FarmAnimal, NetRef<FarmAnimal>, SerializableDictionary<long, FarmAnimal>, NetLongDictionary<FarmAnimal, NetRef<FarmAnimal>>>)(object)val.animals).Values.GetEnumerator();
				try
				{
					while (enumerator.MoveNext())
					{
						FarmAnimal current = enumerator.Current;
						if ((current == null) | flag)
						{
							continue;
						}
						Point tilePoint = ((Character)current).TilePoint;
						if ((bounds).Contains(tilePoint))
						{
							bool flag2 = val3 != null && AnimalQueue.IsHarvestTool(val2) && AnimalQueue.ReadyForTool(current, val3);
							bool flag3 = AnimalQueue.HasHarvestToolInInventory() && AnimalQueue.BagToolReadyFor(current, out Tool _);
							bool flag4 = !((NetFieldBase<bool, NetBool>)(object)current.wasPet).Value | flag2 | flag3;
							array[(tilePoint.Y - bounds.Y) * width2 + (tilePoint.X - bounds.X)] = ((!flag4) ? TileUtil.TillBlock.WrongTool : TileUtil.TillBlock.None);
							if (flag4)
							{
								num++;
							}
							else
							{
								num2++;
							}
						}
					}
				}
				finally
				{
					((IDisposable)enumerator/*cast due to constrained. prefix*/).Dispose();
				}
				if (!flag)
				{
					foreach (NPC character in val.characters)
					{
						Pet val4 = (Pet)(object)((character is Pet) ? character : null);
						if (val4 != null && (bounds).Contains(((Character)character).TilePoint))
						{
							Point tilePoint2 = ((Character)character).TilePoint;
							bool flag5 = !AnimalQueue.PetToday(val4) && ((val2 != null) ? val2.QualifiedItemId : null) != "(O)ButterflyPowder";
							array[(tilePoint2.Y - bounds.Y) * width2 + (tilePoint2.X - bounds.X)] = ((!flag5) ? TileUtil.TillBlock.WrongTool : TileUtil.TillBlock.None);
							if (flag5)
							{
								num++;
							}
							else
							{
								num2++;
							}
						}
					}
				}
				foreach (KeyValuePair<Vector2, Object> pair in val.objects.Pairs)
				{
					if ((Bounds).Contains((int)pair.Key.X, (int)pair.Key.Y) && (bounds).Contains((int)pair.Key.X, (int)pair.Key.Y) && AnimalQueue.IsGroundProduce(pair.Value))
					{
						array[((int)pair.Key.Y - bounds.Y) * width2 + ((int)pair.Key.X - bounds.X)] = TileUtil.TillBlock.None;
						num++;
					}
				}
			}
			catch (Exception ex)
			{
				((Mod)Mod).Monitor.Log("动物选框标记失败：" + ex.Message, (LogLevel)3);
				return;
			}
			MarkerOverlay = array;
			TillableCount = num;
			BlockedCount = num2;
			return;
		}
		TileUtil.TillBlock[] array2 = new TileUtil.TillBlock[width * height];
		int num3 = 0;
		int num4 = 0;
		// 杂物自动换工具开着、手里又是镰刀/斧头/镐头时，「这一格能不能处理」要按身上真带着的那几把工具判，
		// 和真跑起来时的分工表同一套规则：标绿的就是一定会去干的，标红的就是身上没工具能干的。
		DebrisPlan? selPlan = DebrisPlan.Build(val, Game1.player, Mod.Config);
		HashSet<Point> pondTiles = (flag ? TileUtil.PondFootprintExceptOutput(val) : null);
		Farmer player3 = Game1.player;
		if (TileUtil.IsSprinklerItem((player3 != null) ? player3.CurrentItem : null))
		{
			Farmer player4 = Game1.player;
			Item val8 = ((player4 != null) ? player4.CurrentItem : null);
			Object val9 = (Object)((val8 is Object) ? val8 : null);
			if (val9 != null)
			{
				HashSet<Vector2> hashSet = new HashSet<Vector2>(TileUtil.PlanSprinklers(Bounds, val9.GetBaseRadiusForSprinkler(), TileUtil.SprinklerGapFor(Mod.Config), TileUtil.TrellisLayout(Mod.Config)));
				Vector2 item = default(Vector2);
				for (int j = 0; j < array2.Length; j++)
				{
					item = new Vector2((float)(Bounds.X + j % width), (float)(Bounds.Y + j / width));
					array2[j] = ((!hashSet.Contains(item)) ? TileUtil.TillBlock.WrongTool : TileUtil.TillBlock.None);
					if (array2[j] == TileUtil.TillBlock.None)
					{
						num3++;
					}
					else
					{
						num4++;
					}
				}
				MarkerOverlay = array2;
				TillableCount = num3;
				BlockedCount = num4;
				return;
			}
		}
		Vector2 tile = default(Vector2);
		for (int k = 0; k < height; k++)
		{
			for (int l = 0; l < width; l++)
			{
				tile = new Vector2((float)(Bounds.X + l), (float)(Bounds.Y + k));
				if (pondTiles != null && pondTiles.Contains(new Point(Bounds.X + l, Bounds.Y + k)))
				{
					array2[k * width + l] = NoMarker;
					continue;
				}
				Farmer player5 = Game1.player;
				if (TileUtil.IsSaplingItem((player5 != null) ? player5.CurrentItem : null))
				{
					Farmer player6 = Game1.player;
					var (num5, num6) = TileUtil.GetSaplingSpacing((player6 != null) ? player6.CurrentItem : null, Mod.Config);
					if ((Bounds.X + l - (Bounds).Left) % num5 != 0 || (Bounds.Y + k - (Bounds).Top) % num6 != 0)
					{
						array2[k * width + l] = TileUtil.TillBlock.WrongTool;
						num4++;
						continue;
					}
				}
				Farmer player7 = Game1.player;
				Item val10 = ((player7 != null) ? player7.CurrentItem : null);
				TileUtil.TileWork tileWork = TileUtil.TileWork.None;
				if (selPlan != null)
				{
					// 身上别的工具能清的杂物（树 / 树枝 / 树桩 / 石头 / 巨石，以及按优先级归了别家的杂草）也算能处理。
					// 分工表内部已经问过手里这把了，别在外面再问一遍。
					tileWork = selPlan.WorkAt(tile);
				}
				if (tileWork == TileUtil.TileWork.None)
				{
					// 谁都不能干：退回手里这把的判据，好让红格的原因（开关关着 / 工具不对）说得准。
					tileWork = TileUtil.GetTileWork(val, tile, val10, Mod.Config);
				}
				int num7;
				if (tileWork != TileUtil.TileWork.None)
				{
					num7 = ((!TileUtil.IsWanted(tileWork, Mod.Config)) ? 7 : 0);
				}
				else if (!(val10 is Hoe))
				{
					Object val11 = (Object)((val10 is Object) ? val10 : null);
					num7 = ((val11 != null && TileUtil.IsSowSeed((Item)val11) && TileUtil.CanSowHere(val, tile, val11) && Mod.Config.SowSprinkleredOnly) ? 9 : 8);
				}
				else
				{
					num7 = (int)TileUtil.GetTillBlockReason(val, tile, Mod.Config);
				}
				if ((array2[k * width + l] = (TileUtil.TillBlock)num7) == TileUtil.TillBlock.None)
				{
					num3++;
				}
				else
				{
					num4++;
				}
			}
		}
		MarkerOverlay = array2;
		TillableCount = num3;
		BlockedCount = num4;
	}

	public void Draw(SpriteBatch batch)
	{
		if (Active)
		{
			DrawTileMarkers(batch);
			DrawOutline(batch);
		}
	}

	private void DrawTileMarkers(SpriteBatch batch)
	{
		if (MarkerOverlay == null || Game1.mouseCursors == null)
		{
			return;
		}
		Rectangle val = MarkerBounds ?? Bounds;
		int width = val.Width;
		for (int i = 0; i < MarkerOverlay.Length; i++)
		{
			if (MarkerOverlay[i] != (TileUtil.TillBlock)99)
			{
				int num = ((MarkerOverlay[i] != TileUtil.TillBlock.None) ? 1 : 0);
				Vector2 val2 = TileUtil.ToScreen(new Vector2((float)(val.X + i % width), (float)(val.Y + i / width)));
				batch.Draw(Game1.mouseCursors, val2, (Rectangle?)new Rectangle(194 + num * 16, 388, 16, 16), Color.White, 0f, Vector2.Zero, 4f, (SpriteEffects)0, 0.999f);
			}
		}
	}

	private void DrawOutline(SpriteBatch batch)
	{
		EnsurePixel();
		if (Pixel != null)
		{
			Vector2 val = TileUtil.ToScreen(new Vector2((float)Bounds.X, (float)Bounds.Y));
			Rectangle val2 = default(Rectangle);
			val2 = new Rectangle((int)val.X, (int)val.Y, Bounds.Width * 64, Bounds.Height * 64);
			if (MarkerOverlay == null)
			{
				batch.Draw(Pixel, val2, Color.White * 0.12f);
			}
			Color val3 = Color.White * 0.9f;
			batch.Draw(Pixel, new Rectangle(val2.X, val2.Y, val2.Width, 4), val3);
			batch.Draw(Pixel, new Rectangle(val2.X, (val2).Bottom - 4, val2.Width, 4), val3);
			batch.Draw(Pixel, new Rectangle(val2.X, val2.Y, 4, val2.Height), val3);
			batch.Draw(Pixel, new Rectangle((val2).Right - 4, val2.Y, 4, val2.Height), val3);
		}
	}

	private void EnsurePixel()
	{
		if (Pixel != null || PixelBroken)
		{
			return;
		}
		try
		{
			Pixel = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
			Pixel.SetData<Color>((Color[])(object)new Color[1] { Color.White });
		}
		catch (Exception ex)
		{
			Pixel = null;
			PixelBroken = true;
			((Mod)Mod).Monitor.Log("创建选框贴图失败，已关闭选框描边：" + ex.Message, (LogLevel)3);
		}
	}
}
