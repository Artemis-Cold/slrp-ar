using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;

// UI ONLY: keeps the existing QR calibration, NavMesh and arrow generation untouched.
// This component is already present and wired up in HouseARNavigation.unity.
public class QRNavigationReadyUI : MonoBehaviour
{
    [Header("Existing Components - keep Inspector references")]
    [SerializeField] private QRMapCalibrator qrCalibrator;
    [SerializeField] private TMP_Dropdown destinationDropdown;
    [SerializeField] private TMP_Text statusText;
    [SerializeField, Min(0f)] private float settlingSeconds = 1f;

    [Header("User Guidance")]
    [SerializeField, Min(0f)] private float routePreparationSeconds = 1f;
    [SerializeField, Range(10f, 80f)] private float turnThresholdDegrees = 28f;
    [SerializeField, Range(100f, 180f)] private float turnAroundThresholdDegrees = 145f;

    [Header("Route distance: physical scale needs checking")]
    [Tooltip("1 Unity map unit is treated as 1 meter until you measure a real hallway and set the ratio. Does not modify route geometry.")]
    [SerializeField, Min(0.1f)] private float physicalMetersPerMapUnit = 1f;

    // Referenced at runtime; no new Inspector assignments are needed.
    private IndoorPathVisualizer navigator;
    private ParishFloorNavigationController floorFlow;
    private Transform arMapOrigin;
    private Camera arCamera;
    private GameObject arrivalPanel;

    private float calibratedAt = -1f;
    private float destinationSelectedAt = -1f;
    private float nextDirectionRefresh;
    private bool ready;
    private Transform previousDestination;
    private NavMeshPath guidancePath;

    // UI elements created within the existing NavigationCanvas.
    private GameObject headerCard;
    private GameObject scanningLocationHint;
    private GameObject destinationCard;
    private GameObject directionCard;
    private Text headerEyebrow;
    private Text readableStatusText;
    private TMP_Text destinationHeading;
    private TMP_Text directionHeading;
    private TMP_Text directionTitle;
    private TMP_Text directionDetail;
    private RectTransform progressFill;
    private GameObject lastStyledDropdownList;
    // High-priority staircase handoff overlay. This is UI-only and never changes routes.
    private GameObject floorChangeOverlay;
    private RectTransform floorChangeCardRect;
    private Text floorChangeTitle;
    private Text floorChangeDescription;
    private Text floorChangeDestination;
    private RectTransform headerCardRect;
    private RectTransform destinationCardRect;
    private RectTransform directionCardRect;
    private Vector2 lastSafeAreaSize = new Vector2(-1f, -1f);
    private Rect lastSafeArea = new Rect(-1f, -1f, -1f, -1f);

    // We draw the option list ourselves so item height/spacing are reliable
    // on iPhone and Android. TMP_Dropdown still owns the options and selection.
    private RectTransform uiCanvasRect;
    private RectTransform destinationSelectorRect;
    private GameObject customOptionPopup;
    private GameObject customOptionBackdrop;
    private Button customDropdownTrigger;
    private const float PopupRowHeight = 78f;
    private const float PopupRowGap = 9f;

    private static readonly Color Navy = new Color(0.68f, 0.125f, 0.145f, 0.97f);
    private static readonly Color DeepNavy = new Color(0.97f, 0.95f, 0.92f, 0.98f);
    private static readonly Color Teal = new Color(0.97f, 0.79f, 0.43f, 1f);
    private static readonly Color White = new Color(1f, 0.985f, 0.96f, 1f);
    private static readonly Color Muted = new Color(0.96f, 0.86f, 0.82f, 1f);

    private void Awake()
    {
        // Prevent route selection before the QR is calibrated.
        if (destinationDropdown != null)
            destinationDropdown.interactable = false;
    }

    private void Start()
    {
        if (qrCalibrator == null || destinationDropdown == null || statusText == null)
        {
            Debug.LogError("QRNavigationReadyUI: Missing one of the existing QR, dropdown, or status text references.");
            enabled = false;
            return;
        }

        navigator = FindAnyObjectByType<IndoorPathVisualizer>();
        floorFlow = GetComponent<ParishFloorNavigationController>();
        arCamera = Camera.main;
        if (arCamera == null) arCamera = FindFirstObjectByType<Camera>();
        GameObject mapObject = GameObject.Find("ARMapOrigin");
        if (mapObject != null) arMapOrigin = mapObject.transform;
        GameObject arrivalObject = GameObject.Find("ArrivalPanel");
        if (arrivalObject != null) arrivalPanel = arrivalObject;

        guidancePath = new NavMeshPath();
        BuildAppearance();
        ShowScanning();
    }

    // TMP_Dropdown generates its visible option rows only when opened.
    // Style that newly created list as well as the hidden template.
    private void LateUpdate()
    {
        RefreshSafeAreaLayout();
        StyleOpenedDropdownIfNeeded();
    }

