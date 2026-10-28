using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RaidAnimalFix;

// RIMEXODUS — SEAMLESS WORLD (RimExodus.SeamlessWorld) + Multiplayer.
// RimExodus склеивает соседние тайлы мира в одну бесшовную местность: подгружает
// соседние карты, усыпляет и удаляет дальние, ведёт пешек через швы. В MP это
// ломалось в нескольких местах (все патчи ниже действуют только в MP):
//  1. Инкрементальная генерация карт создаёт карту в обход MapGenerator.GenerateMap —
//     MP не заводит для неё AsyncTimeComp/MultiplayerMapComp (карта не тикает,
//     спавн вещей падает), а шаги генерации режутся бюджетом реального времени (8 мс).
//     → в MP карта генерируется синхронно, ванильным путём.
//  2. Очередь предзагрузки соседей заполняется из симуляции, а разбирается по номеру
//     кадра Unity → карта появлялась в разные тики. → разбор на ближайшем тике.
//  3. «Карта активна» = Find.CurrentMap (камера клиента): пробуждение при переключении
//     карты, удержание в Sweep, условия вопроса о сохранении. → синхронный счётчик
//     игроков на карте (AsyncTimeComp.CurrentPlayerCount).
//  4. В мировом тике MP в контексте стоит фракция-наблюдатель: «есть пешки игрока»,
//     «дом игрока», «угроза игроку», домашняя зона, караваны — всё смотрело на неё.
//     → учитываются все фракции игроков.
//  5. Вопрос «сохранить карту?» — окно из тика, ответ из UI. → в MP карта сохраняется
//     в архив автоматически (без потери построек).
//  6. Приказ «идти на соседнюю карту» и цель приказа (SeamlessCommandTargets) живут
//     только у кликнувшего клиента, а читаются симуляцией. → синхронные команды.
//  7. Кнопки «усыпить / удалить карту» на карте мира меняли состояние локально.
//     → синхронные команды с проверкой, что карта не чужая.
//  8. Поворот пешки к цели на другой карте зависел от видимости в камере клиента.
//  9. Торговля с поселением прямо на его карте открывала локальный Dialog_Trade
//     (сделка только у одного клиента). → в MP отключена с пояснением.
// 10. При усыплении RimExodus чистит ванильные тик-листы, а в MP вещи живут в
//     тик-листах карты → после пробуждения вещи тикали дважды. → TickManager.
//     RemoveAllFromMap в MP чистит и тик-листы карты (исправляет это для всех модов).
// 11. Статика RimExodus (очереди, «теневые караваны», пропуска) переживала
//     SaveAndReload точки входа у старых клиентов и была пустой у нового.
// 12. С асинхронным временем новая карта создавалась на паузе, а соседние карты
//     шли с разной скоростью → новая карта наследует скорость, смена скорости
//     распространяется на связанные швами карты этого игрока.
// 13. Настройки RimExodus читает симуляция → заблокированы во время MP-игры и
//     входят в сверку настроек.
// 14. С асинхронным временем пустая клетка получала скорость кластера (12), а MP гнал
//     её на ▶▶▶ ×12 («ничего не происходит» считается по наблюдателю) — соседние карты
//     симулировались быстрее колонии, лаги у всех. → пустая клетка идёт не быстрее ▶.
// 15. Панель колонистов: каждая загруженная клетка без своих колонистов была пустой
//     группой с кнопкой скорости MP. → в MP такие группы скрыты.
// 16. Быстрая зона троттлинга (15 проходов по карте) строилась заново после каждого
//     перехода пешки через шов → фризы. → кэш по отпечатку клеток пустоты.
// 17. Рендер соседних карт каждый кадр брал DrawPos у ВСЕХ пешек всех соседей, а в MP
//     этот геттер дорогой (патч MP на PawnTweener) → ~1 мс на кадр. → сначала отсев по клетке.

/// <summary>1. Синхронная (ванильная) генерация соседних карт в MP; скорость новой карты.</summary>
[HarmonyPatch]
public static class RexSyncGeneration
{
	private sealed class State
	{
		public bool restoreFlag;

		public Map origin;

		public HashSet<Map> before;
	}

	private static PropertyInfo _settings;

	private static FieldInfo _flag;

	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type tm = RimExodusMp.T("SeamlessTileManager");
		Type mod = RimExodusMp.T("RimExodusMod");
		Type st = RimExodusMp.T("RimExodusSettings");
		_settings = (mod == null) ? null : AccessTools.Property(mod, "Settings");
		_flag = (st == null) ? null : AccessTools.Field(st, "incrementalGenerationEnabled");
		return (tm == null || _settings == null || _flag == null) ? null : AccessTools.Method(tm, "GenerateTileMap");
	}

	private static void Prefix(MapComponent __instance, out State __state)
	{
		__state = null;
		if (!MpHelper.IsMultiplayer())
		{
			return;
		}
		__state = new State
		{
			origin = __instance?.map,
			before = new HashSet<Map>(Find.Maps)
		};
		try
		{
			object settings = _settings.GetValue(null, null);
			if (settings != null && (bool)_flag.GetValue(settings))
			{
				_flag.SetValue(settings, false);
				__state.restoreFlag = true;
			}
		}
		catch
		{
		}
	}

	private static void Finalizer(State __state)
	{
		if (__state == null)
		{
			return;
		}
		try
		{
			if (__state.restoreFlag)
			{
				object settings = _settings.GetValue(null, null);
				if (settings != null)
				{
					_flag.SetValue(settings, true);
				}
			}
			// Асинхронное время: MP создаёт новую карту на паузе. Пешка, перешедшая шов,
			// замерла бы — новая карта получает скорость карты, с которой её открыли.
			if (RimExodusMp.AsyncTimeOn && RimExodusMp.TryGetSpeed(__state.origin, out TimeSpeed speed))
			{
				foreach (Map map in Find.Maps)
				{
					if (!__state.before.Contains(map))
					{
						RimExodusMp.SetSpeed(map, speed);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warning("[RaidAnimalFix] RimExodus generation finalizer: " + ex.Message);
		}
	}
}

/// <summary>2. Очередь предзагрузки соседей: в MP разбирается на ближайшем тике, а не через 2 кадра.</summary>
[HarmonyPatch]
public static class RexPreloadTickGate
{
	private static IList _queue;

	private static FieldInfo _minFrame;

	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessTilePreloader");
		if (t == null)
		{
			return null;
		}
		_queue = AccessTools.Field(t, "pendingQueue")?.GetValue(null) as IList;
		_minFrame = AccessTools.Field(t, "minConsumeFrame");
		return (_queue == null || _minFrame == null) ? null : AccessTools.Method(t, "ConsumeQueued");
	}

	private static void Prefix()
	{
		if (_queue.Count > 0 && MpHelper.IsMultiplayer())
		{
			_minFrame.SetValue(null, -1);
		}
	}
}

/// <summary>
/// 3a. RimExodus будит карту в сеттере Game.CurrentMap — это действие одного клиента
/// (а MP ещё и переключает CurrentMap на время команд). В MP пробуждение делает
/// RexWakeOnPlayerView по синхронному счётчику игроков.
/// </summary>
[HarmonyPatch]
public static class RexCurrentMapWakeLocalOff
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type outer = RimExodusMp.T("Patches_Dormancy");
		Type inner = (outer == null) ? null : AccessTools.Inner(outer, "Patch_Game_CurrentMap_Setter");
		return (inner == null) ? null : AccessTools.DeclaredMethod(inner, "Prefix");
	}

	private static bool Prefix()
	{
		return !MpHelper.IsMultiplayer();
	}
}

