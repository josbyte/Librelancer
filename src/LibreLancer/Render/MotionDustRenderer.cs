// MIT License - Copyright (c) Callum McGing
// This file is subject to the terms and conditions defined in
// LICENSE, which is part of this source code package

using System;
using System.Numerics;
using LibreLancer.Fx;
using LibreLancer.Render.Materials;
using LibreLancer.Resources;

namespace LibreLancer.Render;

public class MotionDustRenderer
{
    private const int MaximumParticles = 50;
    private const float MinimumDepth = 60f;
    private const float MaximumDepth = 325f;
    private const float PointWidth = 0.005f;
    private const float MaximumTrail = 0.04f;
    private const float TrailStartSpeed = 0.2f;
    private const float FullTrailSpeed = 1f;
    private const float FullAlphaSpeed = 0.045f;
    private const float FrustumLimit = 1f;
    private const float SpawnEdge = 0.89f;
    private const float IncomingPanSpawnFraction = 0.5f;
    private const float PanCoherence = 0.55f;
    private const float MinimumPanSpeed = 0.01f;
    private const float MotionSmoothingSeconds = 0.12f;

    private readonly ParticleEffect effect;
    private readonly Vector3[] worldPositions;
    private readonly Vector2[] screenPositions;
    private readonly Vector2[] screenMotion;
    private readonly float[] screenDepths;
    private readonly float[] viewDepths;
    private readonly float[] particleTimes;
    private readonly float[] smoothedMotionSpeeds;
    private readonly Random random = new(1337);

    private FxBasicAppearance? appearance;
    private double previousTime = double.NaN;
    private bool initialized;
    private bool warnedMissingAppearance;
    private bool warnedMissingTexture;
    private Vector2 trailDirection = Vector2.UnitX;
    private Vector2 smoothedPanVelocity;
    private Vector3 previousCameraPosition;
    private bool hasPreviousCameraPosition;
    private float smoothedForwardSpeed;
    private float smoothedMeanSpeed;

    public int ParticleCount => worldPositions.Length;

    public MotionDustRenderer(ParticleEffect effect, int maxParticles)
    {
        this.effect = effect;
        var count = Math.Clamp(maxParticles, 1, MaximumParticles);
        worldPositions = new Vector3[count];
        screenPositions = new Vector2[count];
        screenMotion = new Vector2[count];
        screenDepths = new float[count];
        viewDepths = new float[count];
        particleTimes = new float[count];
        smoothedMotionSpeeds = new float[count];
    }

    public void Reset()
    {
        initialized = false;
        previousTime = double.NaN;
        trailDirection = Vector2.UnitX;
        smoothedPanVelocity = Vector2.Zero;
        hasPreviousCameraPosition = false;
        smoothedForwardSpeed = 0f;
        smoothedMeanSpeed = 0f;
    }

