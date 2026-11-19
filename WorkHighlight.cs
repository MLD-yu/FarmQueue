using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
using Object = StardewValley.Object;

namespace FarmQueue;

internal static class WorkHighlight
{
	public static readonly HashSet<Vector2> Tiles;

	/// <summary>作用在土地上的待办格（锄/种/浇/摆放/施肥）：地上铺绿色网格，不染色实体。</summary>
	public static readonly HashSet<Vector2> GridTiles = new HashSet<Vector2>();

	public static readonly HashSet<Character> Animals;

	/// <summary>待收产物的鱼塘（格队列执行期）：按实例引用，鱼塘是建筑、没有单一格坐标。</summary>
	public static readonly HashSet<FishPond> Ponds;

	public static bool Tinting;

	public static bool PaintingLight;

	public static int DiagDraws;

	public static readonly Color GlowColor;

	public static void UpdateFrom(TillQueue queue)
	{
		if (!queue.IsRunning)
		{
			LastRefreshTick = -1;
			Tiles.Clear();
			GridTiles.Clear();
			Ponds.Clear();
			return;
		}
		// 节流：每 10 tick 向世界现算一次「这格还有没有活」。
		// 队列账本（TileDone）只在弹出/验收时更新，会漏掉「一挥手顺带干完」的格子，
		// 现算才能保证耕完/浇完的格子立刻灭标。
		if (LastRefreshTick >= 0 && Game1.ticks - LastRefreshTick < 10)
		{
			return;
		}
		LastRefreshTick = Game1.ticks;
		Tiles.Clear();
		GridTiles.Clear();
		Ponds.Clear();
		foreach ((Vector2 tile, TileUtil.TileWork work) in queue.PendingTileWorks())
		{
			if (!queue.StillHasWork(tile))
			{
				continue;
			}
			if (work == TileUtil.TileWork.ScythePond)
			{
				FishPond pond = queue.PondAt(tile);
				if (pond != null)
				{
					Ponds.Add(pond);
				}
				continue;
			}
			// 土地作业画网格，实体作业发光/冒气泡（见 IsGridWork 注释）。
			if (IsGridWork(work))
			{
				GridTiles.Add(tile);
			}
			else
			{
				Tiles.Add(tile);
			}
		}
	}

	private static int LastRefreshTick = -1;

	/// <summary>作用在土地上的活：不产生实体气泡，也不进入夜间高亮。</summary>
	internal static bool IsGridWork(TileUtil.TileWork work)
	{
		switch (work)
		{
		case TileUtil.TileWork.Till:
		case TileUtil.TileWork.WaterDirt:
		case TileUtil.TileWork.WaterCrop:
		case TileUtil.TileWork.Sow:
		case TileUtil.TileWork.Fertilize:
		case TileUtil.TileWork.SprinklerPlace:
		case TileUtil.TileWork.SaplingPlace:
		case TileUtil.TileWork.PlaceObject:
			return true;
		default:
			return false;
		}
	}

	private static RenderTarget2D? NightSnapshot;

	private static bool NightSnapshotReady;

	private static readonly BlendState CopyWorldRgb = new BlendState
	{
		ColorWriteChannels = ColorWriteChannels.Red | ColorWriteChannels.Green | ColorWriteChannels.Blue,
		ColorSourceBlend = Blend.One,
		ColorDestinationBlend = Blend.Zero,
		AlphaSourceBlend = Blend.Zero,
		AlphaDestinationBlend = Blend.Zero
	};

	private static readonly BlendState ClearMaskAlpha = new BlendState
	{
		ColorWriteChannels = ColorWriteChannels.Alpha,
		ColorSourceBlend = Blend.Zero,
		ColorDestinationBlend = Blend.Zero,
		AlphaBlendFunction = BlendFunction.Add,
		AlphaSourceBlend = Blend.Zero,
		AlphaDestinationBlend = Blend.Zero
	};

	private static readonly BlendState WriteMaskAlpha = new BlendState
	{
		ColorWriteChannels = ColorWriteChannels.Alpha,
		ColorSourceBlend = Blend.Zero,
		ColorDestinationBlend = Blend.Zero,
		AlphaBlendFunction = BlendFunction.Max,
		AlphaSourceBlend = Blend.One,
		AlphaDestinationBlend = Blend.One
	};