/// <summary>
/// 3b. Игрок открыл карту → мировая команда MP PlayerCount увеличивает счётчик на ней
/// (у всех клиентов в один тик). Здесь же будим/снимаем троттлинг — детерминированно.
/// </summary>
[HarmonyPatch]
public static class RexWakeOnPlayerView
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RafTypes.Find("Multiplayer.Client.AsyncTimeComp");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "IncreasePlayerCount");
	}

	private static void Postfix(object __instance)
	{
		try
		{
			Map map = RimExodusMp.MapOfAsyncTime(__instance);
			if (map == null || map.Disposed)
			{
				return;
			}
			if (RimExodusMp.IsDormant(map))
			{
				RimExodusMp.Wake(map, "player started viewing (multiplayer, synced)");
			}
			else
			{
				RimExodusMp.Unthrottle(map, "player started viewing (multiplayer, synced)");
			}
		}
		catch (Exception ex)
		{
			Log.Warning("[RaidAnimalFix] RimExodus wake on view: " + ex.Message);
		}
	}
}

/// <summary>
/// 3c + 4. Governor: Find.CurrentMap в Sweep / LoadedGame / PromptStillEligible → в MP null
/// (просматриваемые карты учитывает HasPlayerPawn ниже); проверка
/// «караван игрока» (Faction == Faction.OfPlayer) → любая фракция игрока.
/// </summary>
[HarmonyPatch]
public static class RexGovernorSimCurrentMap
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static IEnumerable<MethodBase> TargetMethods()
	{
		Type t = RimExodusMp.T("SeamlessDormancyGovernor");
		if (t == null)
		{
			yield break;
		}
		foreach (string name in new string[3] { "Sweep", "LoadedGame", "PromptStillEligible" })
		{
			MethodInfo m = AccessTools.DeclaredMethod(t, name);
			if (m != null)
			{
				yield return m;
			}
		}
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
	{
		MethodInfo currentMap = AccessTools.PropertyGetter(typeof(Find), "CurrentMap");
		MethodInfo ofPlayer = AccessTools.PropertyGetter(typeof(Faction), "OfPlayer");
		MethodInfo woFaction = AccessTools.PropertyGetter(typeof(WorldObject), "Faction");
		List<CodeInstruction> list = instructions.ToList();
		int maps = 0;
		int factions = 0;
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i].Calls(currentMap))
			{
				list[i].operand = AccessTools.Method(typeof(RexGovernorSimCurrentMap), "SimCurrentMap");
				maps++;
			}
			else if (list[i].Calls(ofPlayer) && i > 0 && list[i - 1].Calls(woFaction))
			{
				// стек: [фракция каравана] → [фракция каравана, SameIfPlayer(фракция каравана)]
				CodeInstruction dup = new CodeInstruction(OpCodes.Dup);
				dup.labels.AddRange(list[i].labels);
				list[i].labels.Clear();
				list[i].opcode = OpCodes.Call;
				list[i].operand = AccessTools.Method(typeof(RexGovernorSimCurrentMap), "SameIfPlayer");
				list.Insert(i, dup);
				i++;
				factions++;
			}
		}
		if (maps == 0)
		{
			Log.Warning("[RaidAnimalFix] RimExodus " + __originalMethod?.Name + ": Find.CurrentMap not found (RimExodus updated?)");
		}
		if (__originalMethod?.Name == "Sweep" && factions == 0)
		{
			Log.Warning("[RaidAnimalFix] RimExodus Sweep: caravan faction check not found (RimExodus updated?)");
		}
		return list;
	}

	public static Map SimCurrentMap()
	{
		return MpHelper.IsMultiplayer() ? null : Find.CurrentMap;
	}

	public static Faction SameIfPlayer(Faction f)
	{
		return (MpHelper.IsMultiplayer() && RimExodusMp.IsPlayerFaction(f)) ? f : Faction.OfPlayer;
	}
}

/// <summary>4a. «На карте есть пешка игрока»: любая фракция игрока, плюс карта, на которую кто-то смотрит.</summary>
[HarmonyPatch]
public static class RexHasPlayerPawnMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessMapGovernance");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "HasPlayerPawn", new Type[1] { typeof(Map) });
	}

	private static bool Prefix(Map map, ref bool __result)
	{
		if (!MpHelper.IsMultiplayer())
		{
			return true;
		}
		__result = map != null && !map.Disposed && (RimExodusMp.AnyPlayerViewing(map) || RimExodusMp.AnyPlayerPawn(map));
		return false;
	}
}

/// <summary>4b. «Дом игрока» (защищён от сна/удаления): дом любой фракции игрока.</summary>
[HarmonyPatch]
public static class RexProtectedHomeMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessMapGovernance");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "IsProtectedHome", new Type[1] { typeof(Map) });
	}

	private static bool Prefix(Map map, ref bool __result)
	{
		if (!MpHelper.IsMultiplayer())
		{
			return true;
		}
		MapParent parent = map?.Parent;
		__result = map != null && !map.Disposed && (map.IsPlayerHome || map.wasSpawnedViaGravShipLanding || (parent != null && parent.def.canBePlayerHome && RimExodusMp.IsPlayerFaction(parent.Faction)));
		return false;
	}
}

