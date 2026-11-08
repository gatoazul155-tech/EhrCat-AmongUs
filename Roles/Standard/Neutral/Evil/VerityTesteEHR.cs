using AmongUs.GameOptions;
using EHR.Modules;
using EHR.Patches;
using Hazel;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static EHR.Options;

namespace EHR.Roles;

// Verity de teste: imortal no host. A morte é recusada ANTES de acontecer (como o Opportunist faz),
// então não existe corpo para ser reportado. Só para testar a ideia; a transformação ainda não está aqui.
internal class VerityTesteEHR : RoleBase
{
    public static bool On;

    // Enfurecimento: depois da ultima reuniao, sabota reator e O2 e conta 60s para vencer sozinho.
    private static float EnrageSeconds => EnrageTime.GetInt();

    public static OptionItem EnrageTime;
    public static OptionItem KillCooldown;
    public static OptionItem CanUseVents;
    public static OptionItem ArrowMode;
    public static OptionItem WinsWithCrewAfterTasks;

    private static readonly string[] ArrowDirs = ["→", "↗", "↑", "↖", "←", "↙", "↓", "↘"];
    private static readonly Dictionary<byte, Color32> ConfusedColors = [];
    private static readonly Dictionary<byte, int> FrozenDirs = [];
    private static long lastFreezeTS;
    private static long lastNotifyTS;
    private static bool enraged;
    private static long enrageStart;
    private static bool autoSkip;

    // Usado pelo CheckForEndVoting: funciona como se o host tivesse apertado F6.
    // Permite ao host registrar o "pular" automatico do Verity quando todos ja votaram.
    public static bool AllowAutoVote;

    public static bool TakeAutoSkip()
    {
        if (!autoSkip) return false;
        autoSkip = false;
        return true;
    }

    // Aviso "algo chega": None = nada agendado; senao, reunioes que faltam ate a proxima mensagem.
    private const int None = int.MinValue;
    private static int pendingMeetings = None;
    public static bool ImpostorExiled;
    public static bool ImpostorLeft;
    private static long lastSkinTS;
    public static bool EnrageEnded;
    private static bool commandUsed;

    // Chamado pelo /verity: agenda o aviso para daqui a 3 reunioes.
    // Retorna false se o /verity ja foi usado nesta partida.
    public static void OnAnyoneDied()
    {
        if (!TryGetAliveVerity(out PlayerControl verity)) return;
        verity.Notify("Alguém morreu.");
    }

    public static bool Schedule()
    {
        if (commandUsed) return false;
        commandUsed = true;
        int count = 3;
        if (count <= 0)
        {
            // 0 reunioes: enfurece na hora, sem esperar.
            pendingMeetings = None;
            if (!TryGetAliveVerity(out _)) return true;
            StartEnrage(closeMeeting: true);
            return true;
        }
        pendingMeetings = count;
        Logger.Info($"Aviso agendado: {count} reuniões", "VerityTesteEHR");
        return true;
    }

    // Chamado no inicio de cada reuniao (so o host).
    public static void OnMeetingStart()
    {
        if (AmongUsClient.Instance.AmHost) RefreshConfusedColors();
        // Na primeira reuniao, revela quem e o Verity para todos.
        if (AmongUsClient.Instance.AmHost && MeetingStates.FirstMeeting && TryGetAliveVerity(out PlayerControl firstVerity)) Utils.SendMessage($"O Verity é {firstVerity.GetRealName()}");
        if (AmongUsClient.Instance.AmHost && pendingMeetings == None && !enraged && TryGetAliveVerity(out _)) Utils.SendMessage(Utils.ColorString(Color.yellow, "O Verity deverá usar /cmd verity para enfurecer quando quiser."));
        if (!AmongUsClient.Instance.AmHost || pendingMeetings == None) return;

        pendingMeetings--;
        Logger.Info($"Reunião iniciada, faltam {pendingMeetings}", "VerityTesteEHR");
        if (pendingMeetings > 0)
        {
            Say(pendingMeetings == 1 ? "Algo chega na próxima reunião" : $"Algo chega daqui a {pendingMeetings} reuniões");
            return;
        }

        // Ultima contagem: o aviso e cancelado e o Verity enfurece.
        Say("Algo chegaria nesta reunião, mas foi cancelado.");
        pendingMeetings = None;
        if (TryGetAliveVerity(out _)) StartEnrage(closeMeeting: true);
    }

