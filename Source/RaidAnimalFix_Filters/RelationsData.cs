using System;
using System.Collections.Generic;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RaidAnimalFix;

/// <summary>Дипломатический статус пары игроков (хранится числом в сейве — значения не менять).</summary>
public enum RafRelationKind
{
	Neutral = 0,
	Ally = 1,
	War = 2
}

/// <summary>Разрешения гостя на карте хозяина (индексы для sync-метода — не менять).</summary>
public enum RafGuestPerm
{
	Designate = 0,
	Food = 1,
	Beds = 2,
	ForbidToggle = 3
}

/// <summary>Действия из окна дипломатии (передаются в sync-метод числом — не менять).</summary>
public static class RafDiploAction
{
	public const int ProposeAlliance = 1;

	public const int ProposePeace = 2;

	public const int DeclareWar = 3;

	public const int BreakAlliance = 4;

	public const int Accept = 5;

	public const int Decline = 6;

	public const int Withdraw = 7;

	public const int CancelPending = 8;
}

/// <summary>
/// Состояние пары фракций игроков. Ключ — loadID (a &lt; b), а не ссылки:
/// так ExposeData не зависит от порядка загрузки фракций, а перебор списка детерминирован.
/// </summary>
public class RafRelationPair : IExposable
{
	public int a;

	public int b;

	public int kind;

	/// <summary>Мировой тик последней вступившей в силу смены статуса (для кулдауна).</summary>
	public int lastChangeTick = -9999999;

	/// <summary>Объявленная односторонне смена (война / разрыв союза), -1 — нет.</summary>
	public int pendingKind = -1;

	public int pendingBy = -1;

	public int pendingAt = -1;

	public void ExposeData()
	{
		Scribe_Values.Look(ref a, "a", 0);
		Scribe_Values.Look(ref b, "b", 0);
		Scribe_Values.Look(ref kind, "kind", 0);
		Scribe_Values.Look(ref lastChangeTick, "lastChange", -9999999);
		Scribe_Values.Look(ref pendingKind, "pendingKind", -1);
		Scribe_Values.Look(ref pendingBy, "pendingBy", -1);
		Scribe_Values.Look(ref pendingAt, "pendingAt", -1);
	}
}

/// <summary>Предложение союза/мира: ждёт согласия второй стороны.</summary>
public class RafProposal : IExposable
{
	public int from;

	public int to;

	/// <summary>Целевой статус (RafRelationKind).</summary>
	public int kind;

	public int tick;

	public void ExposeData()
	{
		Scribe_Values.Look(ref from, "from", 0);
		Scribe_Values.Look(ref to, "to", 0);
		Scribe_Values.Look(ref kind, "kind", 0);
		Scribe_Values.Look(ref tick, "tick", 0);
	}
}

/// <summary>Явно заданные хозяином разрешения для гостя (guest = -1 — «все союзники»).</summary>
public class RafGuestPerms : IExposable
{
	public int host;

	public int guest;

	public bool designate;

	public bool food;

	public bool beds;

	public bool forbid;

	public bool Get(RafGuestPerm p)
	{
		switch (p)
		{
		case RafGuestPerm.Designate:
			return designate;
		case RafGuestPerm.Food:
			return food;
		case RafGuestPerm.Beds:
			return beds;
		default:
			return forbid;
		}
	}

	public void Set(RafGuestPerm p, bool v)
	{
		switch (p)
		{
		case RafGuestPerm.Designate:
			designate = v;
			break;
		case RafGuestPerm.Food:
			food = v;
			break;
		case RafGuestPerm.Beds:
			beds = v;
			break;
		default:
			forbid = v;
			break;
		}
	}

	public void ExposeData()
	{
		Scribe_Values.Look(ref host, "host", 0);
		Scribe_Values.Look(ref guest, "guest", 0);
		Scribe_Values.Look(ref designate, "designate", false);
		Scribe_Values.Look(ref food, "food", false);
		Scribe_Values.Look(ref beds, "beds", false);
		Scribe_Values.Look(ref forbid, "forbid", false);
	}
}

