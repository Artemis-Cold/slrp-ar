using System.Collections;
using UnityEngine;

public class MarkerRecenterManager : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform arCamera;
    [SerializeField] private Transform arMapOrigin;
    [SerializeField] private Transform virtualMarker;

    [Header("Marker Setup")]
    [SerializeField] private float markerDistanceFromCamera = 0.75f;
    [SerializeField] private float startupDelay = 2f;

    private bool calibrated = false;

    private IEnumerator Start()
    {
        yield return new WaitForSeconds(startupDelay);

        RecenterFromMarker();
    }

    public void RecenterFromMarker()
    {
        if (calibrated)
            return;

        if (!ValidateReferences())
            return;

        /*
         * Temporary physical-marker estimate.
         *
         * Later QR/image tracking will replace this.
         */
        Vector3 physicalMarkerPosition =
            arCamera.position +
            arCamera.forward * markerDistanceFromCamera;

        /*
         * Camera looks toward marker.
         * Marker forward therefore points toward camera.
         */
        Vector3 physicalMarkerForward =
            -arCamera.forward;

        physicalMarkerForward.y = 0f;

        if (physicalMarkerForward.sqrMagnitude < 0.001f)
            physicalMarkerForward = Vector3.forward;

        physicalMarkerForward.Normalize();

        Quaternion physicalMarkerRotation =
            Quaternion.LookRotation(
                physicalMarkerForward,
                Vector3.up
            );

        /*
         * virtualMarker is now stored directly
         * in NavigationMap/world coordinates.
         */
        Vector3 virtualMarkerPosition =
            virtualMarker.position;

        Quaternion virtualMarkerRotation =
            virtualMarker.rotation;

        /*
         * Rotate AR representation so the map marker's
         * orientation matches the physical marker.
         */
        Quaternion mapRotation =
            physicalMarkerRotation *
            Quaternion.Inverse(virtualMarkerRotation);

        arMapOrigin.rotation = mapRotation;

        /*
         * Move ARMapOrigin so the virtual marker
         * maps onto the estimated physical marker.
         */
        Vector3 markerOffset =
            mapRotation * virtualMarkerPosition;

        arMapOrigin.position =
            physicalMarkerPosition -
            markerOffset;

        calibrated = true;

        Debug.Log("=== RECENTER COMPLETE ===");
        Debug.Log("Physical Marker: " + physicalMarkerPosition);
        Debug.Log("Virtual Marker: " + virtualMarkerPosition);
        Debug.Log("ARMapOrigin Position: " + arMapOrigin.position);
        Debug.Log("ARMapOrigin Rotation: " + arMapOrigin.eulerAngles);
    }

    public void ResetCalibration()
    {
        calibrated = false;

        arMapOrigin.position = Vector3.zero;
        arMapOrigin.rotation = Quaternion.identity;

        Debug.Log("Calibration reset.");
    }

    private bool ValidateReferences()
    {
        if (arCamera == null)
        {
            Debug.LogError("AR Camera is not assigned.");
            return false;
        }

        if (arMapOrigin == null)
        {
            Debug.LogError("ARMapOrigin is not assigned.");
            return false;
        }

        if (virtualMarker == null)
        {
            Debug.LogError("Virtual Marker is not assigned.");
            return false;
        }

        return true;
    }
}