/// <summary>4c. «Активная угроза» (не усыплять карту посреди боя): угроза любой фракции игрока.</summary>
[HarmonyPatch]
public static class RexActiveThreatMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessMapGovernance");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "HasActiveThreat", new Type[1] { typeof(Map) });
	}

	private static bool Prefix(Map map, ref bool __result)
	{
		if (!MpHelper.IsMultiplayer() || map == null || map.Disposed)
		{
			return true;
		}
		List<Faction> factions = Find.FactionManager.AllFactionsListForReading;
		for (int i = 0; i < factions.Count; i++)
		{
			if (RimExodusMp.IsPlayerFaction(factions[i]) && GenHostility.AnyHostileActiveThreatTo(map, factions[i]))
			{
				__result = true;
				return false;
			}
		}
		return true;
	}
}

/// <summary>
/// 4d. Решение «сохранить карту» и снимок архива читают домашнюю зону. В мировом тике
/// MP на картах стоит зона наблюдателя (пустая) → построенный аванпост удалялся.
/// На время вызова ставим контекст фракции с наибольшей домашней зоной.
/// </summary>
[HarmonyPatch]
public static class RexPreserveFactionContext
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static IEnumerable<MethodBase> TargetMethods()
	{
		MethodInfo evaluate = AccessTools.DeclaredMethod(RimExodusMp.T("SeamlessMapModificationTracker"), "Evaluate");
		if (evaluate != null)
		{
			yield return evaluate;
		}
		MethodInfo capture = AccessTools.DeclaredMethod(RimExodusMp.T("ZoneMapRecord"), "Capture");
		if (capture != null)
		{
			yield return capture;
		}
	}

	private static void Prefix(Map map, out bool __state)
	{
		__state = false;
		if (!MpHelper.IsMultiplayer() || map == null || map.Disposed || !MpSim.CanRestoreContext)
		{
			return;
		}
		Faction best = RimExodusMp.BestHomeFaction(map, out int _);
		if (best == null)
		{
			return;
		}
		try
		{
			MpSim.PushFaction(map, best);
			__state = true;
		}
		catch (Exception ex)
		{
			Log.Warning("[RaidAnimalFix] RimExodus preserve context: " + ex.Message);
		}
	}

	private static void Finalizer(Map map, bool __state)
	{
		if (!__state)
		{
			return;
		}
		try
		{
			MpSim.PopFaction(map);
		}
		catch (Exception ex)
		{
			Log.Warning("[RaidAnimalFix] RimExodus preserve context pop: " + ex.Message);
		}
	}
}

/// <summary>
/// 5. Вопрос «сохранить карту?» в MP: окно открывалось бы из тика у всех, ответ шёл бы
/// из UI одного клиента. Карту с небольшой домашней зоной сохраняем в архив сразу
/// (так же, как RimExodus делает с большой) — постройки не теряются.
/// </summary>
[HarmonyPatch]
public static class RexPreservePromptAutoKeep
{
	private static MethodInfo _archive;

	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessDormancyGovernor");
		_archive = (t == null) ? null : AccessTools.DeclaredMethod(t, "ArchiveTileMap");
		return (_archive == null) ? null : AccessTools.DeclaredMethod(t, "QueuePreservePrompt");
	}

	private static bool Prefix(object __instance, MapParent parent, Map map, int tile, int homeCells, ref bool __result)
	{
		if (!MpHelper.IsMultiplayer())
		{
			return true;
		}
		try
		{
			_archive.Invoke(__instance, new object[5] { parent, map, tile, homeCells, "auto-kept: multiplayer has no shared prompt" });
		}
		catch (Exception ex)
		{
			// Карта остаётся как есть; следующий Sweep попробует снова.
			Log.Warning("[RaidAnimalFix] RimExodus auto-archive: " + (ex.InnerException ?? ex).Message);
		}
		__result = true;
		return false;
	}
}

/// <summary>8. Поворот к цели на соседней карте — всегда (а не только если пешку видно в камере клиента).</summary>
[HarmonyPatch]
public static class RexCrossMapFacingMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("Patch_PawnRotationTracker_CrossMapFacing");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "PawnPotentiallyVisible", new Type[1] { typeof(Pawn) });
	}

	private static bool Prefix(ref bool __result)
	{
		if (!MpHelper.IsMultiplayer())
		{
			return true;
		}
		__result = true;
		return false;
	}
}

/// <summary>
/// 3d. Автофокус камеры на прибытие пешки — из тика, по камере клиента, и выставляет
/// сохраняемый флаг autoFocused только у части клиентов. В MP отключён.
/// </summary>
[HarmonyPatch]
public static class RexAutoFocusMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessCameraFocus");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "TryAutoFocusOnArrival");
	}

	private static bool Prefix()
	{
		return !MpHelper.IsMultiplayer();
	}
}

/// <summary>
/// 6a. Цель приказа на соседней карте. RimExodus пишет её из UI (меню по клику на
/// соседнюю карту), а читает и UI, и симуляция (старт пути пешки, достижимость).
/// В MP у симуляции своя таблица, которая меняется только синхронными командами.
/// </summary>
internal static class RexCommandTargets
{
	private static ConditionalWeakTable<Pawn, object> _sim = new ConditionalWeakTable<Pawn, object>();

	private static bool _resolved;

	private static Type _type;

	private static FieldInfo _map;

	private static FieldInfo _cell;

	private static void Resolve()
	{
		if (_resolved)
		{
			return;
		}
		_resolved = true;
		Type t = RimExodusMp.T("SeamlessCommandTargets");
		_type = (t == null) ? null : AccessTools.Inner(t, "CommandTarget");
		_map = (_type == null) ? null : AccessTools.Field(_type, "map");
		_cell = (_type == null) ? null : AccessTools.Field(_type, "cell");
	}

	public static void Reset()
	{
		_sim = new ConditionalWeakTable<Pawn, object>();
	}

	public static bool TryGetRaw(Pawn pawn, out object raw)
	{
		Resolve();
		raw = null;
		if (pawn == null || _map == null || !_sim.TryGetValue(pawn, out raw))
		{
			return false;
		}
		Map map = _map.GetValue(raw) as Map;
		if (map == null || map.Disposed)
		{
			raw = null;
			return false;
		}
		return true;
	}

	public static bool Has(Pawn pawn)
	{
		return TryGetRaw(pawn, out object _);
	}

	public static bool SameAs(Pawn pawn, Map map, IntVec3 cell)
	{
		return TryGetRaw(pawn, out object raw) && _map.GetValue(raw) == map && (IntVec3)_cell.GetValue(raw) == cell;
	}