	public static void CaptureNightGlow(SpriteBatch batch)
	{
		NightSnapshotReady = false;
		GameLocation loc = Game1.currentLocation;
		if (loc == null || !Game1.isDarkOut(loc) || (Tiles.Count == 0 && Animals.Count == 0 && Ponds.Count == 0))
		{
			return;
		}
		GraphicsDevice device = Game1.game1.GraphicsDevice;
		RenderTargetBinding[] bindings = device.GetRenderTargets();
		RenderTarget2D? target = (bindings.Length > 0) ? (bindings[0].RenderTarget as RenderTarget2D) : null;
		if (target == null)
		{
			return;
		}
		if (NightSnapshot == null || NightSnapshot.IsDisposed || NightSnapshot.Width != target.Width || NightSnapshot.Height != target.Height)
		{
			NightSnapshot?.Dispose();
			NightSnapshot = new RenderTarget2D(device, target.Width, target.Height, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
		}
		batch.End();
		try
		{
			device.SetRenderTarget(NightSnapshot);
			device.Clear(Color.Transparent);
			batch.Begin(SpriteSortMode.Deferred, CopyWorldRgb, SamplerState.PointClamp);
			batch.Draw(target, Vector2.Zero, Color.White);
			batch.End();
			batch.Begin(SpriteSortMode.Deferred, ClearMaskAlpha, SamplerState.PointClamp);
			batch.Draw(Game1.staminaRect, NightSnapshot.Bounds, Color.White);
			batch.End();
			PaintingLight = true;
			try
			{
				batch.Begin(SpriteSortMode.Deferred, WriteMaskAlpha, SamplerState.PointClamp);
				DrawQueuedEntities(batch, loc);
			}
			finally
			{
				PaintingLight = false;
				batch.End();
			}
		}
		finally
		{
			device.SetRenderTarget(target);
			batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
		}
		NightSnapshotReady = true;
	}

	/// <summary>土地作业的待办格铺绿色网格（和框选时能干活那一格的格标同一张图）。</summary>
	public static void DrawGridTiles(SpriteBatch batch)
	{
		if (GridTiles.Count == 0 || Game1.mouseCursors == null)
		{
			return;
		}
		foreach (Vector2 tile in GridTiles)
		{
			Vector2 pos = TileUtil.ToScreen(tile);
			// 视口外不画：格标是贴在格子上的 4 倍缩放 16x16 贴图（64 像素见方），
			// 大选框（几百上千格）里视口外的那部分是纯白花的 Draw 调用。
			if (OffScreen(pos))
			{
				continue;
			}
			batch.Draw(Game1.mouseCursors, pos, (Rectangle?)new Rectangle(194, 388, 16, 16), Color.White, 0f, Vector2.Zero, 4f, (SpriteEffects)0, 0.999f);
		}
	}

	public static void DrawAfterLighting(SpriteBatch batch)
	{
		batch.End();
		batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp);
		if (NightSnapshotReady && NightSnapshot != null)
		{
			NightSnapshotReady = false;
			batch.Draw(NightSnapshot, Vector2.Zero, Color.White);
		}
		DrawTaskBubbles(batch);
		batch.End();
		batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp);
	}

	public static void DrawTaskBubbles(SpriteBatch batch)
	{
		GameLocation? loc = Game1.currentLocation;
		if (loc == null || Game1.mouseCursors == null || ModEntry.Instance == null || !ModEntry.Instance.Config.ShowTaskBubbles)
		{
			return;
		}
		double milliseconds = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0.0;
		float bob = 4f * (float)Math.Round(Math.Sin(milliseconds / 250.0), 2);
		foreach (Vector2 tile in Tiles)
		{
			if (loc.objects.ContainsKey(tile) || loc.terrainFeatures.ContainsKey(tile))
			{
				DrawTaskBubble(batch, tile.X * 64f + 32f, tile.Y * 64f - 16f, bob);
			}
		}
		foreach (ResourceClump clump in loc.resourceClumps)
		{
			if (clump != null && GlowResourceClump.ClumpQueued(clump))
			{
				Rectangle bounds = ((TerrainFeature)clump).getBoundingBox();
				DrawTaskBubble(batch, bounds.Center.X, bounds.Y - 16f, bob);
			}
		}
		foreach (Character animal in Animals)
		{
			if (animal != null && animal.currentLocation == loc)
			{
				DrawTaskBubble(batch, animal.StandingPixel.X, animal.Position.Y - 16f, bob);
			}
		}
		foreach (FishPond pond in Ponds)
		{
			if (pond != null)
			{
				float centerX = pond.tileX.Value * 64f + pond.tilesWide.Value * 32f;
				DrawTaskBubble(batch, centerX, pond.tileY.Value * 64f - 16f, bob);
			}
		}
	}

	/// <summary>裁剪余量：气泡是画在格子上方的 4 倍缩放 20x24 贴图（约 80x96 像素），
	/// 传进来的已经是它自己的左上角屏幕坐标，128 像素够把它整个包住。</summary>
	private const float CullMargin = 128f;

	/// <summary>屏幕外吗。拿不到 GraphicsDevice 时一律当「在屏内」，宁可多画也不要漏画。</summary>
	private static bool OffScreen(Vector2 screen)
	{
		GraphicsDevice device = Game1.game1?.GraphicsDevice;
		if (device == null)
		{
			return false;
		}
		if (screen.X < 0f - CullMargin || screen.Y < 0f - CullMargin)
		{
			return true;
		}
		if (screen.X > (float)device.Viewport.Width + CullMargin || screen.Y > (float)device.Viewport.Height + CullMargin)
		{
			return true;
		}
		return false;
	}

	private static void DrawTaskBubble(SpriteBatch batch, float centerX, float bottomY, float bob)
	{
		Vector2 world = new Vector2(centerX - 40f, bottomY - 96f + bob);
		Vector2 screen = Game1.GlobalToLocal(Game1.viewport, world);
		if (OffScreen(screen))
		{
			return;
		}
		batch.Draw(Game1.mouseCursors, screen, (Rectangle?)new Rectangle(141, 465, 20, 24), Color.White * 0.9f, 0f, Vector2.Zero, 4f, (SpriteEffects)0, 1f);
		batch.Draw(Game1.mouseCursors, screen + new Vector2(30f, 16f), (Rectangle?)new Rectangle(403, 496, 5, 14), Color.White * 0.9f, 0f, Vector2.Zero, 4f, (SpriteEffects)0, 1f);
	}

	private static void DrawQueuedEntities(SpriteBatch batch, GameLocation loc)
	{
		foreach (Character c in Animals)
		{
			if (c != null && c.currentLocation == loc)
			{
				c.draw(batch);
			}
		}
		foreach (FishPond pond in Ponds)
		{
			if (pond != null)
			{
				pond.draw(batch);
			}
		}
		if (Tiles.Count > 0)
		{
			foreach (KeyValuePair<Vector2, Object> pair in loc.objects.Pairs)
			{
				if (pair.Value != null && Tiles.Contains(pair.Key))
				{
					pair.Value.draw(batch, (int)pair.Key.X, (int)pair.Key.Y, 1f);
				}
			}
			foreach (KeyValuePair<Vector2, TerrainFeature> pair in loc.terrainFeatures.Pairs)
			{
				if (pair.Value != null && Tiles.Contains(pair.Key))
				{
					pair.Value.draw(batch);
				}
			}
		}
		foreach (ResourceClump clump in loc.resourceClumps)
		{
			if (clump != null && GlowResourceClump.ClumpQueued(clump))
			{
				clump.draw(batch);
			}
		}
	}

	public static void UpdateFrom(AnimalQueue animals)
	{
		Animals.Clear();
		if (!animals.IsRunning)
		{
			return;
		}
		foreach (Character item in animals.PendingCritters())
		{
			Animals.Add(item);
		}
	}

	public static Color TintOf(Color color)
	{
		if (Game1.currentLocation == null || !Game1.isDarkOut(Game1.currentLocation))
		{
			return color;
		}
		return Color.Lerp(color, GlowColor, 0.82f);
	}

	internal static MethodBase FindDrawMethod(Type type)
	{
		MethodInfo[] methods = type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo methodInfo = null;
		MethodInfo[] array = methods;
		MethodInfo[] array2 = array;
		foreach (MethodInfo methodInfo2 in array2)
		{
			ParameterInfo[] parameters = methodInfo2.GetParameters();
			if (!(methodInfo2.Name != "draw") && parameters.Length != 0 && !(parameters[0].ParameterType != typeof(SpriteBatch)) && (methodInfo == null || parameters.Length < methodInfo.GetParameters().Length))
			{
				methodInfo = methodInfo2;
			}
		}
		if (methodInfo == null)
		{
			throw new MissingMethodException(type.Name + ".draw(SpriteBatch...) 找不到");
		}
		return methodInfo;
	}

	static WorkHighlight()
	{
		Tiles = new HashSet<Vector2>();
		Animals = new HashSet<Character>();
		Ponds = new HashSet<FishPond>();
		GlowColor = new Color(242, 238, 190);
	}
}
