using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Missions;
using LibreLancer.Data.Schema.Solar;
using LibreLancer.Data.Schema.Ships;
using LibreLancer.Server.Components;
using LibreLancer.World;
using Zone = LibreLancer.Data.GameData.World.Zone;

namespace LibreLancer.Server;

[Flags]
public enum ArrivalTargets
{
    None = 0,
    Tradelane = 1 << 1,
    DockingRing = 1 << 2,
    JumpGate = 1 << 3,
    Station = 1 << 4,
    Capital = 1 << 5,
    Cruise = 1 << 6,
    Buzz = 1 << 7,
    Objects = Tradelane | DockingRing | JumpGate | Station | Capital,
    All = Objects | Cruise | Buzz
}

public partial class SpacePopulationManager
{
    private static ArrivalTargets TranslateArrival(EncounterArrival? arrival)
    {
        if (arrival == null)
            return ArrivalTargets.None;

        ArrivalTargets allow = ArrivalTargets.None;
        foreach (var a in arrival.Includes)
        {
            allow |= ConvertArrival(a);
        }

        ArrivalTargets disallow = ArrivalTargets.None;
        foreach (var a in arrival.Excludes)
        {
            disallow |= ConvertArrival(a);
        }

        return allow & ~disallow;
    }

    private static ArrivalTargets ConvertArrival(Arrivals arrival) => arrival switch
    {
        Arrivals.all => ArrivalTargets.All,
        Arrivals.object_all => ArrivalTargets.Objects,
        Arrivals.tradelane => ArrivalTargets.Tradelane,
        Arrivals.object_docking_ring => ArrivalTargets.DockingRing,
        Arrivals.object_jump_gate => ArrivalTargets.JumpGate,
        Arrivals.object_station => ArrivalTargets.Station,
        Arrivals.object_capital => ArrivalTargets.Capital,
        Arrivals.cruise => ArrivalTargets.Cruise,
        Arrivals.buzz => ArrivalTargets.Buzz,
        _ => ArrivalTargets.None
    };

    private bool TryFindSpawnLocation(
        ZoneState state,
        EncounterInfo info,
        GameObject[] players,
        float zoneCreationDistance,
        bool allowCloseSpawn,
        out SpawnLocation spawn)
    {
        spawn = default;
        var arrivalTargets = TranslateArrival(info.FormationDefinition?.Arrival);
        if (IsPatrolEncounter(state, info) &&
            (info.FormationDefinition?.Arrival == null ||
             (arrivalTargets & (ArrivalTargets.Cruise | ArrivalTargets.Buzz)) != 0) &&
            TryFindPatrolPathSpawnLocation(state, players, zoneCreationDistance, out spawn))
        {
            if (IsInsideRandomMissionNoSpawnZone(spawn.Position))
            {
                spawn = default;
                return false;
            }
            return true;
        }

        var preferObjectArrival = info.FormationDefinition?.Behavior == EncounterBehavior.trade;
        var avoidBaseArrivals = UsesMoorCapableShip(info);
        if (!preferObjectArrival &&
            TryFindFreeSpaceSpawnLocation(state.Zone, arrivalTargets, players, zoneCreationDistance, allowCloseSpawn, out spawn))
        {
            return true;
        }

        if (TryFindArrivalObject(
            state.Zone,
            arrivalTargets,
            players,
            zoneCreationDistance,
            allowCloseSpawn,
            avoidBaseArrivals,
            out var arrivalObject,
            out var arrivalIndex,
            out var arrivalLane,
            out var arrivalPosition,
            out var arrivalOrientation))
        {
            spawn = new SpawnLocation(
                arrivalPosition,
                arrivalOrientation,
                arrivalObject.Nickname,
                arrivalIndex,
                ArrivalLane: arrivalLane);
            return true;
        }

        if ((preferObjectArrival || avoidBaseArrivals) &&
            TryFindFreeSpaceSpawnLocation(
                state.Zone,
                arrivalTargets | ArrivalTargets.Cruise,
                players,
                zoneCreationDistance,
                allowCloseSpawn,
                out spawn))
        {
            return true;
        }

        return false;
    }

    private static bool IsPatrolEncounter(ZoneState state, EncounterInfo info)
    {
        var behavior = info.FormationDefinition?.Behavior ?? EncounterBehavior.wander;
        return behavior == EncounterBehavior.patrol_path || IsPatrol(state.Zone);
    }

