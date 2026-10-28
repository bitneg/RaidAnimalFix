using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace RaidAnimalFix;

// ISEKAI RPG LEVELING (JellyCreative.IsekaiLeveling) + Multiplayer Compatibility.
// В MPCompat есть патч IsekaiRPGCompat, но он оставляет несколько дыр:
// перехват в симуляции, локальные UI-переключатели, Find.CurrentMap в наградах,
// identity-хэши, Rand из UI и статики, которые переживают SaveAndReload.
// Всё через рефлексию — ни на Isekai, ни на MP, ни на MPCompat ссылок нет.

/// <summary>Общие помощники для патчей Isekai.</summary>
internal static class IsekaiMp
{
	public static Type T(string name)
	{
		return RafTypes.Find(name);
	}

	public static bool ModPresent => T("IsekaiLeveling.IsekaiComponent") != null;

	private static bool _iiResolved;

	private static PropertyInfo _inInterface;

	/// <summary>
	/// Мы точно в симуляции (тик/команда/отложенное событие симуляции).
	/// Если рефлексия MP не удалась — false: ничего не меняем.
	/// </summary>
	public static bool DefinitelyInSim()
	{
		try
		{
			if (!MpHelper.IsMultiplayer())
			{
				return false;
			}
			if (MpSim.simEventDepth > 0)
			{
				return true;
			}
			if (!_iiResolved)
			{
				_iiResolved = true;
				Type mp = T("Multiplayer.Client.Multiplayer");
				_inInterface = (mp == null) ? null : AccessTools.Property(mp, "InInterface");
			}
			if (_inInterface == null)
			{
				return false;
			}
			return !(bool)_inInterface.GetValue(null, null);
		}
		catch
		{
			return false;
		}
	}

	public static ThingComp CompOfType(Thing thing, Type compType)
	{
		if (compType == null || !(thing is ThingWithComps twc) || twc.AllComps == null)
		{
			return null;
		}
		List<ThingComp> comps = twc.AllComps;
		for (int i = 0; i < comps.Count; i++)
		{
			if (compType.IsInstanceOfType(comps[i]))
			{
				return comps[i];
			}
		}
		return null;
	}

	/// <summary>Очистить static-коллекцию или сбросить static-значение. Null-safe.</summary>
	public static void ResetStatic(string typeName, string fieldName, object resetValue = null)
	{
		try
		{
			Type type = T(typeName);
			FieldInfo field = (type == null) ? null : AccessTools.Field(type, fieldName);
			if (field == null || !field.IsStatic)
			{
				return;
			}
			object value = field.GetValue(null);
			if (value is IDictionary dict)
			{
				dict.Clear();
				return;
			}
			if (value is IList list && !list.IsFixedSize)
			{
				list.Clear();
				return;
			}
			if (value != null)
			{
				// HashSet<T> и прочие ICollection<T>: у них есть Clear() без параметров.
				MethodInfo clear = value.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
				if (clear != null && (value is ICollection || value.GetType().GetInterfaces().Any((Type i) => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>))))
				{
					clear.Invoke(value, null);
					return;
				}
			}
			if (field.IsInitOnly || field.IsLiteral)
			{
				return;
			}
			if (field.FieldType.IsValueType)
			{
				field.SetValue(null, resetValue ?? Activator.CreateInstance(field.FieldType));
			}
			else if (!(value is ICollection) && !(value is IEnumerable && !(value is string)))
			{
				field.SetValue(null, resetValue);
			}
		}
		catch (Exception ex)
		{
			Log.Warning("[RaidAnimalFix] Isekai static reset " + typeName + "." + fieldName + ": " + ex.Message);
		}
	}
}

/// <summary>
/// FIX 1. Префиксы MPCompat (UnlockNodePrefix, TryUnlockChainPrefix, RespecPrefix,
/// DevAddLevelPrefix, AttemptRefinementPrefix, RepairItemPrefix, TryAddRunePrefix,
/// RemoveRuneAtPrefix) проверяют только MP.IsInMultiplayer, а не InInterface.
/// Когда оригинал Isekai зовётся из симуляции (TreeAutoAssigner при LevelUp,
/// работа кузницы и т.п.), префикс всё равно уходит в Synced*-обёртку: та
/// выполняется сразу, но вызывающему возвращается фиктивный результат
/// (false / 0 / Success), а TryAddRune вообще зависит от того, открыто ли у
/// клиента окно Window_RunicStation. Разные клиенты — разный результат → desync.
///
/// Решение: в начало каждого такого compat-префикса транспайлером вставляем
/// <c>if (IsekaiCompatSimBypass.Bypass()) return true;</c> — в симуляции
/// compat-префикс сразу говорит Harmony «выполняй оригинал Isekai».
///
/// Почему транспайлер, а не наш префикс с <c>ref bool __result</c>: у части
/// compat-методов есть собственный параметр с именем «__result» (ref bool /
/// ref int / ref object). Harmony разбирает имена спец-параметров раньше
/// обычных, так что <c>__result</c> в нашем префиксе формально означал бы
/// возвращаемое значение compat-метода, но это хрупко и легко спутать с
/// параметром цели (при несовпадении типа ref int vs bool Harmony ещё и упадёт
/// при патче). Вставка IL в начало тела никаких имён не разрешает и
/// одинаково работает для всех восьми сигнатур; out/ref-параметры compat-метода
/// не трогаем — их заполнит оригинальный метод Isekai.
/// </summary>
[HarmonyPatch]
public static class IsekaiCompatSimBypass
{
	private static readonly string[] Names = new string[8] { "UnlockNodePrefix", "TryUnlockChainPrefix", "RespecPrefix", "DevAddLevelPrefix", "AttemptRefinementPrefix", "RepairItemPrefix", "TryAddRunePrefix", "RemoveRuneAtPrefix" };