	public static void SimSet(Pawn pawn, Map map, IntVec3 cell)
	{
		Resolve();
		if (pawn == null || _type == null || _map == null || _cell == null)
		{
			return;
		}
		object raw = Activator.CreateInstance(_type);
		_map.SetValue(raw, map);
		_cell.SetValue(raw, cell);
		_sim.Remove(pawn);
		_sim.Add(pawn, raw);
	}

	public static void SimRemove(Pawn pawn)
	{
		if (pawn != null)
		{
			_sim.Remove(pawn);
		}
	}
}

[HarmonyPatch]
public static class RexCommandTargetSetMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessCommandTargets");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "Set", new Type[3]
		{
			typeof(Pawn),
			typeof(Map),
			typeof(IntVec3)
		});
	}

	private static bool Prefix(Pawn pawn, Map map, IntVec3 cell)
	{
		if (pawn == null || !MpHelper.IsMultiplayer())
		{
			return true;
		}
		if (MpSim.InSim())
		{
			RexCommandTargets.SimSet(pawn, map, cell);
			return false;
		}
		// UI: локальная таблица (оригинал) — для меню и подсветки; в симуляцию — командой.
		if (map != null && !RexCommandTargets.SameAs(pawn, map, cell))
		{
			RexMpSync.SetCommandTarget(pawn, map.uniqueID, cell);
		}
		return true;
	}
}

[HarmonyPatch]
public static class RexCommandTargetRemoveMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessCommandTargets");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "Remove", new Type[1] { typeof(Pawn) });
	}

	private static bool Prefix(Pawn pawn)
	{
		if (pawn == null || !MpHelper.IsMultiplayer())
		{
			return true;
		}
		if (MpSim.InSim())
		{
			RexCommandTargets.SimRemove(pawn);
			return false;
		}
		if (RexCommandTargets.Has(pawn))
		{
			RexMpSync.RemoveCommandTarget(pawn);
		}
		return true;
	}
}

[HarmonyPatch]
public static class RexCommandTargetGetMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessCommandTargets");
		if (t == null)
		{
			return null;
		}
		foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
		{
			ParameterInfo[] ps = m.GetParameters();
			if (m.Name == "TryGet" && ps.Length == 2 && ps[0].ParameterType == typeof(Pawn) && ps[1].ParameterType.IsByRef)
			{
				return m;
			}
		}
		return null;
	}

	private static bool Prefix(Pawn pawn, object[] __args, ref bool __result)
	{
		if (!MpHelper.IsMultiplayer() || !MpSim.InSim())
		{
			return true;
		}
		if (RexCommandTargets.TryGetRaw(pawn, out object raw))
		{
			__args[1] = raw;
			__result = true;
		}
		else
		{
			__args[1] = null;
			__result = false;
		}
		return false;
	}
}

/// <summary>
/// 6b. Приказ «идти на соседнюю карту» (дрефт-приказ, групповой приказ, транспорт):
/// RimExodus выдаёт пропуск и стартует работу прямо из UI. В MP — синхронной командой.
/// Внутри симуляции (продолжение работы после шва) идёт как есть.
/// </summary>
[HarmonyPatch]
public static class RexBridgeOrderMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessCrossMapOrders");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "TryBridgeJob", new Type[4]
		{
			typeof(Pawn),
			typeof(Map),
			typeof(IntVec3),
			typeof(Job)
		});
	}

	private static bool Prefix(Pawn pawn, Map targetMap, IntVec3 targetLocalCell, Job nextJob, ref bool __result)
	{
		if (!MpHelper.IsMultiplayer() || nextJob != null || pawn == null || targetMap == null || MpSim.InSim())
		{
			return true;
		}
		RexMpSync.BridgeOrder(pawn, targetMap.uniqueID, targetLocalCell);
		__result = true;
		return false;
	}
}

/// <summary>7. Кнопки «усыпить / удалить / удалить архив» на карте мира → синхронные команды.</summary>
[HarmonyPatch]
public static class RexManualDormancyGizmosMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessManualDormancyGizmos");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "ForParent", new Type[1] { typeof(MapParent) });
	}

	private static void Postfix(ref IEnumerable<Gizmo> __result, MapParent parent)
	{
		if (__result == null || parent == null || !MpHelper.IsMultiplayer())
		{
			return;
		}
		List<Gizmo> list = __result.ToList();
		string deleteArchived = "RimExodus_DeleteArchivedMap".Translate();
		string deleteMap = "RimExodus_DeleteTileMap".Translate();
		string sleep = "RimExodus_SleepTileMap".Translate();
		foreach (Gizmo gizmo in list)
		{
			if (!(gizmo is Command_Action command))
			{
				continue;
			}
			if (command.defaultLabel == deleteArchived)
			{
				command.action = delegate
				{
					Confirm("RimExodus_DeleteArchivedMapConfirm".Translate(parent.Label), delegate
					{
						RexMpSync.DeleteArchivedMap(parent, MpHelper.GetCurrentPlayerFaction());
					});
				};
			}
			else if (command.defaultLabel == deleteMap)
			{
				command.action = delegate
				{
					DeleteMap(parent);
				};
			}
			else if (command.defaultLabel == sleep)
			{
				command.action = delegate
				{
					SleepMap(parent);
				};
			}
		}
		__result = list;
	}

	private static void Confirm(string text, Action action)
	{
		Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, action, destructive: true));
	}

	private static void DeleteMap(MapParent parent)
	{
		Map map = parent.Map;
		if (map == null)
		{
			return;
		}
		string key = RimExodusMp.IsTileParent(parent) ? "RimExodus_DeleteTileMapConfirm" : "RimExodus_DeletePoiMapConfirm";
		Confirm(key.Translate(parent.Label), delegate
		{
			if (RimExodusMp.HasPlayerPawn(map))
			{
				Confirm("RimExodus_DeleteTileMapPlayerPawnConfirm".Translate(parent.Label), delegate
				{
					RexMpSync.RemoveMap(parent, MpHelper.GetCurrentPlayerFaction());
				});
			}
			else
			{
				RexMpSync.RemoveMap(parent, MpHelper.GetCurrentPlayerFaction());
			}
		});
	}

	private static void SleepMap(MapParent parent)
	{
		Map map = parent.Map;
		if (map == null)
		{
			return;
		}
		if (!RimExodusMp.HasPlayerPawn(map))
		{
			RexMpSync.ManualSleep(parent, MpHelper.GetCurrentPlayerFaction());
			return;
		}
		Confirm("RimExodus_SleepTileMapPlayerPawnConfirm".Translate(parent.Label), delegate
		{
			RexMpSync.ManualSleep(parent, MpHelper.GetCurrentPlayerFaction());
		});
	}
}

