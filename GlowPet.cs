using System;
using System.Reflection;
using HarmonyLib;
using StardewValley;
using StardewValley.Characters;

namespace FarmQueue;

[HighlightPatch]
[HarmonyPatch]
internal static class GlowPet
{
	private static MethodBase TargetMethod()
	{
		return WorkHighlight.FindDrawMethod(typeof(Pet));
	}

	[HarmonyPrefix]
	private static void Prefix(Character __instance)
	{
		if (__instance != null && WorkHighlight.Animals.Contains(__instance))
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
