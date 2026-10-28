using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RaidAnimalFix;

[HarmonyPatch]
public static class MpQuestTickOrderFix
{
	// Зовётся каждый тик мира и каждой карты с квестами. Буфер переиспользуем, а сортируем
	// только если порядок по id нарушен (кэш MP почти всегда уже упорядочен) — раньше
	// здесь были новый список и сортировка тысяч квестов на каждый вызов.
	private static readonly List<Quest> Buffer = new List<Quest>();

	private static readonly Comparison<Quest> ById = (Quest a, Quest b) => a.id.CompareTo(b.id);

	private static bool _busy;

	public static MethodBase TargetMethod()
	{
		Type type = RafTypes.Find("Multiplayer.Client.Comp.MultiplayerAsyncQuest");
		if (type == null)
		{
			return null;
		}
		return AccessTools.Method(type, "TickQuests", new Type[1] { typeof(IEnumerable<Quest>) });
	}

	[HarmonyPrefix]
	public static bool Prefix(IEnumerable<Quest> quests)
	{
		if (quests == null)
		{
			return false;
		}
		// Вложенный вызов (квест в своём тике тикнул чужой список) — свой список.
		List<Quest> list = _busy ? new List<Quest>() : Buffer;
		list.Clear();
		try
		{
			foreach (Quest quest in quests)
			{
				if (quest != null)
				{
					list.Add(quest);
				}
			}
		}
		catch (Exception ex)
		{
			list.Clear();
			CaravanLeaveHelper.LogErrorThrottled("MpQuestTickOrderFix.snapshot", ex);
			return true;
		}
		// MP кладёт квест в кэш повторно при принятии (CacheQuest не проверяет, есть ли он
		// уже в мировом списке) — такой квест тикал дважды за тик, таймеры шли вдвое
		// быстрее. Кэш строится при загрузке, так что у хоста дубли есть, а у зашедшего
		// позже — нет: разное число тиков квеста у клиентов. Тикаем каждый квест один раз.
		for (int i = 1; i < list.Count; i++)
		{
			if (list[i - 1].id >= list[i].id)
			{
				list.Sort(ById);
				int w = 1;
				for (int r = 1; r < list.Count; r++)
				{
					if (list[r].id != list[w - 1].id)
					{
						list[w++] = list[r];
					}
				}
				list.RemoveRange(w, list.Count - w);
				break;
			}
		}
		bool outer = !_busy;
		_busy = true;
		try
		{
			for (int i = 0; i < list.Count; i++)
			{
				try
				{
					list[i].QuestTick();
				}
				catch (Exception ex2)
				{
					Log.Error("[RaidAnimalFix] QuestTick failed for quest " + list[i].id + " (" + list[i].name + "): " + ex2);
				}
			}
		}
		finally
		{
			if (outer)
			{
				_busy = false;
				list.Clear();
			}
		}
		return false;
	}
}

/// <summary>
/// MP кэширует квест для своего тика только из QuestGen.Generate, при принятии и при
/// загрузке. Квест, собранный модом вручную (Quest.MakeRaw + QuestManager.Add — так
/// делает Isekai), до перезагрузки не тикает вовсе: у хоста предложение висит без тика,
/// а у игрока, загрузившегося позже, оно уже в кэше и тикает. Кэшируем при добавлении,
/// если квеста ещё нет ни в одном списке MP — как после перезагрузки.
/// </summary>
[HarmonyPatch(typeof(QuestManager), "Add")]
public static class MpQuestCacheOnAdd
{
	private static MethodInfo _cache;

	private static IList _world;

	private static IDictionary _maps;

	private static bool Prepare()
	{
		Type t = RafTypes.Find("Multiplayer.Client.Comp.MultiplayerAsyncQuest");
		if (t == null)
		{
			return false;
		}
		_cache = AccessTools.Method(t, "CacheQuest", new Type[1] { typeof(Quest) });
		_world = AccessTools.Field(t, "worldQuestsCache")?.GetValue(null) as IList;
		_maps = AccessTools.Field(t, "mapQuestsCache")?.GetValue(null) as IDictionary;
		return _cache != null && _world != null && _maps != null;
	}

	private static void Postfix(Quest quest)
	{
		// AsyncTimeOn читает флаг игры MP и без RimExodus (кэш MP есть только при асинхронном времени).
		if (quest == null || !MpHelper.IsMultiplayer() || !RimExodusMp.AsyncTimeOn)
		{
			return;
		}
		try
		{
			if (_world.Contains(quest))
			{
				return;
			}
			foreach (object list in _maps.Values)
			{
				if (list is IList l && l.Contains(quest))
				{
					return;
				}
			}
			_cache.Invoke(null, new object[1] { quest });
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("MpQuestCacheOnAdd", ex);
		}
	}
}