/// <summary>Синхронные команды MP для RimExodus (выполняются у всех клиентов).</summary>
public static class RexMpSync
{
	// Карту цели передаём числом: параметр Map в MP не сериализуется, а задаёт карту
	// контекста команды (она должна совпадать с картой пешки).
	private static Map MapById(int id)
	{
		List<Map> maps = Find.Maps;
		for (int i = 0; i < maps.Count; i++)
		{
			if (maps[i].uniqueID == id)
			{
				return maps[i];
			}
		}
		return null;
	}

	[SyncMethod]
	public static void SetCommandTarget(Pawn pawn, int mapId, IntVec3 cell)
	{
		Map map = MapById(mapId);
		if (pawn != null && map != null && !map.Disposed)
		{
			RexCommandTargets.SimSet(pawn, map, cell);
		}
	}

	[SyncMethod]
	public static void RemoveCommandTarget(Pawn pawn)
	{
		RexCommandTargets.SimRemove(pawn);
	}

	[SyncMethod]
	public static void BridgeOrder(Pawn pawn, int targetMapId, IntVec3 cell)
	{
		Map targetMap = MapById(targetMapId);
		if (pawn == null || !pawn.Spawned || pawn.Dead || targetMap == null || targetMap.Disposed || pawn.Map == targetMap || !cell.InBounds(targetMap))
		{
			return;
		}
		RimExodusMp.TryBridgeJob(pawn, targetMap, cell);
	}

	[SyncMethod]
	public static void ManualSleep(MapParent parent, Faction actor)
	{
		Map map = parent?.Map;
		if (!CanManage(parent, map, actor) || RimExodusMp.IsDormant(map))
		{
			return;
		}
		RimExodusMp.Sleep(map, "player gizmo (world map, multiplayer)", manual: true);
	}

	[SyncMethod]
	public static void RemoveMap(MapParent parent, Faction actor)
	{
		Map map = parent?.Map;
		if (!CanManage(parent, map, actor))
		{
			return;
		}
		RimExodusMp.RemoveRollingMap(map, parent);
	}

	[SyncMethod]
	public static void DeleteArchivedMap(MapParent parent, Faction actor)
	{
		if (parent == null || parent.Destroyed || parent.HasMap || !RimExodusMp.HasPreserveRecord(parent))
		{
			return;
		}
		RimExodusMp.ReleaseTileMesh(parent.Tile);
		parent.Destroy();
	}

	private static bool CanManage(MapParent parent, Map map, Faction actor)
	{
		if (parent == null || parent.Destroyed || map == null || map.Disposed || parent.Map != map || !RimExodusMp.IsGoverned(map) || RimExodusMp.IsProtectedHome(map) || RimExodusMp.IsGenerating(map))
		{
			return false;
		}
		if (RimExodusMp.AnyPlayerViewing(map))
		{
			Notify("RAF_RimExodus_MapViewed");
			return false;
		}
		if (RimExodusMp.OtherPlayerPawns(map, actor) || RimExodusMp.OtherPlayersHomeCells(map, actor) > 0)
		{
			Notify("RAF_RimExodus_MapNotYours");
			return false;
		}
		return true;
	}

	private static void Notify(string key)
	{
		if (MP.IsExecutingSyncCommandIssuedBySelf)
		{
			Messages.Message("[Desync Fix] " + key.Translate().Resolve(), MessageTypeDefOf.RejectInput, historical: false);
		}
	}
}

/// <summary>9a. «Поговорить с торговцем поселения» в MP недоступно (сделка прошла бы только у одного клиента).</summary>
[HarmonyPatch]
public static class RexSettlementTalkMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("FloatMenuOptionProvider_SettlementTalk");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "GetOptionsFor", new Type[2]
		{
			typeof(Pawn),
			typeof(FloatMenuContext)
		});
	}

	private static void Postfix(ref IEnumerable<FloatMenuOption> __result)
	{
		if (__result == null || !MpHelper.IsMultiplayer())
		{
			return;
		}
		if (__result.Any())
		{
			__result = new List<FloatMenuOption>
			{
				new FloatMenuOption("[Desync Fix] " + "RAF_RimExodus_SettlementTalkMp".Translate().Resolve(), null)
			};
		}
	}
}

/// <summary>9b. Страховка: окно торговца поселения в MP не открывается (приказ мог быть отдан до входа в MP).</summary>
[HarmonyPatch]
public static class RexSettlementDialogMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SettlementTraderDialog");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "Open");
	}

	private static bool Prefix(Pawn negotiator)
	{
		if (!MpHelper.IsMultiplayer())
		{
			return true;
		}
		if (negotiator != null && negotiator.Faction == MpHelper.GetCurrentPlayerFaction())
		{
			Messages.Message("[Desync Fix] " + "RAF_RimExodus_SettlementTalkMp".Translate().Resolve(), MessageTypeDefOf.RejectInput, historical: false);
		}
		return false;
	}
}

/// <summary>
/// «Теневые караваны» (для торговли с поселением на его карте) строятся в мировом тике
/// по FreeColonistsSpawned — в MP это пешки фракции-наблюдателя, то есть никого, зато
/// устаревшая статика после перезагрузки тратила ID каравана только у старых клиентов.
/// Торговля на карте в MP отключена — караваны не нужны.
/// </summary>
[HarmonyPatch]
public static class RexShadowCaravanMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessShadowCaravan");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "Maintain", new Type[1] { typeof(bool) });
	}

	private static bool Prefix()
	{
		return !MpHelper.IsMultiplayer();
	}
}