    public void Draw(ICamera camera, ParticleEffectPool pool, ResourceManager resources, double totalTime)
    {
        if (!LoadAppearance(resources))
        {
            return;
        }

        var elapsed = double.IsNaN(previousTime)
            ? 1f / 60f
            : (float)Math.Clamp(totalTime - previousTime, 1.0 / 240.0, 0.1);
        previousTime = totalTime;

        Matrix4x4.Invert(camera.View, out var inverseView);
        var cameraDelta = hasPreviousCameraPosition
            ? camera.Position - previousCameraPosition
            : Vector3.Zero;
        previousCameraPosition = camera.Position;
        hasPreviousCameraPosition = true;
        var cameraForward = Vector3.Normalize(
            Vector3.TransformNormal(-Vector3.UnitZ, inverseView));
        var forwardDistance = Vector3.Dot(cameraDelta, cameraForward);
        if (!initialized)
        {
            Initialize(camera, inverseView);
        }

        var averagePanMotion = UpdateProjection(
            camera.ViewProjection,
            forwardDistance,
            out var meanMotion,
            out var meanPanMotion);
        var inverseElapsed = 1f / elapsed;
        var coherentPan = meanPanMotion * inverseElapsed >= MinimumPanSpeed &&
                          averagePanMotion.Length() / meanPanMotion >= PanCoherence;
        var smoothing = 1f - MathF.Exp(-elapsed / MotionSmoothingSeconds);
        var targetPanVelocity = coherentPan
            ? averagePanMotion * inverseElapsed
            : Vector2.Zero;
        smoothedPanVelocity = Vector2.Lerp(smoothedPanVelocity, targetPanVelocity, smoothing);
        smoothedForwardSpeed = MathHelper.Lerp(
            smoothedForwardSpeed,
            forwardDistance * inverseElapsed,
            smoothing);
        smoothedMeanSpeed = MathHelper.Lerp(
            smoothedMeanSpeed,
            meanMotion * inverseElapsed,
            smoothing);
        var cameraAlpha = MathHelper.Clamp(smoothedMeanSpeed / FullAlphaSpeed, 0f, 0.44f);
        if (smoothedPanVelocity.LengthSquared() > 0.000001f)
        {
            // Static dust moves across the screen in the direction opposite to the camera.
            trailDirection = Vector2.Normalize(smoothedPanVelocity);
        }
        for (var i = 0; i < worldPositions.Length; i++)
        {
            if (!InsideFrustum(screenPositions[i], screenDepths[i], viewDepths[i]))
            {
                Respawn(
                    i,
                    camera,
                    inverseView,
                    averagePanMotion,
                    coherentPan,
                    forwardDistance,
                    smoothedPanVelocity,
                    smoothedForwardSpeed,
                    elapsed);
            }

            smoothedMotionSpeeds[i] = MathHelper.Lerp(
                smoothedMotionSpeeds[i],
                screenMotion[i].Length() * inverseElapsed,
                smoothing);
            DrawParticle(
                i,
                pool,
                cameraAlpha,
                smoothedMotionSpeeds[i],
                smoothedPanVelocity,
                smoothedForwardSpeed);
        }

        pool.DrawBuffer(
            ParticleDrawKind.Screen,
            appearance!,
            resources,
            Matrix4x4.CreateTranslation(camera.Position),
            0
        );
    }

    private bool LoadAppearance(ResourceManager resources)
    {
        appearance ??= FindAppearance(effect);
        if (appearance == null)
        {
            if (!warnedMissingAppearance)
            {
                FLLog.Warning("Dust", $"Spacedust effect '{effect.Nickname}' has no billboard appearance");
                warnedMissingAppearance = true;
            }
            return false;
        }

        appearance.TextureHandler.Update(appearance.Texture, resources);
        if (appearance.TextureHandler.Texture != null)
        {
            return true;
        }

        if (!warnedMissingTexture)
        {
            FLLog.Warning("Dust", $"Spacedust effect '{effect.Nickname}' could not load texture '{appearance.Texture}'");
            warnedMissingTexture = true;
        }
        return false;
    }

    private void Initialize(ICamera camera, Matrix4x4 inverseView)
    {
        for (var i = 0; i < worldPositions.Length; i++)
        {
            screenPositions[i] = new Vector2(NextFloat(-0.92f, 0.92f), NextFloat(-0.92f, 0.92f));
            worldPositions[i] = ScreenToWorld(screenPositions[i], RandomDepth(), camera.Projection, inverseView);
            Project(
                worldPositions[i],
                camera.ViewProjection,
                out _,
                out screenDepths[i],
                out viewDepths[i]);
            particleTimes[i] = NextFloat(0.15f, 0.85f);
            screenMotion[i] = Vector2.Zero;
            smoothedMotionSpeeds[i] = 0f;
        }
        initialized = true;
    }