/// <summary>
/// Дипломатия между фракциями игроков в multifaction. Ваниль меняет отношения
/// игроков навсегда (захват пешки, раздевание, нападение на поселение), а
/// естественный дрейф репутации игроков пропускает — вернуть мир нельзя.
/// Здесь статус пары (союз / нейтралитет / война) — синхронное состояние мира;
/// ванильные отношения выставляются из него детерминированно в симуляции.
/// Всё хранится по loadID в списках: порядок перебора одинаков у всех клиентов.
/// </summary>
public class RafRelationsData : WorldComponent
{
	public const int TicksPerDay = 60000;

	/// <summary>Объявление войны / разрыв союза вступает в силу через сутки.</summary>
	public const int PendingDelayTicks = TicksPerDay;

	/// <summary>После смены статуса следующую можно начать только через 5 дней.</summary>
	public const int CooldownTicks = 5 * TicksPerDay;

	/// <summary>Неотвеченное предложение сгорает через 7 дней.</summary>
	public const int ProposalExpireTicks = 7 * TicksPerDay;

	/// <summary>Как часто сверять ванильные отношения с нашим состоянием.</summary>
	public const int ReconcileInterval = 2500;

	public List<RafRelationPair> pairs = new List<RafRelationPair>();

	public List<RafProposal> proposals = new List<RafProposal>();

	public List<RafGuestPerms> perms = new List<RafGuestPerms>();

	private int lastProcessTick = -1;

	private int lastReconcileTick = -1;

	public RafRelationsData(World world)
		: base(world)
	{
	}

	public static RafRelationsData Get()
	{
		try
		{
			return Find.World?.GetComponent<RafRelationsData>();
		}
		catch
		{
			return null;
		}
	}

