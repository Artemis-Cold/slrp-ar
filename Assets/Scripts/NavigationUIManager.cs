using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class NavigationUIManager : MonoBehaviour
{
    [Header("Navigation")]
    [SerializeField] private IndoorPathVisualizer navigator;

    [Header("Parish cross-floor route orchestration") ]
    [SerializeField] private ParishFloorNavigationController parishFloorController;

    [Header("Locations (Destinations)")]
    [SerializeField] private Transform locationsRoot;

    [Header("Destination UI")]
    [SerializeField] private TMP_Dropdown destinationDropdown;

    [Header("Starting Location UI")]
    [SerializeField] private Transform calibrationPointsRoot;
    [SerializeField] private TMP_Dropdown startingLocationDropdown;

    [Header("Arrival UI")]
    [SerializeField] private GameObject arrivalPanel;
    [SerializeField] private TMP_Text arrivalText;

    private readonly List<Transform> locations = new List<Transform>();
    private readonly List<Transform> calibrationPoints = new List<Transform>();

    // Selected reference point only. It does not calibrate the AR map yet.
    public Transform SelectedCalibrationPoint { get; private set; }

    private void Start()
    {
        Debug.Log("NavigationUIManager Start()");
        if (!ValidateReferences())
        {
            Debug.LogError("NavigationUIManager validation failed.");
            return;
        }

        if (parishFloorController == null) parishFloorController = GetComponent<ParishFloorNavigationController>();
        PopulateDestinationDropdown();
        PopulateStartingLocationDropdown();
        arrivalPanel.SetActive(false);
        navigator.DestinationReached += OnDestinationReached;
    }

    private void OnDestroy()
    {
        if (destinationDropdown != null)
            destinationDropdown.onValueChanged.RemoveListener(OnDestinationSelected);

        if (startingLocationDropdown != null)
            startingLocationDropdown.onValueChanged.RemoveListener(OnStartingLocationSelected);

        if (navigator != null)
            navigator.DestinationReached -= OnDestinationReached;
    }

    private bool ValidateReferences()
    {
        if (navigator == null || locationsRoot == null || destinationDropdown == null ||
            calibrationPointsRoot == null || startingLocationDropdown == null ||
            arrivalPanel == null || arrivalText == null)
        {
            Debug.LogError("NavigationUIManager: Assign Navigator, Locations Root, Destination Dropdown, " +
                           "Calibration Points Root, Starting Location Dropdown, Arrival Panel, and Arrival Text.");
            return false;
        }
        return true;
    }

    private void PopulateDestinationDropdown()
    {
        locations.Clear();
        var options = new List<string> { "Select destination" };
        foreach (Transform location in locationsRoot)
        {
            locations.Add(location);
            options.Add(FormatLocationName(location.name));
        }

        destinationDropdown.onValueChanged.RemoveListener(OnDestinationSelected);
        destinationDropdown.ClearOptions();
        destinationDropdown.AddOptions(options);
        destinationDropdown.SetValueWithoutNotify(0);
        destinationDropdown.RefreshShownValue();
        destinationDropdown.onValueChanged.AddListener(OnDestinationSelected);
        Debug.Log("Destination count: " + locations.Count);
    }

    private void PopulateStartingLocationDropdown()
    {
        calibrationPoints.Clear();
        SelectedCalibrationPoint = null;
        var options = new List<string> { "Select starting location" };
        foreach (Transform point in calibrationPointsRoot)
        {
            calibrationPoints.Add(point);
            string displayName = point.name.EndsWith("_Calibration")
                ? point.name.Substring(0, point.name.Length - "_Calibration".Length)
                : point.name;
            options.Add(FormatLocationName(displayName));
        }

        startingLocationDropdown.onValueChanged.RemoveListener(OnStartingLocationSelected);
        startingLocationDropdown.ClearOptions();
        startingLocationDropdown.AddOptions(options);
        startingLocationDropdown.SetValueWithoutNotify(0);
        startingLocationDropdown.RefreshShownValue();
        startingLocationDropdown.onValueChanged.AddListener(OnStartingLocationSelected);
        Debug.Log("Calibration point count: " + calibrationPoints.Count);
    }

    private void OnStartingLocationSelected(int index)
    {
        SelectedCalibrationPoint = index > 0 && index <= calibrationPoints.Count
            ? calibrationPoints[index - 1]
            : null;

        Debug.Log(SelectedCalibrationPoint == null
            ? "Starting location cleared."
            : "Selected starting location: " + SelectedCalibrationPoint.name +
              " (AR map has not been calibrated yet)");
    }

    private void OnDestinationSelected(int index)
    {
        arrivalPanel.SetActive(false);
        if (index <= 0 || index > locations.Count)
        {
            if (parishFloorController != null) parishFloorController.CancelRoute();
            else navigator.StopNavigation();
            return;
        }

        Transform destination = locations[index - 1];
        if (parishFloorController != null) parishFloorController.SelectDestination(destination);
        else navigator.SetDestination(destination);
        Debug.Log("Selected destination: " + destination.name);
    }

    private void OnDestinationReached(Transform destination)
    {
        if (parishFloorController != null && parishFloorController.TryHandleWaypointArrival(destination))
            return; // Internal stairs checkpoint: not a destination and no arrival popup.
        if (parishFloorController != null) parishFloorController.NotifyFinalArrival(destination);
        arrivalText.text = "You have arrived at\n" + FormatLocationName(destination.name);
        arrivalPanel.SetActive(true);
    }

    public void CloseArrivalPanel()
    {
        arrivalPanel.SetActive(false);
        destinationDropdown.SetValueWithoutNotify(0);
        destinationDropdown.RefreshShownValue();
        if (parishFloorController != null) parishFloorController.CancelRoute();
        else navigator.StopNavigation();
    }

    // Called after a staircase QR has been scanned. Restores the FINAL
    // destination silently (without triggering a new dropdown selection).
    public void RestoreDestinationSelection(Transform destination)
    {
        if (destination == null || destinationDropdown == null) return;
        int index = locations.IndexOf(destination);
        if (index < 0) return;
        destinationDropdown.SetValueWithoutNotify(index + 1);
        destinationDropdown.RefreshShownValue();
        if (arrivalPanel != null) arrivalPanel.SetActive(false);
    }

    private string FormatLocationName(string rawName)
    {
        return rawName.Replace("_", " ");
    }
}