	private static Type CompatType => RafTypes.Find("Multiplayer.Compat.IsekaiRPGCompat");

	private static bool Prepare()
	{
		return CompatType != null && IsekaiMp.ModPresent;
	}

	private static IEnumerable<MethodBase> TargetMethods()
	{
		Type type = CompatType;
		if (type == null)
		{
			yield break;
		}
		foreach (string name in Names)
		{
			MethodInfo m = AccessTools.DeclaredMethod(type, name);
			if (m != null && m.IsStatic && m.ReturnType == typeof(bool))
			{
				yield return m;
			}
			else
			{
				Log.Message("[RaidAnimalFix] IsekaiCompatSimBypass: " + name + " not found");
			}
		}
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
	{
		List<CodeInstruction> list = instructions.ToList();
		if (list.Count == 0)
		{
			return list;
		}
		Label start = generator.DefineLabel();
		list[0].labels.Add(start);
		list.InsertRange(0, new CodeInstruction[4]
		{
			new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(IsekaiCompatSimBypass), "Bypass")),
			new CodeInstruction(OpCodes.Brfalse, start),
			new CodeInstruction(OpCodes.Ldc_I4_1),
			new CodeInstruction(OpCodes.Ret)
		});
		return list;
	}

	public static bool Bypass()
	{
		return IsekaiMp.DefinitelyInSim();
	}
}

/// <summary>
/// FIX 2 + FIX 11. Регистрация синхронизации MP (на каждом клиенте одинаково,
/// при старте, до входа в игру):
/// — IsekaiComponent.autoDistributeStats: галочка «авто-распределение» в
///   ITab_IsekaiStats меняет поле локально (Widgets.CheckboxLabeled(ref ...)),
///   а читает его LevelUp в симуляции → sync field + Watch вокруг FillTab;
/// — IsekaiComponent.AutoDistributeByClass: вызывается из той же галочки сразу
///   после переключения и меняет статы → sync method;
/// — PawnStatGenerator.UpdateRankTraitFromStats(Pawn, IsekaiComponent): из UI
///   (ITab.ProcessPendingStatChanges) меняет трейт ранга → sync method. MP умеет
///   сериализовать ThingComp (SyncDictRimWorld: ThingComp, implicit — подклассы
///   тоже) и Pawn, так что регистрация допустима. В симуляции sync-метод просто
///   выполняется на месте.
/// </summary>
[StaticConstructorOnStartup]
public static class IsekaiMpSync
{
	public static bool Ready;

	private static object _autoDistField;

	private static MethodInfo _fieldWatch;

	private static MethodInfo _watchBegin;

	private static MethodInfo _watchEnd;

	static IsekaiMpSync()
	{
		try
		{
			Register();
		}
		catch (Exception ex)
		{
			Log.Warning("[RaidAnimalFix] Isekai MP sync registration failed: " + ex);
		}
	}

