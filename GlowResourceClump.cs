using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.TerrainFeatures;

namespace FarmQueue;

[HighlightPatch]
[HarmonyPatch(typeof(ResourceClump), "draw", new Type[] { typeof(SpriteBatch) })]
internal static class GlowResourceClump
{
	[HarmonyPrefix]
	private static void Prefix(ResourceClump __instance)
	{
		if (ClumpQueued(__instance))
		{
			WorkHighlight.Tinting = true;
		}
	}

	// finalizer 而不是 postfix：draw 抛异常时 postfix 不跑，Tinting 会一直留着（之后每次 SpriteBatch.Draw
	// 都白走一遍 isDarkOut + Lerp，还会把不该染色的东西染色）。__exception 原样返回，异常照旧上抛。
	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		WorkHighlight.Tinting = false;
		return __exception;
	}

	internal static bool ClumpQueued(ResourceClump clump)
	{
		Rectangle boundingBox = ((TerrainFeature)clump).getBoundingBox();
		int num = boundingBox.X / 64;
		int num2 = boundingBox.Y / 64;
		int num3 = Math.Max(1, (boundingBox.Width + 63) / 64);
		int num4 = Math.Max(1, (boundingBox.Height + 63) / 64);
		for (int i = 0; i < num3; i++)
		{
			for (int j = 0; j < num4; j++)
			{
				if (WorkHighlight.Tiles.Contains(new Vector2((float)(num + i), (float)(num2 + j))))
				{
					return true;
				}
			}
		}
		return false;
	}
}
