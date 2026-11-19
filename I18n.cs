using System;
using StardewModdingAPI;

namespace FarmQueue;

internal static class I18n
{
	private static ITranslationHelper? Helper;

	public static void Init(ITranslationHelper helper)
	{
		Helper = helper;
	}

	public static string T(string key)
	{
		return T(key, null);
	}

	public static string T(string key, object? tokens)
	{
		if (Helper == null)
		{
			return key;
		}
		try
		{
			string text = ((object)Helper.Get(key, tokens)).ToString();
			return text.StartsWith("(no translation:", StringComparison.Ordinal) ? key : text;
		}
		catch
		{
			return key;
		}
	}
}