	private static void Register()
	{
		Type comp = IsekaiMp.T("IsekaiLeveling.IsekaiComponent");
		if (comp == null)
		{
			return;
		}
		Type mp = IsekaiMp.T("Multiplayer.API.MP");
		FieldInfo enabled = (mp == null) ? null : mp.GetField("enabled", BindingFlags.Static | BindingFlags.Public);
		if (enabled == null || !(bool)enabled.GetValue(null))
		{
			return;
		}
		Type syncType = IsekaiMp.T("Multiplayer.API.SyncType");
		Type iSyncField = IsekaiMp.T("Multiplayer.API.ISyncField");
		MethodInfo regField = mp.GetMethod("RegisterSyncField", BindingFlags.Static | BindingFlags.Public, null, new Type[1] { typeof(FieldInfo) }, null);
		MethodInfo regMethod = (syncType == null) ? null : mp.GetMethod("RegisterSyncMethod", BindingFlags.Static | BindingFlags.Public, null, new Type[2]
		{
			typeof(MethodInfo),
			syncType.MakeArrayType()
		}, null);
		_watchBegin = mp.GetMethod("WatchBegin", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
		_watchEnd = mp.GetMethod("WatchEnd", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
		_fieldWatch = iSyncField?.GetMethod("Watch", new Type[2]
		{
			typeof(object),
			typeof(object)
		});
		if (regField == null || regMethod == null || _watchBegin == null || _watchEnd == null || _fieldWatch == null)
		{
			Log.Warning("[RaidAnimalFix] Isekai MP sync: MP API members not found");
			return;
		}
		// Порядок регистрации фиксирован — одинаков на всех клиентах.
		FieldInfo autoField = AccessTools.Field(comp, "autoDistributeStats");
		if (autoField != null)
		{
			_autoDistField = regField.Invoke(null, new object[1] { autoField });
		}
		MethodInfo autoMethod = AccessTools.DeclaredMethod(comp, "AutoDistributeByClass", Type.EmptyTypes);
		if (autoMethod != null)
		{
			regMethod.Invoke(null, new object[2] { autoMethod, null });
		}
		Type gen = IsekaiMp.T("IsekaiLeveling.PawnStatGenerator");
		MethodInfo rankTrait = (gen == null) ? null : AccessTools.DeclaredMethod(gen, "UpdateRankTraitFromStats", new Type[2]
		{
			typeof(Pawn),
			comp
		});
		if (rankTrait != null)
		{
			regMethod.Invoke(null, new object[2] { rankTrait, null });
		}
		Ready = _autoDistField != null;
		Log.Message("[RaidAnimalFix] Isekai MP sync registered (autoDistributeStats field: " + (_autoDistField != null) + ", AutoDistributeByClass: " + (autoMethod != null) + ", UpdateRankTraitFromStats: " + (rankTrait != null) + ")");
	}

	public static bool BeginWatchAutoDistribute(ThingComp comp)
	{
		if (!Ready || comp == null)
		{
			return false;
		}
		try
		{
			_watchBegin.Invoke(null, null);
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("Isekai WatchBegin", ex);
			return false;
		}
		try
		{
			_fieldWatch.Invoke(_autoDistField, new object[2] { comp, null });
		}
		catch (Exception ex2)
		{
			CaravanLeaveHelper.LogErrorThrottled("Isekai autoDistributeStats Watch", ex2);
		}
		return true;
	}

	public static void EndWatch()
	{
		try
		{
			_watchEnd.Invoke(null, null);
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("Isekai WatchEnd", ex);
		}
	}
}

/// <summary>
/// FIX 2 (часть UI). Watch автодистрибуции вокруг ITab_IsekaiStats.FillTab:
/// FillTabVanilla, DrawStatPanel и DrawStatsContent (все три галочки)
/// вызываются только изнутри FillTab, так что одной пары Begin/End хватает.
/// Стек watch в MP вложенный, с compat-ским Watch статов не конфликтует.
/// </summary>
[HarmonyPatch]
public static class IsekaiAutoDistributeWatch
{
	private static readonly MethodInfo SelPawnGetter = AccessTools.PropertyGetter(typeof(ITab), "SelPawn");

	private static MethodBase TargetMethod()
	{
		Type type = IsekaiMp.T("IsekaiLeveling.UI.ITab_IsekaiStats");
		return (type == null) ? null : AccessTools.DeclaredMethod(type, "FillTab");
	}

	private static void Prefix(ITab __instance, ref bool __state)
	{
		__state = false;
		if (!IsekaiMpSync.Ready || !MpHelper.IsMultiplayer())
		{
			return;
		}
		try
		{
			Pawn pawn = (SelPawnGetter?.Invoke(__instance, null) as Pawn) ?? (Find.Selector.SingleSelectedThing as Pawn);
			ThingComp comp = IsekaiMp.CompOfType(pawn, IsekaiMp.T("IsekaiLeveling.IsekaiComponent"));
			__state = IsekaiMpSync.BeginWatchAutoDistribute(comp);
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("IsekaiAutoDistributeWatch", ex);
		}
	}

	private static void Finalizer(bool __state)
	{
		if (__state)
		{
			IsekaiMpSync.EndWatch();
		}
	}
}

/// <summary>
/// FIX 3. GuildDropZoneUtility.TryFindDropZone сначала смотрит Find.CurrentMap —
/// карту под камерой конкретного клиента — а зовётся из симуляции (доставка
/// наград). При двух домашних картах с зоной награды падают на разные карты.
/// В MP: та же логика, но только обход Find.Maps по порядку.
/// </summary>
[HarmonyPatch]
public static class IsekaiGuildDropZoneFix
{
	private static MethodBase TargetMethod()
	{
		Type type = IsekaiMp.T("IsekaiLeveling.Quests.GuildDropZoneUtility");
		return (type == null) ? null : AccessTools.DeclaredMethod(type, "TryFindDropZone", new Type[2]
		{
			typeof(Map).MakeByRefType(),
			typeof(IntVec3).MakeByRefType()
		});
	}

	private static bool Prefix(ref Map map, ref IntVec3 cell, ref bool __result)
	{
		if (!MpHelper.IsMultiplayer())
		{
			return true;
		}
		map = null;
		cell = IntVec3.Invalid;
		__result = false;
		ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("Isekai_GuildDropZone");
		List<Map> maps = Find.Maps;
		if (def == null || maps == null)
		{
			return false;
		}
		for (int i = 0; i < maps.Count; i++)
		{
			Map m = maps[i];
			if (m == null || !m.IsPlayerHome)
			{
				continue;
			}
			List<Thing> list = m.listerThings?.ThingsOfDef(def);
			if (list != null && list.Count > 0)
			{
				map = m;
				cell = list[0].Position;
				__result = true;
				return false;
			}
		}
		return false;
	}
}

/// <summary>
/// FIX 4. CompUseEffect_ManaCore.CompFloatMenuOptions обнуляет поле симуляции
/// pendingBulkAbsorb при каждом построении контекстного меню — у того клиента,
/// который кликнул ПКМ. DoEffect потом поглощает разное число ядер.
/// В MP пропускаем сброс и сразу отдаём GetBulkAbsorbOptions (выбор опции
/// синхронизирует MPCompat через RegisterSyncDelegateLambda).
/// </summary>
[HarmonyPatch]
public static class IsekaiManaCoreMenuFix
{
	private static MethodInfo _getOptions;

	private static MethodBase TargetMethod()
	{
		Type type = IsekaiMp.T("IsekaiLeveling.CompUseEffect_ManaCore");
		if (type == null)
		{
			return null;
		}
		_getOptions = AccessTools.DeclaredMethod(type, "GetBulkAbsorbOptions", new Type[1] { typeof(Pawn) });
		return (_getOptions == null) ? null : AccessTools.DeclaredMethod(type, "CompFloatMenuOptions", new Type[1] { typeof(Pawn) });
	}

	private static bool Prefix(object __instance, Pawn selPawn, ref IEnumerable<FloatMenuOption> __result)
	{
		if (_getOptions == null || !MpHelper.IsMultiplayer())
		{
			return true;
		}
		try
		{
			__result = (_getOptions.Invoke(__instance, new object[1] { selPawn }) as IEnumerable<FloatMenuOption>) ?? Enumerable.Empty<FloatMenuOption>();
			return false;
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("IsekaiManaCoreMenuFix", ex);
			return true;
		}
	}
}

/// <summary>
/// FIX 5. RaidRankPatches.WasLordProcessed/MarkLordProcessed хранят
/// lord.GetHashCode() — identity-хэш объекта, свой в каждом процессе. Плюс
/// очистка «при 100 записях» зависит от локального счётчика, а у
/// присоединившегося клиента он другой. В MP храним lord.loadID и чистим набор
/// только в Game.FinalizeInit (SaveAndReload идёт на всех клиентах сразу).
/// </summary>
[HarmonyPatch]
public static class IsekaiLordProcessedWasFix
{
	internal static readonly HashSet<int> Processed = new HashSet<int>();

	private static MethodBase TargetMethod()
	{
		Type type = IsekaiMp.T("IsekaiLeveling.MobRanking.RaidRankPatches");
		return (type == null) ? null : AccessTools.DeclaredMethod(type, "WasLordProcessed", new Type[1] { typeof(Lord) });
	}

	private static bool Prefix(Lord lord, ref bool __result)
	{
		if (!MpHelper.IsMultiplayer())
		{
			return true;
		}
		__result = lord == null || Processed.Contains(lord.loadID);
		return false;
	}
}

[HarmonyPatch]
public static class IsekaiLordProcessedMarkFix
{
	private static MethodBase TargetMethod()
	{
		Type type = IsekaiMp.T("IsekaiLeveling.MobRanking.RaidRankPatches");
		return (type == null) ? null : AccessTools.DeclaredMethod(type, "MarkLordProcessed", new Type[1] { typeof(Lord) });
	}

	private static bool Prefix(Lord lord)
	{
		if (!MpHelper.IsMultiplayer())
		{
			return true;
		}
		if (lord != null)
		{
			Processed.Add(lord.loadID);
		}
		return false;
	}

	private static HashSet<int> Processed => IsekaiLordProcessedWasFix.Processed;
}

/// <summary>RaidRankPatches.ClearAll чистит и наш набор loadID.</summary>
[HarmonyPatch]
public static class IsekaiLordProcessedClearFix
{
	private static MethodBase TargetMethod()
	{
		Type type = IsekaiMp.T("IsekaiLeveling.MobRanking.RaidRankPatches");
		return (type == null) ? null : AccessTools.DeclaredMethod(type, "ClearAll", Type.EmptyTypes);
	}

	private static void Postfix()
	{
		IsekaiLordProcessedWasFix.Processed.Clear();
	}
}

/// <summary>
/// FIX 6. MobRankComponent.RecalculateRank крутит Rand (вариация ранга,
/// шанс элиты, имя). Впервые его может дёрнуть UI (инспектор/ITab для
/// неспавненной пешки) — только у одного клиента: тот получает свой ранг и
/// сдвигает общий Rand. В MP — свой детерминированный сид по thingIDNumber,
/// после выхода состояние Rand восстанавливается.
/// </summary>
[HarmonyPatch]
public static class IsekaiMobRankRandFix
{
	private static MethodBase TargetMethod()
	{
		Type type = IsekaiMp.T("IsekaiLeveling.MobRanking.MobRankComponent");
		return (type == null) ? null : AccessTools.DeclaredMethod(type, "RecalculateRank", Type.EmptyTypes);
	}

	private static void Prefix(ThingComp __instance, ref bool __state)
	{
		__state = false;
		if (__instance?.parent == null || !MpHelper.IsMultiplayer())
		{
			return;
		}
		Rand.PushState(Gen.HashCombineInt(__instance.parent.thingIDNumber, 0x15EA));
		__state = true;
	}

	private static void Finalizer(bool __state)
	{
		if (__state)
		{
			Rand.PopState();
		}
	}
}

/// <summary>
/// FIX 7. Настройки Isekai (множители XP, кап уровня, режимы ранжирования…)
/// читаются симуляцией, а у каждого клиента свои. Во время MP-игры окно
/// настроек блокируем — вместо него поясняющая надпись.
/// </summary>
[HarmonyPatch]
public static class IsekaiSettingsLockFix
{
	private static MethodBase TargetMethod()
	{
		Type type = IsekaiMp.T("IsekaiLeveling.IsekaiMod");
		return (type == null) ? null : AccessTools.DeclaredMethod(type, "DoSettingsWindowContents", new Type[1] { typeof(Rect) });
	}

	private static bool Prefix(Rect inRect)
	{
		if (Current.ProgramState != ProgramState.Playing || !MpHelper.IsMultiplayer())
		{
			return true;
		}
		GameFont font = Text.Font;
		Text.Font = GameFont.Small;
		Widgets.Label(inRect, "[Desync Fix] " + "RAF_IsekaiSettingsLocked".Translate().Resolve());
		Text.Font = font;
		return false;
	}
}

/// <summary>
/// FIX 8. NationRankSystem.RunOnGameLoad / IsekaiTitleApplier.RunOnGameLoad
/// идут из FinalizeInit и трогают трейты/пешек; напрямую Rand они не зовут,
/// но ванильный код под ними может. Изолируем Rand на время прохода
/// (сид — TicksGame, одинаковый у всех после SaveAndReload), чтобы общее
/// состояние генератора не зависело от того, что сделал проход.
/// </summary>
[HarmonyPatch]
public static class IsekaiGameLoadRandFix
{
	private static bool Prepare()
	{
		return IsekaiMp.ModPresent;
	}

	private static IEnumerable<MethodBase> TargetMethods()
	{
		foreach (string name in new string[2] { "IsekaiLeveling.NationRankSystem", "IsekaiLeveling.IsekaiTitleApplier" })
		{
			Type type = IsekaiMp.T(name);
			MethodInfo m = (type == null) ? null : AccessTools.DeclaredMethod(type, "RunOnGameLoad", Type.EmptyTypes);
			if (m != null)
			{
				yield return m;
			}
		}
	}

	private static void Prefix(ref bool __state)
	{
		__state = false;
		if (!MpHelper.IsMultiplayer())
		{
			return;
		}
		Rand.PushState((Find.TickManager != null) ? Find.TickManager.TicksGame : 0);
		__state = true;
	}

	private static void Finalizer(bool __state)
	{
		if (__state)
		{
			Rand.PopState();
		}
	}
}

/// <summary>
/// FIX 9. Static-поля переживают SaveAndReload у уже подключённых клиентов,
/// а у присоединившегося они пустые → разное поведение после точки входа.
/// В Game.FinalizeInit (до постфикса самого Isekai — Priority.First) чистим
/// кэши и счётчики. Часть из них Isekai и сам чистит через ClearAll, но мы
/// не полагаемся на то, что его постфикс отработает целиком.
/// </summary>
[HarmonyPatch(typeof(Game), "FinalizeInit")]
public static class IsekaiStaticResetOnLoad
{
	private static bool Prepare()
	{
		return IsekaiMp.ModPresent;
	}

	[HarmonyPriority(Priority.First)]
	private static void Postfix()
	{
		try
		{
			IsekaiMp.ResetStatic("IsekaiLeveling.SkillTree.PassiveTreeTracker", "cachedColonistCounts");
			IsekaiMp.ResetStatic("IsekaiLeveling.ThoughtWorker_IsekaiIdentity", "ColonyRankCache");
			IsekaiMp.ResetStatic("IsekaiLeveling.Quests.IncidentWorker_IsekaiHunt", "bountyPawnIds");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.DamageTracker", "damageRecords");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.DamageTracker", "recordTimestamps");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.DamageTracker", "lastCleanupTick", 0);
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.Patch_WorkXP", "lastWorkXPTick");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.Patch_AbilityXP", "lastAbilityXPTick");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.Patch_AbilityXP_World", "lastWorldAbilityXPTick");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.Patch_PsychicEntropyXP", "lastEntropyXPTick");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.Patch_PsychicEntropyXP", "accumulatedEntropy");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.Patch_MeditationXP", "lastMeditationXPTick");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.Patch_SkillLearnXP", "lastAwardTick");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.ResurrectionPreserver", "deathCache");
			IsekaiMp.ResetStatic("IsekaiLeveling.Patches.ResurrectionPreserver", "nameCache");
			IsekaiMp.ResetStatic("IsekaiLeveling.MobRanking.RaidRankPatches", "processedLords");
			IsekaiMp.ResetStatic("IsekaiLeveling.MobRanking.RaidRankPatches", "lastRaidPoints", 0f);
			IsekaiMp.ResetStatic("IsekaiLeveling.MobRanking.RaidRankPatches", "lastRaidMap");
			IsekaiLordProcessedWasFix.Processed.Clear();
		}
		catch (Exception ex)
		{
			Log.Warning("[RaidAnimalFix] IsekaiStaticResetOnLoad: " + ex.Message);
		}
	}
}