	public override void ExposeData()
	{
		base.ExposeData();
		Scribe_Collections.Look(ref pairs, "rafRelPairs", LookMode.Deep);
		Scribe_Collections.Look(ref proposals, "rafRelProposals", LookMode.Deep);
		Scribe_Collections.Look(ref perms, "rafRelPerms", LookMode.Deep);
		Scribe_Values.Look(ref lastProcessTick, "rafRelLastProcess", -1);
		Scribe_Values.Look(ref lastReconcileTick, "rafRelLastReconcile", -1);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			if (pairs == null)
			{
				pairs = new List<RafRelationPair>();
			}
			if (proposals == null)
			{
				proposals = new List<RafProposal>();
			}
			if (perms == null)
			{
				perms = new List<RafGuestPerms>();
			}
			pairs.RemoveAll((RafRelationPair p) => p == null);
			proposals.RemoveAll((RafProposal p) => p == null);
			perms.RemoveAll((RafGuestPerms p) => p == null);
		}
	}

	// ───────────── Доступ к состоянию (чтение безопасно и в UI, и в симуляции) ─────────────

	public RafRelationPair FindPair(int x, int y)
	{
		int lo = Math.Min(x, y);
		int hi = Math.Max(x, y);
		for (int i = 0; i < pairs.Count; i++)
		{
			if (pairs[i].a == lo && pairs[i].b == hi)
			{
				return pairs[i];
			}
		}
		return null;
	}

	/// <summary>Создаёт запись пары — только из симуляции (sync-метод / тик мира).</summary>
	public RafRelationPair GetOrCreatePair(int x, int y)
	{
		RafRelationPair p = FindPair(x, y);
		if (p == null)
		{
			p = new RafRelationPair
			{
				a = Math.Min(x, y),
				b = Math.Max(x, y)
			};
			pairs.Add(p);
		}
		return p;
	}

	public RafRelationKind KindOf(Faction x, Faction y)
	{
		if (x == null || y == null || x == y)
		{
			return RafRelationKind.Neutral;
		}
		RafRelationPair p = FindPair(x.loadID, y.loadID);
		return (p == null) ? RafRelationKind.Neutral : ((RafRelationKind)p.kind);
	}

	public RafProposal FindProposal(int from, int to)
	{
		for (int i = 0; i < proposals.Count; i++)
		{
			if (proposals[i].from == from && proposals[i].to == to)
			{
				return proposals[i];
			}
		}
		return null;
	}

	public RafGuestPerms FindPerms(int host, int guest)
	{
		for (int i = 0; i < perms.Count; i++)
		{
			if (perms[i].host == host && perms[i].guest == guest)
			{
				return perms[i];
			}
		}
		return null;
	}

	// ───────────── Тик мира: отложенные объявления, истечение предложений, сверка ─────────────

	/// <summary>
	/// WorldComponentTick в MP идёт в тике мира у всех клиентов одинаково
	/// (Faction.OfPlayer = наблюдатель). Время — мировые тики MP, а не TicksGame:
	/// TicksGame в async time зависит от того, какая карта тикает.
	/// </summary>
	public override void WorldComponentTick()
	{
		if (!RelationsUtil.InMultiplayer)
		{
			return;
		}
		try
		{
			int now = RelationsUtil.NowWorldTicks();
			if (now - lastProcessTick >= 60 || now < lastProcessTick)
			{
				lastProcessTick = now;
				ProcessPending(now);
			}
			if (now - lastReconcileTick >= ReconcileInterval || now < lastReconcileTick)
			{
				lastReconcileTick = now;
				Reconcile();
			}
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("RafRelationsData.Tick", ex);
		}
	}

	private void ProcessPending(int now)
	{
		for (int i = 0; i < pairs.Count; i++)
		{
			RafRelationPair p = pairs[i];
			if (p.pendingKind >= 0 && p.pendingAt >= 0 && now >= p.pendingAt)
			{
				Faction fa = RelationsUtil.FactionById(p.a);
				Faction fb = RelationsUtil.FactionById(p.b);
				Faction by = RelationsUtil.FactionById(p.pendingBy);
				RafRelationKind kind = (RafRelationKind)p.pendingKind;
				if (fa == null || fb == null)
				{
					p.pendingKind = -1;
					p.pendingAt = -1;
					p.pendingBy = -1;
					continue;
				}
				SetKind(p, fa, fb, kind, now);
				Faction other = (by == fa) ? fb : fa;
				string key = (kind == RafRelationKind.War) ? "RAF_Relations_MsgWarStarted" : "RAF_Relations_MsgAllianceEnded";
				RelationsUtil.NotifyLocal(null, key.Translate(by?.Name ?? "?", other?.Name ?? "?"), (kind == RafRelationKind.War) ? MessageTypeDefOf.ThreatBig : MessageTypeDefOf.NeutralEvent, onlyFactions: new Faction[2] { fa, fb });
			}
		}
		for (int num = proposals.Count - 1; num >= 0; num--)
		{
			RafProposal pr = proposals[num];
			if (now - pr.tick >= ProposalExpireTicks || RelationsUtil.FactionById(pr.from) == null || RelationsUtil.FactionById(pr.to) == null)
			{
				proposals.RemoveAt(num);
			}
		}
	}

	/// <summary>
	/// Сверка ванили с нашим состоянием: чинит отношения, изменённые путями,
	/// которые мы не перехватили (прямая запись baseGoodwill модами, старые сейвы,
	/// где захват пешки уже сделал игроков врагами навсегда).
	/// Перебор — AllFactionsListForReading (одинаковый порядок у всех).
	/// </summary>
	public void Reconcile()
	{
		List<Faction> all = Find.FactionManager.AllFactionsListForReading;
		for (int i = 0; i < all.Count; i++)
		{
			Faction fa = all[i];
			if (!MpContext.IsRealPlayerFaction(fa))
			{
				continue;
			}
			for (int j = i + 1; j < all.Count; j++)
			{
				Faction fb = all[j];
				if (!MpContext.IsRealPlayerFaction(fb))
				{
					continue;
				}
				RafRelationKind kind = KindOf(fa, fb);
				if (!RelationsUtil.VanillaMatches(fa, fb, kind))
				{
					Log.Message("[RaidAnimalFix] Relations: ванильные отношения " + fa.Name + " ↔ " + fb.Name + " разошлись с дипломатией (" + kind + ") — восстанавливаем");
					RelationsUtil.ApplyVanilla(fa, fb, kind);
				}
			}
		}
	}

	/// <summary>Вступление статуса в силу (только симуляция).</summary>
	public void SetKind(RafRelationPair p, Faction fa, Faction fb, RafRelationKind kind, int now)
	{
		p.kind = (int)kind;
		p.lastChangeTick = now;
		p.pendingKind = -1;
		p.pendingAt = -1;
		p.pendingBy = -1;
		proposals.RemoveAll((RafProposal pr) => (pr.from == p.a && pr.to == p.b) || (pr.from == p.b && pr.to == p.a));
		RelationsUtil.ApplyVanilla(fa, fb, kind);
		Log.Message("[RaidAnimalFix] Relations: " + fa.Name + " ↔ " + fb.Name + " = " + kind);
	}

	// ───────────── Проверка допустимости действия (UI — для кнопок, sync — для валидации) ─────────────

	public bool CanDo(Faction from, Faction to, int action, int now, out string reason)
	{
		reason = null;
		if (!MpContext.IsRealPlayerFaction(from) || !MpContext.IsRealPlayerFaction(to) || from == to)
		{
			reason = "RAF_Relations_ReasonInvalid".Translate();
			return false;
		}
		RafRelationPair p = FindPair(from.loadID, to.loadID);
		RafRelationKind kind = (p == null) ? RafRelationKind.Neutral : ((RafRelationKind)p.kind);
		bool pending = p != null && p.pendingKind >= 0;
		int cooldownLeft = (p == null) ? 0 : (p.lastChangeTick + CooldownTicks - now);
		RafProposal mine = FindProposal(from.loadID, to.loadID);
		RafProposal theirs = FindProposal(to.loadID, from.loadID);
		switch (action)
		{
		case RafDiploAction.ProposeAlliance:
		case RafDiploAction.ProposePeace:
		case RafDiploAction.DeclareWar:
		case RafDiploAction.BreakAlliance:
			if (pending)
			{
				reason = "RAF_Relations_ReasonPending".Translate();
				return false;
			}
			if (cooldownLeft > 0)
			{
				reason = "RAF_Relations_ReasonCooldown".Translate(cooldownLeft.ToStringTicksToPeriod());
				return false;
			}
			if (action == RafDiploAction.ProposeAlliance && kind != RafRelationKind.Neutral)
			{
				reason = ((kind == RafRelationKind.War) ? "RAF_Relations_ReasonNeedPeace" : "RAF_Relations_ReasonAlready").Translate();
				return false;
			}
			if (action == RafDiploAction.ProposePeace && kind != RafRelationKind.War)
			{
				reason = "RAF_Relations_ReasonNotAtWar".Translate();
				return false;
			}
			if (action == RafDiploAction.DeclareWar && kind == RafRelationKind.War)
			{
				reason = "RAF_Relations_ReasonAlready".Translate();
				return false;
			}
			if (action == RafDiploAction.BreakAlliance && kind != RafRelationKind.Ally)
			{
				reason = "RAF_Relations_ReasonNotAllied".Translate();
				return false;
			}
			if ((action == RafDiploAction.ProposeAlliance || action == RafDiploAction.ProposePeace) && mine != null)
			{
				reason = "RAF_Relations_ReasonProposed".Translate();
				return false;
			}
			return true;
		case RafDiploAction.Accept:
			if (theirs == null)
			{
				reason = "RAF_Relations_ReasonNoProposal".Translate();
				return false;
			}
			if (pending)
			{
				reason = "RAF_Relations_ReasonPending".Translate();
				return false;
			}
			if ((theirs.kind == (int)RafRelationKind.Ally && kind != RafRelationKind.Neutral) || (theirs.kind == (int)RafRelationKind.Neutral && kind != RafRelationKind.War))
			{
				reason = "RAF_Relations_ReasonStale".Translate();
				return false;
			}
			return true;
		case RafDiploAction.Decline:
			if (theirs == null)
			{
				reason = "RAF_Relations_ReasonNoProposal".Translate();
				return false;
			}
			return true;
		case RafDiploAction.Withdraw:
			if (mine == null)
			{
				reason = "RAF_Relations_ReasonNoProposal".Translate();
				return false;
			}
			return true;
		case RafDiploAction.CancelPending:
			if (!pending || p.pendingBy != from.loadID)
			{
				reason = "RAF_Relations_ReasonNoPending".Translate();
				return false;
			}
			return true;
		default:
			reason = "RAF_Relations_ReasonInvalid".Translate();
			return false;
		}
	}

	// ───────────── Синхронные действия ─────────────

	/// <summary>
	/// Единственная точка изменения дипломатии из UI. MP перехватывает вызов в
	/// интерфейсе и выполняет у всех как глобальную команду, где Faction.OfPlayer =
	/// фракция отправителя — по ней проверяем, что «from» действительно его фракция
	/// (нельзя принять предложение или объявить войну за другого игрока).
	/// </summary>
	[SyncMethod]
	public static void SyncDiploAction(Faction from, Faction to, int action)
	{
		RafRelationsData data = Get();
		if (data == null || from == null || to == null)
		{
			return;
		}
		if (RelationsUtil.InMultiplayer && Faction.OfPlayer != from)
		{
			Log.Warning("[RaidAnimalFix] Relations: команда от " + Faction.OfPlayer?.Name + " пытается действовать за " + from.Name + " — отклонено");
			return;
		}
		int now = RelationsUtil.NowWorldTicks();
		if (!data.CanDo(from, to, action, now, out var reason))
		{
			Log.Message("[RaidAnimalFix] Relations: действие " + action + " " + from.Name + " → " + to.Name + " отклонено: " + reason);
			return;
		}
		RafRelationPair p = data.GetOrCreatePair(from.loadID, to.loadID);
		Faction fa = RelationsUtil.FactionById(p.a);
		Faction fb = RelationsUtil.FactionById(p.b);
		switch (action)
		{
		case RafDiploAction.ProposeAlliance:
		case RafDiploAction.ProposePeace:
		{
			int target = (action == RafDiploAction.ProposeAlliance) ? 1 : 0;
			// Встречное такое же предложение = согласие.
			RafProposal theirs = data.FindProposal(to.loadID, from.loadID);
			if (theirs != null && theirs.kind == target)
			{
				data.SetKind(p, fa, fb, (RafRelationKind)target, now);
				NotifyBoth(from, to, (target == 1) ? "RAF_Relations_MsgAllianceFormed" : "RAF_Relations_MsgPeaceMade", MessageTypeDefOf.PositiveEvent);
				break;
			}
			data.proposals.Add(new RafProposal
			{
				from = from.loadID,
				to = to.loadID,
				kind = target,
				tick = now
			});
			RelationsUtil.NotifyLocal(to, ((target == 1) ? "RAF_Relations_MsgAllianceProposed" : "RAF_Relations_MsgPeaceProposed").Translate(from.Name), MessageTypeDefOf.NeutralEvent);
			RelationsUtil.NotifyLocal(from, "RAF_Relations_MsgProposalSent".Translate(to.Name), MessageTypeDefOf.SilentInput);
			break;
		}
		case RafDiploAction.DeclareWar:
		case RafDiploAction.BreakAlliance:
			p.pendingKind = (action == RafDiploAction.DeclareWar) ? 2 : 0;
			p.pendingBy = from.loadID;
			p.pendingAt = now + PendingDelayTicks;
			data.proposals.RemoveAll((RafProposal pr) => (pr.from == p.a && pr.to == p.b) || (pr.from == p.b && pr.to == p.a));
			RelationsUtil.NotifyLocal(null, ((action == RafDiploAction.DeclareWar) ? "RAF_Relations_MsgWarDeclared" : "RAF_Relations_MsgAllianceBreaking").Translate(from.Name, to.Name, PendingDelayTicks.ToStringTicksToPeriod()), (action == RafDiploAction.DeclareWar) ? MessageTypeDefOf.ThreatBig : MessageTypeDefOf.NegativeEvent);
			break;
		case RafDiploAction.Accept:
		{
			RafProposal pr2 = data.FindProposal(to.loadID, from.loadID);
			RafRelationKind k = (RafRelationKind)pr2.kind;
			data.SetKind(p, fa, fb, k, now);
			NotifyBoth(from, to, (k == RafRelationKind.Ally) ? "RAF_Relations_MsgAllianceFormed" : "RAF_Relations_MsgPeaceMade", MessageTypeDefOf.PositiveEvent);
			break;
		}
		case RafDiploAction.Decline:
			data.proposals.Remove(data.FindProposal(to.loadID, from.loadID));
			RelationsUtil.NotifyLocal(to, "RAF_Relations_MsgProposalDeclined".Translate(from.Name), MessageTypeDefOf.NegativeEvent);
			break;
		case RafDiploAction.Withdraw:
			data.proposals.Remove(data.FindProposal(from.loadID, to.loadID));
			RelationsUtil.NotifyLocal(to, "RAF_Relations_MsgProposalWithdrawn".Translate(from.Name), MessageTypeDefOf.NeutralEvent);
			break;
		case RafDiploAction.CancelPending:
			p.pendingKind = -1;
			p.pendingAt = -1;
			p.pendingBy = -1;
			RelationsUtil.NotifyLocal(null, "RAF_Relations_MsgPendingCancelled".Translate(from.Name, to.Name), MessageTypeDefOf.PositiveEvent, onlyFactions: new Faction[2] { from, to });
			break;
		}
	}

	private static void NotifyBoth(Faction x, Faction y, string key, MessageTypeDef type)
	{
		RelationsUtil.NotifyLocal(null, key.Translate(x.Name, y.Name), type, onlyFactions: new Faction[2] { x, y });
	}

	/// <summary>
	/// Хозяин меняет разрешение гостя (guestLoadId = -1 — значение для «всех союзников»;
	/// perm = -1 — сбросить явную запись к умолчаниям). Проверка host == Faction.OfPlayer
	/// команды — менять разрешения своей карты может только сам хозяин.
	/// </summary>
	[SyncMethod]
	public static void SyncSetGuestPerm(Faction host, int guestLoadId, int perm, bool value)
	{
		RafRelationsData data = Get();
		if (data == null || !MpContext.IsRealPlayerFaction(host))
		{
			return;
		}
		if (RelationsUtil.InMultiplayer && Faction.OfPlayer != host)
		{
			Log.Warning("[RaidAnimalFix] Relations: " + Faction.OfPlayer?.Name + " пытается менять разрешения " + host.Name + " — отклонено");
			return;
		}
		Faction guest = null;
		if (guestLoadId != -1)
		{
			guest = RelationsUtil.FactionById(guestLoadId);
			if (!MpContext.IsRealPlayerFaction(guest) || guest == host)
			{
				return;
			}
		}
		RafGuestPerms entry = data.FindPerms(host.loadID, guestLoadId);
		if (perm < 0)
		{
			if (entry != null)
			{
				data.perms.Remove(entry);
			}
			return;
		}
		if (perm > 3)
		{
			return;
		}
		if (entry == null)
		{
			entry = new RafGuestPerms
			{
				host = host.loadID,
				guest = guestLoadId
			};
			// Новая запись стартует с текущих эффективных значений, чтобы
			// переключение одного флажка не сбрасывало остальные.
			for (int i = 0; i < 4; i++)
			{
				bool cur = (guest == null) || data.EffectivePermIgnoringWar(host, guest, (RafGuestPerm)i);
				entry.Set((RafGuestPerm)i, cur);
			}
			data.perms.Add(entry);
		}
		entry.Set((RafGuestPerm)perm, value);
	}

	/// <summary>Без учёта войны (война запрещает всё поверх этого).</summary>
	public bool EffectivePermIgnoringWar(Faction host, Faction guest, RafGuestPerm p)
	{
		RafGuestPerms e = FindPerms(host.loadID, guest.loadID);
		if (e != null)
		{
			return e.Get(p);
		}
		if (KindOf(host, guest) == RafRelationKind.Ally)
		{
			RafGuestPerms all = FindPerms(host.loadID, -1);
			return all == null || all.Get(p);
		}
		return false;
	}
}

