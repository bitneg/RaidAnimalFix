using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RaidAnimalFix;

/// <summary>
/// Очки угрозы рейда считаются по Map.PlayerPawnsForStoryteller =
/// PawnsInFaction(Faction.OfPlayer): в тике карты хозяина это только его пешки.
/// Отряд гостя (10 бойцов) на карте хозяина рейдом не учитывается, и рейд
/// выходит смехотворным. Добавляем свободных колонистов других реальных игроков
/// на карте, принадлежащей реальному игроку, когда считает сам хозяин
/// (OfPlayer == владелец карты — это и есть тик карты / инцидент хозяина).
/// Порядок — порядок списка пешек карты (синхронный).
/// </summary>
[HarmonyPatch(typeof(Map), "PlayerPawnsForStoryteller", MethodType.Getter)]
public static class GuestThreatPawns
{
	/// <summary>Доля учитываемых гостей (1.0 — все). Константа кода, не настройка клиента.</summary>
	public const float GuestThreatFactor = 1f;

	private static void Postfix(Map __instance, ref IEnumerable<Pawn> __result)
	{
		if (!RelationsUtil.InMultiplayer || GuestThreatFactor <= 0f)
		{
			return;
		}
		try
		{
			List<Pawn> guests = GuestColonists(__instance);
			if (guests == null || guests.Count == 0)
			{
				return;
			}
			List<Pawn> list = new List<Pawn>(__result);
			list.AddRange(guests);
			__result = list;
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("GuestThreatPawns", ex);
		}
	}

	/// <summary>
	/// Свободные колонисты чужих реальных игроков на карте владельца (с учётом
	/// GuestThreatFactor). null — не наш случай (не MP-карта игрока или считает не владелец).
	/// </summary>
	public static List<Pawn> GuestColonists(Map map)
	{
		Faction owner = map?.ParentFaction;
		if (owner == null || !owner.IsPlayer || Faction.OfPlayer != owner || !MpContext.IsRealPlayerFaction(owner))
		{
			return null;
		}
		List<Pawn> result = null;
		IReadOnlyList<Pawn> spawned = map.mapPawns.AllPawnsSpawned;
		for (int i = 0; i < spawned.Count; i++)
		{
			Pawn p = spawned[i];
			Faction f = p.Faction;
			if (f != null && f != owner && f.IsPlayer && p.IsFreeColonist && !p.Dead && MpContext.IsRealPlayerFaction(f))
			{
				if (result == null)
				{
					result = new List<Pawn>();
				}
				result.Add(p);
			}
		}
		if (result != null && GuestThreatFactor < 1f)
		{
			int keep = (int)Math.Round(result.Count * GuestThreatFactor);
			if (keep < result.Count)
			{
				result.RemoveRange(keep, result.Count - keep);
			}
		}
		return result;
	}
}

/// <summary>
/// Богатство для угрозы: на домашней карте — wealthWatcher (считает пешки
/// фракции контекста), иначе — снаряжение колонистов OfPlayer. Снаряжение
/// гостей (и их стоимость как пешек) добавляем с тем же множителем.
/// </summary>
[HarmonyPatch(typeof(Map), "PlayerWealthForStoryteller", MethodType.Getter)]
public static class GuestThreatWealth
{
	private static void Postfix(Map __instance, ref float __result)
	{
		if (!RelationsUtil.InMultiplayer || GuestThreatPawns.GuestThreatFactor <= 0f)
		{
			return;
		}
		try
		{
			if (__instance.TreatAsPlayerHomeForThreatPoints && Find.Storyteller.difficulty.fixedWealthMode)
			{
				return;
			}
			List<Pawn> guests = GuestThreatPawns.GuestColonists(__instance);
			if (guests == null)
			{
				return;
			}
			float add = 0f;
			for (int i = 0; i < guests.Count; i++)
			{
				add += WealthWatcher.GetEquipmentApparelAndInventoryWealth(guests[i]);
				if (__instance.TreatAsPlayerHomeForThreatPoints)
				{
					add += guests[i].MarketValue;
				}
			}
			__result += add;
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("GuestThreatWealth", ex);
		}
	}
}

/// <summary>
/// Рейд на карту, где стоят колонисты другого игрока: письмо о рейде получает
/// только хозяин (фильтр писем по фракции контекста), а гость узнаёт о бое,
/// когда его пешек уже бьют. Показываем гостям локальное сообщение.
///
/// 3.0.946: new Message(...) брал ID из синхронного счётчика
/// (GetNextMessageID → GetNextID, MP пишет его в трейс), а создавалось сообщение
/// только у гостя — «Trace hashes don't match».
/// 3.0.947: решение снова локальное, но сообщение создаётся через
/// Messages.Message(string, LookTargets, def, historical: false): для этой
/// перегрузки MP (MessagesMarker / NextMessageIdPatch) выдаёт локальный
/// отрицательный ID, синхронный счётчик не трогается. Так сообщение получает
/// только сам гость, без расчёта на фильтры показа у остальных.
/// </summary>
[HarmonyPatch(typeof(IncidentWorker_Raid), "TryExecuteWorker")]
public static class GuestRaidNotice
{
	private static void Postfix(IncidentParms parms, bool __result)
	{
		if (!__result || parms == null || !(parms.target is Map map) || !RelationsUtil.InMultiplayer)
		{
			return;
		}
		try
		{
			Faction owner = map.ParentFaction;
			Faction me = RelationsUtil.LocalFaction();
			if (me == null || me == owner || !MpContext.IsRealPlayerFaction(me))
			{
				return;
			}
			if (!NotificationFilter.MapHasColonistsOf(map, me))
			{
				return;
			}
			string raider = parms.faction?.Name ?? "?";
			string place = map.Parent?.LabelCap ?? map.ToString();
			Pawn mine = null;
			IReadOnlyList<Pawn> spawned = map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < spawned.Count; i++)
			{
				if (spawned[i].Faction == me && spawned[i].IsFreeColonist)
				{
					mine = spawned[i];
					break;
				}
			}
			// Без свободного колониста (только рабы и т. п.) цель — своё поселение,
			// чтобы фильтр сообщений мода не принял сообщение за чужое
			LookTargets targets = (mine != null) ? new LookTargets(mine) : RelationsUtil.OwnLookTarget(me);
			Messages.Message("[RaidAnimalFix] " + "RAF_Relations_MsgRaidOnHostMap".Translate(raider, place), targets, MessageTypeDefOf.ThreatBig, historical: false);
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("GuestRaidNotice", ex);
		}
	}
}
