using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer.Data.GameData;
using LibreLancer.Data.Schema.Pilots;
using LibreLancer.Data.Schema.Ships;
using LibreLancer.Data.Schema.Solar;
using LibreLancer.Missions;
using LibreLancer.Server.Ai;
using LibreLancer.World;
using LibreLancer.World.Components;
using Pilot = LibreLancer.Data.GameData.Pilot;

namespace LibreLancer.Server.Components
{
    public class SNPCComponent : SRepComponent
    {
        public Bodypart? CommHead;
        public Bodypart? CommBody;
        public Accessory? CommHelmet;

        public AiState? CurrentDirective;
        private NPCManager manager;
        public MissionRuntime? MissionRuntime;

        public Pilot? Pilot;
        public StateGraph? StateGraph;
        private readonly StateGraph? leaderStateGraph;
        private readonly StateGraph? escortStateGraph;

        private Random random = new();

        public float GetStateValue(StateGraphEntry row, StateGraphEntry column, float defaultVal = 0.0f)
        {
            if (StateGraph == null)
            {
                return defaultVal;
            }

            if ((int) row >= StateGraph.Data.Count)
            {
                return defaultVal;
            }

            var tableRow = StateGraph.Data[(int) row];

            if ((int) column >= tableRow.Length)
            {
                return defaultVal;
            }

            return tableRow[(int) column];
        }


        public SNPCComponent(GameObject parent, NPCManager manager, StateGraph stateGraph) : base(parent)
        {
            this.manager = manager;
            StateGraph = stateGraph;
            leaderStateGraph = stateGraph;
            if (stateGraph != null)
            {
                var escortDescription = new StateGraphDescription(stateGraph.Description.Name, "ESCORT");
                manager.World.Server.GameData.Items.Ini.StateGraphDb.Tables.TryGetValue(escortDescription,
                    out escortStateGraph);
            }
        }

        public void StartTradelane()
        {
            if (Parent.TryGetComponent<ShipPhysicsComponent>(out var component))
            {
                component.Active = false;
            }
        }

        public void Docked()
        {
            manager.Despawn(Parent, false);
        }

        public void Attack(GameObject tgt, GameWorld world)
        {
            SetState(new AiAttackState(tgt), world);
        }

        public void SetState(AiState? state, GameWorld world)
        {
            this.CurrentDirective = state;
            lastStateChangeReason = state == null ? "directive cleared" : $"directive set: {state.GetDebugInfo()}";
            lastBlockReason = state == null ? "none" : "directive active";
            state?.OnStart(Parent, world, this);
        }

        private Dictionary<AttackTarget, int> attackPref = new();
        private GameObject? stayInRangeObject;
        private Vector3 stayInRangePoint;
        private float stayInRangeRadius;

        public void SetPilot(Pilot? pilot)
        {
            Pilot = pilot;
            attackPref = new Dictionary<AttackTarget, int>();

            if (pilot == null)
            {
                return;
            }

            if (Pilot!.Job == null)
            {
                return;
            }

            for (int i = 0; i < Pilot.Job.AttackPreferences.Count; i++)
            {
                int weight = Pilot.Job.AttackPreferences.Count - i;

                attackPref[Pilot.Job.AttackPreferences[i].Target] = weight;
            }
        }

        public void SetStayInRange(GameObject? target, Vector3 point, float radius)
        {
            stayInRangeObject = target;
            stayInRangePoint = point;
            stayInRangeRadius = MathF.Max(0, radius);
        }

        public void ClearStayInRange()
        {
            stayInRangeObject = null;
            stayInRangePoint = Vector3.Zero;
            stayInRangeRadius = 0;
        }

        private bool TryGetStayInRangeCenter(out Vector3 center)
        {
            if (stayInRangeRadius <= 0)
            {
                center = Vector3.Zero;
                return false;
            }
            center = stayInRangeObject?.WorldTransform.Position ?? stayInRangePoint;
            return stayInRangeObject == null || stayInRangeObject.Flags.HasFlag(GameObjectFlags.Exists);
        }

        public static AttackTarget ClassifyAttackTarget(GameObject obj)
        {
            if (obj.TryGetComponent<ShipComponent>(out var ship))
            {
                return ship.Ship.ShipType switch
                {
                    ShipType.Fighter => AttackTarget.Fighter,
                    ShipType.Freighter => AttackTarget.Freighter,
                    ShipType.Gunboat => AttackTarget.Gunboat,
                    ShipType.Cruiser => AttackTarget.Cruiser,
                    ShipType.Transport => AttackTarget.Transport,
                    ShipType.Capital => AttackTarget.Capital,
                    _ => AttackTarget.Anything
                };
            }

            return obj.SystemObject?.Archetype?.Type switch
            {
                ArchetypeType.jump_gate or ArchetypeType.jump_hole or ArchetypeType.jumphole => AttackTarget.Jumpgate,
                ArchetypeType.weapons_platform => AttackTarget.Weapons_Platform,
                ArchetypeType.destroyable_depot => AttackTarget.Destroyable_Depot,
                ArchetypeType.tradelane_ring => AttackTarget.Tradelane,
                _ when obj.Kind == GameObjectKind.Solar => AttackTarget.Solar,
                _ => AttackTarget.Anything
            };
        }

        private int GetHostileWeight(GameObject obj)
        {
            if (manager.HostileClamp &&
                "player".Equals(obj.Nickname, StringComparison.OrdinalIgnoreCase))
            {
                if (manager.AttackingPlayer >= manager.PlayerEnemyClampMax)
                    return -100;
                if (manager.AttackingPlayer < manager.PlayerEnemyClampMin)
                    return 100;
            }

            var target = ClassifyAttackTarget(obj);
            if (attackPref.TryGetValue(target, out var weight))
                return weight;
            return attackPref.GetValueOrDefault(AttackTarget.Anything);
        }

        private double missileTimer;

        public bool ShouldFireMissiles(double time)
        {
            missileTimer -= time;

            if (missileTimer <= 0)
            {
                missileTimer = ValueWithVariance(Pilot?.Missile?.LaunchIntervalTime,
                    Pilot?.Missile?.LaunchVariancePercent);
                return true;
            }

            return false;
        }

        private float ValueWithVariance(float? value, float? variance)
        {
            if (value == null)
            {
                return 0;
            }

            var b = value.Value;
            var v = variance.HasValue ? random.NextFloat(-variance.Value, variance.Value) : 0;
            return b + (b * v);
        }

