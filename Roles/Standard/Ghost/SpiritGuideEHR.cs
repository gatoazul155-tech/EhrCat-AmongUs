using AmongUs.GameOptions;

namespace EHR.Roles;

public class SpiritGuideEHR : IGhostRole
{
    public Team Team => Team.Crewmate;
    public RoleTypes RoleTypes => RoleTypes.SpiritGuide;
    public int Cooldown => (int)CrewmateVanillaRoles.SpiritGuideCooldown.GetFloat();

    public void OnProtect(PlayerControl pc, PlayerControl target) { }

    public void OnAssign(PlayerControl pc) { }

    public void SetupCustomOption()
    {
        // Options are registered in CrewmateVanillaRoles (spawn chance and cooldown), under the Ghost Roles tab.
    }
}
