using AmongUs.GameOptions;
using EHR.Modules;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EHR.Roles;

// Mudamorfo de teste (v1): tripulante. Usa o pet para se transformar na aparencia do jogador mais proximo.
// Enquanto estiver transformado, os impostores o veem como impostor e nao conseguem mata-lo.
// Quando volta ao normal, nao aparece mais como impostor.
internal class MudamorfoTesteEHR : RoleBase
{
    public static bool On;

    public static OptionItem TransformCooldown;
    public static OptionItem TransformDuration;

    private static readonly Dictionary<byte, NetworkedPlayerInfo.PlayerOutfit> OriginalOutfits = [];
    private static readonly Dictionary<byte, long> TransformEndTS = [];
    private static readonly Dictionary<byte, long> LastTransformTS = [];

    public override bool IsEnable => On;

    private const int Id = 646200;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.MudamorfoTesteEHR);

        TransformCooldown = new FloatOptionItem(Id + 1, "MudamorfoTransformCooldown", new(0f, 180f, 0.5f), 20f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.MudamorfoTesteEHR])
            .SetValueFormat(OptionFormat.Seconds);

        TransformDuration = new FloatOptionItem(Id + 2, "MudamorfoTransformDuration", new(1f, 60f, 1f), 10f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.MudamorfoTesteEHR])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        On = false;
        OriginalOutfits.Clear();
        TransformEndTS.Clear();
        LastTransformTS.Clear();
    }

    public override void Add(byte playerId)
    {
        On = true;
        LastTransformTS[playerId] = 0;
    }

    public static bool IsTransformed(PlayerControl pc) => pc && TransformEndTS.ContainsKey(pc.PlayerId);

    public override void OnPet(PlayerControl pc)
    {
        if (!pc || !pc.IsAlive() || !AmongUsClient.Instance.AmHost) return;

        if (IsTransformed(pc))
        {
            Revert(pc);
            return;
        }

        if (Utils.TimeStamp - LastTransformTS.GetValueOrDefault(pc.PlayerId) < TransformCooldown.GetFloat()) return;

        PlayerControl target = Main.CachedAlivePlayerControls()
            .Where(p => p.PlayerId != pc.PlayerId)
            .OrderBy(p => Vector2.Distance(pc.transform.position, p.transform.position))
            .FirstOrDefault();
        if (!target) return;

        var o = target.Data.DefaultOutfit;
        OriginalOutfits.TryAdd(pc.PlayerId, pc.Data.DefaultOutfit);
        Utils.RpcChangeSkin(pc, new NetworkedPlayerInfo.PlayerOutfit().Set(pc.Data.PlayerName, o.ColorId, o.HatId, o.SkinId, o.VisorId, o.PetId, ""));

        TransformEndTS[pc.PlayerId] = Utils.TimeStamp + (long)TransformDuration.GetFloat();
        LastTransformTS[pc.PlayerId] = Utils.TimeStamp;
        pc.Notify(Utils.ColorString(Color.cyan, $"Você se transformou em {target.Data.PlayerName}"));
        Utils.NotifyRoles();
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !pc || !IsTransformed(pc)) return;
        // Na reuniao o Mudamorfo volta a aparencia normal.
        if (GameStates.IsMeeting || Utils.TimeStamp >= TransformEndTS[pc.PlayerId] || !pc.IsAlive()) Revert(pc);
    }

    private static void Revert(PlayerControl pc)
    {
        TransformEndTS.Remove(pc.PlayerId);
        if (OriginalOutfits.TryGetValue(pc.PlayerId, out var o))
        {
            Utils.RpcChangeSkin(pc, new NetworkedPlayerInfo.PlayerOutfit().Set(pc.Data.PlayerName, o.ColorId, o.HatId, o.SkinId, o.VisorId, o.PetId, ""));
            OriginalOutfits.Remove(pc.PlayerId);
        }
        Utils.NotifyRoles();
    }

    // Impostores nao conseguem matar o Mudamorfo enquanto ele esta transformado.
    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        if (IsTransformed(target) && killer && killer.IsImpostor())
        {
            killer.Notify(Utils.ColorString(Color.yellow, "Esse jogador não pode ser morto agora"));
            return false;
        }
        return base.OnCheckMurderAsTarget(killer, target);
    }

    // Enquanto transformado, o impostor ve o Mudamorfo como impostor.
    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (seer.IsImpostor() && IsTransformed(target)) return true;
        return base.KnowRole(seer, target);
    }
}