        private bool inBurst = false;
        private float burstTimer = 0;
        private float fireTimer = 0;
        private int fireCycle = 0; // Track cycles for weapon grouping
        private int weaponGroupIndex = 0; // Track which weapon group to fire

        public struct FireInfo
        {
            public bool ShouldFireRegular;
            public bool ShouldFireAutoTurrets;
        }

        public FireInfo RunFireTimers(float dt)
        {
            var fireInfo = new FireInfo { ShouldFireRegular = false, ShouldFireAutoTurrets = false };

            // Check if ship has auto-turret weapons
            bool hasAutoTurrets = false;

            if (Parent.TryGetComponent<WeaponControlComponent>(out var weapons))
            {
                foreach (var gun in Parent.GetChildComponents<GunComponent>())
                {
                    if (gun.Object.Def.AutoTurret)
                    {
                        hasAutoTurrets = true;
                        break;
                    }
                }
            }

            if (inBurst)
            {
                burstTimer -= dt;

                if (burstTimer <= 0)
                {
                    inBurst = false;
                    burstTimer = Pilot?.Gun?.FireNoBurstIntervalTime ?? 0;
                }
                else
                {
                    // Handle regular guns
                    fireTimer -= dt;

                    if (fireTimer <= 0)
                    {
                        var interval = Pilot?.Gun?.FireIntervalTime ?? 0;

                        if (interval == 0)
                        {
                            interval = 0.1f; // minimum interval for NPCs
                        }

                        fireTimer = ValueWithVariance(interval,
                            Pilot?.Gun?.FireIntervalVariancePercent);
                        fireInfo.ShouldFireRegular = true;

                        // Auto-turrets fire based on their interval timing
                        if (hasAutoTurrets)
                        {
                            fireCycle++;
                            // Use auto-turret interval timing from INI
                            float autoTurretInterval = Pilot?.Gun?.AutoTurretIntervalTime ?? 0.2f;

                            if (autoTurretInterval <= 0 || fireCycle >= Math.Max(1, (int) (autoTurretInterval / 0.1f)))
                            {
                                fireInfo.ShouldFireAutoTurrets = true;
                                fireCycle = 0; // Reset cycle counter
                            }
                        }
                    }
                }
            }
            else
            {
                burstTimer -= dt;

                if (burstTimer <= 0)
                {
                    inBurst = true;
                    burstTimer = ValueWithVariance(Pilot?.Gun?.FireBurstIntervalTime ?? 1f,
                        Pilot?.Gun?.FireBurstIntervalVariancePercent);
                    // Reset timer when starting new burst
                    fireTimer = 0;
                }
            }

            return fireInfo;
        }

        public void FireWeaponGroups(WeaponControlComponent weapons, FireInfo fireInfo, GameWorld world)
        {
            // Get all weapons and group them by type
            var regularGuns = new List<GunComponent>();
            var autoTurrets = new List<GunComponent>();

            foreach (var gun in Parent.GetChildComponents<GunComponent>())
            {
                if (gun.Object.Def.AutoTurret)
                {
                    autoTurrets.Add(gun);
                }
                else
                {
                    regularGuns.Add(gun);
                }
            }

            // Create separate aim points for different weapon types due to accuracy differences
            Vector3 regularAim = weapons.AimPoint; // Use existing aim point for regular guns
            Vector3 autoTurretAim = weapons.AimPoint; // Will be recalculated with more inaccuracy

            // If auto-turrets are firing, get a less accurate aim point
            if (fireInfo.ShouldFireAutoTurrets &&
                Parent.GetComponent<SelectedTargetComponent>()?.Selected is GameObject target)
            {
                autoTurretAim = GetAimPosition(target, weapons, true); // More inaccurate aim point
            }

            // Fire regular weapons in groups based on burst timing
            if (fireInfo.ShouldFireRegular && regularGuns.Count > 0)
            {
                // Use INI parameters to determine weapon grouping
                float burstInterval = Pilot?.Gun?.FireBurstIntervalTime ?? 1f;
                float fireInterval = Pilot?.Gun?.FireIntervalTime ?? 0.1f;
                float noBurstInterval = Pilot?.Gun?.FireNoBurstIntervalTime ?? 2f;

                // Determine weapon grouping strategy based on timing parameters
                int weaponsToFire;

                if (burstInterval < 0.3f)
                {
                    // Rapid fire - fire more weapons per burst
                    weaponsToFire = Math.Max(1, regularGuns.Count / 2); // 50% of weapons
                }
                else if (burstInterval < 1.0f)
                {
                    // Medium fire rate - fire moderate number of weapons
                    weaponsToFire = Math.Max(1, regularGuns.Count / 3); // 33% of weapons
                }
                else
                {
                    // Slow fire rate - fire fewer weapons per burst
                    weaponsToFire = Math.Max(1, regularGuns.Count / 4); // 25% of weapons
                }

                // Use weapon group cycling to distribute firing
                for (int i = 0; i < weaponsToFire && i < regularGuns.Count; i++)
                {
                    int weaponIndex = (weaponGroupIndex + i) % regularGuns.Count;
                    regularGuns[weaponIndex].Fire(regularAim, world);
                }

                // Advance weapon group for next firing cycle
                weaponGroupIndex = (weaponGroupIndex + weaponsToFire) % regularGuns.Count;
            }

            // Fire auto-turrets in groups with their own timing
            if (fireInfo.ShouldFireAutoTurrets && autoTurrets.Count > 0)
            {
                // Use auto-turret specific parameters for grouping
                float autoTurretBurstInterval = Pilot?.Gun?.AutoTurretBurstIntervalTime ?? 1f;

                // Auto-turrets typically fire fewer weapons per cycle
                int turretsToFire;

                if (autoTurretBurstInterval < 0.5f)
                {
                    turretsToFire = Math.Max(1, autoTurrets.Count / 2); // 50% for rapid auto-turrets
                }
                else
                {
                    turretsToFire = Math.Max(1, autoTurrets.Count / 4); // 25% for normal auto-turrets
                }

                for (int i = 0; i < turretsToFire && i < autoTurrets.Count; i++)
                {
                    int turretIndex = (fireCycle * turretsToFire + i) % autoTurrets.Count;
                    autoTurrets[turretIndex].Fire(autoTurretAim, world);
                }
            }
        }