/// <summary>
/// FIX 9b. ThoughtWorker_IsekaiIdentity.GetColonyRanks кэширует ранги колонии
/// на 2500 тиков, а наполняется кэш и из UI (оценка мыслей во вкладке
/// настроения) — момент обновления у клиентов разный, мысли расходятся.
/// В MP кэш перед каждым вызовом сбрасываем: пересчёт — один проход по
/// FreeColonistsSpawned с GetCached-компонентом, это дёшево.
/// </summary>
[HarmonyPatch]
public static class IsekaiColonyRankCacheFix
{
	private static IDictionary _cache;

	private static MethodBase TargetMethod()
	{
		Type type = IsekaiMp.T("IsekaiLeveling.ThoughtWorker_IsekaiIdentity");
		if (type == null)
		{
			return null;
		}
		_cache = AccessTools.Field(type, "ColonyRankCache")?.GetValue(null) as IDictionary;
		return (_cache == null) ? null : AccessTools.DeclaredMethod(type, "GetColonyRanks", new Type[1] { typeof(Map) });
	}

	private static void Prefix()
	{
		if (_cache != null && _cache.Count > 0 && MpHelper.IsMultiplayer())
		{
			_cache.Clear();
		}
	}
}

/// <summary>
/// FIX 10. IsekaiAnimations.Play* (частицы/текст XP, левел-апа и т.п.) берут
/// Rand.Range для разброса и при этом гейтятся локальными настройками
/// клиента (эффекты вкл/выкл). Вызываются из симуляции → у одного клиента
/// Rand сдвинулся, у другого нет. В MP изолируем: PushState/PopState.
/// </summary>
[HarmonyPatch]
public static class IsekaiAnimationsRandFix
{
	private static bool Prepare()
	{
		return IsekaiMp.T("IsekaiLeveling.UI.IsekaiAnimations") != null;
	}

