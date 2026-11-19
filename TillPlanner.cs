using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;

namespace FarmQueue;

internal static class TillPlanner
{
	internal readonly record struct Swing(Vector2 Target, Vector2 Stand, int Facing, int Power, List<Vector2> Covers);

	private static readonly MethodInfo? AffectedTilesMethod = typeof(Tool).GetMethod("tilesAffected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	public static bool IsAvailable => (object)AffectedTilesMethod != null;

	public static List<Swing> Plan(GameLocation location, Tool tool, ICollection<Vector2> targets, Func<Vector2, bool> canStand, Farmer player, int maxPower)
	{
		List<Swing> list = new List<Swing>();
		if ((object)AffectedTilesMethod == null || targets.Count == 0 || maxPower < 0)
		{
			return list;
		}
		HashSet<Vector2> hashSet = new HashSet<Vector2>(targets);
		List<Swing> list2 = new List<Swing>();
		int facingDirection = ((Character)player).FacingDirection;
		try
		{
			foreach (Vector2 target in targets)
			{
				for (int i = 0; i < 4; i++)
				{
					((Character)player).FacingDirection = i;
					for (int j = 0; j <= maxPower; j++)
					{
						List<Vector2> list3 = Invoke(tool, target, j, player);
						if (list3 == null || list3.Count == 0)
						{
							continue;
						}
						bool flag = true;
						List<Vector2> list4 = new List<Vector2>(list3.Count);
						foreach (Vector2 item in list3)
						{
							if (targets.Contains(item))
							{
								list4.Add(item);
							}
							else if (TileUtil.WouldTill(location, item))
							{
								flag = false;
								break;
							}
						}
						if (flag && list4.Count != 0)
						{
							Vector2 val = target;
							Vector2 val2 = (Vector2)(i switch
							{
								0 => new Vector2(0f, 1f), 
								1 => new Vector2(-1f, 0f), 
								2 => new Vector2(0f, -1f), 
								_ => new Vector2(1f, 0f), 
							});
							Vector2 val3 = val + val2;
							if (canStand(val3) || targets.Contains(val3))
							{
								list2.Add(new Swing(target, val3, i, j, list4));
							}
						}
					}
				}
			}
		}
		finally
		{
			((Character)player).FacingDirection = facingDirection;
		}
		while (hashSet.Count > 0)
		{
			Swing? swing = null;
			int num = 0;
			foreach (Swing item2 in list2)
			{
				int num2 = 0;
				foreach (Vector2 cover in item2.Covers)
				{
					if (hashSet.Contains(cover))
					{
						num2++;
					}
				}
				bool flag2;
				if (num2 > num)
				{
					flag2 = true;
				}
				else if (num2 == num && num2 > 0 && swing.HasValue)
				{
					Swing valueOrDefault = swing.GetValueOrDefault();
					bool flag3 = num2 >= hashSet.Count;
					bool flag4 = num >= hashSet.Count;
					flag2 = (flag3 && !flag4) || ((!(flag3 & flag4)) ? (item2.Power > valueOrDefault.Power) : (item2.Power < valueOrDefault.Power));
				}
				else
				{
					flag2 = false;
				}
				if (flag2)
				{
					num = num2;
					swing = item2;
				}
			}
			if (swing.HasValue)
			{
				Swing valueOrDefault2 = swing.GetValueOrDefault();
				if (num > 0)
				{
					list.Add(valueOrDefault2);
					foreach (Vector2 cover2 in valueOrDefault2.Covers)
					{
						hashSet.Remove(cover2);
					}
					continue;
				}
			}
			foreach (Vector2 item3 in hashSet.OrderBy(delegate(Vector2 p)
			{
				return p.Y;
			}).ThenBy(delegate(Vector2 p)
			{
				return p.X;
			}))
			{
				Vector2? val4 = SingleSwingStand(item3, canStand);
				if (val4.HasValue)
				{
					Vector2 valueOrDefault3 = val4.GetValueOrDefault();
					list.Add(new Swing(item3, valueOrDefault3, FacingTo(valueOrDefault3, item3), 0, new List<Vector2> { item3 }));
				}
			}
			break;
		}
		return list;
	}

	private static Vector2? SingleSwingStand(Vector2 target, Func<Vector2, bool> canStand)
	{
		Vector2[] array = (Vector2[])(object)new Vector2[4]
		{
			new Vector2(0f, -1f),
			new Vector2(1f, 0f),
			new Vector2(0f, 1f),
			new Vector2(-1f, 0f)
		};
		Vector2[] array2 = array;
		Vector2[] array3 = array2;
		foreach (Vector2 val in array3)
		{
			Vector2 val2 = target + val;
			if (canStand(val2))
			{
				return val2;
			}
		}
		return null;
	}

	private static int FacingTo(Vector2 stand, Vector2 target)
	{
		float num = target.X - stand.X;
		float num2 = target.Y - stand.Y;
		if (Math.Abs(num) > Math.Abs(num2))
		{
			if (!(num > 0f))
			{
				return 3;
			}
			return 1;
		}
		if (!(num2 > 0f))
		{
			return 0;
		}
		return 2;
	}

	private static List<Vector2>? Invoke(Tool tool, Vector2 tile, int power, Farmer who)
	{
		try
		{
			return AffectedTilesMethod?.Invoke(tool, new object[3] { tile, power, who }) as List<Vector2>;
		}
		catch
		{
			return null;
		}
	}
}