        private Vector3 AddInaccuracy(Vector3 target, Vector3 myPos, float distance, float maxRange,
            bool isAutoTurret = false)
        {
            if (Pilot?.Gun == null || distance <= 0)
            {
                return target;
            }

            float angleDeg = Pilot.Gun.FireAccuracyConeAngle;

            if (angleDeg <= 0)
            {
                return target;
            }

            float cone = angleDeg * MathF.PI / 180f;

            Vector3 dir = Vector3.Normalize(target - myPos);

            Vector3 randomVec;

            do
            {
                randomVec = new Vector3(
                    random.NextFloat(-1f, 1f),
                    random.NextFloat(-1f, 1f),
                    random.NextFloat(-1f, 1f)
                );
            } while (randomVec.LengthSquared() < 0.01f);

            randomVec = Vector3.Normalize(randomVec);

            float dot = Vector3.Dot(dir, randomVec);
            float currentAngle = MathF.Acos(dot);

            if (currentAngle > cone)
            {
                float t = cone / currentAngle;
                randomVec = Vector3.Normalize(Vector3.Lerp(dir, randomVec, t));
            }

            return myPos + randomVec * distance;
        }


        private GameObject? lastShootAt;

        public Vector3 GetAimPosition(GameObject other, WeaponControlComponent weapons, bool isAutoTurret = false)
        {
            if (other.PhysicsComponent == null)
            {
                return other.WorldTransform.Position;
            }

            var myPos = Parent.PhysicsComponent!.Body.Position;
            var myVelocity = Parent.PhysicsComponent.Body.LinearVelocity;
            var otherPos = other.PhysicsComponent.Body.Position;
            var otherVelocity = other.PhysicsComponent.Body.LinearVelocity;
            var avgSpeed = weapons.GetAverageGunSpeed();
            var maxRange = weapons.GetGunMaxRange();

            if (Aiming.GetTargetLeading((otherPos - myPos), (otherVelocity - myVelocity), avgSpeed, out var t))
            {
                var predictedPos = otherPos + otherVelocity * t;
                var leadDist = Vector3.Distance(myPos, predictedPos);
                return AddInaccuracy(predictedPos, myPos, leadDist, maxRange, isAutoTurret);
            }

            var staticDist = Vector3.Distance(myPos, otherPos);
            return AddInaccuracy(otherPos, myPos, staticDist, maxRange, isAutoTurret);
        }

        private GameObject? GetHostileAndFire(double time, GameWorld world)
        {
            // Get hostile
            GameObject? shootAt = null;
            int shootAtWeight = -1000;
            float shootAtDistance = float.MaxValue;
            var myPos = Parent.WorldTransform.Position;
            var hasStayInRange = TryGetStayInRangeCenter(out var stayInRangeCenter);

            foreach (var other in world.SpatialLookup
                         .GetNearbyObjects(Parent, myPos, 5000))
            {
                if ((other.Flags & GameObjectFlags.Cloaked) == GameObjectFlags.Cloaked)
                {
                    continue;
                }

                if (other.TryGetComponent<STradelaneMoveComponent>(out _))
                {
                    continue;
                }

                if (!(Vector3.Distance(other.WorldTransform.Position, myPos) < 5000) ||
                    !IsHostileTo(other))
                {
                    continue;
                }

                if (hasStayInRange &&
                    Vector3.DistanceSquared(other.WorldTransform.Position, stayInRangeCenter) >
                    stayInRangeRadius * stayInRangeRadius)
                {
                    continue;
                }

                int weight = GetHostileWeight(other);
                var distance = Vector3.DistanceSquared(other.WorldTransform.Position, myPos);

                if (weight > shootAtWeight || weight == shootAtWeight && distance < shootAtDistance)
                {
                    shootAtWeight = weight;
                    shootAtDistance = distance;
                    shootAt = other;
                }
            }

            Parent.GetComponent<SelectedTargetComponent>()!.Selected = shootAt;

            // Shoot at hostile
            if (shootAt != null && Parent.TryGetComponent<WeaponControlComponent>(out var weapons))
            {
                if ("player".Equals(shootAt.Nickname, StringComparison.OrdinalIgnoreCase))
                {
                    manager.AttackingPlayer++;
                }

                var dist = Vector3.Distance(shootAt.WorldTransform.Position, myPos);

                var gunRange = weapons.GetGunMaxRange() * 0.95f;
                weapons.AimPoint = GetAimPosition(shootAt, weapons, false); // Regular guns aim

                var missileMax = weapons.GetMissileMaxRange();
                var missileRange = Pilot?.Missile?.LaunchRange ?? missileMax;

                if (missileMax < missileRange)
                {
                    missileRange = missileMax;
                }

                // Fire Missiles
                if ((Pilot?.Missile?.MissileLaunchAllowOutOfRange ?? false) ||
                    dist <= missileRange)
                {
                    missileTimer -= time;

                    if (missileTimer <= 0)
                    {
                        weapons.FireMissiles(world);
                        missileTimer = ValueWithVariance(Pilot?.Missile?.LaunchIntervalTime,
                            Pilot?.Missile?.LaunchVariancePercent);
                        missileTimer = Pilot?.Missile?.LaunchIntervalTime ?? 0;
                    }
                }

                // Fire guns
                if (dist < gunRange)
                {
                    var fireInfo = RunFireTimers((float) time);

                    if (fireInfo.ShouldFireRegular || fireInfo.ShouldFireAutoTurrets)
                    {
                        // Fire regular guns and auto-turrets separately based on their timers
                        FireWeaponGroups(weapons, fireInfo, world);
                    }
                }
            }
            else
            {
                // fireTimer = Pilot?.Gun?.FireIntervalTime ?? 0;
                // missileTimer = Pilot?.Missile?.LaunchIntervalTime ?? 0;
            }

            return shootAt;
        }

        private StateGraphEntry currentState = StateGraphEntry.NULL;
        private StateGraphEntry previousState = StateGraphEntry.NULL;

        private double timeInState = 0;
        private string lastTransitionTrace = "none";
        private string lastStateChangeReason = "initial";
        private string lastBlockReason = "none";

        // State graph manoeuvre state. The state graph selects the high-level
        // behaviour; the pilot blocks select the concrete style and direction.
        private string maneuverStyle = "";
        private string maneuverDirection = "";
        private bool buzzPassing;
        private double buzzPassStart;
        private Vector3 buzzPassDirection;
        private float buzzHeadDistance;
        private StrafeControls maneuverStrafe;
        private bool gunboatHasReference;
        private Vector3 gunboatReference;
        private Vector3 gunboatRunDirection;