	private static IEnumerable<MethodBase> TargetMethods()
	{
		Type type = IsekaiMp.T("IsekaiLeveling.UI.IsekaiAnimations");
		if (type == null)
		{
			yield break;
		}
		foreach (MethodInfo m in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
		{
			if (m.Name.StartsWith("Play") && !m.ContainsGenericParameters && !m.IsAbstract)
			{
				yield return m;
			}
		}
	}

	private static void Prefix(ref bool __state)
	{
		__state = false;
		if (!MpHelper.IsMultiplayer())
		{
			return;
		}
		Rand.PushState();
		__state = true;
	}

	private static void Finalizer(bool __state)
	{
		if (__state)
		{
			Rand.PopState();
		}
	}
}

/// <summary>
/// Фракции живых игроков по loadID — порядок одинаков у всех клиентов.
/// </summary>
internal static class IsekaiPerFaction
{
	public static List<Faction> RealPlayerFactions()
	{
		List<Faction> result = new List<Faction>();
		List<Faction> all = Find.FactionManager.AllFactionsListForReading;
		for (int i = 0; i < all.Count; i++)
		{
			if (DlcPf.IsReal(all[i]))
			{
				result.Add(all[i]);
			}
		}
		result.SortBy((Faction f) => f.loadID);
		return result;
	}