    // Enfurece o Verity: papel de matar, aviso e contagem.
    private static void StartEnrage(bool closeMeeting)
    {
        enraged = true;
        SetVerityKillRole(true);
        enrageStart = Utils.TimeStamp;
        Utils.SendMessage("Corram pelas suas vidas");
        Logger.Info("Verity enfurecido", "VerityTesteEHR");
        if (!closeMeeting) return;
        LateTask.New(() =>
        {
            if (MeetingHud.Instance == null) return;
            MeetingHudRpcClosePatch.AllowClose = true;
            MeetingHud.Instance.RpcClose();
        }, 0.5f, log: false);
    }

    // Como o modo Batata Quente: so o proprio Verity recebe a funcao de Impostor localmente
    // (desync), para o botao de matar aparecer mesmo para quem nao tem o mod.
    // Seta que aponta de quem ve para o Verity (usada durante o enfurecimento).
    public static string ArrowToVerity(PlayerControl seer)
    {
        if (!enraged || !TryGetAliveVerity(out PlayerControl verity) || verity.PlayerId == seer.PlayerId) return string.Empty;
        return $"<color=#ff0000>{ArrowDirs[GetDirIndex(seer, verity)]}</color>";
    }

    private static Color32 GetColorOf(byte id) => Main.PlayerColors.TryGetValue(id, out Color32 c) ? c : new Color32(255, 255, 255, 255);

    private static string ToHex(Color32 c) => ColorUtility.ToHtmlStringRGB(c);

    // Embaralha as cores dos jogadores (modo Confuso). Chamado a cada reuniao.
    private static void RefreshConfusedColors()
    {
        ConfusedColors.Clear();
        var ids = Main.CachedAlivePlayerControls().Select(p => p.PlayerId).ToList();
        var colors = ids.Select(GetColorOf).OrderBy(_ => Guid.NewGuid()).ToList();
        for (int i = 0; i < ids.Count; i++) ConfusedColors[ids[i]] = colors[i];
    }

    // Direcao da seta: 0 = direita, anda no sentido anti-horario (8 setas).
    private static int GetDirIndex(PlayerControl seer, PlayerControl target)
    {
        Vector2 diff = target.Pos() - seer.Pos();
        float angle = Mathf.Atan2(diff.y, diff.x) * Mathf.Rad2Deg;
        int sector = Mathf.RoundToInt(angle / 45f);
        return ((sector % 8) + 8) % 8;
    }

    private static string BuildArrows(PlayerControl seer)
    {
        int mode = ArrowMode.GetValue(); // 0 Cores, 1 Cinza, 2 Confuso, 3 Atrasado, 4 Desligado
        if (mode == 4) return string.Empty;

        if (mode == 2 && ConfusedColors.Count == 0) RefreshConfusedColors();

        long now = Utils.TimeStamp;
        bool refreshFrozen = mode == 3 && now - lastFreezeTS >= 5;
        if (refreshFrozen) lastFreezeTS = now;

        string result = string.Empty;
        foreach (PlayerControl p in Main.CachedAlivePlayerControls())
        {
            if (p.PlayerId == seer.PlayerId) continue;

            int dirIndex;
            if (mode == 3)
            {
                if (refreshFrozen || !FrozenDirs.ContainsKey(p.PlayerId)) FrozenDirs[p.PlayerId] = GetDirIndex(seer, p);
                dirIndex = FrozenDirs[p.PlayerId];
            }
            else dirIndex = GetDirIndex(seer, p);

            string hex = mode switch
            {
                1 or 3 => "888888",
                2 => ToHex(ConfusedColors.TryGetValue(p.PlayerId, out Color32 cc) ? cc : GetColorOf(p.PlayerId)),
                _ => ToHex(GetColorOf(p.PlayerId)),
            };
            result += $"<color=#{hex}>{ArrowDirs[dirIndex]}</color>";
        }
        return result;
    }

