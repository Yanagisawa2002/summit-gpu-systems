using System.Collections.Generic;
using Summit.GpuAutotuning;
using Summit.GpuPrimitives;
using UnityEngine;

namespace GpuSystems.Samples
{
    /// <summary>
    /// Minimal public-API sample. The timing values are synthetic and exist to
    /// explain the selection contract; they are not measurements of this GPU.
    /// </summary>
    public sealed class GpuSystemsPolicyQuickStart : MonoBehaviour
    {
        private GpuDeviceFingerprint device;
        private GpuAutotuneCandidateTiming selected;
        private GpuPrimitiveBackend safeUnmeasuredBackend;

        private void Awake()
        {
            device = GpuDeviceFingerprint.Capture();
            var candidates = new List<GpuAutotuneCandidateTiming>
            {
                new GpuAutotuneCandidateTiming(
                    "portable",
                    new[] { 1.00, 1.01, 1.02, 1.01, 1.00 },
                    validationPassed: true),
                new GpuAutotuneCandidateTiming(
                    "measured-candidate",
                    new[] { 0.78, 0.80, 0.79, 0.81, 0.80 },
                    validationPassed: true),
                new GpuAutotuneCandidateTiming(
                    "invalid-fast-path",
                    new[] { 0.60, 0.61, 0.59, 0.60, 0.60 },
                    validationPassed: false)
            };
            selected = GpuAutotuneSelector.Select(
                "portable",
                candidates,
                maximumP99RegressionPercent: 2.0,
                minimumMedianImprovementPercent: 1.0);

            // No exact-device, holdout-accepted profile is supplied here.
            // Production callers therefore receive the conservative backend.
            var resolver = new GpuPrimitiveBackendResolver(profile: null);
            safeUnmeasuredBackend = resolver.ResolveMeasuredOrPortable(
                "exclusive-scan");
        }

        private void OnGUI()
        {
            const float width = 720f;
            const float height = 250f;
            Rect panel = new Rect(
                (Screen.width - width) * 0.5f,
                (Screen.height - height) * 0.5f,
                width,
                height);
            GUILayout.BeginArea(panel, GUI.skin.box);
            GUILayout.Label("GPU Systems Toolkit — Policy Quick Start");
            GUILayout.Space(8f);
            GUILayout.Label("Device key: " + device.StableKey);
            GUILayout.Label(
                "Synthetic validated selection: " + selected.CandidateId);
            GUILayout.Label(
                "Synthetic median: " + selected.MedianMs.ToString("F3") +
                " ms; P99: " + selected.P99Ms.ToString("F3") + " ms");
            GUILayout.Label(
                "Missing exact-device profile → " + safeUnmeasuredBackend);
            GUILayout.Space(12f);
            GUILayout.Label(
                "This sample explains the API contract. It does not benchmark " +
                "or claim performance for the current device.");
            GUILayout.EndArea();
        }
    }
}