    private void Update()
    {
        if (qrCalibrator == null || destinationDropdown == null) return;

        // Stage 1: scan the physical marker.
        if (!qrCalibrator.IsCalibrated)
        {
            calibratedAt = -1f;
            destinationSelectedAt = -1f;
            previousDestination = null;
            ready = false;
            destinationDropdown.interactable = false;
            SetDestinationVisible(false);
            ShowScanning();
            // After tapping SCAN QR, return to an unobstructed camera view.
            // The controller retains the original destination during rescanning.
            SetFloorChangeOverlayVisible(false);
            if (floorFlow != null && floorFlow.IsWaitingForNextFloorQr)
            {
                SetHeader("FLOOR CHANGE / SCAN", "Scan the " + floorFlow.TargetFloorName + " QR", 0f);
                SetDirectionVisible(true);
                SetText(directionHeading, "YOUR DESTINATION IS SAVED");
                SetText(directionTitle, "Scan the stair QR");
                SetText(directionDetail, "Find the QR on the " + floorFlow.TargetFloorName +
                    ". Hold your camera steady to resume " + GetSavedDestinationName() + ".");
            }
            return;
        }

        if (calibratedAt < 0f) calibratedAt = Time.time;

        // Scanning note disappears as soon as the recognized marker calibrates.
        SetScanningLocationHintVisible(false);

        // Stage 2: give tracking a brief opportunity to settle.
        float timeSinceCalibration = Time.time - calibratedAt;
        if (!ready && timeSinceCalibration < settlingSeconds)
        {
            destinationDropdown.interactable = false;
            SetDestinationVisible(false);
            SetHeader("STEP 02 / POSITIONING", "Calibrating AR...", 
                Mathf.Clamp01(timeSinceCalibration / Mathf.Max(0.01f, settlingSeconds)));
            SetDirectionVisible(false);
            return;
        }

        ready = true;
        SetDestinationVisible(true);
        destinationDropdown.interactable = true;

        // A stair waypoint is not a destination: arrows are paused while the
        // visitor changes floors. Keep the actual destination in a prominent,
        // non-overlapping handoff card until the user starts the next QR scan.
        if (floorFlow != null && floorFlow.IsWaitingForNextFloorQr)
        {
            destinationDropdown.interactable = false;
            SetDestinationVisible(false);
            SetHeader("FLOOR CHANGE", "Proceed to the " + floorFlow.TargetFloorName, 1f);
            SetDirectionVisible(false);
            UpdateFloorChangeOverlayContent();
            SetFloorChangeOverlayVisible(true);
            return;
        }
        SetFloorChangeOverlayVisible(false);
        if (floorFlow != null && floorFlow.HasPendingDestination &&
            !floorFlow.IsHeadingToStairs && !string.IsNullOrEmpty(floorFlow.StatusDetail))
        {
            SetHeader("ROUTE CHECK", "Cannot prepare route", 1f);
            SetDirectionVisible(true);
            SetText(directionHeading, "NAVIGATION");
            SetText(directionTitle, "Check your position");
            SetText(directionDetail, floorFlow.StatusDetail);
            return;
        }

        Transform selected = navigator != null ? navigator.CurrentDestination : null;
        if (selected == null || (navigator != null && navigator.HasArrived))
        {
            previousDestination = null;
            destinationSelectedAt = -1f;
            SetHeader("STEP 03 / DESTINATION", "Ready to navigate", 1f);
            SetDirectionVisible(false);
            return;
        }

        // Navigation may remain calibrated while AR pose becomes unreliable.
        // Do not display moving instructions computed from an unsafe position.
        if (navigator != null && navigator.IsRouteSafetyPaused)
        {
            SetHeader("STEP 05 / TRACKING", "Navigation paused", 1f);
            SetDirectionVisible(true);
            SetText(directionHeading, "TRACKING UNRELIABLE");
            SetText(directionTitle, navigator.RequiresQrRescan ? "Rescan QR" : "Hold phone steady");
            SetText(directionDetail, navigator.RequiresQrRescan
                ? "Rescan a registered location QR to recalibrate your location."
                : "Move into better lighting. Navigation will resume after tracking stabilizes.");
            return;
        }

        // Stage 3: short route-preparation indication on each destination change.
        if (selected != previousDestination)
        {
            previousDestination = selected;
            destinationSelectedAt = Time.time;
            nextDirectionRefresh = 0f;
        }

        float routeElapsed = Time.time - destinationSelectedAt;
        if (routeElapsed < routePreparationSeconds)
        {
            SetHeader("STEP 04 / ROUTE", "Preparing your route...",
                Mathf.Clamp01(routeElapsed / Mathf.Max(0.01f, routePreparationSeconds)));
            SetDirectionVisible(true);
            SetText(directionHeading, "YOUR NEXT MOVE");
            SetText(directionTitle, "Getting directions");
            SetText(directionDetail, "Hold your phone steady for a moment.");
            return;
        }

        SetHeader("STEP 05 / NAVIGATING", floorFlow != null && floorFlow.IsHeadingToStairs
            ? "Follow arrows to the stairs" : "Navigation active", 1f);
        SetDirectionVisible(true);
        if (Time.time >= nextDirectionRefresh)
        {
            nextDirectionRefresh = Time.time + 0.4f;
            UpdateTurnGuidance(selected);
        }
    }

    // Hide the WHOLE selector card, not just its click interaction.
    // TMP_Dropdown is a child of this runtime card, so its label and options
    // remain out of view until the QR map has calibrated and settled.
    private void SetDestinationVisible(bool visible)
    {
        if (destinationCard == null || destinationCard.activeSelf == visible)
            return;

        if (!visible && destinationDropdown != null)
        {
            destinationDropdown.Hide();
            CloseCustomOptions();
        }

        destinationCard.SetActive(visible);
    }

    private void ShowScanning()
    {
        // Display the real calibration stage instead of appearing frozen after
        // a successful QR text decode. Never advance to Step 02 until the
        // calibrator accepts the tracked pose.
        string hint = qrCalibrator == null ? "Scan a location QR code" :
            qrCalibrator.CalibrationHint;
        float progress = qrCalibrator == null ? 0f : qrCalibrator.CalibrationProgress;
        SetHeader("STEP 01 / SCANNING", hint, progress);
        // The stairs instruction is in the QR help/warning modal, not a
        // separate floating label under the main navigation card.
        SetScanningLocationHintVisible(false);
        SetDirectionVisible(false);
    }

    private void SetScanningLocationHintVisible(bool visible)
    {
        if (scanningLocationHint != null && scanningLocationHint.activeSelf != visible)
            scanningLocationHint.SetActive(visible);
    }

    private void SetHeader(string eyebrow, string message, float progress)
    {
        // The scene-linked TMP status remains synchronized for existing logic,
        // but the visible light lettering uses the regular uGUI text renderer.
        // That avoids the TMP font material's dark tint on light glyphs.
        if (headerEyebrow != null) headerEyebrow.text = eyebrow;
        SetText(statusText, message);
        if (readableStatusText != null) readableStatusText.text = message;
        if (progressFill != null)
        {
            progressFill.anchorMin = new Vector2(0f, 0f);
            progressFill.anchorMax = new Vector2(Mathf.Clamp01(progress), 1f);
            progressFill.offsetMin = Vector2.zero;
            progressFill.offsetMax = Vector2.zero;
        }
    }

    private void UpdateTurnGuidance(Transform destination)
    {
        if (navigator == null || arMapOrigin == null || arCamera == null || destination == null)
        {
            SetInstruction("Finding your direction", "Keep your phone upright and move slowly.");
            return;
        }

        // Use the EXACT current routed target (final room or stair waypoint).
        // The path visualizer's result is authoritative. A failed path must not
        // appear to the user as an infinite "Locating route" spinner.
        if (!navigator.HasValidRoute)
        {
            SetInstruction("Route unavailable", string.IsNullOrEmpty(navigator.LastRouteIssue)
                ? "Checking walkway and tracking. If this persists, rescan a registered location QR."
                : navigator.LastRouteIssue);
            return;
        }

        // Match the floor-aware sampling performed by IndoorPathVisualizer.
        // A handheld camera is above the NavMesh; search at the selected
        // destination/waypoint's floor Y, NOT the camera's head height.
        Vector3 cameraMapPos = arMapOrigin.InverseTransformPoint(arCamera.transform.position);
        cameraMapPos.y = destination.position.y;
        if (!NavMesh.SamplePosition(cameraMapPos, out NavMeshHit start, 2f, NavMesh.AllAreas) ||
            !NavMesh.SamplePosition(destination.position, out NavMeshHit end, 2f, NavMesh.AllAreas) ||
            Mathf.Abs(start.position.y - destination.position.y) > 0.65f ||
            Mathf.Abs(end.position.y - destination.position.y) > 0.65f ||
            !NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, guidancePath) ||
            guidancePath.status != NavMeshPathStatus.PathComplete)
        {
            SetInstruction("Route unavailable", "Could not verify a continuous walkway on this floor. Check the map or rescan its QR.");
            return;
        }
        if (guidancePath.corners.Length < 2)
        {
            SetInstruction("Approaching destination", "Continue carefully to the marked location.");
            return;
        }

