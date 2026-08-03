using System;
using System.Numerics;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.Schema.Equipment;
using LibreLancer.World;

namespace LibreLancer.Server.Components;

/// <summary>
/// Server-authoritative movement and lifetime for countermeasures and mines.
/// These objects use the same networked physical-object path as missiles, but
/// their target selection and detonation rules are different.
/// </summary>
public sealed class SDeployableComponent : GameComponent
{
    public const double LaunchCollisionSafeTime = 1.0;

    public MunitionEquip Munition { get; }
    public GameObject Owner { get; }
    public bool IsMine { get; }

    private double totalTime;
    private GameObject? target;

    public SDeployableComponent(GameObject parent, MunitionEquip munition, GameObject owner, bool isMine) : base(parent)
    {
        Munition = munition;
        Owner = owner;
        IsMine = isMine;
    }

    public bool IsOwnerSafe => totalTime < Munition.Def switch
    {
        Countermeasure cm => cm.OwnerSafeTime,
        Mine mine => mine.OwnerSafeTime,
        _ => 0
    };

    public bool IsCollisionSafe => totalTime < LaunchCollisionSafeTime;

    public bool IsOwner(GameObject obj) => ReferenceEquals(obj, Owner);

    public override void Update(double time, GameWorld world)
    {
        totalTime += time;

        var physics = Parent.PhysicsComponent;
        if (physics?.Body == null)
        {
            return;
        }

        // Give the newly launched object one second to clear the owner's
        // collision volume, then restore normal physics collisions.
        physics.Collidable = !IsCollisionSafe;
        physics.Body.Collidable = !IsCollisionSafe;

        var lifetime = Munition.Def.Lifetime;
        if (lifetime > 0 && totalTime >= lifetime)
        {
            world.Server!.ExplodeMissile(Parent, false);
            return;
        }

        physics.Body.SetDamping(GetLinearDrag(), 0);

        if (!IsMine)
        {
            return;
        }

        var position = physics.Body.Position;
        if (FindDetonationTarget(world, position) != null)
        {
            world.Server!.ExplodeMissile(Parent, true);
            return;
        }

        target = FindNearestShip(world);

        if (target != null)
        {
            var targetPosition = GetNearestCollisionPoint(world, target, position);
            var direction = targetPosition - position;
            var distance = direction.Length();

            if (distance > 0.001f)
            {
                direction /= distance;
                var currentSpeed = physics.Body.LinearVelocity.Length();
                var speed = MathF.Min(GetTopSpeed(), currentSpeed + GetAcceleration() * (float)time);
                physics.Body.LinearVelocity = direction * speed;
                physics.Body.SetOrientation(QuaternionEx.LookAt(position, targetPosition));
            }
        }
    }

    private GameObject? FindNearestShip(GameWorld world)
    {
        var maxDistance = GetSeekDistance();
        var maxDistanceSquared = maxDistance > 0 ? maxDistance * maxDistance : float.PositiveInfinity;
        var origin = Parent.PhysicsComponent!.Body.Position;
        GameObject? closest = null;
        var closestDistanceSquared = maxDistanceSquared;

        foreach (var candidate in world.Objects)
        {
            if (candidate.Kind != GameObjectKind.Ship ||
                !candidate.Flags.HasFlag(GameObjectFlags.Exists) ||
                ReferenceEquals(candidate, Owner) ||
                candidate.Flags.HasFlag(GameObjectFlags.Player) ||
                candidate.PhysicsComponent?.Body == null)
            {
                continue;
            }

            var distanceSquared = Vector3.DistanceSquared(origin, candidate.PhysicsComponent.Body.Position);
            if (distanceSquared < closestDistanceSquared)
            {
                closestDistanceSquared = distanceSquared;
                closest = candidate;
            }
        }

        return closest;
    }

    private GameObject? FindDetonationTarget(GameWorld world, Vector3 position)
    {
        foreach (var candidate in world.Objects)
        {
            if (candidate.Kind != GameObjectKind.Ship ||
                !candidate.Flags.HasFlag(GameObjectFlags.Exists) ||
                candidate.PhysicsComponent?.Body == null ||
                (ReferenceEquals(candidate, Owner) && IsOwnerSafe))
            {
                continue;
            }

            // Players are intentionally excluded from target seeking, but
            // any ship (including a player) still detonates a nearby mine.
            if (IsWithinDetonationDistance(world, candidate, position))
            {
                return candidate;
            }
        }

        return null;
    }

    private bool IsWithinDetonationDistance(GameWorld world, GameObject target, Vector3 position)
    {
        var distance = GetDetonationDistance();
        if (distance < 0 || target.PhysicsComponent?.Body == null)
        {
            return false;
        }

        // SphereTest checks the actual collision shape of the target ship,
        // not its origin. This makes detonation_dist the distance from the
        // mine to the hull, rather than the distance to the ship's centre.
        if (world.Physics != null)
        {
            foreach (var hit in world.Physics.SphereTest(position, distance))
            {
                if (ReferenceEquals(hit?.Tag, target))
                {
                    return true;
                }
            }
        }

        // The broadphase query can miss a ship while its collision compound
        // is being rebuilt or transformed. Use the body's current bounds as
        // a deterministic fallback so the mine cannot pass through the hull
        // and continue toward the ship centre.
        var bounds = target.PhysicsComponent.Body.GetBoundingBox();
        var closest = Vector3.Clamp(position, bounds.Min, bounds.Max);
        return Vector3.DistanceSquared(position, closest) <= distance * distance;
    }

    private Vector3 GetNearestCollisionPoint(GameWorld world, GameObject target, Vector3 position)
    {
        var body = target.PhysicsComponent?.Body;
        if (body == null)
        {
            return target.WorldTransform.Position;
        }

        var toCenter = body.Position - position;
        var centerDistance = toCenter.Length();
        if (centerDistance > 0.001f && world.Physics != null &&
            world.Physics.PointRaycast(Parent.PhysicsComponent?.Body, position,
                toCenter / centerDistance, centerDistance, out var contactPoint,
                out var hit, out _) && ReferenceEquals(hit, body))
        {
            return contactPoint;
        }

        var bounds = body.GetBoundingBox();
        return Vector3.Clamp(position, bounds.Min, bounds.Max);
    }

    private float GetLinearDrag() => Munition.Def is Countermeasure cm
        ? cm.LinearDrag
        : Munition.Def is Mine mine ? mine.LinearDrag : 0;

    private float GetSeekDistance() => Munition.Def is Mine mine ? mine.SeekDist : 0;
    private float GetTopSpeed() => Munition.Def is Mine mine ? mine.TopSpeed : 0;
    private float GetAcceleration() => Munition.Def is Mine mine ? mine.Acceleration : 0;
    private float GetDetonationDistance() => Munition.Def is Mine mine ? mine.DetonationDist : 0;
}