    private static bool UsesMoorCapableShip(EncounterInfo info) =>
        info.Ships.Any(x => x.Ship.Ship?.MissionProperty is
            ShipMissionProperty.can_use_med_moors or ShipMissionProperty.can_use_large_moors);

    private bool TryFindFreeSpaceSpawnLocation(
        Zone zone,
        ArrivalTargets targets,
        GameObject[] players,
        float zoneCreationDistance,
        bool allowCloseSpawn,
        out SpawnLocation spawn)
    {
        spawn = default;
        if ((targets & (ArrivalTargets.Cruise | ArrivalTargets.Buzz)) == 0)
            return false;

        if (!TryFindSpawnPoint(zone, players, zoneCreationDistance, allowCloseSpawn, out var point))
            return false;

        spawn = new SpawnLocation(point, Quaternion.Identity, null, 0);
        return true;
    }

    private bool TryFindArrivalObject(
        Zone zone,
        ArrivalTargets targets,
        GameObject[] players,
        float zoneCreationDistance,
        bool allowCloseSpawn,
        bool avoidBaseArrivals,
        out GameObject arrivalObject,
        out int arrivalIndex,
        out string? arrivalLane,
        out Vector3 arrivalPosition,
        out Quaternion arrivalOrientation)
    {
        arrivalObject = null!;
        arrivalIndex = 0;
        arrivalLane = null;
        arrivalPosition = default;
        arrivalOrientation = Quaternion.Identity;
        if (players.Length == 0 || (targets & ArrivalTargets.Objects) == 0)
            return false;

        var maxDistance = Math.Max(
            zoneCreationDistance > 0 ? zoneCreationDistance : DefaultSpawnMaxDistance,
            DefaultSpawnMaxDistance);
        var minDistance = allowCloseSpawn ? 0 : DefaultSpawnMaxDistance;
        var searchDistance = Math.Max(maxDistance * 2.5f, DefaultPersistDistance);
        var bestScore = float.MaxValue;

        foreach (var obj in world.GameWorld.Objects)
        {
            if (string.IsNullOrWhiteSpace(obj.Nickname) ||
                obj.SystemObject == null ||
                !Alive(obj) ||
                !obj.TryGetComponent<SDockableComponent>(out var dockable) ||
                dockable.DockPoints.Length == 0 ||
                !ObjectMatchesArrival(obj, dockable, targets))
            {
                continue;
            }

            var isTradelane = dockable.Action.Kind == DockKinds.Tradelane;
            if (avoidBaseArrivals && dockable.Action.Kind == DockKinds.Base)
            {
                continue;
            }

            if (!isTradelane &&
                (!zone.ContainsPoint(obj.WorldTransform.Position) ||
                 IsInsideRandomMissionNoSpawnZone(obj.WorldTransform.Position)))
            {
                continue;
            }

            var dockIndex = 0;
            string? lane = null;
            var distance = DistanceToNearestPlayer(obj.WorldTransform.Position, players);
            var proximity = distance;
            var candidatePosition = obj.WorldTransform.Position;
            var candidateOrientation = obj.WorldTransform.Orientation;

            if (isTradelane)
            {
                if (!TryFindTradelaneDirection(
                        obj,
                        zone,
                        dockable,
                        players,
                        minDistance,
                        searchDistance,
                        out lane,
                        out candidatePosition,
                        out candidateOrientation,
                        out distance,
                        out proximity))
                {
                    continue;
                }
            }
            else if (!dockable.TryGetUndockIndex(out dockIndex, allowMoors: false))
            {
                continue;
            }

            if (distance < minDistance || distance > searchDistance)
                continue;

            var score = isTradelane
                ? proximity
                : distance + random.NextSingle() * 250f;
            if (score < bestScore)
            {
                bestScore = score;
                arrivalObject = obj;
                arrivalIndex = dockIndex;
                arrivalLane = lane;
                arrivalPosition = candidatePosition;
                arrivalOrientation = candidateOrientation;
            }
        }

        return arrivalObject != null;
    }

