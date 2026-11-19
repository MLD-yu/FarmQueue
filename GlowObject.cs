using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using Object = StardewValley.Object;

namespace FarmQueue;

[HighlightPatch]
[HarmonyPatch(typeof(Object), "draw", new Type[]
{
	typeof(SpriteBatch),
	typeof(int),
	typeof(int),
	typeof(float)
})]
internal static class GlowObject
{
	[HarmonyPrefix]
	private static void Prefix(int x, int y)
	{
		if (WorkHighlight.Tiles.Contains(new Vector2((float)x, (float)y)))
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
}
