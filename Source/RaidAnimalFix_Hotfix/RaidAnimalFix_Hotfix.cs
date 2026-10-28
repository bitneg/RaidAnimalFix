using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Verse.AI;
using Verse.AI.Group;

[assembly: AssemblyVersion("3.0.947.0")]
[assembly: AssemblyFileVersion("3.0.947.0")]

namespace RaidAnimalFix
{
    [StaticConstructorOnStartup]
    public static class HotfixInitializer
    {
        static HotfixInitializer()
        {
            try
            {
                Harmony harmony = new Harmony("customfix.raidanimalfix.hotfix");
                int ok = 0, fail = 0;
                foreach (Type t in typeof(HotfixInitializer).Assembly.GetTypes())
                {
                    if (t.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
                        continue;
                    try
                    {
                        new PatchClassProcessor(harmony, t).Patch();
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Log.Warning("[RaidAnimalFix] Hotfix patch failed " + t.Name + ": " + ex.Message);
                        UnpatchClass(harmony, t);
                    }
                }
                Log.Message("[RaidAnimalFix] Hotfix 3.0.947.0: patched " + ok + ", failed " + fail);
            }
            catch (Exception ex)
            {
                Log.Error("[RaidAnimalFix] Hotfix init failed: " + ex);
            }
        }

        /// <summary>
        /// После ошибки патча снимаем всё, что класс успел наложить: иначе битая
        /// замена остаётся в Harmony и роняет трассировщик MP посреди тика.
        /// </summary>
        private static void UnpatchClass(Harmony harmony, Type patchClass)
        {
            try
            {
                foreach (MethodBase original in new List<MethodBase>(Harmony.GetAllPatchedMethods()))
                {
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                    if (info == null) continue;
                    List<Patch> all = new List<Patch>();
                    all.AddRange(info.Prefixes); all.AddRange(info.Postfixes); all.AddRange(info.Transpilers); all.AddRange(info.Finalizers);
                    foreach (Patch p in all)
                    {
                        if (p.owner == harmony.Id && p.PatchMethod != null && p.PatchMethod.DeclaringType == patchClass)
                        {
                            try { harmony.Unpatch(original, p.PatchMethod); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[RaidAnimalFix] Hotfix cleanup " + patchClass.Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Курица Chicken1392237: Notify_JobStarted падает, если работа уже null.
    /// Ваниль глотает исключение и обрывает остаток тика пешки — у сторон
    /// дальше расходится случайность. Пустую работу просто пропускаем.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_FlightTracker))]
    [HarmonyPatch("Notify_JobStarted")]
    public static class FlightJobNullFix
    {
        [HarmonyPrefix]
        public static bool Prefix(Job job)
        {
            return job != null && job.def != null;
        }
    }

    /// <summary>
    /// Победа в засаде каравана: FreeColonists.RandomElement, когда все свои
    /// без сознания. Письмо уже ушло, wonBattle не ставится — каждый тик
    /// снова письмо и снова падение. Завершаем бой без рассказа.
    /// </summary>
    [HarmonyPatch(typeof(CaravansBattlefield))]
    [HarmonyPatch("CheckWonBattle")]
    public static class CaravanAmbushEmptyColonistsFix
    {
        private static readonly FieldInfo WonField =
            AccessTools.Field(typeof(CaravansBattlefield), "wonBattle");

        [HarmonyPrefix]
        public static bool Prefix(CaravansBattlefield __instance)
        {
            try
            {
                if (__instance == null || WonField == null)
                    return true;
                if ((bool)WonField.GetValue(__instance))
                    return true;
                Map map = __instance.Map;
                if (map == null || map.mapPawns == null)
                    return true;
                if (GenHostility.AnyHostileActiveThreatToPlayer(map, false, false))
                    return true;

                IEnumerable<Pawn> free = map.mapPawns.FreeColonists;
                if (free != null)
                {
                    foreach (Pawn p in free)
                    {
                        if (p != null)
                            return true;
                    }
                }

                TimedDetectionRaids raids = __instance.GetComponent<TimedDetectionRaids>();
                if (raids != null)
                {
                    raids.SetNotifiedSilently();
                    string left = raids.DetectionCountdownTimeLeftString;
                    Find.LetterStack.ReceiveLetter(
                        "LetterLabelCaravansBattlefieldVictory".Translate(),
                        "LetterCaravansBattlefieldVictory".Translate(left),
                        LetterDefOf.PositiveEvent,
                        __instance,
                        null, null, null, null, 0, true);
                }
                WonField.SetValue(__instance, true);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[RaidAnimalFix] CaravanAmbushEmptyColonists: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// GenerateBackCompatibilityNameFor заново кидает имя в грамматику.
    /// На стыковке Faction.OfPlayer часто зритель — русские падежи
    /// накладываются второй раз («Торговляя», «Караванн»), и генератор
    /// жрёт случайность только у вошедшего. Если имя уже есть — не трогаем.
    /// </summary>
    [HarmonyPatch(typeof(QuestUtility))]
    [HarmonyPatch("GenerateBackCompatibilityNameFor")]
    public static class QuestNameRerollFix
    {
        [HarmonyPrefix]
        public static bool Prefix(Quest quest)
        {
            return quest == null || quest.name.NullOrEmpty();
        }
    }

    /// <summary>
    /// Квесты живут на мировых часах, а TicksGame в MP — часы текущего
    /// контекста (карта / мир / UI-карта под камерой). Срок предложения
    /// пишем и читаем ТОЛЬКО от мировых часов MP (AsyncWorldTimeComp.worldTicks).
    ///
    /// 3.0.936: убран статический кэш RemCache (ключ — один TicksGame).
    /// Его наполнял и UI (тревоги каждый кадр, гизмо торговых запросов →
    /// Quest.State), и симуляция. У игрока с камерой на «отстающей» карте
    /// кэш отдавал остаток на тик старше → квест истекал на разных тиках,
    /// а запасной подбор «карты с подходящими часами» растягивал разницу
    /// до дней. Теперь геттер — чистая функция синхронного состояния.
    /// Старые офферы, записанные по часам карты (остаток > 32 дней),
    /// перебазируются один раз из QuestTick — он идёт только в симуляции.
    /// </summary>
    internal static class QuestClock
    {
        private const int MaxOfferTicks = 32 * 60000;

        private static bool _resolved;
        private static FieldInfo _gameField;
        private static FieldInfo _worldCompField;
        private static FieldInfo _worldTicksField;
        private static FieldInfo _mapTicksField;
        private static MethodInfo _asyncTimeOfMap;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Type mp = AccessTools.TypeByName("Multiplayer.Client.Multiplayer");
                if (mp != null)
                    _gameField = AccessTools.Field(mp, "game");
                if (_gameField != null)
                    _worldCompField = AccessTools.Field(_gameField.FieldType, "asyncWorldTimeComp");
                if (_worldCompField != null)
                    _worldTicksField = AccessTools.Field(_worldCompField.FieldType, "worldTicks");
                Type ext = AccessTools.TypeByName("Multiplayer.Client.Extensions");
                if (ext != null)
                    _asyncTimeOfMap = AccessTools.Method(ext, "AsyncTime", new Type[] { typeof(Map) });
                Type atc = AccessTools.TypeByName("Multiplayer.Client.AsyncTimeComp");
                if (atc != null)
                    _mapTicksField = AccessTools.Field(atc, "mapTicks");
            }
            catch (Exception ex)
            {
                Log.Warning("[RaidAnimalFix] QuestClock resolve: " + ex.Message);
            }
        }

        // Геттер срока зовётся для каждого квеста каждый тик (State, Historical —
        // дважды) и при каждом переборе квестов из ИИ пешек (IsQuestLodger и т.п.):
        // десятки тысяч раз за тик при больших списках. Объект часов мира кэшируем
        // на MP-игру, поле читаем компилированной ссылкой — без рефлексии и упаковки.
        private static AccessTools.FieldRef<object> _gameRef;
        private static AccessTools.FieldRef<object, int> _worldTicksRef;
        private static bool _gameRefTried;
        private static object _cachedGame;
        private static object _cachedComp;

        public static bool TryWorld(out int ticks)
        {
            ticks = 0;
            Resolve();
            if (_worldTicksField == null) return false;
            try
            {
                if (!_gameRefTried)
                {
                    _gameRefTried = true;
                    try { _gameRef = AccessTools.StaticFieldRefAccess<object>(_gameField); }
                    catch { _gameRef = null; }
                    try { _worldTicksRef = AccessTools.FieldRefAccess<object, int>(_worldTicksField); }
                    catch { _worldTicksRef = null; }
                }
                object game = (_gameRef != null) ? _gameRef() : _gameField.GetValue(null);
                if (game == null) return false;
                if (!ReferenceEquals(game, _cachedGame) || _cachedComp == null)
                {
                    _cachedGame = game;
                    _cachedComp = _worldCompField.GetValue(game);
                }
                if (_cachedComp == null) return false;
                ticks = (_worldTicksRef != null) ? _worldTicksRef(_cachedComp) : (int)_worldTicksField.GetValue(_cachedComp);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryMapTicks(Map map, out int ticks)
        {
            ticks = 0;
            if (map == null || _asyncTimeOfMap == null || _mapTicksField == null) return false;
            try
            {
                object comp = _asyncTimeOfMap.Invoke(null, new object[] { map });
                if (comp == null) return false;
                ticks = (int)_mapTicksField.GetValue(comp);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static int RemainingOffer(Quest quest, int vanilla)
        {
            int world;
            if (!TryWorld(out world))
                return vanilla;
            return Mathf.Max(quest.acceptanceExpireTick - world, 0);
        }

        public static void StampFromWorld(Quest quest, int delayTicks)
        {
            if (quest == null || delayTicks < 0) return;
            int world;
            if (!TryWorld(out world)) return;
            quest.acceptanceExpireTick = world + delayTicks;
        }

        /// <summary>
        /// Только из симуляции (Quest.QuestTick). Оффер, записанный по часам
        /// карты, которая ушла вперёд мира, даёт остаток > 32 дней. Переносим
        /// на мировые часы по той карте (порядок Find.Maps), чей остаток
        /// попадает в 0..32 дня. Все входы синхронны → одинаково у всех.
        /// </summary>
        public static void RebaseLegacyOffer(Quest quest)
        {
            if (quest == null || quest.acceptanceExpireTick < 0 || quest.acceptanceTick >= 0)
                return;
            int world;
            if (!TryWorld(out world)) return;
            int rem = quest.acceptanceExpireTick - world;
            // Historical дорогой (дважды State) — проверяем только для редких «старых» офферов.
            if (rem <= MaxOfferTicks || quest.Historical) return;
            List<Map> maps = Find.Maps;
            int best = -1;
            for (int i = 0; i < maps.Count; i++)
            {
                int mt;
                if (!TryMapTicks(maps[i], out mt)) continue;
                int r = quest.acceptanceExpireTick - mt;
                if (r >= 0 && r <= MaxOfferTicks && (best < 0 || r < best))
                    best = r;
            }
            if (best < 0) best = MaxOfferTicks;
            quest.acceptanceExpireTick = world + best;
            Log.Message("[RaidAnimalFix] QuestClock: оффер квеста " + quest.id + " перенесён на мировые часы (" + best + " тиков)");
        }
    }

    [HarmonyPatch(typeof(Quest))]
    [HarmonyPatch("TicksUntilExpiry", MethodType.Getter)]
    public static class QuestExpiryWorldClockFix
    {
        [HarmonyPostfix]
        public static void Postfix(Quest __instance, ref int __result)
        {
            try
            {
                if (__instance == null || __instance.acceptanceExpireTick < 0) return;
                __result = QuestClock.RemainingOffer(__instance, __result);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Quest))]
    [HarmonyPatch("QuestTick")]
    public static class QuestLegacyOfferRebase
    {
        [HarmonyPrefix]
        public static void Prefix(Quest __instance)
        {
            try
            {
                QuestClock.RebaseLegacyOffer(__instance);
            }
            catch { }
        }
    }

    [HarmonyPatch]
    public static class QuestGenExpireStampFix
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(QuestGen), "InitializeQuestGen");
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                Quest q = QuestGen.quest;
                if (q == null || q.acceptanceExpireTick < 0) return;
                int delay = q.acceptanceExpireTick - Find.TickManager.TicksGame;
                QuestClock.StampFromWorld(q, delay);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(QuestNode_SetTicksUntilAcceptanceExpiry))]
    [HarmonyPatch("RunInt")]
    public static class QuestNodeExpireStampFix
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                Quest q = QuestGen.quest;
                if (q == null || q.acceptanceExpireTick < 0) return;
                int delay = q.acceptanceExpireTick - Find.TickManager.TicksGame;
                QuestClock.StampFromWorld(q, delay);
            }
            catch { }
        }
    }

    /// <summary>
    /// Сбор в караван: ClosestThingReachable(HaulableEver, lookInHaulSources)
    /// при равной дистанции берёт первый элемент глобального списка.
    /// После стыковки порядок ListerThings расходится → разные TargetA
    /// (MinifiedThing vs Pemmican) на одном startTick, дальше шаг/Filth/PatherFailed.
    /// Берём из transferables: ближе, при ничьей меньший thingID.
    /// </summary>
    [HarmonyPatch(typeof(GatherItemsForCaravanUtility))]
    [HarmonyPatch("FindThingToHaul")]
    public static class CaravanGatherItemOrderFix
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn p, Lord lord, ref Thing __result)
        {
            try
            {
                if (p == null || lord == null || p.Map == null)
                    return true;
                LordJob_FormAndSendCaravan form = lord.LordJob as LordJob_FormAndSendCaravan;
                if (form == null || form.transferables == null)
                    return true;

                Thing best = null;
                int bestDist = int.MaxValue;
                int bestId = int.MaxValue;
                List<TransferableOneWay> transferables = form.transferables;
                for (int i = 0; i < transferables.Count; i++)
                {
                    TransferableOneWay tw = transferables[i];
                    if (tw == null || tw.things == null)
                        continue;
                    if (GatherItemsForCaravanUtility.CountLeftToTransfer(p, tw, lord) <= 0)
                        continue;
                    for (int j = 0; j < tw.things.Count; j++)
                    {
                        Thing x = tw.things[j];
                        if (x == null || x.Destroyed)
                            continue;
                        // Как ваниль (lookInHaulSources): на земле или в хранилище-источнике,
                        // не в инвентаре/руках пешки — иначе работа сбора срывается по кругу.
                        if (!x.Spawned && !(x.ParentHolder is IHaulSource))
                            continue;
                        if (!p.CanReserve(x, 1, -1, null, false))
                            continue;
                        IntVec3 cell = x.PositionHeld;
                        if (!cell.IsValid)
                            continue;
                        if (!p.CanReach(x, PathEndMode.Touch, Danger.Deadly, false, false, TraverseMode.ByPawn))
                            continue;
                        int dx = p.Position.x - cell.x;
                        int dz = p.Position.z - cell.z;
                        int dist = dx * dx + dz * dz;
                        int id = x.thingIDNumber;
                        if (best == null || dist < bestDist || (dist == bestDist && id < bestId))
                        {
                            best = x;
                            bestDist = dist;
                            bestId = id;
                        }
                    }
                }
                __result = best;
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[RaidAnimalFix] CaravanGatherItemOrder: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Filters 3.0.928 пишет отпечаток каждые 60 тиков на каждом
    /// MapPostTick: все карты, ~700 пешек, поимённая строка на своей,
    /// диск каждые 25 проб. Это не геймплей. 3.0.932 снял пробу с
    /// клиентов, хост остался с тем же проходом и начал лагать сам
    /// (Bitn, fingerprint 19:14, хост «too far behind»).
    ///
    /// Периодика не нужна для закрытия рассинхрона: Filters уже зовёт
    /// FlushNow из HandleDesync и HostDumpOnPeerRejoin. Кольцо без
    /// проб пустое, но DumpLive пишет живой снимок всех карт.
    /// Prefix без рефлексии — только return false.
    /// </summary>
    [HarmonyPatch]
    public static class FingerprintPeriodicSkip
    {
        private static bool _logged;

        static bool Prepare()
        {
            return TargetMethod() != null;
        }

        static MethodBase TargetMethod()
        {
            Type t = AccessTools.TypeByName("RaidAnimalFix.StateFingerprintProbe");
            return t == null ? null : AccessTools.Method(t, "Postfix", new Type[] { typeof(Map) });
        }

        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!_logged)
            {
                _logged = true;
                Log.Message("[RaidAnimalFix] fingerprint: периодическая проба выключена (все карты / сотни пешек / диск). Снимок только при рассинхроне и при перезаходе.");
            }
            return false;
        }
    }

    /// <summary>
    /// RimWorld отдаёт в SteamUGC.SetItemContent весь RootDir.
    /// Из-за этого в Workshop уезжали .cursor, Source, _patch_dump*, exe.
    /// На Upload подменяем папку на временную копию только игровых файлов.
    /// GetWorkshopUploadDirectory зовётся только оттуда.
    /// </summary>
    [HarmonyPatch(typeof(ModMetaData))]
    [HarmonyPatch("GetWorkshopUploadDirectory")]
    public static class WorkshopUploadSanitize
    {
        private static readonly string[] KeepDirs = { "About", "Languages", "1.6" };
        private static readonly string[] KeepFiles = { "LoadFolders.xml" };

        [HarmonyPostfix]
        public static void Postfix(ModMetaData __instance, ref DirectoryInfo __result)
        {
            try
            {
                if (__instance == null)
                    return;
                string id = __instance.PackageId;
                if (id == null || id.IndexOf("raidanimalfix", StringComparison.OrdinalIgnoreCase) < 0)
                    return;
                string src = __result != null ? __result.FullName : __instance.RootDir.FullName;
                if (string.IsNullOrEmpty(src) || !Directory.Exists(src))
                    return;

                string dest = Path.Combine(Path.GetTempPath(), "RaidAnimalFix_workshop_upload");
                if (Directory.Exists(dest))
                    Directory.Delete(dest, true);
                Directory.CreateDirectory(dest);

                for (int i = 0; i < KeepDirs.Length; i++)
                {
                    string from = Path.Combine(src, KeepDirs[i]);
                    if (Directory.Exists(from))
                        CopyDir(from, Path.Combine(dest, KeepDirs[i]));
                }
                for (int i = 0; i < KeepFiles.Length; i++)
                {
                    string from = Path.Combine(src, KeepFiles[i]);
                    if (File.Exists(from))
                        File.Copy(from, Path.Combine(dest, KeepFiles[i]), true);
                }

                __result = new DirectoryInfo(dest);
                Log.Message("[RaidAnimalFix] workshop upload: только About, Languages, 1.6, LoadFolders.xml");
            }
            catch (Exception ex)
            {
                Log.Warning("[RaidAnimalFix] workshop sanitize: " + ex.Message);
            }
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            string[] files = Directory.GetFiles(from);
            for (int i = 0; i < files.Length; i++)
                File.Copy(files[i], Path.Combine(to, Path.GetFileName(files[i])), true);
            string[] dirs = Directory.GetDirectories(from);
            for (int i = 0; i < dirs.Length; i++)
                CopyDir(dirs[i], Path.Combine(to, Path.GetFileName(dirs[i])));
        }
    }
}