    private bool TryFindTradelaneDirection(
        GameObject source,
        Zone zone,
        SDockableComponent dockable,
        GameObject[] players,
        float minDistance,
        float searchDistance,
        out string lane,
        out Vector3 spawnPosition,
        out Quaternion spawnOrientation,
        out float distance,
        out float proximity)
    {
        lane = string.Empty;
        spawnPosition = default;
        spawnOrientation = Quaternion.Identity;
        distance = float.MaxValue;
        proximity = float.MaxValue;
        var bestDistance = float.MaxValue;
        var desiredSpawnDistance = (minDistance + searchDistance) * 0.5f;

        foreach (var candidate in new[]
        {
            (Lane: "HpRightLane", Target: dockable.Action.Target),
            (Lane: "HpLeftLane", Target: dockable.Action.TargetLeft)
        })
        {
            if (string.IsNullOrWhiteSpace(candidate.Target) ||
                source.GetHardpoint(candidate.Lane) is not { } sourceHardpoint ||
                world.GameWorld.GetObject(candidate.Target) is not { } target ||
                target.GetHardpoint(candidate.Lane) is not { } targetHardpoint)
            {
                continue;
            }

            var sourceTransform = sourceHardpoint.Transform * source.WorldTransform;
            var targetTransform = targetHardpoint.Transform * target.WorldTransform;
            var laneVector = targetTransform.Position - sourceTransform.Position;
            var laneLength = laneVector.Length();
            if (laneLength <= 1)
            {
                continue;
            }

            var laneDirection = laneVector / laneLength;
            foreach (var player in players)
            {
                var playerPosition = player.WorldTransform.Position;
                var playerAlongLane = Vector3.Dot(playerPosition - sourceTransform.Position, laneDirection);
                // A lane pointing away is skipped here; its opposite directed lane is
                // evaluated from the neighboring ring as another candidate.
                if (playerAlongLane <= 0)
                {
                    continue;
                }

                var closestAlongLane = Math.Clamp(playerAlongLane, 0, laneLength);
                var closestPoint = sourceTransform.Position + laneDirection * closestAlongLane;
                var laneProximity = Vector3.Distance(playerPosition, closestPoint);
                var availableBehindPlayer = closestAlongLane;
                if (availableBehindPlayer < minDistance)
                {
                    continue;
                }

                var spawnOffset = Math.Min(desiredSpawnDistance, availableBehindPlayer);
                var point = closestPoint - laneDirection * spawnOffset;
                var spawnDistance = Vector3.Distance(point, playerPosition);
                if (IsInsideRandomMissionNoSpawnZone(point) ||
                    !zone.ContainsPoint(point) ||
                    spawnDistance < minDistance ||
                    spawnDistance > searchDistance)
                {
                    continue;
                }

                if (laneProximity > proximity + 0.01f ||
                    laneProximity <= proximity + 0.01f && spawnDistance >= bestDistance)
                {
                    continue;
                }

                proximity = laneProximity;
                bestDistance = spawnDistance;
                lane = candidate.Lane;
                spawnPosition = point;
                spawnOrientation = QuaternionEx.LookAt(point, targetTransform.Position);
                distance = spawnDistance;
            }
        }

        return lane.Length > 0;
    }

    private static bool ObjectMatchesArrival(GameObject obj, SDockableComponent dockable, ArrivalTargets targets)
    {
        if (targets == ArrivalTargets.None)
            return false;

        var isTradelane = dockable.Action.Kind == DockKinds.Tradelane;
        if (isTradelane)
            return (targets & ArrivalTargets.Tradelane) != 0;

        var kinds = GetObjectArrivalTargets(obj, dockable);
        return (targets & kinds) != 0;
    }

    private static ArrivalTargets GetObjectArrivalTargets(GameObject obj, SDockableComponent dockable)
    {
        ArrivalTargets kinds = ArrivalTargets.None;

        var type = obj.SystemObject?.Archetype?.Type ?? ArchetypeType.NONE;
        switch (type)
        {
            case ArchetypeType.docking_ring:
                kinds |= ArrivalTargets.DockingRing;
                break;
            case ArchetypeType.jump_gate:
            case ArchetypeType.jump_hole:
            case ArchetypeType.jumphole:
                kinds |= ArrivalTargets.JumpGate;
                break;
            case ArchetypeType.station:
            case ArchetypeType.weapons_platform:
                kinds |= ArrivalTargets.Station;
                break;
        }

        if (IsCapitalObject(obj))
            kinds |= ArrivalTargets.Capital;

        return kinds;
    }

    private static bool IsCapitalObject(GameObject obj)
    {
        var nickname = obj.SystemObject?.Archetype?.Nickname ?? obj.Nickname ?? string.Empty;
        return nickname.Contains("capital", StringComparison.OrdinalIgnoreCase) ||
               nickname.Contains("battleship", StringComparison.OrdinalIgnoreCase) ||
               nickname.Contains("cruiser", StringComparison.OrdinalIgnoreCase) ||
               nickname.Contains("dreadnought", StringComparison.OrdinalIgnoreCase);
    }
}