/// <summary>
/// 11. Статика RimExodus переживает SaveAndReload (точка входа игрока): у старых
/// клиентов остаются очереди/пропуска/кэши со старыми объектами, у нового — пусто.
/// Перед загрузкой игры в MP сбрасываем её у всех одинаково.
/// </summary>
[HarmonyPatch]
public static class RexStaticReset
{
	private static readonly string[][] Collections = new string[][]
	{
		new string[3] { "SeamlessTilePreloader", "pendingQueue", "queuedHashes" },
		new string[4] { "SeamlessShadowCaravan", "shadows", "shadowMaps", "originalOwners" },
		new string[4] { "SeamlessTransferGrants", "Grants", "StrayNpcs", "RecentAssaultLords" },
		new string[3] { "SeamlessDormancyManager", "dormantMaps", "manualDormantMaps" },
		new string[3] { "SeamlessTickThrottle", "throttled", "tickSuspendedMaps" },
		new string[2] { "SeamlessBridgeCluster", "shares" },
		new string[2] { "SeamlessSelectionTracker", "pending" },
		new string[2] { "SeamlessEdgeCells", "_cache" }
	};

	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.DeclaredMethod(typeof(Game), "LoadGame");
		yield return AccessTools.DeclaredMethod(typeof(Game), "InitNewGame");
	}

	private static void Prefix()
	{
		if (!MpHelper.IsMultiplayer())
		{
			return;
		}
		foreach (string[] group in Collections)
		{
			for (int i = 1; i < group.Length; i++)
			{
				IsekaiMp.ResetStatic("RimExodus." + group[0], group[i]);
			}
		}
		IsekaiMp.ResetStatic("RimExodus.SeamlessTilePreloader", "minConsumeFrame", -1);
		IsekaiMp.ResetStatic("RimExodus.SeamlessShadowCaravan", "nextRefreshTick", 0);
		IsekaiMp.ResetStatic("RimExodus.SeamlessPawnLocationTracker", "dirty", false);
		IsekaiMp.ResetStatic("RimExodus.IncrementalMapGenerator", "current");
		IsekaiMp.ResetStatic("RimExodus.SeamlessTileManager", "GeneratingNativeSeamlessly", false);
		IsekaiMp.ResetStatic("RimExodus.SeamlessTileManager", "NeighborGenerationSourceTile", -1);
		IsekaiMp.ResetStatic("RimExodus.SeamlessTileManager", "NeighborGenerationSourceMapId", -1);
		IsekaiMp.ResetStatic("RimExodus.SeamlessTileManager", "NeighborGenerationSourceTriggerCell", IntVec3.Invalid);
		RexCommandTargets.Reset();
	}
}

/// <summary>
/// 10. TickManager.RemoveAllFromMap чистит только ванильные тик-листы, а в MP вещи карты
/// зарегистрированы в тик-листах её AsyncTimeComp. Мод, который «замораживает» карту
/// и потом перерегистрирует вещи (RimExodus при сне/пробуждении), получал двойную
/// регистрацию: вещи тикали дважды за тик. В MP чистим и тик-листы карты.
/// </summary>
[HarmonyPatch(typeof(TickManager), "RemoveAllFromMap")]
public static class MpRemoveAllFromMapFix
{
	private static FieldInfo[] _lists;

	private static bool Prepare()
	{
		Type t = RafTypes.Find("Multiplayer.Client.AsyncTimeComp");
		if (t == null)
		{
			return false;
		}
		_lists = new FieldInfo[3]
		{
			AccessTools.Field(t, "tickListNormal"),
			AccessTools.Field(t, "tickListRare"),
			AccessTools.Field(t, "tickListLong")
		};
		return _lists.All((FieldInfo f) => f != null && f.FieldType == typeof(TickList));
	}

	private static void Postfix(Map map)
	{
		if (map == null || !MpHelper.IsMultiplayer())
		{
			return;
		}
		object comp = RimExodusMp.AsyncTime(map);
		if (comp == null)
		{
			return;
		}
		Predicate<Thing> onMap = (Thing x) => x.Map == map;
		for (int i = 0; i < _lists.Length; i++)
		{
			(_lists[i].GetValue(comp) as TickList)?.RemoveWhere(onMap);
		}
	}
}

/// <summary>
/// 12. Асинхронное время: смена скорости карты (команда MP MapTimeSpeed) распространяется
/// на карты, связанные с ней швами RimExodus, если там нет пешек и домашней зоны
/// других игроков. Иначе пешка, перешедшая шов, попадала в карту на паузе.
/// </summary>
[HarmonyPatch]
public static class RexSeamlessTimeSpeed
{
	private static FieldInfo _cmdType;

	private static FieldInfo _cmdFaction;

	private static object _mapTimeSpeed;

	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type atc = RafTypes.Find("Multiplayer.Client.AsyncTimeComp");
		Type sc = RafTypes.Find("Multiplayer.Common.ScheduledCommand");
		if (atc == null || sc == null)
		{
			return null;
		}
		_cmdType = AccessTools.Field(sc, "type");
		_cmdFaction = AccessTools.Field(sc, "factionId");
		if (_cmdType == null || _cmdFaction == null || !_cmdType.FieldType.IsEnum)
		{
			return null;
		}
		try
		{
			_mapTimeSpeed = Enum.Parse(_cmdType.FieldType, "MapTimeSpeed");
		}
		catch
		{
			return null;
		}
		return AccessTools.DeclaredMethod(atc, "ExecuteCmd", new Type[1] { sc });
	}

	private static void Postfix(object __instance, object cmd)
	{
		try
		{
			if (cmd == null || !_mapTimeSpeed.Equals(_cmdType.GetValue(cmd)) || !RimExodusMp.AsyncTimeOn)
			{
				return;
			}
			Map map = RimExodusMp.MapOfAsyncTime(__instance);
			if (map == null || map.Disposed || !RimExodusMp.TryGetSpeed(map, out TimeSpeed speed))
			{
				return;
			}
			int factionId = (int)_cmdFaction.GetValue(cmd);
			Faction actor = Find.FactionManager.AllFactionsListForReading.FirstOrDefault((Faction f) => f.loadID == factionId);
			List<Map> cluster = RimExodusMp.ConnectedMaps(map, (Map m) => !RimExodusMp.OtherPlayerPawns(m, actor) && RimExodusMp.OtherPlayersHomeCells(m, actor) == 0 && !(m.Parent != null && m.Parent.def.canBePlayerHome && RimExodusMp.IsPlayerFaction(m.Parent.Faction) && m.Parent.Faction != actor));
			for (int i = 0; i < cluster.Count; i++)
			{
				if (cluster[i] != map)
				{
					RimExodusMp.SetSpeed(cluster[i], speed);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warning("[RaidAnimalFix] RimExodus time speed: " + ex.Message);
		}
	}
}

/// <summary>13. Настройки RimExodus во время MP-игры заблокированы (их читает симуляция).</summary>
[HarmonyPatch]
public static class RexSettingsLockMp
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("RimExodusMod");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "DoSettingsWindowContents", new Type[1] { typeof(Rect) });
	}

	private static bool Prefix(Rect inRect)
	{
		if (Current.ProgramState != ProgramState.Playing || !MpHelper.IsMultiplayer())
		{
			return true;
		}
		GameFont font = Text.Font;
		Text.Font = GameFont.Small;
		Widgets.Label(inRect, "[Desync Fix] " + "RAF_RimExodus_SettingsLocked".Translate().Resolve());
		Text.Font = font;
		return false;
	}
}

