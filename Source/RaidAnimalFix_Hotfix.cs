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

[assembly: AssemblyVersion("3.0.935.0")]
[assembly: AssemblyFileVersion("3.0.935.0")]

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
                    }
                }
                Log.Message("[RaidAnimalFix] Hotfix 3.0.935.0: patched " + ok + ", failed " + fail);
            }
            catch (Exception ex)
            {
                Log.Error("[RaidAnimalFix] Hotfix init failed: " + ex);
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
    /// Квесты живут на мировых часах, а TicksGame в MP — часы текущей карты.
    /// Предложение ставится как mapTicks+8дней, список читает worldTicks
    /// (или наоборот) → в колонке «1г/2г» вместо 4–8 дней, State врёт,
    /// оффер не сгорает. Новые квесты сразу пишем от мира; старые
    /// в геттере пересчитываем от той карты, чей clock совпал.
    ///
    /// Геттер горячий: vanilla Quest.State зовёт TicksUntilExpiry,
    /// Historical — State дважды, QuestTick — Historical+Expiry+State,
    /// QuestManagerTick — каждый квест каждый тик, AlertsReadout — каждый кадр.
    /// Рефлексия + обход всех карт на каждый get сажал FPS (10 карт у Bitn).
    /// Мир/карты — раз за TicksGame; remaining — раз за квест за тик.
    /// Карты только если remaining абсурден (>32 дней).
    /// </summary>
    internal static class QuestClock
    {
        private const int MaxOfferTicks = 32 * 60000;

        private static MethodInfo _world;
        private static MethodInfo _map;
        private static bool _resolved;
        private static readonly object[] WorldArgs = new object[] { 0 };
        private static readonly object[] MapArgs = new object[] { null, 0 };

        private static int _clockKey = int.MinValue;
        private static int _worldTicks;
        private static bool _worldOk;
        private static int[] _mapTicks;
        private static int _mapTickCount = -1;
        private static readonly Dictionary<int, int> RemCache = new Dictionary<int, int>(64);

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            Type t = AccessTools.TypeByName("RaidAnimalFix.MpMapTime");
            if (t == null) return;
            _world = AccessTools.Method(t, "TryGetWorldTicks");
            _map = AccessTools.Method(t, "TryGetMapTicks", new Type[] { typeof(Map), typeof(int).MakeByRefType() });
        }

        private static void EnsureClock()
        {
            int key = Find.TickManager.TicksGame;
            if (_clockKey == key) return;
            _clockKey = key;
            _mapTickCount = -1;
            RemCache.Clear();
            _worldTicks = key;
            _worldOk = false;
            Resolve();
            if (_world == null) return;
            WorldArgs[0] = 0;
            if ((bool)_world.Invoke(null, WorldArgs))
            {
                _worldTicks = (int)WorldArgs[0];
                _worldOk = true;
            }
        }

        private static void EnsureMapTicks()
        {
            if (_mapTickCount >= 0) return;
            _mapTickCount = 0;
            Resolve();
            List<Map> maps = Find.Maps;
            if (_map == null || maps == null) return;
            int n = maps.Count;
            if (_mapTicks == null || _mapTicks.Length < n)
                _mapTicks = new int[Math.Max(n, 8)];
            for (int i = 0; i < n; i++)
            {
                MapArgs[0] = maps[i];
                MapArgs[1] = 0;
                if ((bool)_map.Invoke(null, MapArgs))
                    _mapTicks[_mapTickCount++] = (int)MapArgs[1];
            }
            MapArgs[0] = null;
        }

        public static bool TryWorld(out int ticks)
        {
            EnsureClock();
            ticks = _worldTicks;
            return _worldOk;
        }

        public static int RemainingOffer(Quest quest)
        {
            int expire = quest.acceptanceExpireTick;
            int cached;
            if (RemCache.TryGetValue(quest.id, out cached))
                return cached;

            int rem;
            int world;
            if (!TryWorld(out world))
                rem = Mathf.Max(expire - Find.TickManager.TicksGame, 0);
            else
            {
                rem = expire - world;
                if (rem < 0 || rem > MaxOfferTicks)
                {
                    EnsureMapTicks();
                    int best = -1;
                    for (int i = 0; i < _mapTickCount; i++)
                    {
                        int r = expire - _mapTicks[i];
                        if (r >= 0 && r <= MaxOfferTicks && (best < 0 || r < best))
                            best = r;
                    }
                    rem = best >= 0 ? best : Mathf.Max(rem, 0);
                }
            }
            RemCache[quest.id] = rem;
            return rem;
        }

        public static void StampFromWorld(Quest quest, int delayTicks)
        {
            if (quest == null || delayTicks < 0) return;
            int world;
            if (!TryWorld(out world)) return;
            quest.acceptanceExpireTick = world + delayTicks;
            RemCache[quest.id] = delayTicks;
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
                __result = QuestClock.RemainingOffer(__instance);
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