    private Vector2 UpdateProjection(
        Matrix4x4 viewProjection,
        float forwardDistance,
        out float meanMotion,
        out float meanPanMotion)
    {
        var total = Vector2.Zero;
        var totalMotion = 0f;
        var totalPanMotion = 0f;
        var projectedCount = 0;
        for (var i = 0; i < worldPositions.Length; i++)
        {
            var previous = screenPositions[i];
            if (Project(
                    worldPositions[i],
                    viewProjection,
                    out var current,
                    out var depth,
                    out var viewDepth))
            {
                screenPositions[i] = current;
                screenDepths[i] = depth;
                viewDepths[i] = viewDepth;
                screenMotion[i] = current - previous;
                if (InsideFrustum(current, depth, viewDepth))
                {
                    totalMotion += screenMotion[i].Length();
                    var previousViewDepth = viewDepth + forwardDistance;
                    var forwardMotion = Vector2.Zero;
                    if (MathF.Abs(previousViewDepth) > 0.001f)
                    {
                        forwardMotion = current * (forwardDistance / previousViewDepth);
                    }
                    var panMotion = screenMotion[i] - forwardMotion;
                    total += panMotion;
                    totalPanMotion += panMotion.Length();
                    projectedCount++;
                }
            }
            else
            {
                screenPositions[i] = new Vector2(2f, 2f);
                screenDepths[i] = depth;
                viewDepths[i] = viewDepth;
                screenMotion[i] = Vector2.Zero;
            }
        }

        if (projectedCount == 0)
        {
            meanMotion = 0f;
            meanPanMotion = 0f;
            return Vector2.Zero;
        }

        meanMotion = totalMotion / projectedCount;
        meanPanMotion = totalPanMotion / projectedCount;
        return total / projectedCount;
    }

    private void Respawn(
        int index,
        ICamera camera,
        Matrix4x4 inverseView,
        Vector2 averagePanMotion,
        bool coherentPan,
        float forwardDistance,
        Vector2 panVelocity,
        float forwardSpeed,
        float elapsed)
    {
        var spawnFromPanEdge = coherentPan &&
                               random.NextDouble() < IncomingPanSpawnFraction;
        var spawn = spawnFromPanEdge
            ? IncomingPosition(averagePanMotion)
            : RandomFrontPosition();
        var depthRange = MaximumDepth - MinimumDepth;
        var spawnNear = viewDepths[index] > MaximumDepth || forwardDistance < -0.000001f;
        var depth = spawnFromPanEdge
            ? RandomDepth()
            : spawnNear
                ? NextFloat(MinimumDepth, MinimumDepth + (depthRange * 0.3f))
                : NextFloat(MinimumDepth + (depthRange * 0.7f), MaximumDepth);
        screenPositions[index] = spawn;
        screenMotion[index] = Vector2.Zero;
        worldPositions[index] = ScreenToWorld(spawn, depth, camera.Projection, inverseView);
        Project(
            worldPositions[index],
            camera.ViewProjection,
            out _,
            out screenDepths[index],
            out viewDepths[index]);
        var spawnVelocity = GetOppositeCameraMotion(
            spawn,
            viewDepths[index],
            panVelocity,
            forwardSpeed);
        screenMotion[index] = spawnVelocity * elapsed;
        smoothedMotionSpeeds[index] = spawnVelocity.Length();
        particleTimes[index] = NextFloat(0.15f, 0.85f);
    }

    private static Vector2 GetOppositeCameraMotion(
        Vector2 screenPosition,
        float viewDepth,
        Vector2 panVelocity,
        float forwardSpeed)
    {
        var motion = panVelocity;
        if (MathF.Abs(forwardSpeed) > 0.000001f && viewDepth > 0.001f)
        {
            motion += screenPosition * (forwardSpeed / viewDepth);
        }
        return motion;
    }