/// <summary>
/// 14. Асинхронное время: пустая клетка RimExodus (MapParent_SeamlessTile — нет пешек
/// игроков, никто не смотрит, нет активной угрозы, не чей-то дом) идёт не быстрее ▶:
/// 1 тик карты за тик сервера. Скорость клеткам раздаёт кластер (12), и при ▶▶▶ у соседа
/// MP гнал пустую полноразмерную карту ×6, а «ничего не происходит» он считает по пешкам
/// фракции-наблюдателя (на клетке их нет никогда) → ×12. Две-три клетки симулировались
/// быстрее самой колонии — у обоих игроков. Пешка игрока вошла или кто-то открыл клетку —
/// она снова идёт со скоростью кластера, только без ×12. Все входы — состояние симуляции
/// (пешки, синхронный счётчик зрителей, список угроз губернатора), так что число тиков
/// карты одинаково у всех. Без асинхронного времени MP берёт минимум по всем картам —
/// там клетки не трогаем, иначе замедлилась бы вся игра.
/// </summary>
[HarmonyPatch]
public static class RexIdleTileSpeedCap
{
	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RafTypes.Find("Multiplayer.Client.AsyncTimeComp");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "TickRateMultiplier", new Type[1] { typeof(TimeSpeed) });
	}

	private static void Postfix(object __instance, TimeSpeed speed, ref float __result)
	{
		if (__result <= 1f)
		{
			return;
		}
		try
		{
			Map map = RimExodusMp.MapOfAsyncTime(__instance);
			if (!RimExodusMp.IsSeamlessTileMap(map) || !RimExodusMp.AsyncTimeOn)
			{
				return;
			}
			if (RimExodusMp.PlayerCountOf(__instance) > 0 || RimExodusMp.AnyPlayerPawn(map) || RimExodusMp.InThreatKeepalive(map) || IsPlayerHomeTile(map))
			{
				if (speed == TimeSpeed.Superfast && __result > 6f)
				{
					__result = 6f;
				}
				return;
			}
			__result = 1f;
		}
		catch (Exception ex)
		{
			Log.ErrorOnce("[RaidAnimalFix] RimExodus idle tile speed: " + ex.Message, 0x5E1D7A11);
		}
	}

	// Как RexProtectedHomeMp, но без Map.IsPlayerHome: тот смотрит на Faction.OfPlayer,
	// а вне тика карты это фракция этого клиента.
	private static bool IsPlayerHomeTile(Map map)
	{
		MapParent parent = map.Parent;
		return map.wasSpawnedViaGravShipLanding || (parent != null && parent.def.canBePlayerHome && RimExodusMp.IsPlayerFaction(parent.Faction));
	}
}

/// <summary>
/// 15. Панель колонистов в MP: ваниль рисует группу для каждой карты (пустую рамку, если
/// колонистов нет), а MP под каждой группой — кнопку скорости. Каждая загруженная клетка
/// RimExodus без своих колонистов становилась такой группой. Скрываем их (как RimExodus
/// со своей галочкой «скрывать пустые группы», но только клетки: своя колония, клетки
/// со своими колонистами и колонии других игроков остаются). Только UI.
/// </summary>
[HarmonyPatch(typeof(ColonistBar), "CheckRecacheEntries")]
[HarmonyAfter("RimExodus.SeamlessWorld")]
[HarmonyPriority(Priority.Last)]
public static class RexColonistBarIdleTilesMp
{
	private static readonly AccessTools.FieldRef<ColonistBar, List<ColonistBar.Entry>> Entries = AccessTools.FieldRefAccess<ColonistBar, List<ColonistBar.Entry>>("cachedEntries");

	private static readonly AccessTools.FieldRef<ColonistBar, List<Vector2>> DrawLocs = AccessTools.FieldRefAccess<ColonistBar, List<Vector2>>("cachedDrawLocs");

	private static readonly AccessTools.FieldRef<ColonistBar, List<int>> Groups = AccessTools.FieldRefAccess<ColonistBar, List<int>>("cachedReorderableGroups");

	private static readonly AccessTools.FieldRef<ColonistBar, float> Scale = AccessTools.FieldRefAccess<ColonistBar, float>("cachedScale");

	private static readonly AccessTools.FieldRef<ColonistBar, ColonistBarDrawLocsFinder> Finder = AccessTools.FieldRefAccess<ColonistBar, ColonistBarDrawLocsFinder>("drawLocsFinder");

	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static void Postfix(ColonistBar __instance)
	{
		if (!MpHelper.IsMultiplayer())
		{
			return;
		}
		List<ColonistBar.Entry> list = Entries(__instance);
		List<Vector2> drawLocs = DrawLocs(__instance);
		List<int> groups = Groups(__instance);
		ColonistBarDrawLocsFinder finder = Finder(__instance);
		// Пустая группа карты — запись без пешки (ваниль добавляет её, когда своих колонистов нет).
		if (list == null || drawLocs == null || groups == null || finder == null || list.RemoveAll((ColonistBar.Entry e) => e.pawn == null && RimExodusMp.IsSeamlessTileMap(e.map)) == 0)
		{
			return;
		}
		int prev = int.MinValue;
		int group = -1;
		for (int i = 0; i < list.Count; i++)
		{
			ColonistBar.Entry entry = list[i];
			if (entry.group != prev)
			{
				prev = entry.group;
				group++;
			}
			list[i] = new ColonistBar.Entry(entry.pawn, entry.map, group);
		}
		groups.Clear();
		for (int i = 0; i < list.Count; i++)
		{
			groups.Add(-1);
		}
		finder.CalculateDrawLocs(drawLocs, out float scale, group + 1);
		Scale(__instance) = scale;
	}
}