	/// <summary>Мировой тик MP с несколькими фракциями: Faction.OfPlayer — наблюдатель.</summary>
	public static bool InWorldSpectatorTick()
	{
		return DlcPf.Active && DlcPf.InSpectatorContext();
	}
}

/// <summary>
/// FIX 11. Квесты гильдии (IsekaiHuntTracker): раз в N дней (настройка) трекер ищет
/// «дом игрока с колонистами Isekai» — m.IsPlayerHome && FreeColonists… — и создаёт
/// охоту. Трекер тикает в мировом тике MP, где при нескольких фракциях
/// Faction.OfPlayer — наблюдатель: дом не находился, квест не создавался, таймер не
/// сбрасывался. Охоты приходили только от рассказчика (шанс 0.1, повтор 0.1 дня) —
/// то долго ничего, то несколько подряд. Да и таймер был бы один на всех игроков.
/// В мировом проходе трекера квест больше не создаётся (отложенные спавны и
/// страховка «зависших» квестов идут как раньше). После него — для каждой фракции
/// игрока по очереди, в её контексте и со своим таймером в DlcPerFactionData
/// (мировые тики; без записи — общий таймер трекера) — то же, что трекер делает
/// в одиночной игре.
/// </summary>
[HarmonyPatch]
public static class IsekaiGuildQuestSpectatorBlock
{
	private static MethodBase TargetMethod()
	{
		Type t = IsekaiMp.T("IsekaiLeveling.Quests.IsekaiHuntTracker");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "TrySpawnDailyHuntQuest");
	}

	private static bool Prefix()
	{
		return !IsekaiPerFaction.InWorldSpectatorTick();
	}
}

[HarmonyPatch]
public static class IsekaiGuildQuestPerFaction
{
	private static AccessTools.FieldRef<object, int> _lastQuestTick;

	private static MethodInfo _trySpawn;

	private static MethodInfo _interval;

	private static MethodBase TargetMethod()
	{
		Type t = IsekaiMp.T("IsekaiLeveling.Quests.IsekaiHuntTracker");
		if (t == null || AccessTools.DeclaredField(t, "lastQuestTick")?.FieldType != typeof(int))
		{
			return null;
		}
		_lastQuestTick = AccessTools.FieldRefAccess<int>(t, "lastQuestTick");
		_trySpawn = AccessTools.DeclaredMethod(t, "TrySpawnDailyHuntQuest", Type.EmptyTypes);
		_interval = AccessTools.DeclaredMethod(t, "GetTicksBetweenQuests", Type.EmptyTypes);
		return (_trySpawn == null || _interval == null) ? null : AccessTools.DeclaredMethod(t, "GameComponentTick");
	}