        Vector3 nextCorner = guidancePath.corners[1];
        Vector3 directionInMap = nextCorner - start.position;
        directionInMap.y = 0f;
        if (directionInMap.sqrMagnitude < 0.01f)
        {
            SetInstruction("Continue straight", "Follow the floor route.");
            return;
        }

        Vector3 worldDirection = arMapOrigin.TransformDirection(directionInMap.normalized);
        worldDirection.y = 0f;
        Vector3 cameraForward = arCamera.transform.forward;
        cameraForward.y = 0f;
        if (worldDirection.sqrMagnitude < 0.001f || cameraForward.sqrMagnitude < 0.001f)
        {
            SetInstruction("Hold phone upright", "Direction will update automatically.");
            return;
        }

        float angle = Vector3.SignedAngle(cameraForward.normalized,
            worldDirection.normalized, Vector3.up);

        string instruction;
        if (Mathf.Abs(angle) >= turnAroundThresholdDegrees)
            instruction = "Turn around";
        else if (angle >= turnThresholdDegrees)
            instruction = "Turn right";
        else if (angle <= -turnThresholdDegrees)
            instruction = "Turn left";
        else
            instruction = "Continue straight";

        float meters = 0f;
        Vector3[] corners = guidancePath.corners;
        for (int i = 0; i < corners.Length - 1; i++)
            meters += Vector3.Distance(corners[i], corners[i + 1]);