/// <summary>Публичный API для других фич (гейтинг по союзу/войне).</summary>
public static class RelationsApi
{
	/// <summary>Две разные фракции реальных игроков в союзе (по дипломатии мода).</summary>
	public static bool AreAllied(Faction a, Faction b)
	{
		return Kind(a, b) == RafRelationKind.Ally;
	}

	/// <summary>Две разные фракции реальных игроков воюют (война уже вступила в силу).</summary>
	public static bool AtWar(Faction a, Faction b)
	{
		return Kind(a, b) == RafRelationKind.War;
	}

	public static RafRelationKind Kind(Faction a, Faction b)
	{
		if (a == null || b == null || a == b || !MpContext.IsRealPlayerFaction(a) || !MpContext.IsRealPlayerFaction(b))
		{
			return RafRelationKind.Neutral;
		}
		RafRelationsData data = RafRelationsData.Get();
		return (data == null) ? RafRelationKind.Neutral : data.KindOf(a, b);
	}

	/// <summary>
	/// Разрешено ли гостю действие на карте хозяина. Для «не того случая»
	/// (не MP, своя карта, не игроки) — true: ограничение не применяется.
	/// </summary>
	public static bool GuestAllowed(Faction host, Faction guest, RafGuestPerm p)
	{
		if (host == null || guest == null || host == guest || !RelationsUtil.InMultiplayer)
		{
			return true;
		}
		if (!MpContext.IsRealPlayerFaction(host) || !MpContext.IsRealPlayerFaction(guest))
		{
			return true;
		}
		RafRelationsData data = RafRelationsData.Get();
		if (data == null)
		{
			return true;
		}
		if (data.KindOf(host, guest) == RafRelationKind.War)
		{
			return false;
		}
		return data.EffectivePermIgnoringWar(host, guest, p);
	}