	private static void Postfix(GameComponent __instance)
	{
		if (!IsekaiPerFaction.InWorldSpectatorTick() || Find.TickManager.TicksGame % 250 != 0)
		{
			return;
		}
		List<Faction> factions = IsekaiPerFaction.RealPlayerFactions();
		for (int i = 0; i < factions.Count; i++)
		{
			Faction f = factions[i];
			if (!DlcPf.PushWorld(f))
			{
				continue;
			}
			try
			{
				DlcPf.IntSwap swap = DlcPf.BeginInt("isekaiGuild:" + f.loadID, ref _lastQuestTick(__instance), isTick: true);
				try
				{
					int now = Find.TickManager.TicksGame;
					int last = _lastQuestTick(__instance);
					if (last < 0)
					{
						_lastQuestTick(__instance) = now;
					}
					else if (now - last >= (int)_interval.Invoke(__instance, null))
					{
						_trySpawn.Invoke(__instance, null);
					}
				}
				finally
				{
					DlcPf.EndInt(swap, ref _lastQuestTick(__instance));
				}
			}
			catch (Exception ex)
			{
				CaravanLeaveHelper.LogErrorThrottled("Isekai guild quest", ex.InnerException ?? ex);
			}
			finally
			{
				DlcPf.PopWorld();
			}
		}
	}
}

/// <summary>
/// FIX 12. Вызовы на дуэль за титул (TitleDuelTracker): ежедневный бросок перебирает
/// m.IsPlayerHome / FreeColonistsSpawned, а прибытие на место дуэли считает пешки
/// Faction.OfPlayer — в мировом тике MP это наблюдатель: вызовов не было, а начатая
/// дуэль не замечала прибывшего чемпиона. Дуэль идёт → тик трекера выполняется в
/// контексте фракции чемпиона. Дуэли нет → бросок для каждой фракции игрока по
/// очереди (по loadID), в её контексте и со своим «днём последнего броска»; дуэль по-
/// прежнему одна на всех — следующая фракция бросает, когда эта закончится.
/// </summary>
[HarmonyPatch]
public static class IsekaiTitleDuelPerFaction
{
	private static AccessTools.FieldRef<object, int> _state;

	private static AccessTools.FieldRef<object, Pawn> _champion;

	private static AccessTools.FieldRef<object, int> _lastRolledDay;

	private static MethodBase TargetMethod()
	{
		Type t = IsekaiMp.T("IsekaiLeveling.TitleDuelTracker");
		if (t == null || AccessTools.DeclaredField(t, "state")?.FieldType != typeof(int) || AccessTools.DeclaredField(t, "champion")?.FieldType != typeof(Pawn) || AccessTools.DeclaredField(t, "lastRolledDay")?.FieldType != typeof(int))
		{
			return null;
		}
		_state = AccessTools.FieldRefAccess<int>(t, "state");
		_champion = AccessTools.FieldRefAccess<Pawn>(t, "champion");
		_lastRolledDay = AccessTools.FieldRefAccess<int>(t, "lastRolledDay");
		return AccessTools.DeclaredMethod(t, "GameComponentTick");
	}

	// Внутренний вызов идёт уже в контексте фракции — префикс его пропускает.
	private static bool Prefix(GameComponent __instance)
	{
		if (!IsekaiPerFaction.InWorldSpectatorTick())
		{
			return true;
		}
		int t = Find.TickManager.TicksGame;
		bool machineTick = t % 250 == 137;
		bool rollTick = t % 2500 == 1731;
		if (!machineTick && !rollTick)
		{
			return true;
		}
		try
		{
			if (_state(__instance) != 0)
			{
				Faction owner = _champion(__instance)?.Faction;
				if (!DlcPf.IsReal(owner) || !DlcPf.PushWorld(owner))
				{
					return true;
				}
				try
				{
					__instance.GameComponentTick();
				}
				finally
				{
					DlcPf.PopWorld();
				}
				return false;
			}
			if (!rollTick)
			{
				return true;
			}
			List<Faction> factions = IsekaiPerFaction.RealPlayerFactions();
			for (int i = 0; i < factions.Count && _state(__instance) == 0; i++)
			{
				Faction f = factions[i];
				if (!DlcPf.PushWorld(f))
				{
					continue;
				}
				try
				{
					DlcPf.IntSwap swap = DlcPf.BeginInt("isekaiDuelDay:" + f.loadID, ref _lastRolledDay(__instance), isTick: false);
					try
					{
						__instance.GameComponentTick();
					}
					finally
					{
						DlcPf.EndInt(swap, ref _lastRolledDay(__instance));
					}
				}
				finally
				{
					DlcPf.PopWorld();
				}
			}
			return false;
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("Isekai title duel", ex);
			return false;
		}
	}
}