    private void DrawParticle(
        int index,
        ParticleEffectPool pool,
        float cameraAlpha,
        float motionSpeed,
        Vector2 panVelocity,
        float forwardSpeed)
    {
        var trailAmount = MathHelper.Clamp(
            (motionSpeed - TrailStartSpeed) / (FullTrailSpeed - TrailStartSpeed),
            0f,
            1f);
        trailAmount *= trailAmount;
        var length = MathHelper.Lerp(PointWidth, MaximumTrail, trailAmount);
        var oppositeCameraMotion = GetOppositeCameraMotion(
            screenPositions[index],
            viewDepths[index],
            panVelocity,
            forwardSpeed);
        var oppositeCameraDirection = oppositeCameraMotion.LengthSquared() > 0.000001f
            ? Vector2.Normalize(oppositeCameraMotion)
            : trailDirection;
        var angle = MathF.Atan2(oppositeCameraDirection.X, oppositeCameraDirection.Y);
        var drawPosition = screenPositions[index];
        if (motionSpeed > 0.000001f)
        {
            drawPosition += oppositeCameraDirection * (length - PointWidth);
        }
        var color = appearance!.Color.GetValue(0, particleTimes[index]);

        pool.AddParticle(
            appearance.TextureHandler,
            new Vector3(drawPosition, screenDepths[index]),
            new Vector2(PointWidth, length),
            new Color4(color, cameraAlpha),
            0,
            Vector3.Zero,
            angle,
            appearance.FlipHorizontal,
            appearance.FlipVertical
        );
    }

    private Vector2 IncomingPosition(Vector2 motion)
    {
        if (MathF.Abs(motion.X) > MathF.Abs(motion.Y) && MathF.Abs(motion.X) > 0.00001f)
        {
            return new Vector2(-MathF.Sign(motion.X) * SpawnEdge, NextFloat(-0.9f, 0.9f));
        }
        if (MathF.Abs(motion.Y) > 0.00001f)
        {
            return new Vector2(NextFloat(-0.9f, 0.9f), -MathF.Sign(motion.Y) * SpawnEdge);
        }

        return RandomFrontPosition();
    }

    private Vector2 RandomFrontPosition() =>
        new(NextFloat(-0.9f, 0.9f), NextFloat(-0.9f, 0.9f));

    private float RandomDepth() => NextFloat(MinimumDepth, MaximumDepth);

    private static Vector3 ScreenToWorld(Vector2 screen, float depth, Matrix4x4 projection, Matrix4x4 inverseView)
    {
        var viewPosition = new Vector3(
            screen.X * depth / projection.M11,
            screen.Y * depth / projection.M22,
            -depth
        );
        return Vector3.Transform(viewPosition, inverseView);
    }

    private static bool Project(
        Vector3 position,
        Matrix4x4 viewProjection,
        out Vector2 screen,
        out float depth,
        out float viewDepth)
    {
        var clip = Vector4.Transform(new Vector4(position, 1f), viewProjection);
        if (clip.W <= 0.001f)
        {
            screen = default;
            depth = 1f;
            viewDepth = 0f;
            return false;
        }
        screen = new Vector2(clip.X, clip.Y) / clip.W;
        depth = clip.Z / clip.W;
        viewDepth = clip.W;
        return true;
    }

    private static bool InsideFrustum(Vector2 screen, float depth, float viewDepth) =>
        MathF.Abs(screen.X) <= FrustumLimit &&
        MathF.Abs(screen.Y) <= FrustumLimit &&
        depth is >= 0f and <= 1f &&
        viewDepth is >= MinimumDepth and <= MaximumDepth;

    private float NextFloat(float min, float max) => min + ((float)random.NextDouble() * (max - min));

    private static FxBasicAppearance? FindAppearance(ParticleEffect effect)
    {
        foreach (var app in effect.Appearances)
        {
            if (app.Appearance is FLDustAppearance dust)
            {
                return dust;
            }
        }
        foreach (var app in effect.Appearances)
        {
            if (app.Appearance is FxBasicAppearance basic)
            {
                return basic;
            }
        }
        return null;
    }
}