        // Combat Buzz has a close attack pass followed by a separation pass.
        // These are deliberately tighter than the broad vanilla pilot hint so
        // an NPC does not immediately run from a target it spawned near.
        private const float BuzzBreakDistance = 100f;
        private const float BuzzReengageDistance = 400f;
        private const float GunboatReferenceRefreshDistance = 1000f;

        public string GetDebugInfo()
        {
            string ls = lastShootAt == null ? "none" : lastShootAt.Nickname ?? "no nickname";
            var maxRange = 0f;

            if (Parent.TryGetComponent<WeaponControlComponent>(out var wp))
            {
                maxRange = wp.GetGunMaxRange() * 0.95f;
            }

            bool physActive = false;

            if (Parent.TryGetComponent<ShipPhysicsComponent>(out var ps))
            {
                physActive = ps.Active;
            }

            var formation = "";

            if (Parent.Formation != null)
            {
                formation = Parent.Formation.ToString();
            }

            // Debug weapon counts
            int totalGuns = 0;
            int autoTurrets = 0;
            int regularGuns = 0;

            foreach (var gun in Parent.GetChildComponents<GunComponent>())
            {
                totalGuns++;

                if (gun.Object.Def.AutoTurret)
                {
                    autoTurrets++;
                }
                else
                {
                    regularGuns++;
                }
            }

            AutopilotBehaviors beh = AutopilotBehaviors.None;

            if (Parent.TryGetComponent<AutopilotComponent>(out var ap))
            {
                beh = ap.CurrentBehavior;
            }

            var directive = CurrentDirective?.GetDebugInfo() ?? "null";
            var directiveRunnerActive = Parent.TryGetComponent<DirectiveRunnerComponent>(out var directiveRunner) && directiveRunner.Active;
            var selectedTarget = Parent.GetComponent<SelectedTargetComponent>()?.Selected;
            var target = selectedTarget ?? lastShootAt;
            var targetLabel = target == null ? "none" : string.IsNullOrWhiteSpace(target.Nickname) ? $"#{target.NetID}" : $"{target.Nickname} #{target.NetID}";
            var graphWeights =
                $"Face={GetStateValue(currentState, StateGraphEntry.Face):0.###}, " +
                $"Trail={GetStateValue(currentState, StateGraphEntry.Trail):0.###}, " +
                $"Buzz={GetStateValue(currentState, StateGraphEntry.Buzz):0.###}, " +
                $"Evade={GetStateValue(currentState, StateGraphEntry.Evade):0.###}";

            // Show accuracy info for debugging
            float npcPower = Pilot?.Gun?.FireAccuracyPowerNpc ?? 0;
            float npcAngle = Pilot?.Gun?.FireAccuracyConeAngle ?? 0;

            return
                $"Autopilot: {beh}\nShooting At: {ls}\n" +
                $"NPC AI\n" +
                $"Target: {targetLabel}\nBlock Reason: {lastBlockReason}\n" +
                $"Directive: {directive}\nDirective Runner Active: {directiveRunnerActive}\n" +
                $"State: {currentState} (previous {previousState}, {timeInState:F2}s)\n" +
                $"Maneuver: {maneuverStyle} {maneuverDirection}\n" +
                $"State Change: {lastStateChangeReason}\nTransition Weights: {graphWeights}\n" +
                $"Transition Trace: {lastTransitionTrace}\n" +
                $"Max Range: {maxRange}\nPhys Active: {physActive}\n" +
                $"Weapons: {totalGuns} total ({regularGuns} regular, {autoTurrets} auto-turrets)\n" +
                $"Fire Timer: {fireTimer:F2}, Fire Cycle: {fireCycle}\n" +
                $"NPC Base Power: {npcPower} (higher=more inaccuracy)\n" +
                $"NPC Base Angle: {npcAngle}\n" +
                $"Accuracy: Regular=min 5.0, Auto-Turret=10x base power\n" +
                $"InBurst: {inBurst}\n{formation}";
        }

        private bool IsStateAvailable(StateGraphEntry state)
        {
            return state switch
            {
                StateGraphEntry.Buzz or StateGraphEntry.Trail or StateGraphEntry.Face or StateGraphEntry.Strafe => true,
                StateGraphEntry.Formation => CanUseFormationState(),
                StateGraphEntry.Goto => IsGunboat(),
                StateGraphEntry.Guide => Pilot?.Missile != null,
                StateGraphEntry.Flee => ShouldFlee(),
                _ => false
            };
        }

        private bool ShouldFlee()
        {
            if (Pilot?.Job == null || !Parent.TryGetComponent<SHealthComponent>(out var health) || health.MaxHealth <= 0)
                return false;

            var threshold = Pilot.Job.FleeWhenHullDamagedPercent;
            return threshold > 0 && health.CurrentHealth / health.MaxHealth <= threshold;
        }

        private bool IsGunboat() => Parent.TryGetComponent<ShipComponent>(out var ship) &&
                                     ship.Ship.ShipType == ShipType.Gunboat;

        private bool CanUseFormationState()
        {
            var formation = Parent.Formation;
            return formation != null && formation.LeadShip != Parent && formation.Contains(Parent);
        }

        private void RefreshFormationGraph()
        {
            var graph = CanUseFormationState() && escortStateGraph != null
                ? escortStateGraph
                : leaderStateGraph;
            if (graph != null)
                StateGraph = graph;
        }

        private bool ShouldHoldFormation()
        {
            if (!CanUseFormationState() || currentState != StateGraphEntry.Formation)
                return false;

            var formation = Pilot.Formation;
            if (formation == null)
                return false;

            if (Parent.TryGetComponent<SHealthComponent>(out var health) && health.MaxHealth > 0 &&
                formation.BreakFormationDamageTriggerPercent > 0 &&
                damageTaken / health.MaxHealth >= formation.BreakFormationDamageTriggerPercent)
                return false;

            return true;
        }

        private void EnterFormationState(AutopilotComponent autopilot, string reason)
        {
            if (!CanUseFormationState())
                return;

            if (currentState != StateGraphEntry.Formation)
                EnterState(StateGraphEntry.Formation, reason);
            autopilot.StartFormation();
        }

