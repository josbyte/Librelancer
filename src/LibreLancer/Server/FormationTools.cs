using System.Collections.Generic;
using System.Numerics;
using LibreLancer.Server.Components;
using LibreLancer.World;
using LibreLancer.World.Components;

namespace LibreLancer.Server;

public class FormationTools
{
    public static void EnterFormation(GameObject self, GameObject tgt, Vector3 offset)
    {
        List<GameObject>? carriedFollowers = null;
        if (self.Formation != null && self.Formation.LeadShip == self)
        {
            carriedFollowers = new List<GameObject>(self.Formation.Followers);
        }

        if (self.Formation != null && self.Formation != tgt.Formation)
            self.Formation.Remove(self);

        if (tgt.Formation == null)
        {
            tgt.Formation = new ShipFormation(tgt, self);
        }
        else
        {
            if (!tgt.Formation.Contains(self))
                tgt.Formation.Add(self);
        }
        self.Formation = tgt.Formation;
        if (offset != Vector3.Zero)
            tgt.Formation.SetShipOffset(self, offset);
        if (self.TryGetComponent<AutopilotComponent>(out var ap))
        {
            ap.StartFormation();
        }

        if (carriedFollowers == null)
            return;

        foreach (var follower in carriedFollowers)
        {
            if (follower == tgt || follower == self ||
                !follower.Flags.HasFlag(GameObjectFlags.Exists))
                continue;

            if (follower.Formation != null && follower.Formation != tgt.Formation)
                follower.Formation.Remove(follower);

            if (tgt.Formation != null && !tgt.Formation.Contains(follower))
                tgt.Formation.Add(follower);

            follower.Formation = tgt.Formation;
            if (follower.TryGetComponent<AutopilotComponent>(out var followerAp))
                followerAp.StartFormation();
        }
    }

    public static void MakeNewFormation(GameObject obj, GameWorld world, string formation, List<string?> others)
    {
        // TODO: Gross
        var formDef = world.Server!.Server.GameData.Items.GetFormation(formation);
        GameObject? player = null;
        bool playerLead = false;
        // Preserve player (required)
        if (obj.Formation != null)
        {
            if (obj.Formation.LeadShip.TryGetComponent<SPlayerComponent>(out _))
            {
                player = obj.Formation.LeadShip;
                playerLead = true;
            }
            else
            {
                foreach (var x in obj.Formation.Followers)
                {
                    if (x.TryGetComponent<SPlayerComponent>(out _))
                    {
                        player = x;
                        break;
                    }
                }
            }
        }

        obj.Formation?.Remove(obj);
        ShipFormation form;
        if (formDef != null)
        {
            form = new ShipFormation(playerLead ? player! : obj, formDef);
        }
        else
        {
            FLLog.Warning("Mission", $"Formation definition `{formation}` was not found. Falling back to a simple group.");
            form = new ShipFormation(playerLead ? player! : obj);
        }
        if (playerLead && player != null)
        {
            form.Add(obj);
        }

        obj.Formation = form;
        foreach (var tgt in others)
        {
            var o = world.GetObject(tgt);
            if (o == null)
            {
                continue;
            }

            if (tgt != null)
            {
                o.Formation?.Remove(o);
                form.Add(o);
                o.Formation = form;
            }

            if (o.TryGetComponent<AutopilotComponent>(out var ap))
            {
                ap.StartFormation();
            }
        }

        if (player != null && !obj.Formation.Contains(player))
        {
            form.Add(player);
            player.Formation = form;
        }
    }
}