/// <summary>
/// FIX 13. Мёртвые предложения охоты гильдии. Непринятый квест Isekai после очистки
/// (истёк срок предложения) остаётся в QuestManager навсегда, а ванильный и MP-тик
/// квестов, а также проверки ИИ пешек (IsQuestLodger, AnyQuestDisables… и т.п.)
/// перебирают ВЕСЬ список каждый тик. В сейве с тысячами таких предложений (всплеск
/// генерации) это десятки тысяч чтений состояния квеста за тик — FPS падал у всех.
/// При загрузке игры удаляем непринятые очищенные охоты и их письма из архива.
/// Вход — только сохранённое состояние, так что у всех клиентов удаляется одно и то же;
/// раньше кэша квестов MP (его постфикс FinalizeInit идёт последним).
/// </summary>
[HarmonyPatch(typeof(Game), "FinalizeInit")]
[HarmonyBefore("multiplayer")]
public static class IsekaiDeadHuntOfferPurge
{
	public const string HuntRoot = "Isekai_HuntQuestScript";

	public static readonly AccessTools.FieldRef<Quest, bool> CleanedUp = AccessTools.FieldRefAccess<Quest, bool>("cleanedUp");

	private static bool Prepare()
	{
		return IsekaiMp.ModPresent;
	}

	public static bool IsHunt(Quest q)
	{
		return q?.root != null && q.root.defName == HuntRoot;
	}

	private static void Postfix()
	{
		try
		{
			QuestManager manager = Find.QuestManager;
			List<Quest> all = manager?.QuestsListForReading;
			if (all == null)
			{
				return;
			}
			List<Quest> dead = new List<Quest>();
			for (int i = 0; i < all.Count; i++)
			{
				Quest q = all[i];
				if (q != null && q.acceptanceTick < 0 && IsHunt(q) && CleanedUp(q))
				{
					dead.Add(q);
				}
			}
			if (dead.Count == 0)
			{
				return;
			}
			HashSet<Quest> set = new HashSet<Quest>(dead);
			for (int i = 0; i < dead.Count; i++)
			{
				manager.Remove(dead[i]);
			}
			int letters = 0;
			List<Letter> stack = Find.LetterStack?.LettersListForReading;
			for (int i = (stack?.Count ?? 0) - 1; i >= 0; i--)
			{
				if (stack[i] is ChoiceLetter cl && cl.quest != null && set.Contains(cl.quest))
				{
					Find.LetterStack.RemoveLetter(cl);
					letters++;
				}
			}
			List<IArchivable> archive = Find.Archive?.ArchivablesListForReading;
			for (int i = (archive?.Count ?? 0) - 1; i >= 0; i--)
			{
				if (archive[i] is ChoiceLetter cl && cl.quest != null && set.Contains(cl.quest))
				{
					Find.Archive.Remove(cl);
					letters++;
				}
			}
			Log.Message("[RaidAnimalFix] Isekai: удалено просроченных непринятых предложений охоты гильдии: " + dead.Count + " (писем: " + letters + "); квестов осталось " + all.Count + ".");
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("Isekai dead hunt offers", ex);
		}
	}
}

/// <summary>
/// FIX 14. Ограничитель предложений охоты. В сейве нашёлся всплеск: ~3900 охот за
/// несколько тиков. Новая охота не создаётся, пока открыто 25 непринятых предложений
/// (обычно их 1–3: одно в день, срок 3 дня). Счёт по списку активных квестов —
/// синхронному состоянию, одинаково у всех. Несколько охот в одном тике — в лог
/// один раз со стеком, чтобы найти источник всплеска.
/// </summary>
[HarmonyPatch]
public static class IsekaiHuntOfferCap
{
	private const int MaxOpenOffers = 25;

	private static int _lastTick = -1;

	private static int _sameTick;

	private static bool _burstLogged;

	private static bool _capLogged;

	private static MethodBase TargetMethod()
	{
		Type t = IsekaiMp.T("IsekaiLeveling.Quests.IncidentWorker_IsekaiHunt");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "TryExecuteWorker", new Type[1] { typeof(IncidentParms) });
	}

	private static bool Prefix(ref bool __result)
	{
		try
		{
			int now = Find.TickManager.TicksGame;
			_sameTick = (now == _lastTick) ? (_sameTick + 1) : 1;
			_lastTick = now;
			if (_sameTick == 4 && !_burstLogged)
			{
				_burstLogged = true;
				Log.Warning("[RaidAnimalFix] Isekai: несколько охот гильдии в одном тике (" + now + ") — источник:\n" + Environment.StackTrace);
			}
			List<Quest> active = Find.QuestManager.ActiveQuestsListForReading;
			int open = 0;
			for (int i = 0; i < active.Count; i++)
			{
				Quest q = active[i];
				if (q != null && q.acceptanceTick < 0 && IsekaiDeadHuntOfferPurge.IsHunt(q) && !IsekaiDeadHuntOfferPurge.CleanedUp(q))
				{
					open++;
				}
			}
			if (open < MaxOpenOffers)
			{
				return true;
			}
			if (!_capLogged)
			{
				_capLogged = true;
				Log.Warning("[RaidAnimalFix] Isekai: открыто " + open + " предложений охоты — новые не создаются, пока их не станет меньше " + MaxOpenOffers + ".\n" + Environment.StackTrace);
			}
			__result = false;
			return false;
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("Isekai hunt offer cap", ex);
			return true;
		}
	}
}