	/// <summary>Гость = фракция, хозяин = владелец карты (оба реальные игроки, разные).</summary>
	public static bool IsGuestOn(Map map, Faction guest, out Faction host)
	{
		host = map?.ParentFaction;
		return host != null && guest != null && host != guest && RelationsUtil.InMultiplayer && MpContext.IsRealPlayerFaction(host) && MpContext.IsRealPlayerFaction(guest);
	}
}

/// <summary>Общие помощники фичи Relations.</summary>
internal static class RelationsUtil
{
	/// <summary>
	/// &gt; 0 только внутри нашего ApplyVanilla — блокировка ванильных изменений
	/// отношений игроков пропускает лишь наш путь. Транзиентный счётчик вызова,
	/// не состояние игры.
	/// </summary>
	[ThreadStatic]
	public static int applyDepth;

	/// <summary>Быстрая проверка (MP API, без рефлексии) — для горячих путей.</summary>
	public static bool InMultiplayer
	{
		get
		{
			try
			{
				return MP.enabled && MP.IsInMultiplayer;
			}
			catch
			{
				return false;
			}
		}
	}

	public static int NowWorldTicks()
	{
		if (MpMapTime.TryGetWorldTicks(out var ticks))
		{
			return ticks;
		}
		return (Find.TickManager != null) ? Find.TickManager.TicksGame : 0;
	}

