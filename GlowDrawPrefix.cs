using Microsoft.Xna.Framework;

namespace FarmQueue;

internal static class GlowDrawPrefix
{
	public static void Prefix(ref Color color)
	{
		if (WorkHighlight.PaintingLight)
		{
			color = new Color(0, 0, 0, (int)color.A);
			return;
		}
		if (WorkHighlight.Tinting)
		{
			color = WorkHighlight.TintOf(color);
			WorkHighlight.DiagDraws++;
		}
	}
}