/// <summary>
/// 16. Throttle() строит «быструю зону» у швов: 15 проходов по всей карте с новыми
/// массивами на каждую клетку — десятки мс. Переход любой пешки через шов снимает
/// троттлинг с карты прибытия, следующий Sweep ставит его обратно — зона строилась
/// заново на каждый переход, у обоих игроков (в логе десятки «Throttle ON/OFF» подряд).
/// В MP зона кэшируется на карту. Ключ — радиус и отпечаток клеток пустоты (пустоту
/// RimExodus перекрашивает при заполнении швов): один проход вместо пятнадцати, и игрок,
/// зашедший позже, получает ту же зону, что и остальные. Зона только читается.
/// </summary>
[HarmonyPatch]
public static class RexFastRegionCacheMp
{
	private sealed class Key
	{
		public int radius;

		public int voidCount;

		public int voidHash;

		public bool Same(Key other)
		{
			return other != null && radius == other.radius && voidCount == other.voidCount && voidHash == other.voidHash;
		}
	}

	private sealed class Cached
	{
		public Key key;

		public HashSet<IntVec3> cells;
	}

	private static readonly ConditionalWeakTable<Map, Cached> Cache = new ConditionalWeakTable<Map, Cached>();

	private static TerrainDef _void;

	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type t = RimExodusMp.T("SeamlessTickThrottle");
		return (t == null) ? null : AccessTools.DeclaredMethod(t, "BuildFastRegion", new Type[2]
		{
			typeof(Map),
			typeof(int)
		});
	}

	private static bool Prefix(Map map, int radius, ref HashSet<IntVec3> __result, out Key __state)
	{
		__state = null;
		if (map == null || radius <= 0 || !MpHelper.IsMultiplayer())
		{
			return true;
		}
		Key key = Fingerprint(map, radius);
		if (key == null)
		{
			return true;
		}
		if (Cache.TryGetValue(map, out Cached cached) && key.Same(cached.key))
		{
			__result = cached.cells;
			return false;
		}
		__state = key;
		return true;
	}

	private static void Postfix(Map map, HashSet<IntVec3> __result, Key __state)
	{
		if (__state == null || __result == null)
		{
			return;
		}
		Cache.Remove(map);
		Cache.Add(map, new Cached
		{
			key = __state,
			cells = __result
		});
	}

	private static Key Fingerprint(Map map, int radius)
	{
		if (_void == null)
		{
			_void = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
		}
		TerrainDef[] top = map.terrainGrid?.topGrid;
		if (_void == null || top == null)
		{
			return null;
		}
		int count = 0;
		int hash = 17;
		for (int i = 0; i < top.Length; i++)
		{
			if (top[i] == _void)
			{
				count++;
				hash = hash * 31 + i;
			}
		}
		return new Key
		{
			radius = radius,
			voidCount = count,
			voidHash = hash
		};
	}
}

/// <summary>
/// 17. Пешки соседних карт на экране текущей: RimExodus каждый кадр проходит по всем
/// пешкам всех соседей (5 соседей × ~50) и у каждой берёт DrawPos — в MP он идёт через
/// патч MP на PawnTweener (~3–4 мкс), плюс поиск смещения соседа на каждую пешку. Почти
/// все пешки вне экрана. В MP сначала отсеиваем по клетке (смещение соседа — одно на
/// карту, запас 3 клетки покрывает твин между клетками), остальное — как в оригинале.
/// Только отрисовка. Подмена — лишь для известной версии метода (размер IL), иначе
/// работает оригинал.
/// </summary>
[HarmonyPatch]
public static class RexNeighborPawnCullMp
{
	private const int KnownIlSize = 191;

	private delegate bool ProjectCell(Map sourceMap, IntVec3 sourceLocal, Map destinationMap, out IntVec3 projected);

	private delegate bool ProjectVec(Map sourceMap, Vector3 sourceLocal, Map destinationMap, out Vector3 projected);

	private static ProjectCell _cell;

	private static ProjectVec _vec;

	private static bool Prepare()
	{
		return RimExodusMp.Present;
	}

	private static MethodBase TargetMethod()
	{
		Type proj = RimExodusMp.T("SeamlessViewProjection");
		MethodInfo cell = (proj == null) ? null : AccessTools.DeclaredMethod(proj, "TryProject", new Type[4]
		{
			typeof(Map),
			typeof(IntVec3),
			typeof(Map),
			typeof(IntVec3).MakeByRefType()
		});
		MethodInfo vec = (proj == null) ? null : AccessTools.DeclaredMethod(proj, "TryProject", new Type[4]
		{
			typeof(Map),
			typeof(Vector3),
			typeof(Map),
			typeof(Vector3).MakeByRefType()
		});
		Type t = RimExodusMp.T("SeamlessTileRenderer");
		MethodInfo m = (t == null) ? null : AccessTools.DeclaredMethod(t, "DrawNeighborPawns", new Type[2]
		{
			typeof(Map),
			typeof(CellRect)
		});
		if (m == null || cell == null || vec == null || m.GetMethodBody()?.GetILAsByteArray()?.Length != KnownIlSize)
		{
			return null;
		}
		_cell = (ProjectCell)Delegate.CreateDelegate(typeof(ProjectCell), cell);
		_vec = (ProjectVec)Delegate.CreateDelegate(typeof(ProjectVec), vec);
		return m;
	}

	private static bool Prefix(Map neighborMap, CellRect hostViewRect)
	{
		if (!MpHelper.IsMultiplayer() || neighborMap?.mapPawns == null)
		{
			return true;
		}
		Map current = Find.CurrentMap;
		if (!_cell(neighborMap, IntVec3.Zero, current, out IntVec3 offset))
		{
			return false;
		}
		CellRect cull = hostViewRect.ExpandedBy(3);
		IReadOnlyList<Pawn> pawns = neighborMap.mapPawns.AllPawnsSpawned;
		for (int i = 0; i < pawns.Count; i++)
		{
			Pawn pawn = pawns[i];
			if (!cull.Contains(pawn.Position + offset))
			{
				continue;
			}
			try
			{
				if (_vec(neighborMap, pawn.DrawPos, current, out Vector3 projected) && hostViewRect.Contains(new IntVec3(Mathf.FloorToInt(projected.x), 0, Mathf.FloorToInt(projected.z))) && (pawn.def.seeThroughFog || !neighborMap.fogGrid.IsFogged(pawn.Position)))
				{
					pawn.DrawNowAt(projected);
				}
			}
			catch (Exception arg)
			{
				Log.ErrorOnce($"[RimExodus] Failed to draw seamless neighbor pawn {pawn}: {arg}", pawn.thingIDNumber ^ 0x5EAF00D);
			}
		}
		return false;
	}
}
