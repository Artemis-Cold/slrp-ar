
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class QRMarkerDetector : MonoBehaviour
{
    [SerializeField]
    private ARTrackedImageManager trackedImageManager;

    // Image tracking can update every frame. Printing hundreds of repeated
    // lines per second makes the device log noisy and wastes resources while
    // QR decoding/calibration is running.
    private readonly Dictionary<string, float> nextMarkerReportAt =
        new Dictionary<string, float>();

    private void OnEnable()
    {
        if (trackedImageManager == null)
        {
            Debug.LogError("AR Tracked Image Manager is missing!");
            return;
        }

        trackedImageManager.trackablesChanged.AddListener(
            OnTrackedImagesChanged
        );
    }

#if UNITY_IOS && !UNITY_EDITOR
    private float nextIosTrackerStatusAt = 8f;

    private void Update()
    {
        if (Time.time < nextIosTrackerStatusAt || trackedImageManager == null) return;
        nextIosTrackerStatusAt = Time.time + 10f;
        bool subsystemRunning = trackedImageManager.subsystem != null &&
            trackedImageManager.subsystem.running;
        Debug.Log("[SLRP AR QR][iOS] Tracker heartbeat: enabled=" +
            trackedImageManager.enabled + ", subsystemRunning=" + subsystemRunning +
            ", ARSession=" + ARSession.state +
            ", imageTrackables=" + trackedImageManager.trackables.count);
    }
#endif

    private void OnDisable()
    {
        if (trackedImageManager != null)
        {
            trackedImageManager.trackablesChanged.RemoveListener(
                OnTrackedImagesChanged
            );
        }
    }

    private void OnTrackedImagesChanged(
        ARTrackablesChangedEventArgs<ARTrackedImage> args)
    {
        foreach (ARTrackedImage image in args.added)
        {
#if UNITY_IOS && !UNITY_EDITOR
            Debug.Log("[SLRP AR QR][iOS] ARKit image added: " +
                image.referenceImage.name + " (state: " + image.trackingState + ")");
#endif
            ReportImage(image);
        }

        foreach (ARTrackedImage image in args.updated)
        {
            ReportImage(image);
        }

        foreach (var removed in args.removed)
        {
            Debug.Log("Tracked image removed: " + removed.Key);
        }
    }

    private void ReportImage(ARTrackedImage image)
    {
        if (image.trackingState == TrackingState.Tracking)
        {
            float nextReport;
            if (nextMarkerReportAt.TryGetValue(image.referenceImage.name, out nextReport) &&
                Time.time < nextReport) return;
            nextMarkerReportAt[image.referenceImage.name] = Time.time + 3f;
            Debug.Log(
                "QR MARKER DETECTED: " +
                image.referenceImage.name +
                " | Position: " +
                image.transform.position +
                " | Rotation: " +
                image.transform.rotation.eulerAngles
            );
        }
    }
}
