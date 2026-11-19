using HarmonyLib;
using StardewValley;

namespace FarmQueue;

[HarmonyPatch(typeof(FarmerSprite), "StopAnimation")]
internal static class FarmerSpriteStopAnimationPatch
{
	[HarmonyPrefix]
	private static bool Prefix(FarmerSprite __instance)
	{
		ModEntry instance = ModEntry.Instance;
		if (instance != null)
		{
			return !instance.Queue.IsDrivingPlayerWalk(__instance);
		}
		return true;
	}
}