	public static Faction FactionById(int loadId)
	{
		if (loadId < 0 || Find.FactionManager == null)
		{
			return null;
		}
		List<Faction> all = Find.FactionManager.AllFactionsListForReading;
		for (int i = 0; i < all.Count; i++)
		{
			if (all[i].loadID == loadId)
			{
				return all[i];
			}
		}
		return null;
	}

	/// <summary>Фракция локального игрока — ТОЛЬКО для UI и решения «показывать ли сообщение».</summary>
	public static Faction LocalFaction()
	{
		try
		{
			if (InMultiplayer)
			{
				return MP.RealPlayerFaction;
			}
		}
		catch
		{
		}
		return Faction.OfPlayer;
	}

	public static List<Faction> RealPlayerFactions()
	{
		List<Faction> list = new List<Faction>();
		if (Find.FactionManager == null)
		{
			return list;
		}
		List<Faction> all = Find.FactionManager.AllFactionsListForReading;
		for (int i = 0; i < all.Count; i++)
		{
			if (MpContext.IsRealPlayerFaction(all[i]))
			{
				list.Add(all[i]);
			}
		}
		return list;
	}

	/// <summary>
	/// Локальное сообщение (каждый клиент решает сам, показывать ли его). Не меняет
	/// состояние игры, поэтому чтение RealPlayerFaction здесь безопасно.
	/// audience — конкретная фракция; onlyFactions — набор фракций; оба null — все игроки.
	/// Цель сообщения — поселение получателя: так фильтр сообщений мода считает
	/// его «своим», даже если команду отправил другой игрок.
	/// 3.0.947: только перегрузка Messages.Message(string, ..., historical: false) —
	/// для неё MP выдаёт локальный ID; new Message(...) брал ID из синхронного
	/// счётчика у части игроков (SyncDiploAction, тик мира) → «Trace hashes don't match».
	/// </summary>
	public static void NotifyLocal(Faction audience, string text, MessageTypeDef type, Faction[] onlyFactions = null)
	{
		try
		{
			Faction me = LocalFaction();
			if (me == null || MpContext.IsSpectator(me))
			{
				return;
			}
			if (audience != null && me != audience)
			{
				return;
			}
			if (onlyFactions != null && Array.IndexOf(onlyFactions, me) < 0)
			{
				return;
			}
			Messages.Message("[RaidAnimalFix] " + text, OwnLookTarget(me), type, historical: false);
		}
		catch (Exception ex)
		{
			CaravanLeaveHelper.LogErrorThrottled("RelationsUtil.NotifyLocal", ex);
		}
	}

