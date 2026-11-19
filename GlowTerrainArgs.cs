using StardewValley.TerrainFeatures;

namespace FarmQueue;

internal static class GlowTerrainArgs
{
	public static void Check(TerrainFeature feature)
	{
		if (feature != null && WorkHighlight.Tiles.Contains(feature.Tile))
		{
			WorkHighlight.Tinting = true;
		}
	}
}
