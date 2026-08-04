using System;
using UnityEngine;

internal readonly struct GpuCinematicShot
{
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly float FieldOfView;
    public readonly float Progress;
    public readonly string Title;
    public readonly string Subtitle;

    public GpuCinematicShot(
        Vector3 position,
        Quaternion rotation,
        float fieldOfView,
        float progress,
        string title,
        string subtitle)
    {
        Position = position;
        Rotation = rotation;
        FieldOfView = fieldOfView;
        Progress = progress;
        Title = title ?? string.Empty;
        Subtitle = subtitle ?? string.Empty;
    }
}

/// <summary>
/// Allocation-free, deterministic camera rail built from authored full-city
/// camera anchors. The same normalized timestamp produces exactly the same
/// pose in both benchmark variants.
/// </summary>
internal sealed class GpuCinematicRoute
{
    public const string RouteId = "manhattan-golden-hour-v1";
    public const float SkylineGalleryProgress = 0.43f;
    public const float FacadeGalleryProgress = 0.60f;
    public const float TextureGalleryProgress = 0.72f;

    private readonly RouteKey[] keys;

    private readonly struct RouteKey
    {
        public readonly float Time;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly float FieldOfView;
        public readonly string Title;
        public readonly string Subtitle;

        public RouteKey(
            float time,
            Vector3 position,
            Quaternion rotation,
            float fieldOfView,
            string title,
            string subtitle)
        {
            Time = time;
            Position = position;
            Rotation = rotation;
            FieldOfView = fieldOfView;
            Title = title;
            Subtitle = subtitle;
        }
    }

    public GpuCinematicRoute(
        Vector3 skylinePosition,
        Vector3 lateralPosition,
        Vector3 mediumPosition,
        Vector3 rooflinePosition,
        Vector3 closePosition,
        Quaternion closeRotation)
    {
        Vector3 closeForward = Vector3.ProjectOnPlane(
            closeRotation * Vector3.forward,
            Vector3.up);
        if (closeForward.sqrMagnitude < 0.01f)
        {
            closeForward = Vector3.forward;
        }
        closeForward.Normalize();
        Vector3 closeRight = Vector3.Cross(Vector3.up, closeForward);
        closeRight.Normalize();

        Vector3 cityFocus = closePosition + closeForward * 390.0f;
        cityFocus.y = Mathf.Max(90.0f, closePosition.y - 75.0f);
        Vector3 texturePosition = closePosition +
            closeForward * 180.0f + closeRight * 280.0f +
            Vector3.up * 560.0f;
        Vector3 textureTarget = closePosition +
            closeForward * 580.0f + closeRight * 40.0f;
        textureTarget.y = Mathf.Max(30.0f, closePosition.y - 170.0f);
        Vector3 pullupPosition = Vector3.Lerp(
            rooflinePosition,
            lateralPosition,
            0.42f) + Vector3.up * 110.0f;

        keys = new[]
        {
            new RouteKey(0.00f, skylinePosition,
                LookAt(skylinePosition, cityFocus + Vector3.up * 720.0f, -0.5f),
                50.0f, "CITY AT SCALE",
                "35.9M triangles / full-city GPU geometry"),
            new RouteKey(0.14f, lateralPosition,
                LookAt(lateralPosition, cityFocus + Vector3.up * 650.0f, 0.8f),
                44.0f, "MANHATTAN REVEAL",
                "1.08M buildings / deterministic golden hour"),
            new RouteKey(0.29f, mediumPosition,
                LookAt(mediumPosition, cityFocus + Vector3.up * 450.0f, 1.1f),
                40.0f, "DENSITY SWEEP",
                "61.8M vertices / 280K GPU clusters"),
            new RouteKey(0.43f, rooflinePosition,
                LookAt(rooflinePosition, cityFocus + Vector3.up * 360.0f, -1.0f),
                36.0f, "MANHATTAN SKYLINE",
                "Full-resolution buildings remain GPU resident"),
            new RouteKey(0.57f, closePosition,
                LookAt(closePosition, cityFocus + Vector3.up * 80.0f, -0.4f),
                38.0f, "FACADE CANYON",
                "Metadata-driven facade materials and projected shadows"),
            new RouteKey(0.70f, texturePosition,
                LookAt(texturePosition, textureTarget, 0.9f),
                52.0f, "2K VIRTUAL-TEXTURE PAGES",
                "19,278 logical pages / ~15.24 cm source GSD"),
            new RouteKey(0.82f, pullupPosition,
                LookAt(pullupPosition, cityFocus + Vector3.up * 560.0f, 1.2f),
                42.0f, "GPU-RESIDENT CITY",
                "One hero view plus three robotics payload views"),
            new RouteKey(0.92f, lateralPosition,
                LookAt(lateralPosition, cityFocus + Vector3.up * 650.0f, -0.7f),
                46.0f, "MULTI-SENSOR LOAD",
                "Identical camera, texture and weather inputs for A / B"),
            new RouteKey(1.00f, skylinePosition,
                LookAt(skylinePosition, cityFocus + Vector3.up * 720.0f, 0.0f),
                50.0f, "VALIDATED OUTPUT",
                "Fixed route enables rendered-output equivalence checks")
        };
    }

    public GpuCinematicShot Evaluate(float normalizedTime)
    {
        float time = Mathf.Clamp01(normalizedTime);
        int keyIndex = FindKeyIndex(time);
        RouteKey from = keys[keyIndex];
        RouteKey to = keys[Mathf.Min(keyIndex + 1, keys.Length - 1)];
        float duration = Mathf.Max(0.0001f, to.Time - from.Time);
        float local = Mathf.Clamp01((time - from.Time) / duration);
        float eased = SmootherStep(local);

        return new GpuCinematicShot(
            Vector3.LerpUnclamped(from.Position, to.Position, eased),
            Quaternion.SlerpUnclamped(from.Rotation, to.Rotation, eased),
            Mathf.LerpUnclamped(from.FieldOfView, to.FieldOfView, eased),
            time,
            from.Title,
            from.Subtitle);
    }

    public bool TryValidate(out string error)
    {
        if (keys == null || keys.Length < 2)
        {
            error = "Cinematic route requires at least two keys.";
            return false;
        }
        for (int sample = 0; sample <= 512; sample++)
        {
            GpuCinematicShot shot = Evaluate(sample / 512.0f);
            if (!IsFinite(shot.Position) || !IsFinite(shot.Rotation) ||
                !IsFinite(shot.FieldOfView))
            {
                error = "Cinematic route contains a non-finite pose.";
                return false;
            }
            if (shot.Position.y < 190.0f)
            {
                error = "Cinematic route falls below the validated 190 m floor.";
                return false;
            }
            if (shot.FieldOfView < 30.0f || shot.FieldOfView > 65.0f)
            {
                error = "Cinematic route field of view is outside [30,65].";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private int FindKeyIndex(float time)
    {
        for (int index = 0; index < keys.Length - 1; index++)
        {
            if (time < keys[index + 1].Time)
            {
                return index;
            }
        }
        return keys.Length - 2;
    }

    private static Quaternion LookAt(
        Vector3 position,
        Vector3 target,
        float rollDegrees)
    {
        Vector3 direction = target - position;
        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = Vector3.forward;
        }
        Quaternion look = Quaternion.LookRotation(
            direction.normalized,
            Vector3.up);
        return look * Quaternion.Euler(0.0f, 0.0f, rollDegrees);
    }

    private static float SmootherStep(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.x) && IsFinite(value.y) &&
            IsFinite(value.z) && IsFinite(value.w);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