        // The state graph is a weighted directed graph, not a normalized matrix.
        // Select from all currently executable outgoing edges, preserving their
        // relative weights and leaving illegal/non-combat states out of the roll.
        private void Transition()
        {
            var from = currentState;
            var candidates = new List<(StateGraphEntry State, float Weight)>();
            float total = 0;

            for (var i = 0; i < (int)StateGraphEntry._Count; i++)
            {
                var state = (StateGraphEntry)i;
                var weight = GetStateValue(currentState, state);
                if (weight <= 0 || !IsStateAvailable(state))
                    continue;
                candidates.Add((state, weight));
                total += weight;
            }

            if (total <= 0)
            {
                lastTransitionTrace = "no executable outgoing edges";
                return;
            }

            var roll = random.NextSingle() * total;
            var cursor = 0f;
            foreach (var candidate in candidates)
            {
                cursor += candidate.Weight;
                if (roll > cursor)
                    continue;

                EnterState(candidate.State, $"weighted transition from {from}");
                lastTransitionTrace = $"roll={roll:0.###}/{total:0.###}; selected {candidate.State} ({candidate.Weight:0.###})";
                return;
            }

            // Rounding can put a float roll infinitesimally past the final sum.
            var fallback = candidates[^1];
            EnterState(fallback.State, $"weighted transition from {from}");
            lastTransitionTrace = $"rounding fallback selected {fallback.State}";
        }

        private string PickDirection(List<DirectionWeight>? weights, string fallback)
        {
            if (weights == null || weights.Count == 0)
                return fallback;

            float total = 0;
            foreach (var item in weights)
                total += MathF.Max(0, item.Weight);
            if (total <= 0)
                return fallback;

            var roll = random.NextSingle() * total;
            foreach (var item in weights)
            {
                roll -= MathF.Max(0, item.Weight);
                if (roll <= 0)
                    return item.Direction ?? fallback;
            }
            return weights[^1].Direction ?? fallback;
        }

        private string PickStyle(List<EvadeBreakStyle>? weights, string fallback)
        {
            if (weights == null || weights.Count == 0)
                return fallback;
            float total = 0;
            foreach (var item in weights)
                total += MathF.Max(0, item.Weight);
            if (total <= 0)
                return fallback;
            var roll = random.NextSingle() * total;
            foreach (var item in weights)
            {
                roll -= MathF.Max(0, item.Weight);
                if (roll <= 0)
                    return item.Style ?? fallback;
            }
            return weights[^1].Style ?? fallback;
        }

        private string PickStyle(List<DodgeStyle>? weights, string fallback)
        {
            if (weights == null || weights.Count == 0)
                return fallback;
            float total = 0;
            foreach (var item in weights)
                total += MathF.Max(0, item.Weight);
            if (total <= 0)
                return fallback;
            var roll = random.NextSingle() * total;
            foreach (var item in weights)
            {
                roll -= MathF.Max(0, item.Weight);
                if (roll <= 0)
                    return item.Style ?? fallback;
            }
            return weights[^1].Style ?? fallback;
        }

        private string PickStyle(List<HeadTowardsStyle>? weights, string fallback)
        {
            if (weights == null || weights.Count == 0)
                return fallback;
            float total = 0;
            foreach (var item in weights)
                total += MathF.Max(0, item.Weight);
            if (total <= 0)
                return fallback;
            var roll = random.NextSingle() * total;
            foreach (var item in weights)
            {
                roll -= MathF.Max(0, item.Weight);
                if (roll <= 0)
                    return item.Style ?? fallback;
            }
            return weights[^1].Style ?? fallback;
        }

        private string PickStyle(List<BuzzPassByStyle>? weights, string fallback)
        {
            if (weights == null || weights.Count == 0)
                return fallback;
            float total = 0;
            foreach (var item in weights)
                total += MathF.Max(0, item.Weight);
            if (total <= 0)
                return fallback;
            var roll = random.NextSingle() * total;
            foreach (var item in weights)
            {
                roll -= MathF.Max(0, item.Weight);
                if (roll <= 0)
                    return item.Style ?? fallback;
            }
            return weights[^1].Style ?? fallback;
        }

        private float evadeX = 0;
        private float evadeY = 0;
        private float evadeZ = 0;
        private bool evadeThrust = false;

        private void EnterState(StateGraphEntry e, string reason)
        {
            previousState = currentState;
            currentState = e;
            timeInState = 0;
            lastStateChangeReason = reason;
            maneuverStyle = "";
            maneuverDirection = "";
            maneuverStrafe = StrafeControls.None;
            buzzPassing = false;
            buzzPassStart = 0;
            buzzHeadDistance = 0;
            evadeX = evadeY = evadeZ = 0;
            evadeThrust = false;

            // State graph manoeuvres own direct steering. Leaving a stale Goto
            // active would make Autopilot overwrite the state inputs.
            if (Parent.TryGetComponent<AutopilotComponent>(out var autopilot))
                autopilot.Cancel();

            switch (e)
            {
                case StateGraphEntry.Evade:
                {
                    var evade = Pilot?.EvadeBreak;
                    maneuverStyle = PickStyle(evade?.StyleWeights, "sideways");
                    maneuverDirection = PickDirection(evade?.DirectionWeights, "left");
                    var turn = evade?.TurnThrottle ?? 1;
                    var roll = evade?.RollThrottle ?? 0;
                    SetDirectionControls(maneuverDirection, turn, roll, out evadeX, out evadeY, out evadeZ);
                    evadeThrust = maneuverStyle.Equals("outrun", StringComparison.OrdinalIgnoreCase);
                    break;
                }
                case StateGraphEntry.DrasticEvade:
                {
                    var dodge = Pilot?.EvadeDodge;
                    maneuverStyle = PickStyle(dodge?.DodgeStyleWeights, "corkscrew");
                    maneuverDirection = PickDirection(dodge?.DodgeDirectionWeights, "left");
                    SetDirectionControls(maneuverDirection, dodge?.DodgeTurnThrottle ?? 1,
                        dodge?.DodgeCorkscrewRollThrottle ?? 0, out evadeX, out evadeY, out evadeZ);
                    maneuverStrafe = DirectionToStrafe(maneuverDirection);
                    break;
                }
                case StateGraphEntry.Buzz:
                    maneuverStyle = PickStyle(Pilot?.BuzzHeadToward?.HeadTowardsStyleWeight, "straight_to");
                    maneuverDirection = PickDirection(Pilot?.BuzzHeadToward?.DodgeDirectionWeights, "right");
                    buzzHeadDistance = BuzzBreakDistance;
                    break;
                case StateGraphEntry.Strafe:
                    maneuverDirection = random.Next(0, 2) == 0 ? "left" : "right";
                    maneuverStrafe = DirectionToStrafe(maneuverDirection);
                    break;
                case StateGraphEntry.Goto when IsGunboat():
                    gunboatHasReference = false;
                    break;
            }
        }

