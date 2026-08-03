using System;
using System.Linq;
using System.Numerics;
using LibreLancer.Client.Components;
using LibreLancer.Data.GameData.Items;

namespace LibreLancer.World.Components;

/// <summary>
/// Common firing path for equipment which launches a physical munition rather
/// than a beam projectile.  The server owns the spawned object; the client
/// only queues the launch and plays the firing sound immediately.
/// </summary>
public abstract class MunitionLauncherComponent : WeaponComponent
{
    protected Hardpoint? HpFire;

    protected MunitionLauncherComponent(GameObject parent) : base(parent)
    {
    }

    public abstract MunitionEquip? Munition { get; }
    protected abstract float MuzzleVelocity { get; }
    protected abstract float PowerUsage { get; }
    protected abstract bool IsMine { get; }
    protected abstract string? UseAnimation { get; }

    protected override float TurnRate => 0;

    public override float MaxRange => Munition == null
        ? 0
        : Munition.Def.Lifetime * MuzzleVelocity;

    public override int IdsName => Parent.GetComponent<EquipmentComponent>()?.Equipment.IdsName ?? 0;

    public int AmmoCount
    {
        get
        {
            if (Munition == null || Parent.Parent == null ||
                !Parent.Parent.TryGetComponent<AbstractCargoComponent>(out var cargo))
            {
                return 0;
            }

            return cargo.GetCargo(0)
                .Where(x => x.EquipCRC == Munition.CRC && string.IsNullOrEmpty(x.Hardpoint))
                .Sum(x => x.Count);
        }
    }

    public bool UsesAmmo => Munition?.Def.RequiresAmmo == true;

    protected override bool OnFire(Vector3 point, GameWorld world, GameObject? target, bool fromServer)
    {
        if (Munition == null || Parent.Parent == null)
        {
            return false;
        }

        if (!TryGetFireTransform(out var transform))
        {
            return false;
        }

        if (Munition.Def.RequiresAmmo && AmmoCount <= 0)
        {
            return false;
        }

        // The server validates both resources. The local client performs the
        // same checks for immediate feedback; the server remains authoritative.
        var authoritativePower = world.Server != null || !fromServer;
        if (authoritativePower && PowerUsage > 0 &&
            Parent.Parent.TryGetComponent<PowerCoreComponent>(out var power))
        {
            if (power.CurrentEnergy < PowerUsage)
            {
                return false;
            }

            if (Munition.Def.RequiresAmmo &&
                (!Parent.Parent.TryGetComponent<AbstractCargoComponent>(out var cargo) ||
                 cargo.TryConsume(Munition) == 0))
            {
                return false;
            }

            power.CurrentEnergy -= PowerUsage;
        }
        else if (Munition.Def.RequiresAmmo &&
                 (!Parent.Parent.TryGetComponent<AbstractCargoComponent>(out var cargo) ||
                  cargo.TryConsume(Munition) == 0))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(UseAnimation))
        {
            Parent.AnimationComponent?.StartAnimation(UseAnimation, false);
        }
        if (Parent.TryGetComponent<CMuzzleFlashComponent>(out var muzzleFlash))
        {
            muzzleFlash.OnFired();
        }

        if (world.Server != null)
        {
            world.Server.FireDeployable(transform, Munition, MuzzleVelocity, Parent.Parent, IsMine);
        }
        else
        {
            var hardpoint = Parent.Attachment;
            if (hardpoint == null)
            {
                return false;
            }

            world.Projectiles.PlayProjectileSound(Parent.Parent, Munition.Def.OneShotSound,
                transform.Position, hardpoint.Name);
            world.Projectiles.QueueMissile(hardpoint.CRC, null);
        }

        CurrentCooldown = GetRefireDelay();
        return true;
    }

    protected abstract double GetRefireDelay();

    private bool TryGetFireTransform(out Transform3D transform)
    {
        transform = Transform3D.Identity;
        if (Parent.Parent == null || Parent.Attachment == null)
        {
            return false;
        }

        HpFire ??= Parent.GetHardpoints()
            .FirstOrDefault(x => x.Name.StartsWith("hpfire", StringComparison.OrdinalIgnoreCase));

        // The physics body is the authoritative transform while the ship is
        // turning. WorldTransform can still contain the previous simulation
        // step at the moment the fire command is handled, which offsets the
        // projectile from the hardpoint during continuous rotation.
        var shipTransform = Parent.Parent.PhysicsComponent?.Body is { } body
            ? new Transform3D(body.Position, body.Orientation)
            : Parent.Parent.WorldTransform;
        var mount = Parent.Attachment.Transform * shipTransform;
        transform = (HpFire?.Transform ?? Transform3D.Identity) * mount;
        return true;
    }
}

public sealed class CountermeasureLauncherComponent : MunitionLauncherComponent
{
    public CountermeasureEquipment Object { get; }

    public CountermeasureLauncherComponent(GameObject parent, CountermeasureEquipment equipment) : base(parent)
    {
        Object = equipment;
    }

    public override MunitionEquip? Munition => Object.Munition;
    protected override float MuzzleVelocity => Object.Def.MuzzleVelocity;
    protected override float PowerUsage => Object.Def.PowerUsage;
    protected override bool IsMine => false;
    protected override string? UseAnimation => Object.Def.UseAnimation;
    protected override double GetRefireDelay() => Object.Def.RefireDelay;
}

public sealed class MineLauncherComponent : MunitionLauncherComponent
{
    public MineDropperEquipment Object { get; }

    public MineLauncherComponent(GameObject parent, MineDropperEquipment equipment) : base(parent)
    {
        Object = equipment;
    }

    public override MunitionEquip? Munition => Object.Mine;
    protected override float MuzzleVelocity => Object.Def.MuzzleVelocity;
    protected override float PowerUsage => Object.Def.PowerUsage;
    protected override bool IsMine => true;
    protected override string? UseAnimation => Object.Def.UseAnimation;
    protected override double GetRefireDelay() => Object.Def.RefireDelay;
}