    // Como o modo Batata Quente: so o proprio Verity recebe a funcao de Impostor localmente
    // (desync), para o botao de matar aparecer mesmo para quem nao tem o mod.
    // Rastreador: o Verity ve a posicao de todos (setas), como o rastreador do Among Us.
    private static void UpdateTracker(PlayerControl verity)
    {
        foreach (PlayerControl p in Main.CachedAllPlayerControls())
        {
            if (p.PlayerId == verity.PlayerId) continue;
            if (p.IsAlive()) TargetArrow.Add(verity.PlayerId, p.PlayerId);
            else TargetArrow.Remove(verity.PlayerId, p.PlayerId);
        }
    }

    private static void SetVerityKillRole(bool enableKill)
    {
        PlayerControl verity = Main.CachedAlivePlayerControls().FirstOrDefault(p => p.Is(CustomRoles.VerityTesteEHR));
        if (verity == null) return;
        // Como o Speedrun: troca a base do papel, assim o botao aparece para todos (inclusive sem mod).
        verity.RpcChangeRoleBasis(enableKill ? CustomRoles.SerialKiller : CustomRoles.Crewmate);
        verity.SyncSettings();
        if (enableKill)
        {
            float kcd = KillCooldown.GetFloat();
            Main.AllPlayerKillCooldown[verity.PlayerId] = kcd;
            LateTask.New(() => verity.SetKillCooldown(kcd), 0.2f, log: false);
        }
    }

    private static void Say(string msg)
    {
        PlayerControl verity = Main.CachedAlivePlayerControls().FirstOrDefault(p => p.Is(CustomRoles.VerityTesteEHR));
        if (verity == null) return;
        verity.RpcSendChat(msg);
    }

    public override bool IsEnable => On;

    public override void Init()
    {
        On = false;
        lastFreezeTS = 0;
        lastNotifyTS = 0;
        FrozenDirs.Clear();
        ConfusedColors.Clear();
        enraged = false;
        enrageStart = 0;
        pendingMeetings = None;
        commandUsed = false;
        ImpostorExiled = false;
        ImpostorLeft = false;
        EnrageEnded = false;
        autoSkip = false;
    }

    public override void Add(byte playerId)
    {
        On = true;

        // Primeiro tira qualquer skin e deixa so o amarelo (cor 5), sem chapeu/pet/visor.
        // Depois de um tempo entrega o visor de sorriso.
        // Duas tarefas independentes (nao aninhadas): a primeira tira a skin, a segunda entrega o visor.
        LateTask.New(() =>
        {
            PlayerControl pc = PlayerControl.AllPlayerControls.ToArray().FirstOrDefault(p => p.PlayerId == playerId);
            if (pc == null) return;
            ApplyPlainYellow(pc);
        }, 2f, log: false);

        LateTask.New(() =>
        {
            PlayerControl pc = PlayerControl.AllPlayerControls.ToArray().FirstOrDefault(p => p.PlayerId == playerId);
            if (pc == null || !pc.IsAlive()) return;
            ApplyVisor(pc);
        }, 4f, log: false);
    }

    // Deixa o Verity so amarelo, sem skin, visor, chapeu ou pet.
    public static void ApplyPlainYellow(PlayerControl pc)
    {
        Utils.RpcChangeSkin(pc, new NetworkedPlayerInfo.PlayerOutfit().Set(pc.Data.PlayerName, 5, "hat_NoHat", "", "skin_None", "", ""));
    }

    // Visor de sorriso com papel (visor_bsb2_noteSmile), a partida toda.
    public static void ApplyVisor(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !pc) return;

        // Mesmo visor (visor_bsb2_noteSmile), mas enviado direto como troca de visor, sem trocar a skin inteira.
        pc.SetVisor(VerityVisorId, 5);
        pc.Data.DefaultOutfit.VisorSequenceId += 10;