        private static StrafeControls DirectionToStrafe(string direction) => direction.ToLowerInvariant() switch
        {
            "left" => StrafeControls.Left,
            "right" => StrafeControls.Right,
            "up" => StrafeControls.Up,
            "down" => StrafeControls.Down,
            _ => StrafeControls.None
        };

        private static void SetDirectionControls(string direction, float turn, float roll,
            out float pitch, out float yaw, out float outRoll)
        {
            pitch = yaw = outRoll = 0;
            switch (direction.ToLowerInvariant())
            {
                case "left":
                    yaw = -turn;
                    outRoll = roll;
                    break;
                case "right":
                    yaw = turn;
                    outRoll = -roll;
                    break;
                case "up":
                    pitch = -turn;
                    break;
                case "down":
                    pitch = turn;
                    break;
            }
        }

        private void ResetStateGraphState(string reason)
        {
            if (currentState != StateGraphEntry.NULL)
            {
                previousState = currentState;
            }

            currentState = StateGraphEntry.NULL;
            timeInState = 0;
            lastStateChangeReason = reason;
        }

        private double damageTimer = 3;
        private float damageTaken = 0;

        public void TakingDamage(float amount)
        {
            damageTimer = 3;
            damageTaken += amount;

            if (currentState is StateGraphEntry.Evade or StateGraphEntry.DrasticEvade ||
                !Parent.TryGetComponent<SHealthComponent>(out var health) || health.MaxHealth <= 0)
                return;

            var reaction = Pilot?.DamageReaction;
            if (reaction == null)
                return;

            var damagePercent = damageTaken / health.MaxHealth;
            var evadeWeight = GetStateValue(currentState, StateGraphEntry.Evade);
            var drasticWeight = GetStateValue(currentState, StateGraphEntry.DrasticEvade);
            var canEvade = reaction.EvadeBreakDamageTriggerPercent > 0 &&
                           damagePercent >= reaction.EvadeBreakDamageTriggerPercent && evadeWeight > 0 &&
                           Pilot?.EvadeBreak != null;
            var canDrastic = reaction.EvadeDodgeMoreDamageTriggerPercent > 0 &&
                             damagePercent >= reaction.EvadeDodgeMoreDamageTriggerPercent && drasticWeight > 0 &&
                             Pilot?.EvadeDodge != null;

            if (!canEvade && !canDrastic)
                return;

            var total = (canEvade ? evadeWeight : 0) + (canDrastic ? drasticWeight : 0);
            var chosen = canDrastic && random.NextSingle() * total >= (canEvade ? evadeWeight : 0)
                ? StateGraphEntry.DrasticEvade
                : StateGraphEntry.Evade;
            lastTransitionTrace =
                $"damage trigger: {damagePercent:P1}, evade={evadeWeight:0.###}, drastic={drasticWeight:0.###}; selected {chosen}";
            EnterState(chosen, $"damage reaction: {damagePercent:P1}");
        }

        private void SteerTowards(ShipSteeringComponent steering, Vector3 point, float throttle,
            float turnThrottle = 1, float roll = 0)
        {
            var local = Parent.InverseTransformPoint(point);
            if (local.LengthSquared() > 0.0001f)
                local = Vector3.Normalize(local);

            steering.InYaw = MathHelper.Clamp(local.X * turnThrottle, -1, 1);
            steering.InPitch = MathHelper.Clamp(-local.Y * turnThrottle, -1, 1);
            steering.InRoll = MathHelper.Clamp(roll, -1, 1);
            steering.InThrottle = MathHelper.Clamp(throttle, -1, 1);
        }

        private Vector3 TargetForward(GameObject target) =>
            Vector3.Transform(-Vector3.UnitZ, target.WorldTransform.Orientation);

        private void UpdateFace(GameObject target, ShipSteeringComponent steering)
        {
            var distance = Vector3.Distance(Parent.WorldTransform.Position, target.WorldTransform.Position);
            var engineKill = Pilot?.EngineKill;
            steering.EngineKill = engineKill is { FaceTime: > 0, MaxTargetDistance: > 0 } &&
                                  timeInState <= engineKill.FaceTime && distance <= engineKill.MaxTargetDistance;
            SteerTowards(steering, target.WorldTransform.Position, steering.EngineKill ? 0 : 1);
        }

        private void UpdateTrail(GameObject target, ShipSteeringComponent steering)
        {
            var trail = Pilot?.Trail;
            var distance = trail?.Distance ?? 150;
            var point = target.WorldTransform.Position - TargetForward(target) * distance;
            var actualDistance = Vector3.Distance(Parent.WorldTransform.Position, target.WorldTransform.Position);
            var throttle = actualDistance > distance * 1.2f ? 1 : actualDistance < distance * .75f ? -.25f : .35f;
            SteerTowards(steering, point, throttle, trail?.MaxTurnThrottle ?? 1);
        }