        meters *= Mathf.Max(0.1f, physicalMetersPerMapUnit);
        SetInstruction(instruction, "Est. " + meters.ToString("F1") + " m via route  |  Follow the arrows");
    }

    private void SetInstruction(string title, string detail)
    {
        SetText(directionHeading, "YOUR NEXT MOVE");
        SetText(directionTitle, title);
        SetText(directionDetail, detail);
    }

    private void SetDirectionVisible(bool visible)
    {
        if (directionCard != null && directionCard.activeSelf != visible)
            directionCard.SetActive(visible);
    }

    private static void SetText(TMP_Text label, string message)
    {
        if (label != null && label.text != message)
            label.text = message;
    }

    // The themed panels are built at runtime. The working scene, component GUIDs,
    // existing QR calibration, and NavMesh calculations are not changed.
    private static readonly Color Cardinal = new Color(0.70f, 0.115f, 0.14f, 1f);
    private static readonly Color Burgundy = new Color(0.46f, 0.075f, 0.095f, 1f);
    private static readonly Color Cream = new Color(1f, 0.988f, 0.965f, 1f);
    private static readonly Color SoftCream = new Color(0.979f, 0.961f, 0.937f, 1f);
    private static readonly Color WarmWhite = new Color(1f, 0.996f, 0.985f, 1f);
    private static readonly Color Ink = new Color(0.20f, 0.17f, 0.17f, 1f);
    private static readonly Color MutedInk = new Color(0.49f, 0.44f, 0.43f, 1f);
    private static readonly Color RoseTint = new Color(0.98f, 0.88f, 0.87f, 1f);
    private static readonly Color Border = new Color(0.88f, 0.82f, 0.79f, 1f);

    private static Sprite roundedSprite;
    private static Texture2D roundedTexture;

    private void BuildAppearance()
    {
        Canvas canvas = destinationDropdown.GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            Debug.LogWarning("QRNavigationReadyUI: NavigationCanvas not found.");
            return;
        }
        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
        if (canvasRect == null) return;
        uiCanvasRect = canvasRect;

        if (arrivalPanel == null)
        {
            foreach (Transform child in canvas.transform)
            {
                if (child.name == "ArrivalPanel")
                {
                    arrivalPanel = child.gameObject;
                    break;
                }
            }
        }

        // First card: parish-themed status. Short enough to leave the camera unobstructed.
        headerCard = CreatePanel("SLRP_AR_Parish_Status", canvasRect,
            new Vector2(0.5f, 1f), new Vector2(0f, -48f),
            new Vector2(944f, 230f), Cardinal, true);
        AddShadow(headerCard, new Color(0.1f, 0.04f, 0.04f, 0.21f), new Vector2(0f, -7f));
        headerCardRect = headerCard.GetComponent<RectTransform>();

        // Solid crimson header. Legacy uGUI Text is used ONLY for the three
        // light-colored header strings because the project's TMP materials
        // have repeatedly rendered light text as an almost invisible dark tint.
        // The permanent RESCAN QR button on the right remains unchanged.
        CreateReliableHeaderText("Parish_Brand", headerCard.transform as RectTransform,
            new Vector2(-120f, -25f), new Vector2(565f, 32f),
            25, White, "SLRP AR", false);
        headerEyebrow = CreateReliableHeaderText("Status_Step", headerCard.transform as RectTransform,
            new Vector2(-120f, -78f), new Vector2(565f, 32f),
            24, White, "STEP 01 / SCANNING", false);

        // Keep the Inspector-linked TMP object and its reference intact,
        // but prevent its dark text from being drawn underneath the new label.
        RectTransform statusRect = statusText.rectTransform;
        statusRect.SetParent(headerCard.transform, false);
        SetRect(statusRect, new Vector2(0.5f, 1f), new Vector2(-120f, -123f),
            new Vector2(565f, 65f));
        statusText.enabled = false;
        readableStatusText = CreateReliableHeaderText("Visible_Status", headerCard.transform as RectTransform,
            new Vector2(-120f, -123f), new Vector2(565f, 65f),
            43, White, "Scan a location QR code", true);

        RectTransform progressBack = CreatePanel("Status_Progress_Back", headerCard.transform as RectTransform,
            new Vector2(0.5f, 0f), new Vector2(0f, 22f),
            new Vector2(820f, 8f), new Color(1f, 0.84f, 0.81f, 0.3f), true)
            .GetComponent<RectTransform>();
        progressFill = CreatePanel("Status_Progress_Fill", progressBack,
            new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, Teal, false)
            .GetComponent<RectTransform>();
        progressFill.anchorMin = new Vector2(0f, 0f);
        progressFill.anchorMax = new Vector2(0f, 1f);
        progressFill.pivot = new Vector2(0f, 0.5f);
        progressFill.offsetMin = Vector2.zero;
        progressFill.offsetMax = Vector2.zero;

        // No floating scanning hint: the stairs instruction now appears
        // inside the QR help, invalid-code and tracking warning modals.

        // Second card: light destination selector, following the parish dashboard.
        destinationCard = CreatePanel("SLRP_AR_Destination_Card", canvasRect,
            new Vector2(0.5f, 1f), new Vector2(0f, -306f),
            new Vector2(944f, 179f), Cream, true);
        AddShadow(destinationCard, new Color(0.12f, 0.04f, 0.04f, 0.15f), new Vector2(0f, -5f));
        destinationCardRect = destinationCard.GetComponent<RectTransform>();
        // Keep the destination panel below the dropdown's popup layer.
        // The selector itself will be parented INSIDE this card, so their
        // positions always remain aligned on both phone and Game view.
        destinationHeading = CreateLabel("Destination_Heading", destinationCard.transform as RectTransform,
            new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(812f, 40f),
            27f, Burgundy, TextAlignmentOptions.MidlineLeft, "CHOOSE YOUR DESTINATION");
        destinationHeading.fontStyle = FontStyles.Bold;
        CreatePanel("Destination_Divider", destinationCard.transform as RectTransform,
            new Vector2(0.5f, 1f), new Vector2(0f, -66f), new Vector2(820f, 2f), Border, false);

        RectTransform dropdownRect = destinationDropdown.GetComponent<RectTransform>();
        destinationSelectorRect = dropdownRect;
        dropdownRect.SetParent(destinationCard.transform, false);
        SetRect(dropdownRect, new Vector2(0.5f, 1f), new Vector2(0f, -92f),
            new Vector2(818f, 76f));
        dropdownRect.SetAsLastSibling();
        // Apply sizing to the existing visible caption independently of its old template.
        if (destinationDropdown.captionText != null)
        {
            RectTransform captionRect = destinationDropdown.captionText.rectTransform;
            captionRect.anchorMin = Vector2.zero;
            captionRect.anchorMax = Vector2.one;
            captionRect.offsetMin = new Vector2(22f, 2f);
            captionRect.offsetMax = new Vector2(-58f, -2f);
        }
        Image dropdownImage = destinationDropdown.GetComponent<Image>();
        StyleImage(dropdownImage, WarmWhite, true);
        if (dropdownImage != null)
        {
            dropdownImage.raycastTarget = true;
            UnityEngine.UI.Outline outline = dropdownImage.GetComponent<UnityEngine.UI.Outline>();
            if (outline == null) outline = dropdownImage.gameObject.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color(0.79f, 0.73f, 0.70f, 0.7f);
            outline.effectDistance = new Vector2(1f, -1f);
        }
        if (destinationDropdown.captionText != null)
        {
            destinationDropdown.captionText.color = Ink;
            destinationDropdown.captionText.fontSize = 32f;
            destinationDropdown.captionText.fontStyle = FontStyles.Normal;
            destinationDropdown.captionText.margin = Vector4.zero;
            destinationDropdown.captionText.alignment = TextAlignmentOptions.MidlineLeft;
            destinationDropdown.captionText.enableAutoSizing = false;
        }
        destinationDropdown.colors = MakeDropdownColors();
        destinationDropdown.transition = Selectable.Transition.ColorTint;
        StyleDropdownTemplate();
        BuildCustomDropdownTrigger();
        // Initial state: QR not scanned, selector entirely hidden.
        destinationCard.SetActive(false);

        // Third card: clear high-contrast route instruction, placed at bottom.
        directionCard = CreatePanel("SLRP_AR_Direction_Card", canvasRect,
            new Vector2(0.5f, 0f), new Vector2(0f, 76f),
            new Vector2(944f, 259f), Cream, true);
        AddShadow(directionCard, new Color(0.10f, 0.035f, 0.035f, 0.25f), new Vector2(0f, -6f));
        directionCardRect = directionCard.GetComponent<RectTransform>();
        CreatePanel("Direction_Red_Accent", directionCard.transform as RectTransform,
            new Vector2(0f, 0.5f), new Vector2(13f, 0f), new Vector2(10f, 168f), Cardinal, true);
        directionHeading = CreateLabel("Direction_Label", directionCard.transform as RectTransform,
            new Vector2(0.5f, 1f), new Vector2(12f, -31f), new Vector2(812f, 35f),
            25f, Cardinal, TextAlignmentOptions.MidlineLeft, "YOUR NEXT MOVE");
        directionHeading.fontStyle = FontStyles.Bold;
        directionTitle = CreateLabel("Direction_Instruction", directionCard.transform as RectTransform,
            new Vector2(0.5f, 1f), new Vector2(12f, -83f), new Vector2(812f, 80f),
            52f, Ink, TextAlignmentOptions.MidlineLeft, "Preparing...");
        directionTitle.fontStyle = FontStyles.Bold;
        directionTitle.enableAutoSizing = true;
        directionTitle.fontSizeMin = 32f;
        directionTitle.fontSizeMax = 52f;
        directionTitle.textWrappingMode = TextWrappingModes.Normal;
        directionTitle.overflowMode = TextOverflowModes.Ellipsis;
        directionDetail = CreateLabel("Direction_Detail", directionCard.transform as RectTransform,
            new Vector2(0.5f, 0f), new Vector2(12f, 26f), new Vector2(812f, 67f),
            23f, MutedInk, TextAlignmentOptions.MidlineLeft, "");
        directionDetail.enableAutoSizing = true;
        directionDetail.fontSizeMin = 17f;
        directionDetail.fontSizeMax = 23f;
        directionDetail.textWrappingMode = TextWrappingModes.Normal;
        directionDetail.overflowMode = TextOverflowModes.Ellipsis;
        directionCard.SetActive(false);
        BuildFloorChangeOverlay(canvasRect);
        RefreshSafeAreaLayout(true);
        StyleArrivalPanel();
    }

    // Dedicated, high-contrast staircase handoff. All AR and pathfinding logic
    // is still owned by ParishFloorNavigationController/IndoorPathVisualizer.
    private void BuildFloorChangeOverlay(RectTransform canvasRect)
    {
        floorChangeOverlay = CreatePanel("SLRP_AR_Floor_Change_Overlay", canvasRect,
            new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero,
            new Color(0.10f, 0.035f, 0.04f, 0.58f), false);
        RectTransform overlayRect = floorChangeOverlay.GetComponent<RectTransform>();
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;
        Image shade = floorChangeOverlay.GetComponent<Image>();
        shade.raycastTarget = true; // Prevent changing destinations mid-staircase.

        GameObject card = CreatePanel("SLRP_AR_Floor_Change_Instruction", overlayRect,
            new Vector2(0.5f, 0.5f), Vector2.zero,
            new Vector2(874f, 610f), Cream, true);
        floorChangeCardRect = card.GetComponent<RectTransform>();
        AddShadow(card, new Color(0f, 0f, 0f, 0.32f), new Vector2(0f, -8f));
        CreatePanel("Floor_Change_Accent", floorChangeCardRect,
            new Vector2(0.5f, 1f), new Vector2(0f, -12f),
            new Vector2(788f, 12f), Cardinal, true);

        Text eyebrow = CreateReliableHeaderText("Floor_Change_Eyebrow", floorChangeCardRect,
            new Vector2(0f, -45f), new Vector2(756f, 38f),
            25, Cardinal, "STAIRCASE TRANSITION", true);
        eyebrow.alignment = TextAnchor.MiddleCenter;

        floorChangeTitle = CreateReliableHeaderText("Floor_Change_Title", floorChangeCardRect,
            new Vector2(0f, -96f), new Vector2(754f, 103f),
            49, Ink, "Proceed to the second floor", true);
        floorChangeTitle.alignment = TextAnchor.MiddleCenter;
        floorChangeTitle.resizeTextMinSize = 30;
        floorChangeTitle.resizeTextMaxSize = 49;

        CreatePanel("Floor_Change_Divider", floorChangeCardRect,
            new Vector2(0.5f, 1f), new Vector2(0f, -221f),
            new Vector2(740f, 3f), Border, false);

        floorChangeDescription = CreateReliableHeaderText("Floor_Change_Body", floorChangeCardRect,
            new Vector2(0f, -244f), new Vector2(740f, 122f),
            29, Ink,
            "Use the public stairs. Scan the QR on the next floor to continue navigation.", false);
        floorChangeDescription.alignment = TextAnchor.MiddleCenter;
        floorChangeDescription.resizeTextForBestFit = true;
        floorChangeDescription.resizeTextMinSize = 22;
        floorChangeDescription.resizeTextMaxSize = 29;

        Text savedLabel = CreateReliableHeaderText("Floor_Change_Saved_Label", floorChangeCardRect,
            new Vector2(0f, -387f), new Vector2(742f, 32f),
            23, Cardinal, "DESTINATION SAVED", true);
        savedLabel.alignment = TextAnchor.MiddleCenter;
        floorChangeDestination = CreateReliableHeaderText("Floor_Change_Saved_Destination", floorChangeCardRect,
            new Vector2(0f, -425f), new Vector2(740f, 60f),
            35, Ink, "Main Church", true);
        floorChangeDestination.alignment = TextAnchor.MiddleCenter;

        GameObject scan = CreatePanel("SLRP_AR_Scan_QR_To_Continue", floorChangeCardRect,
            new Vector2(0.5f, 0f), new Vector2(0f, 27f),
            new Vector2(746f, 82f), Cardinal, true);
        Image scanImage = scan.GetComponent<Image>();
        scanImage.raycastTarget = true;
        Button scanButton = scan.AddComponent<Button>();
        scanButton.targetGraphic = scanImage;
        scanButton.onClick.AddListener(BeginNextFloorScan);
        Text scanLabel = CreateReliableHeaderText("Scan_QR_Button_Label",
            scan.GetComponent<RectTransform>(), new Vector2(0f, -12f),
            new Vector2(700f, 55f), 30, White, "SCAN QR TO CONTINUE", true);
        scanLabel.alignment = TextAnchor.MiddleCenter;
        floorChangeOverlay.transform.SetAsLastSibling();
        floorChangeOverlay.SetActive(false);
    }

    private void BeginNextFloorScan()
    {
        // Route preservation and actual QR reset stay in the existing UI.
        WayfinderTrackingWarningUI scanningUI = FindAnyObjectByType<WayfinderTrackingWarningUI>();
        if (scanningUI == null)
        {
            Debug.LogWarning("[SLRP AR] QR scan control unavailable. Use the permanent RESCAN QR button.");
            return;
        }
        scanningUI.BeginFloorChangeQrScan();
        SetFloorChangeOverlayVisible(false);
    }

    private void SetFloorChangeOverlayVisible(bool visible)
    {
        if (floorChangeOverlay != null && floorChangeOverlay.activeSelf != visible)
            floorChangeOverlay.SetActive(visible);
    }

    private void UpdateFloorChangeOverlayContent()
    {
        if (floorFlow == null) return;
        string target = floorFlow.TargetFloorName;
        string title = "Proceed to the " + target;
        string body = "Use the public stairs to reach the " + target +
            ". Scan the QR near the stairs on that floor to continue navigation.";
        string destination = GetSavedDestinationName();
        if (floorChangeTitle != null && floorChangeTitle.text != title)
            floorChangeTitle.text = title;
        if (floorChangeDescription != null && floorChangeDescription.text != body)
            floorChangeDescription.text = body;
        if (floorChangeDestination != null && floorChangeDestination.text != destination)
            floorChangeDestination.text = destination;
    }

    private string GetSavedDestinationName()
    {
        Transform destination = floorFlow != null ? floorFlow.RequestedDestination : null;
        if (destination == null) return "Your selected destination";
        string name = destination.name;
        if (name.StartsWith("F1_") || name.StartsWith("F2_")) name = name.Substring(3);
        name = name.Replace('_', ' ');
        var spaced = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
                spaced.Append(' ');
            spaced.Append(name[i]);
        }
        return spaced.ToString();
    }

    // Anchored panels need to respect OS gesture bars/notches. Compute in
    // canvas units, rather than assuming Screen.safeArea pixels == UI units.
    private void RefreshSafeAreaLayout(bool force = false)
    {
        if (uiCanvasRect == null || headerCardRect == null ||
            destinationCardRect == null || directionCardRect == null) return;
        Rect safe = Screen.safeArea;
        Vector2 size = uiCanvasRect.rect.size;
        if (!force && safe == lastSafeArea && size == lastSafeAreaSize) return;
        lastSafeArea = safe;
        lastSafeAreaSize = size;

        Canvas canvas = uiCanvasRect.GetComponent<Canvas>();
        Camera uiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera : null;
        Vector2 topLocal;
        Vector2 bottomLocal;
        bool gotTop = RectTransformUtility.ScreenPointToLocalPointInRectangle(uiCanvasRect,
            new Vector2(safe.center.x, safe.yMax), uiCamera, out topLocal);
        bool gotBottom = RectTransformUtility.ScreenPointToLocalPointInRectangle(uiCanvasRect,
            new Vector2(safe.center.x, safe.yMin), uiCamera, out bottomLocal);
        float topInset = gotTop ? Mathf.Max(0f, uiCanvasRect.rect.yMax - topLocal.y) : 0f;
        float bottomInset = gotBottom ? Mathf.Max(0f, bottomLocal.y - uiCanvasRect.rect.yMin) : 0f;
        topInset = Mathf.Min(topInset, size.y * 0.2f);
        bottomInset = Mathf.Min(bottomInset, size.y * 0.2f);
        float cardWidth = Mathf.Min(944f, Mathf.Max(310f, size.x - 36f));
        SetRect(headerCardRect, new Vector2(0.5f, 1f),
            new Vector2(0f, -topInset - 28f), new Vector2(cardWidth, 230f));
        SetRect(destinationCardRect, new Vector2(0.5f, 1f),
            new Vector2(0f, -topInset - 284f), new Vector2(cardWidth, 179f));
        SetRect(directionCardRect, new Vector2(0.5f, 0f),
            new Vector2(0f, bottomInset + 26f), new Vector2(cardWidth, 259f));

        if (floorChangeCardRect != null)
        {
            float safeHeight = Mathf.Max(100f, size.y - topInset - bottomInset - 34f);
            float safeWidth = Mathf.Max(100f, size.x - 44f);
            // Scale the whole card rather than scaling individual children:
            // that preserves text/button spacing on narrower mobile screens.
            float scale = Mathf.Min(1f, safeHeight / 610f, safeWidth / 874f);
            SetRect(floorChangeCardRect, new Vector2(0.5f, 0.5f),
                new Vector2(0f, (bottomInset - topInset) * 0.5f),
                new Vector2(874f, 610f));
            floorChangeCardRect.localScale = Vector3.one * scale;
        }
    }

    // A transparent button above the stock TMP_Dropdown prevents Unity's
    // default popup from opening. The real dropdown is still the data source,
    // so NavigationUIManager's existing onValueChanged listener still works.
    private void BuildCustomDropdownTrigger()
    {
        if (destinationSelectorRect == null) return;
        GameObject overlay = new GameObject("SLRP_AR_Styled_Destination_Trigger",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        overlay.layer = 5;
        overlay.transform.SetParent(destinationSelectorRect, false);
        RectTransform rect = overlay.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        Image hitArea = overlay.GetComponent<Image>();
        hitArea.color = new Color(1f, 1f, 1f, 0.001f);
        hitArea.raycastTarget = true;
        customDropdownTrigger = overlay.GetComponent<Button>();
        customDropdownTrigger.transition = Selectable.Transition.None;
        customDropdownTrigger.onClick.AddListener(ToggleCustomOptions);
        overlay.transform.SetAsLastSibling();
    }

    private void ToggleCustomOptions()
    {
        if (customOptionPopup != null)
        {
            CloseCustomOptions();
            return;
        }
        if (!ready || destinationDropdown == null || !destinationDropdown.interactable ||
            destinationCard == null || !destinationCard.activeInHierarchy || uiCanvasRect == null)
            return;

        // A transparent overlay closes the popup when tapping elsewhere.
        customOptionBackdrop = new GameObject("SLRP_AR_Option_Dismiss",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        customOptionBackdrop.layer = 5;
        customOptionBackdrop.transform.SetParent(uiCanvasRect, false);
        RectTransform backdropRect = customOptionBackdrop.GetComponent<RectTransform>();
        backdropRect.anchorMin = Vector2.zero;
        backdropRect.anchorMax = Vector2.one;
        backdropRect.offsetMin = Vector2.zero;
        backdropRect.offsetMax = Vector2.zero;
        Image backdropImage = customOptionBackdrop.GetComponent<Image>();
        backdropImage.color = new Color(0f, 0f, 0f, 0.001f);
        backdropImage.raycastTarget = true;
        customOptionBackdrop.GetComponent<Button>().onClick.AddListener(CloseCustomOptions);

        // Locate popup relative to the dropdown's actual screen position,
        // rather than assuming one canvas size or device aspect ratio.
        Vector3[] corners = new Vector3[4];
        destinationSelectorRect.GetWorldCorners(corners);
        Vector3 bottomCenter = (corners[0] + corners[3]) * 0.5f;
        Vector3 canvasPoint = uiCanvasRect.InverseTransformPoint(bottomCenter);
        float topFromCanvas = canvasPoint.y - uiCanvasRect.rect.yMax - 8f;
        float freeHeight = uiCanvasRect.rect.height + topFromCanvas - 32f;
        int count = destinationDropdown.options.Count;
        float contentHeight = 22f + count * PopupRowHeight +
            Mathf.Max(0, count - 1) * PopupRowGap;
        float popupHeight = Mathf.Clamp(Mathf.Min(548f, contentHeight),
            180f, Mathf.Max(180f, freeHeight));

        customOptionPopup = CreatePanel("SLRP_AR_Styled_Options", uiCanvasRect,
            new Vector2(0.5f, 1f), new Vector2(canvasPoint.x, topFromCanvas),
            new Vector2(destinationSelectorRect.rect.width, popupHeight), WarmWhite, true);
        Image popupImage = customOptionPopup.GetComponent<Image>();
        popupImage.raycastTarget = true;
        AddShadow(customOptionPopup,
            new Color(0.20f, 0.06f, 0.07f, 0.30f), new Vector2(0f, -6f));

        GameObject viewport = new GameObject("Options_Viewport",
            typeof(RectTransform), typeof(RectMask2D));
        viewport.layer = 5;
        viewport.transform.SetParent(customOptionPopup.transform, false);
        RectTransform viewportRect = viewport.GetComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = new Vector2(8f, 8f);
        viewportRect.offsetMax = new Vector2(-8f, -8f);

        GameObject content = new GameObject("Options_Content", typeof(RectTransform));
        content.layer = 5;
        content.transform.SetParent(viewport.transform, false);
        RectTransform contentRect = content.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = new Vector2(0f, contentHeight);

        ScrollRect scroll = customOptionPopup.AddComponent<ScrollRect>();
        scroll.content = contentRect;
        scroll.viewport = viewportRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 44f;

        for (int i = 0; i < count; i++)
        {
            int selectedIndex = i;
            Color bg = i == destinationDropdown.value ? RoseTint : WarmWhite;
            GameObject row = CreatePanel("Option_" + i, contentRect,
                new Vector2(0.5f, 1f),
                new Vector2(0f, -11f - i * (PopupRowHeight + PopupRowGap)),
                new Vector2(0f, PopupRowHeight), bg, true);
            RectTransform rowRect = row.GetComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.offsetMin = new Vector2(12f, rowRect.offsetMin.y);
            rowRect.offsetMax = new Vector2(-12f, rowRect.offsetMax.y);
            Image rowImage = row.GetComponent<Image>();
            rowImage.raycastTarget = true;
            Button rowButton = row.AddComponent<Button>();
            rowButton.targetGraphic = rowImage;
            rowButton.transition = Selectable.Transition.ColorTint;
            ColorBlock colors = ColorBlock.defaultColorBlock;
            colors.normalColor = bg;
            colors.highlightedColor = RoseTint;
            colors.pressedColor = new Color(0.93f, 0.73f, 0.72f, 1f);
            colors.selectedColor = RoseTint;
            colors.colorMultiplier = 1f;
            rowButton.colors = colors;
            rowButton.onClick.AddListener(() =>
            {
                destinationDropdown.value = selectedIndex;
                destinationDropdown.RefreshShownValue();
                CloseCustomOptions();
            });

            string name = destinationDropdown.options[i].text;
            TMP_Text label = CreateLabel("Option_Label", rowRect,
                new Vector2(0.5f, 0.5f), new Vector2(-12f, 0f),
                new Vector2(Mathf.Max(100f, destinationSelectorRect.rect.width - 112f),
                    PopupRowHeight - 12f),
                31f, Ink, TextAlignmentOptions.MidlineLeft, name);
            label.fontStyle = i == destinationDropdown.value ? FontStyles.Bold : FontStyles.Normal;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            if (i == destinationDropdown.value)
                CreateLabel("Selected_Mark", rowRect,
                    new Vector2(1f, 0.5f), new Vector2(-31f, 0f),
                    new Vector2(40f, 50f), 33f, Cardinal,
                    TextAlignmentOptions.Center, "✓");
        }
    }

    private void CloseCustomOptions()
    {
        if (customOptionPopup != null) Destroy(customOptionPopup);
        if (customOptionBackdrop != null) Destroy(customOptionBackdrop);
        customOptionPopup = null;
        customOptionBackdrop = null;
    }

    private void StyleDropdownTemplate()
    {
        RectTransform template = destinationDropdown.template;
        if (template == null) return;

        // TMP_Dropdown clones this template every time it opens, so these changes
        // automatically style every generated row and the scrolling list.
        template.anchorMin = new Vector2(0f, 0f);
        template.anchorMax = new Vector2(1f, 0f);
        template.pivot = new Vector2(0.5f, 1f);
        template.anchoredPosition = new Vector2(0f, -6f);
        template.sizeDelta = new Vector2(0f, 525f);
        StyleImage(template.GetComponent<Image>(), WarmWhite, true);
        AddShadow(template.gameObject, new Color(0.12f, 0.04f, 0.04f, 0.30f), new Vector2(0f, -6f));

        ScrollRect scroll = template.GetComponent<ScrollRect>();
        if (scroll != null)
        {
            scroll.scrollSensitivity = 24f;
            if (scroll.viewport != null)
                StyleImage(scroll.viewport.GetComponent<Image>(), WarmWhite, false);
            if (scroll.verticalScrollbar != null)
            {
                StyleImage(scroll.verticalScrollbar.GetComponent<Image>(), SoftCream, false);
                if (scroll.verticalScrollbar.targetGraphic is Image handle)
                    StyleImage(handle, new Color(0.73f, 0.20f, 0.21f, 1f), true);
            }
        }

        if (destinationDropdown.itemText != null)
        {
            TMP_Text itemLabel = destinationDropdown.itemText;
            itemLabel.color = Ink;
            itemLabel.fontSize = 31f;
            itemLabel.fontStyle = FontStyles.Normal;
            itemLabel.alignment = TextAlignmentOptions.MidlineLeft;
            itemLabel.margin = Vector4.zero;
            itemLabel.enableAutoSizing = false;
            RectTransform itemLabelRect = itemLabel.rectTransform;
            itemLabelRect.anchorMin = new Vector2(0f, 0f);
            itemLabelRect.anchorMax = new Vector2(1f, 1f);
            itemLabelRect.offsetMin = new Vector2(70f, 2f);
            itemLabelRect.offsetMax = new Vector2(-18f, -2f);

            Toggle itemToggle = itemLabel.GetComponentInParent<Toggle>();
            if (itemToggle != null)
            {
                RectTransform row = itemToggle.GetComponent<RectTransform>();
                row.sizeDelta = new Vector2(row.sizeDelta.x, 70f);
                Image background = itemToggle.targetGraphic as Image;
                if (background == null)
                {
                    Transform bg = row.Find("Item Background");
                    if (bg != null) background = bg.GetComponent<Image>();
                }
                if (background != null)
                {
                    StyleImage(background, WarmWhite, false);
                    itemToggle.targetGraphic = background;
                }
                itemToggle.transition = Selectable.Transition.ColorTint;
                ColorBlock c = ColorBlock.defaultColorBlock;
                c.normalColor = Color.white;
                c.highlightedColor = RoseTint;
                c.pressedColor = new Color(0.96f, 0.77f, 0.76f, 1f);
                c.selectedColor = new Color(0.98f, 0.88f, 0.87f, 1f);
                c.disabledColor = SoftCream;
                c.colorMultiplier = 1f;
                c.fadeDuration = 0.12f;
                itemToggle.colors = c;
                if (itemToggle.graphic is Image checkmark)
                {
                    StyleImage(checkmark, Cardinal, false);
                    checkmark.raycastTarget = false;
                    RectTransform checkRect = checkmark.rectTransform;
                    checkRect.anchorMin = checkRect.anchorMax = new Vector2(0.5f, 0.5f);
                    checkRect.anchoredPosition = Vector2.zero;
                    checkRect.sizeDelta = new Vector2(22f, 22f);
                }
                RectTransform checkArea = itemToggle.graphic != null ?
                    itemToggle.graphic.rectTransform.parent as RectTransform : null;
                if (checkArea != null)
                {
                    checkArea.anchorMin = checkArea.anchorMax = new Vector2(0f, 0.5f);
                    checkArea.pivot = new Vector2(0.5f, 0.5f);
                    checkArea.anchoredPosition = new Vector2(37f, 0f);
                    checkArea.sizeDelta = new Vector2(37f, 37f);
                }
            }
        }

        // Original 25px row height is the main reason the options looked unstyled.
        Transform captionArrow = destinationDropdown.transform.Find("Arrow");
        if (captionArrow != null)
        {
            Image chevron = captionArrow.GetComponent<Image>();
            if (chevron != null)
            {
                chevron.color = Cardinal;
                chevron.raycastTarget = false;
            }
            RectTransform chevronRect = captionArrow.GetComponent<RectTransform>();
            if (chevronRect != null)
                chevronRect.sizeDelta = new Vector2(28f, 28f);
        }
    }

    // When opened, TMP_Dropdown creates a separate popup in the Canvas.
    // Styling only the inactive Template is not always reflected by cloned rows,
    // especially when the selection was populated during Start by another script.
    private void StyleOpenedDropdownIfNeeded()
    {
        if (destinationDropdown == null) return;

        Canvas canvas = destinationDropdown.GetComponentInParent<Canvas>();
        if (canvas == null) return;

        Transform popup = canvas.transform.Find("Dropdown List");
        if (popup == null)
        {
            // Some TMPro versions parent the popup differently.
            GameObject popupObject = GameObject.Find("Dropdown List");
            if (popupObject != null) popup = popupObject.transform;
        }
        if (popup == null)
        {
            lastStyledDropdownList = null;
            return;
        }

        if (popup.gameObject == lastStyledDropdownList) return;
        lastStyledDropdownList = popup.gameObject;

        StyleImage(popup.GetComponent<Image>(), WarmWhite, true);
        AddShadow(popup.gameObject,
            new Color(0.25f, 0.08f, 0.08f, 0.28f), new Vector2(0f, -5f));

        ScrollRect scrolling = popup.GetComponent<ScrollRect>();
        if (scrolling != null)
        {
            scrolling.scrollSensitivity = 30f;
            if (scrolling.viewport != null)
                StyleImage(scrolling.viewport.GetComponent<Image>(), WarmWhite, false);
            if (scrolling.verticalScrollbar != null)
            {
                StyleImage(scrolling.verticalScrollbar.GetComponent<Image>(), SoftCream, false);
                if (scrolling.verticalScrollbar.targetGraphic is Image handle)
                    StyleImage(handle, Cardinal, true);
            }
        }

        foreach (Toggle row in popup.GetComponentsInChildren<Toggle>(true))
        {
            if (row.targetGraphic is Image bg)
            {
                StyleImage(bg, WarmWhite, false);
                ColorBlock colors = ColorBlock.defaultColorBlock;
                colors.normalColor = Color.white;
                colors.highlightedColor = RoseTint;
                colors.pressedColor = new Color(0.94f, 0.74f, 0.72f, 1f);
                colors.selectedColor = RoseTint;
                colors.disabledColor = SoftCream;
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0.12f;
                row.transition = Selectable.Transition.ColorTint;
                row.colors = colors;
            }

            TMP_Text optionLabel = row.GetComponentInChildren<TMP_Text>(true);
            if (optionLabel != null)
            {
                optionLabel.color = Ink;
                optionLabel.fontSize = 31f;
                optionLabel.fontStyle = FontStyles.Normal;
                optionLabel.enableAutoSizing = false;
                optionLabel.alignment = TextAlignmentOptions.MidlineLeft;
                optionLabel.raycastTarget = false;
            }
            if (row.graphic is Image check)
            {
                check.color = Cardinal;
                check.raycastTarget = false;
            }
        }
    }

    private void StyleArrivalPanel()
    {
        if (arrivalPanel == null) return;
        StyleImage(arrivalPanel.GetComponent<Image>(), new Color(0.43f, 0.065f, 0.085f, 0.97f), false);
        TMP_Text message = arrivalPanel.GetComponentInChildren<TMP_Text>(true);
        if (message != null)
        {
            message.fontSize = 55f;
            message.fontStyle = FontStyles.Bold;
            message.color = Cream;
            message.alignment = TextAlignmentOptions.Center;
            SetRect(message.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 115f),
                new Vector2(865f, 250f));
        }
        Button closeButton = arrivalPanel.GetComponentInChildren<Button>(true);
        if (closeButton != null)
        {
            StyleImage(closeButton.GetComponent<Image>(), Cream, true);
            SetRect(closeButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f),
                new Vector2(0f, -155f), new Vector2(630f, 116f));
            TMP_Text buttonText = closeButton.GetComponentInChildren<TMP_Text>(true);
            if (buttonText != null)
            {
                buttonText.text = "Start another route";
                buttonText.fontSize = 36f;
                buttonText.color = Burgundy;
                buttonText.fontStyle = FontStyles.Bold;
            }
        }
        arrivalPanel.transform.SetAsLastSibling();
    }

    private static ColorBlock MakeDropdownColors()
    {
        ColorBlock block = ColorBlock.defaultColorBlock;
        block.normalColor = Color.white;
        block.highlightedColor = new Color(1f, 0.94f, 0.94f, 1f);
        block.pressedColor = RoseTint;
        block.selectedColor = Color.white;
        block.disabledColor = new Color(0.88f, 0.83f, 0.81f, 1f);
        block.colorMultiplier = 1f;
        return block;
    }

    private static void StyleImage(Image image, Color color, bool rounded)
    {
        if (image == null) return;
        image.color = color;
        if (rounded)
        {
            Sprite sprite = GetRoundedSprite();
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
            }
        }
    }

    private static Sprite GetRoundedSprite()
    {
        if (roundedSprite != null) return roundedSprite;
        const int size = 64;
        const float radius = 15f;
        roundedTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        roundedTexture.name = "SLRP_AR_Rounded_Card_Runtime";
        roundedTexture.wrapMode = TextureWrapMode.Clamp;
        roundedTexture.filterMode = FilterMode.Bilinear;
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(Mathf.Abs(x - 31.5f) - (size * 0.5f - radius), 0f);
                float dy = Mathf.Max(Mathf.Abs(y - 31.5f) - (size * 0.5f - radius), 0f);
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                byte alpha = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(radius + 0.5f - distance));
                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }
        }
        roundedTexture.SetPixels32(pixels);
        roundedTexture.Apply(false, false);
        roundedSprite = Sprite.Create(roundedTexture, new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
            new Vector4(20f, 20f, 20f, 20f));
        roundedSprite.name = "SLRP_AR_Sliced_Rounded";
        return roundedSprite;
    }

    private static GameObject CreatePanel(string name, RectTransform parent,
        Vector2 anchor, Vector2 position, Vector2 size, Color color, bool rounded)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        obj.layer = 5;
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.GetComponent<RectTransform>();
        SetRect(rect, anchor, position, size);
        Image image = obj.GetComponent<Image>();
        StyleImage(image, color, rounded);
        image.raycastTarget = false;
        return obj;
    }

    private static void AddShadow(GameObject obj, Color color, Vector2 offset)
    {
        if (obj == null) return;
        Shadow shadow = obj.GetComponent<Shadow>();
        if (shadow == null) shadow = obj.AddComponent<Shadow>();
        shadow.effectColor = color;
        shadow.effectDistance = offset;
        shadow.useGraphicAlpha = true;
    }

    private static void SetRect(RectTransform rect, Vector2 anchor,
        Vector2 position, Vector2 size)
    {
        if (rect == null) return;
        rect.anchorMin = rect.anchorMax = anchor;
        rect.pivot = anchor.y > 0.9f ? new Vector2(0.5f, 1f) :
            anchor.y < 0.1f ? new Vector2(0.5f, 0f) : new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }

    // A small, isolated fallback for light lettering on crimson surfaces.
    // Unity 6 includes LegacyRuntime.ttf. OS font fallback covers unusual builds.
    private static Text CreateReliableHeaderText(string name, RectTransform parent,
        Vector2 position, Vector2 size, int fontSize, Color color,
        string value, bool bold)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
        obj.layer = 5;
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.GetComponent<RectTransform>();
        SetRect(rect, new Vector2(0.5f, 1f), position, size);
        Text label = obj.AddComponent<Text>();
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Font.CreateDynamicFontFromOSFont(
                new string[] { "Arial", "Roboto", "Helvetica", "Liberation Sans" }, fontSize);
        label.font = font;
        label.fontSize = fontSize;
        label.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.resizeTextForBestFit = bold;
        if (bold)
        {
            label.resizeTextMinSize = 24;
            label.resizeTextMaxSize = fontSize;
        }
        label.color = color;
        label.raycastTarget = false;
        label.text = value;
        return label;
    }

    private TMP_Text CreateLabel(string name, RectTransform parent, Vector2 anchor,
        Vector2 position, Vector2 size, float fontSize, Color color,
        TextAlignmentOptions alignment, string value)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
        obj.layer = 5;
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.GetComponent<RectTransform>();
        SetRect(rect, anchor, position, size);
        TextMeshProUGUI label = obj.AddComponent<TextMeshProUGUI>();
        if (statusText != null && statusText.font != null) label.font = statusText.font;
        label.fontSize = fontSize;
        label.color = color;
        label.alignment = alignment;
        label.raycastTarget = false;
        label.text = value;
        return label;
    }
}