	public static LookTargets OwnLookTarget(Faction me)
	{
		List<Settlement> settlements = Find.WorldObjects.Settlements;
		for (int i = 0; i < settlements.Count; i++)
		{
			if (settlements[i].Faction == me)
			{
				return new LookTargets(settlements[i]);
			}
		}
		return LookTargets.Invalid;
	}

	private static void Target(RafRelationKind kind, out FactionRelationKind vanilla, out int goodwill)
	{
		switch (kind)
		{
		case RafRelationKind.Ally:
			vanilla = FactionRelationKind.Ally;
			goodwill = 100;
			break;
		case RafRelationKind.War:
			vanilla = FactionRelationKind.Hostile;
			goodwill = -100;
			break;
		default:
			vanilla = FactionRelationKind.Neutral;
			goodwill = 0;
			break;
		}
	}

	public static bool VanillaMatches(Faction a, Faction b, RafRelationKind kind)
	{
		Target(kind, out var vk, out var gw);
		FactionRelation ra = a.RelationWith(b, allowNull: true);
		FactionRelation rb = b.RelationWith(a, allowNull: true);
		return ra != null && rb != null && ra.kind == vk && rb.kind == vk && ra.baseGoodwill == gw && rb.baseGoodwill == gw;
	}

	/// <summary>
	/// Выставляет ванильные отношения в обе стороны. Фракции игроков имеют
	/// goodwill, поэтому SetRelationDirect для них не работает (Log.Error) —
	/// пишем kind и baseGoodwill напрямую, согласованно с порогами
	/// CheckKindThresholds (100 / 0 / -100), чтобы пересчёт ситуаций их не сдвинул.
	/// Письма не шлём (canSendLetter=false): Notify_RelationKindChanged сравнивает
	/// с Faction.OfPlayer, а сообщения у нас свои, локальные.
	/// </summary>
	public static void ApplyVanilla(Faction a, Faction b, RafRelationKind kind)
	{
		if (a == null || b == null || a == b)
		{
			return;
		}
		Target(kind, out var vk, out var gw);
		applyDepth++;
		try
		{
			FactionRelation ra = a.RelationWith(b, allowNull: true);
			if (ra == null)
			{
				a.SetRelation(new FactionRelation(b, vk)
				{
					baseGoodwill = gw
				});
				ra = a.RelationWith(b, allowNull: true);
			}
			FactionRelation rb = b.RelationWith(a, allowNull: true);
			if (ra == null || rb == null)
			{
				return;
			}
			FactionRelationKind prevA = ra.kind;
			FactionRelationKind prevB = rb.kind;
			ra.kind = vk;
			ra.baseGoodwill = gw;
			rb.kind = vk;
			rb.baseGoodwill = gw;
			if (prevA != vk)
			{
				a.Notify_RelationKindChanged(b, prevA, canSendLetter: false, null, GlobalTargetInfo.Invalid, out var _);
			}
			if (prevB != vk)
			{
				b.Notify_RelationKindChanged(a, prevB, canSendLetter: false, null, GlobalTargetInfo.Invalid, out var _);
			}
		}
		catch (Exception ex)
		{
			Log.Error("[RaidAnimalFix] Relations.ApplyVanilla " + a.Name + " ↔ " + b.Name + ": " + ex);
		}
		finally
		{
			applyDepth--;
		}
	}
}