        private void UpdateBuzz(GameObject target, ShipSteeringComponent steering)
        {
            var head = Pilot?.BuzzHeadToward;
            var pass = Pilot?.BuzzPassBy;
            var targetPos = target.WorldTransform.Position;
            var distance = Vector3.Distance(Parent.WorldTransform.Position, targetPos);
            var headDistance = buzzHeadDistance > 0 ? buzzHeadDistance : BuzzBreakDistance;

            if (!buzzPassing && distance <= headDistance)
            {
                buzzPassing = true;
                buzzPassStart = timeInState;
                // A straight pass continues the ship's actual flight path.
                // A break-away pass recalculates its escape vector every frame,
                // so a pursuing target cannot make the NPC turn back towards it.
                buzzPassDirection = TargetForward(Parent);
                maneuverStyle = PickStyle(pass?.PassByStyleWeights, "break_away");
                maneuverDirection = PickDirection(pass?.BreakDirectionWeights, maneuverDirection);
                maneuverStrafe = DirectionToStrafe(maneuverDirection);
            }

            if (!buzzPassing)
            {
                var point = targetPos;
                var side = Vector3.Transform(Vector3.UnitX, target.WorldTransform.Orientation);
                if (maneuverStyle.Equals("slide", StringComparison.OrdinalIgnoreCase))
                    point += side * (maneuverDirection.Equals("left", StringComparison.OrdinalIgnoreCase) ? -headDistance * .35f : headDistance * .35f);
                else if (maneuverStyle.Equals("waggle", StringComparison.OrdinalIgnoreCase))
                    point += side * MathF.Sin((float)timeInState * 4) * headDistance * .25f;

                // Use the established autopilot turn controller for target
                // approach. Direct state controls have a different steering
                // convention and caused the approach to spiral away.
                Parent.GetComponent<AutopilotComponent>()!.GotoVec(point, GotoKind.GotoNoCruise,
                    head?.HeadTowardEngineThrottle ?? .8f, 0, false);
                steering.InRoll = head?.HeadTowardRollThrottle ?? 0;
                steering.CurrentStrafe = maneuverStyle.Equals("slide", StringComparison.OrdinalIgnoreCase)
                    ? DirectionToStrafe(maneuverDirection)
                    : StrafeControls.None;
                return;
            }

            var myPos = Parent.WorldTransform.Position;
            var away = myPos - targetPos;
            if (away.LengthSquared() < .001f)
                away = -buzzPassDirection;
            else
                away = Vector3.Normalize(away);

            var escapeDirection = maneuverStyle.Equals("straight_by", StringComparison.OrdinalIgnoreCase)
                ? buzzPassDirection
                : away;
            var pointAfterPass = myPos + escapeDirection * Math.Max(1000, pass?.DistanceToPassBy ?? 200);
            if (maneuverStyle.Equals("break_away", StringComparison.OrdinalIgnoreCase))
            {
                var side = Vector3.Transform(Vector3.UnitX, target.WorldTransform.Orientation);
                var sign = maneuverDirection.Equals("left", StringComparison.OrdinalIgnoreCase) ||
                           maneuverDirection.Equals("down", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
                if (maneuverDirection.Equals("up", StringComparison.OrdinalIgnoreCase) ||
                    maneuverDirection.Equals("down", StringComparison.OrdinalIgnoreCase))
                    side = Vector3.Transform(Vector3.UnitY, target.WorldTransform.Orientation);
                pointAfterPass += side * sign * Math.Max(500, pass?.DistanceToPassBy ?? 200);
            }
            Parent.GetComponent<AutopilotComponent>()!.GotoVec(pointAfterPass, GotoKind.GotoNoCruise, 1, 0, false);
            steering.InRoll = pass?.PassByRollThrottle ?? 0;
            // The break direction is already reflected in pointAfterPass. It
            // is not a lateral strafe command; applying it here made every
            // pass-by slide sideways for its entire duration.
            steering.CurrentStrafe = StrafeControls.None;

            var passElapsed = timeInState - buzzPassStart;
            var engineKill = Pilot?.EngineKill;
            steering.EngineKill = engineKill is { FaceTime: > 0, MaxTargetDistance: > 0 } &&
                                  passElapsed <= engineKill.FaceTime && distance <= engineKill.MaxTargetDistance;
            // Buzz has no independent afterburner field. The break manoeuvre's
            // existing afterburner delay is the data-driven gate for using the
            // thruster while opening separation.
            steering.Thrust = distance < headDistance &&
                               passElapsed >= (Pilot?.EvadeBreak?.AfterburnerDelay ?? float.MaxValue);
        }

        private void UpdateEvade(ShipSteeringComponent steering)
        {
            var evade = Pilot?.EvadeBreak;
            steering.InThrottle = maneuverStyle.Equals("reverse", StringComparison.OrdinalIgnoreCase) ? -.5f : 1;
            steering.InPitch = evadeX;
            steering.InYaw = evadeY;
            steering.InRoll = evadeZ;
            steering.Thrust = evadeThrust && timeInState >= (evade?.AfterburnerDelay ?? 0);
            steering.EngineKill = maneuverStyle.Equals("reverse", StringComparison.OrdinalIgnoreCase);
            steering.CurrentStrafe = maneuverStyle.Equals("sideways", StringComparison.OrdinalIgnoreCase)
                ? DirectionToStrafe(maneuverDirection)
                : StrafeControls.None;
        }

        private void UpdateDrasticEvade(ShipSteeringComponent steering)
        {
            var dodge = Pilot?.EvadeDodge;
            var phase = (float)timeInState * 8;
            var turn = dodge?.DodgeCorkscrewTurnThrottle ?? dodge?.DodgeTurnThrottle ?? 1;
            steering.InThrottle = 1;
            steering.CurrentStrafe = maneuverStrafe;

            if (maneuverStyle.Equals("corkscrew", StringComparison.OrdinalIgnoreCase))
            {
                steering.InPitch = MathF.Sin(phase) * turn;
                steering.InYaw = MathF.Cos(phase) * turn;
                steering.InRoll = MathF.Sin(phase) * (dodge?.DodgeCorkscrewRollThrottle ?? .5f);
            }
            else if (maneuverStyle.Equals("waggle", StringComparison.OrdinalIgnoreCase))
            {
                steering.InPitch = evadeX * MathF.Sin(phase);
                steering.InYaw = evadeY * MathF.Sin(phase);
                steering.InRoll = evadeZ;
            }
            else
            {
                steering.InPitch = evadeX;
                steering.InYaw = evadeY;
                steering.InRoll = evadeZ;
            }
        }

        private void UpdateStrafe(GameObject target, ShipSteeringComponent steering)
        {
            var strafe = Pilot?.Strafe;
            var distance = Vector3.Distance(Parent.WorldTransform.Position, target.WorldTransform.Position);
            var runAwayDistance = strafe?.RunAwayDistance ?? 300;
            SteerTowards(steering, target.WorldTransform.Position,
                distance < runAwayDistance ? 1 : strafe?.AttackThrottle ?? 1,
                strafe?.TurnThrottle ?? 1);
            steering.CurrentStrafe = maneuverStrafe;
            steering.Thrust = distance < runAwayDistance;
        }

        private void UpdateFlee(GameObject target, ShipSteeringComponent steering)
        {
            var away = Parent.WorldTransform.Position - target.WorldTransform.Position;
            if (away.LengthSquared() < .001f)
                away = -TargetForward(target);
            else
                away = Vector3.Normalize(away);
            SteerTowards(steering, Parent.WorldTransform.Position + away * 2000, 1);
            steering.Thrust = true;
        }

        private void UpdateGunboatRun(GameObject target, ShipSteeringComponent steering)
        {
            var myPosition = Parent.WorldTransform.Position;
            var targetPosition = target.WorldTransform.Position;
            var maximumDrift = MathF.Max(2000, Pilot?.Job?.CombatDriftDistance ?? 10000);
            var targetMoved = gunboatHasReference &&
                              Vector3.DistanceSquared(targetPosition, gunboatReference) >=
                              GunboatReferenceRefreshDistance * GunboatReferenceRefreshDistance;
            var overshot = gunboatHasReference &&
                           Vector3.DistanceSquared(myPosition, gunboatReference) > maximumDrift * maximumDrift;

            if (!gunboatHasReference || targetMoved || overshot)
            {
                gunboatReference = targetPosition;
                gunboatRunDirection = gunboatReference - myPosition;
                if (gunboatRunDirection.LengthSquared() < .001f)
                    gunboatRunDirection = TargetForward(Parent);
                else
                    gunboatRunDirection = Vector3.Normalize(gunboatRunDirection);
                gunboatHasReference = true;
            }

            // The reference stays fixed while the target remains close to it.
            // This makes a gunboat commit to a straight gun run rather than
            // continually orbiting a fast fighter. It turns back only once its
            // job-defined combat-drift leash has been exceeded.
            var runLength = MathHelper.Clamp(maximumDrift * .25f, 1500, 5000);
            var runPoint = gunboatReference + gunboatRunDirection * runLength;
            Parent.GetComponent<AutopilotComponent>()!.GotoVec(runPoint, GotoKind.GotoNoCruise, 1, 0, false);
            steering.CurrentStrafe = StrafeControls.None;
            steering.Thrust = false;
        }

        public override void Update(double time, GameWorld world)
        {
            if (!Parent.TryGetComponent<AutopilotComponent>(out var ap))
            {
                lastBlockReason = "missing autopilot";
                return;
            }

            if (ap.CurrentBehavior == AutopilotBehaviors.Undock)
            {
                lastBlockReason = "undocking";
                return; // no npc yet
            }

            damageTimer -= time;

            if (damageTimer < 0)
            {
                damageTimer = 0;
                damageTaken = 0;
            }

            CurrentDirective?.Update(Parent, world, this, time);

            var shootAt = GetHostileAndFire(time, world);
            lastShootAt = shootAt;
            RefreshFormationGraph();

            var runningDirective = Parent.TryGetComponent<DirectiveRunnerComponent>(out var directiveRunner) &&
                                   directiveRunner.Active;

            if (CurrentDirective != null || runningDirective)
            {
                if (CurrentDirective != null)
                {
                    lastBlockReason = "directive active";
                }
                else if (runningDirective)
                {
                    lastBlockReason = "directive runner active";
                }
                ResetStateGraphState(lastBlockReason);
                return;
            }

            if (shootAt == null)
            {
                gunboatHasReference = false;
                if (currentState == StateGraphEntry.Formation && CanUseFormationState())
                    EnterFormationState(ap, "combat ended; re-enter formation");
                else
                    ResetStateGraphState("no hostile target");
                lastBlockReason = "no hostile target";
                return;
            }

            if (CanUseFormationState())
            {
                if (ShouldHoldFormation())
                {
                    timeInState += time;
                    var maxTime = Pilot?.Formation?.FormationExitMaxTime ?? 0;
                    if (maxTime <= 0 || timeInState < maxTime)
                    {
                        EnterFormationState(ap, "formation graph state");
                        lastBlockReason = "formation graph state";
                        return;
                    }

                    ap.Cancel();
                    Transition();
                    if (currentState == StateGraphEntry.Formation)
                    {
                        EnterFormationState(ap, "formation graph reselected");
                        lastBlockReason = "formation graph reselected";
                        return;
                    }
                }
                else if (ap.CurrentBehavior == AutopilotBehaviors.Formation)
                {
                    // A follower starts with the escort graph. Its Formation
                    // column participates in the same weighted choice as all
                    // combat states; it is not a boolean policy flag.
                    Transition();
                    if (currentState == StateGraphEntry.Formation)
                    {
                        EnterFormationState(ap, "formation graph selected");
                        lastBlockReason = "formation graph selected";
                        return;
                    }
                    ap.Cancel();
                }
            }

            lastBlockReason = "none";

            var si = Parent.GetComponent<ShipSteeringComponent>()!;
            timeInState += time;

            bool canTransition = false;

            si.InThrottle = 0;
            si.InPitch = 0;
            si.InYaw = 0;
            si.InRoll = 0;
            si.Cruise = false;
            si.Thrust = false;
            si.EngineKill = false;
            si.CurrentStrafe = StrafeControls.None;

            switch (currentState)
            {
                case StateGraphEntry.NULL:
                    ap.Cancel();
                    canTransition = true;
                    break;
                case StateGraphEntry.Evade:
                    UpdateEvade(si);
                    canTransition = timeInState >= (Pilot?.EvadeBreak?.Time ?? 5);
                    break;
                case StateGraphEntry.DrasticEvade:
                    UpdateDrasticEvade(si);
                    canTransition = timeInState >= (Pilot?.EvadeDodge?.DodgeTime ?? 2);
                    break;
                case StateGraphEntry.Buzz:
                    UpdateBuzz(shootAt, si);
                    canTransition = buzzPassing &&
                                    timeInState - buzzPassStart >= (Pilot?.BuzzPassBy?.PassByTime ?? 2) &&
                                    Vector3.Distance(Parent.WorldTransform.Position, shootAt.WorldTransform.Position) >=
                                    BuzzReengageDistance;
                    break;
                case StateGraphEntry.Face:
                    UpdateFace(shootAt, si);
                    canTransition = timeInState >= Math.Max(1, Pilot?.EngineKill?.FaceTime ?? 3);
                    break;
                case StateGraphEntry.Trail:
                    UpdateTrail(shootAt, si);
                    canTransition = timeInState >= Math.Max(1, Pilot?.Trail?.BreakTime ?? 3);
                    break;
                case StateGraphEntry.Goto when IsGunboat():
                    UpdateGunboatRun(shootAt, si);
                    break;
                case StateGraphEntry.Strafe:
                    if (IsGunboat())
                    {
                        UpdateGunboatRun(shootAt, si);
                    }
                    else
                    {
                        UpdateStrafe(shootAt, si);
                        canTransition = timeInState >= 2;
                    }
                    break;
                case StateGraphEntry.Flee:
                    UpdateFlee(shootAt, si);
                    canTransition = timeInState >= (Pilot?.EvadeBreak?.Time ?? 5);
                    break;
                case StateGraphEntry.Guide:
                    UpdateFace(shootAt, si);
                    canTransition = timeInState >= 2;
                    break;
                default:
                    canTransition = true;
                    break;
            }

            if (canTransition)
            {
                Transition();
            }
        }

        public void DockWith(GameObject tgt, GameWorld world)
        {
            SetState(new AiDockState(tgt, GotoKind.Goto), world);
        }
    }
}