        CustomRpcSender sender = CustomRpcSender.Create($"VerityVisor({pc.Data.PlayerName})", SendOption.Reliable);
        sender.AutoStartRpc(pc.NetId, RpcCalls.SetVisorStr)
            .Write(VerityVisorId)
            .Write(pc.GetNextRpcSequenceId(RpcCalls.SetVisorStr))
            .EndRpc();
        sender.SendMessage();
    }

    private const string VerityVisorId = "visor_bsb2_noteSmile";

    public static bool IsEnraged() => enraged;

    public static bool TryGetAliveVerity(out PlayerControl pc)
    {
        pc = Main.CachedAlivePlayerControls().FirstOrDefault(p => p.Is(CustomRoles.VerityTesteEHR));
        return pc != null;
    }

    // Tempo aparece em cima do nome de todos durante o enfurecimento.
    public static string GetEnrageSuffix()
    {
        if (!enraged) return string.Empty;
        long elapsed = enrageStart == 0 ? 0 : Utils.TimeStamp - enrageStart;
        long left = Math.Max(0, (long)EnrageSeconds - elapsed);
        return $"<color=#ffff00>Verity: {left}s</color>\n";
    }

    // Enquanto enfurecido, ninguem pode reportar corpo.
    public override bool CheckReportDeadBody(PlayerControl reporter, NetworkedPlayerInfo target, PlayerControl killer)
    {
        // O Verity nunca pode reportar corpo.
        return false;
    }

    // Mostra as setas do rastreador no nome do proprio Verity (durante a partida).
    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting || !seer.Is(CustomRoles.VerityTesteEHR) || seer.PlayerId != target.PlayerId || !seer.IsAlive()) return string.Empty;
        return BuildArrows(seer);
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        // So o Verity ganha botao de matar, e so depois de enfurecer.
        return pc.Is(CustomRoles.VerityTesteEHR) && enraged && pc.IsAlive();
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        if (target == null || killer == null) return true;
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return true;
        if (target.PlayerId != killer.PlayerId && target.Is(CustomRoles.VerityTesteEHR))
        {
            Logger.Info($"{killer.GetNameWithRole()} tentou matar o VerityTesteEHR; bloqueado", "VerityTesteEHR");
            return false;
        }
        return true;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {

        if (AmongUsClient.Instance.AmHost && pc.Is(CustomRoles.VerityTesteEHR) && pc.IsAlive() && !GameStates.IsMeeting && Utils.TimeStamp - lastNotifyTS >= 1)
        {
            lastNotifyTS = Utils.TimeStamp;
            Utils.NotifyRoles(SpecifyTarget: pc);
        }

        if (!enraged || !AmongUsClient.Instance.AmHost || !pc.Is(CustomRoles.VerityTesteEHR) || !pc.IsAlive()) return;
        if (GameStates.IsMeeting) return;

        long now = Utils.TimeStamp;
        if (enrageStart == 0) enrageStart = now;

        // Quando o tempo acaba, o enfurecimento termina (sem vitoria).
        if (now - enrageStart >= EnrageSeconds)
        {
            enraged = false;
            enrageStart = 0;
            EnrageEnded = true;
            SetVerityKillRole(false);
            Logger.Info("Tempo do enfurecimento acabou", "VerityTesteEHR");
            Say("O enfurecimento acabou.");
        }
    }

    public override void SetupCustomOption()
    {
        SetupRoleOptions(10990, TabGroup.NeutralRoles, CustomRoles.VerityTesteEHR);
        // Padrao 100% (indice 20 = Rate100) para o Verity aparecer sem /setrole.
        CustomRoleSpawnChances[CustomRoles.VerityTesteEHR].SetValue(20, false);

        EnrageTime = new IntegerOptionItem(69_214_001, "VerityEnrageTime", new(15, 100, 1), 60, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.VerityTesteEHR])
            .SetValueFormat(OptionFormat.Seconds);
        KillCooldown = new FloatOptionItem(69_214_002, "VerityKillCooldown", new(0f, 5f, 0.5f), 2f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.VerityTesteEHR])
            .SetValueFormat(OptionFormat.Seconds);
        CanUseVents = new BooleanOptionItem(69_214_003, "VerityCanUseVents", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.VerityTesteEHR]);
        ArrowMode = new StringOptionItem(69_214_004, "VerityArrowMode", ["VerityArrowMode.Colors", "VerityArrowMode.Gray", "VerityArrowMode.Confused", "VerityArrowMode.Delayed", "VerityArrowMode.Off"], 0, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.VerityTesteEHR]);
        WinsWithCrewAfterTasks = new BooleanOptionItem(69_214_005, "VerityWinsWithCrewAfterTasks", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.VerityTesteEHR]);
    }

    public static bool CanVerityUseVents() => CanUseVents.GetBool();
    public static bool WinsWithCrewAfterTasksEnabled() => WinsWithCrewAfterTasks.GetBool();
}
