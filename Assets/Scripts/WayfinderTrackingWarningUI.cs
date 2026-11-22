using System;
using System.Collections;
using Unity.Collections;
using ZXing;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Add-on for the existing SLRP AR NavigationCanvas. Does not modify the
/// route generation, ARMapOrigin, QR reference library or existing scene.
/// Reports AR SESSION tracking loss; it does not claim to measure room brightness.
/// </summary>
public class WayfinderTrackingWarningUI : MonoBehaviour
{
    [Header("Existing references (auto-found if unassigned)")]
    [SerializeField] private QRMapCalibrator qrCalibrator;
    [SerializeField] private IndoorPathVisualizer navigator;
    [SerializeField] private TMP_Dropdown destinationDropdown;
    [SerializeField] private ARSession arSession;
    [SerializeField] private ARCameraManager arCameraManager;
    [SerializeField] private Canvas navigationCanvas;

    [Header("QR Content Validation")]
    [Tooltip("Show an error popup when an unrelated QR is decoded. Parish identity verification always decodes QR contents regardless of this popup setting.")]
    [SerializeField] private bool checkUnknownQrCodes = true;
    [Tooltip("Actual text encoded inside your authorized QR images. Add every new marker's payload here when expanding to other rooms.")]
    [SerializeField] private string[] authorizedQrContents =
    {
        "https://qr.generatorqr.com/CSH43Gawp"
    };
    [Tooltip("Seconds between camera QR decoding attempts (not AR image tracking).")]
    [SerializeField, Range(0.5f, 3f)] private float qrCheckIntervalSeconds = 0.9f;

    [Header("QR Scan Help")]
    [SerializeField, Min(5f)] private float scanHelpAfterSeconds = 18f;

    private GameObject overlay;
    private Text headingText;
    private TMP_Text explanationText;
    private TMP_Text stairsReminderText;
    private Button rescanButton;
    private Button dismissButton;
    private Button quickRescanButton;
    // Kept in a separately sorted child canvas so the button is accessible
    // while scanning, navigating, and after the arrival panel appears.
    private RectTransform persistentRescanCanvas;
    private bool currentlyCalibrated;
    private bool scanHelpShown;
    private bool trackingPaused;
    private bool warnedRescanRequired;
    private float scanStartedAt;
    private bool rescanRunning;
    private ZXing.QrCode.QRCodeReader qrReader;
    private float nextQrCheck;
    private string lastInvalidPayload;
    private float lastInvalidAlertAt = -100f;
    private bool invalidQrModalVisible;
    private bool warnedCameraMissing;
    private float nextCpuImageWarningAt = -100f;
    private float nextDecodeErrorLogAt = -100f;
    private float lastVerifiedPayloadLogAt = -100f;
    private string lastLoggedVerifiedMarker = string.Empty;

    // Most AR image trackers do not decode QR payloads. The calibrator reads
    // this only after ZXing actually decodes a parish QR using the CPU camera.
    private string lastDecodedParishMarker = string.Empty;
    private float lastParishDecodeTime = -100f;
    public bool TryGetRecentlyDecodedParishMarker(out string markerName)
    {
        markerName = lastDecodedParishMarker;
        // 3 seconds bridges the decoder's 0.9-second interval and the
        // calibrator's 1.25-second stable-tracking window.
        return !string.IsNullOrEmpty(markerName) &&
               Time.time - lastParishDecodeTime <= 3f;
    }

    private static readonly Color Crimson = new Color(0.67f, 0.12f, 0.15f, 1f);
    private static readonly Color Cream = new Color(0.99f, 0.965f, 0.925f, 1f);
    private static readonly Color TextDark = new Color(0.20f, 0.14f, 0.15f, 1f);

    private void Start()
    {
        if (qrCalibrator == null) qrCalibrator = FindAnyObjectByType<QRMapCalibrator>();
        if (navigator == null) navigator = FindAnyObjectByType<IndoorPathVisualizer>();
        if (arSession == null) arSession = FindAnyObjectByType<ARSession>();
        if (arCameraManager == null)
            arCameraManager = FindAnyObjectByType<ARCameraManager>();
        // Calibration identity verification is separate from the optional
        // invalid-QR warning popup. The scene may have serialized the popup
        // toggle to false before parish verification was added; still decode.
        qrReader = new ZXing.QrCode.QRCodeReader();
#if UNITY_IOS && !UNITY_EDITOR
        Debug.Log("[SLRP AR QR][iOS] QR-content decoding enabled: " +
            "direct luminance + converted camera-image fallback. " +
            "Search Xcode console for [SLRP AR QR].");
#endif
        if (!checkUnknownQrCodes)
            Debug.Log("[SLRP AR QR] Invalid-code popup is disabled, " +
                "but authorized parish QR identity decoding is ACTIVE.");
        if (navigationCanvas == null)
        {
            GameObject named = GameObject.Find("NavigationCanvas");
            if (named != null) navigationCanvas = named.GetComponent<Canvas>();
            if (navigationCanvas == null) navigationCanvas = FindAnyObjectByType<Canvas>();
        }
        if (destinationDropdown == null && navigationCanvas != null)
            destinationDropdown = navigationCanvas.GetComponentInChildren<TMP_Dropdown>(true);

        if (qrCalibrator == null || navigator == null || navigationCanvas == null)
        {
            Debug.LogError("SLRP AR: QR calibrator, navigator or NavigationCanvas missing.");
            enabled = false;
            return;
        }
        BuildModal();
        currentlyCalibrated = qrCalibrator.IsCalibrated;
        scanStartedAt = Time.time;
    }

    private void LateUpdate()
    {
        if (quickRescanButton == null || navigationCanvas == null) return;
        RectTransform root = navigationCanvas.GetComponent<RectTransform>();
        RectTransform buttonRect = quickRescanButton.GetComponent<RectTransform>();
        if (root == null || buttonRect == null) return;
        Rect safe = Screen.safeArea;
        Camera cam = navigationCanvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null : navigationCanvas.worldCamera;
        Vector2 topLocal;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root,
            new Vector2(safe.center.x, safe.yMax), cam, out topLocal)) return;
        float inset = Mathf.Clamp(root.rect.yMax - topLocal.y, 0f, root.rect.height * 0.2f);
        float width = Mathf.Min(944f, Mathf.Max(310f, root.rect.width - 36f));
        Vector2 desired = new Vector2(width * 0.5f - 32f, -inset - 42f);
        if (buttonRect.anchoredPosition != desired)
            buttonRect.anchoredPosition = desired;
    }

    private void Update()
    {
        if (qrCalibrator == null || navigator == null) return;

        bool calibrated = qrCalibrator.IsCalibrated;
        if (!calibrated)
        {
            if (currentlyCalibrated)
            {
                currentlyCalibrated = false;
                scanStartedAt = Time.time;
                scanHelpShown = false;
            }
            // ZXing decodes actual QR contents from the AR camera when supported.
            // A QR that cannot be decoded must NOT be labeled invalid.
            TryDecodeVisibleQr();

            // A scan timeout is help, not proof of an invalid QR.
            if (!invalidQrModalVisible && !scanHelpShown && !rescanRunning &&
                Time.time - scanStartedAt >= scanHelpAfterSeconds)
            {
                scanHelpShown = true;
                string verifiedMarker;
                bool identityVerified = TryGetRecentlyDecodedParishMarker(out verifiedMarker);
                if (identityVerified)
                    ShowModal("AR POSITION NOT READY",
                        "SLRP AR identified the QR, but image tracking has not " +
                        "established a stable position. Keep the marker fully visible, " +
                        "hold both phones still, and check lighting and marker size.");
                else
                    ShowModal("QR SCAN TAKING LONGER",
                        "Point your camera at an authorized location QR code. " +
                        "Hold steady, improve the lighting, and avoid screen glare.");
            }
            return;
        }

        if (!currentlyCalibrated)
        {
            currentlyCalibrated = true;
            scanHelpShown = false;
            HideModal();
        }

        // NavMesh and arrival detection are now gated IN IndoorPathVisualizer,
        // not by disabling its component here. Report the safety state only.
        bool routeActive = navigator.CurrentDestination != null && !navigator.HasArrived;
        if (!routeActive)
        {
            trackingPaused = false;
            warnedRescanRequired = false;
            return;
        }

        if (navigator.IsRouteSafetyPaused)
        {
            bool needsRescan = navigator.RequiresQrRescan;
            // A freshly scanned QR may need a moment to settle. Show a
            // tracking-loss popup only for an actual interruption or a jump,
            // not on the first normal post-scan stabilization interval.
            if (navigator.TrackingSafetyAlertNeeded)
            {
                if (!trackingPaused || warnedRescanRequired != needsRescan)
                {
                    warnedRescanRequired = needsRescan;
                    string title = needsRescan ? "RECALIBRATION NEEDED" : "AR TRACKING UNRELIABLE";
                    string message = navigator.TrackingSafetyMessage;
                    ShowModal(title, message);
                }
                trackingPaused = true;
            }
            return;
        }

        if (trackingPaused)
        {
            trackingPaused = false;
            warnedRescanRequired = false;
            ShowModal("TRACKING RETURNED",
                "Tracking has resumed. Check that the arrows align before walking. If they do not, rescan a registered QR near the stairs or Function Hall.");
        }
    }

    // Called by the dedicated floor-change card. Uses the EXACT existing
    // rescan flow, preserving the pending destination and QR validation.
    public void BeginFloorChangeQrScan()
    {
        RetryScan();
    }

    private void RetryScan()
    {
        if (rescanRunning) return;
        StartCoroutine(ResetAndRescan());
    }

    private IEnumerator ResetAndRescan()
    {
        rescanRunning = true;
        // Preserve a pending cross-floor FINAL destination during QR rescan.
        // Ordinary completed routes still reset normally.
        ParishFloorNavigationController parish = FindAnyObjectByType<ParishFloorNavigationController>();
        if (parish != null) parish.NotifyRescanStarted();
        if (navigator != null) navigator.StopNavigation();
        // The completed-route overlay may still be showing. Close it before
        // resetting, otherwise users cannot see the new scanning flow.
        GameObject arrivalPanel = GameObject.Find("ArrivalPanel");
        if (arrivalPanel != null) arrivalPanel.SetActive(false);
        if (destinationDropdown != null)
        {
            destinationDropdown.Hide();
            destinationDropdown.SetValueWithoutNotify(0);
        }
        if (navigator != null) navigator.ResetSafetyForRescan();
        trackingPaused = false;
        warnedRescanRequired = false;
        if (qrCalibrator != null) qrCalibrator.ResetCalibration();
        currentlyCalibrated = false;
        scanStartedAt = Time.time;
        scanHelpShown = false;
        lastInvalidPayload = null;
        lastDecodedParishMarker = string.Empty;
        lastParishDecodeTime = -100f;
        nextQrCheck = Time.time + 0.5f;
        lastLoggedVerifiedMarker = string.Empty;
        HideModal();

        // ARSession.Reset() restarts the AR tracking session without closing
        // the Unity app. ARKit / ARCore still need light and visual features.
        if (arSession != null) arSession.Reset();
        else Debug.LogWarning("SLRP AR: ARSession not found. Calibration reset only.");

        yield return null;
        rescanRunning = false;
    }

    private void ShowModal(string title, string message, bool invalidQr = false)
    {
        if (overlay == null) return;
        invalidQrModalVisible = invalidQr;
        headingText.text = title;
        explanationText.text = message;
        overlay.SetActive(true);
        overlay.transform.SetAsLastSibling();
    }

    private void HideModal()
    {
        invalidQrModalVisible = false;
        if (overlay != null) overlay.SetActive(false);
    }

    // ARTrackedImageManager can recognize registered images, but cannot decode
    // an arbitrary QR. ZXing examines an occasional camera CPU image to detect
    // QR codes whose decoded content is NOT on our authorized list.
    private void TryDecodeVisibleQr()
    {
        if (rescanRunning || qrReader == null || Time.time < nextQrCheck)
            return;

        if (arCameraManager == null)
        {
            if (!warnedCameraMissing)
            {
                warnedCameraMissing = true;
                Debug.LogError("[SLRP AR QR] ARCameraManager missing. QR contents " +
                    "cannot be verified, so parish calibration is waiting. " +
                    "Assign the enabled AR Camera Manager from Main Camera.");
            }
            return;
        }

        // ARCore image tracking can report a marker before the session exposes
        // a CPU image. We retry; this does NOT imply the QR is unauthorized.
        nextQrCheck = Time.time + qrCheckIntervalSeconds;
        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            if (Time.time >= nextCpuImageWarningAt)
            {
                Debug.LogWarning("[SLRP AR QR] Camera CPU image not available " +
                    "for QR-content verification yet. Hold a registered QR " +
                    "fully inside the camera view in good light.");
                nextCpuImageWarningAt = Time.time + 6f;
            }
            return;
        }

        using (image)
        {
            try
            {
                // Preserve the Android-tested luminance path. On iOS, if the
                // CPU image's plane layout is different or scanning fails, try
                // the ARKit-supported XRCpuImage.Convert() RGBA path instead.
                // Never treat a failed decode as an authorized marker.
                Result decoded = null;
                if (image.planeCount > 0)
                {
                    XRCpuImage.Plane plane = image.GetPlane(0);
                    if (plane.pixelStride == 1)
                        decoded = DecodeQrFromLumaPlane(image, plane);
                }
#if UNITY_IOS && !UNITY_EDITOR
                if (decoded == null)
                    decoded = DecodeQrFromConvertedImage(image);
#endif
                if (decoded == null) return;

                string payload = decoded.Text == null ? string.Empty : decoded.Text.Trim();
                if (string.IsNullOrEmpty(payload)) return;
                if (IsAuthorizedQr(payload))
                {
                    // A validated parish payload is authoritative for LOCATION
                    // ID, while ARTrackedImageManager supplies the camera pose.
                    const string parishPrefix = "WAYFINDER:ST_LORENZO_RUIZ_PARISH:";
                    const string versionSuffix = ":V1";
                    if (payload.StartsWith(parishPrefix, StringComparison.Ordinal) &&
                        payload.EndsWith(versionSuffix, StringComparison.Ordinal))
                    {
                        lastDecodedParishMarker = payload.Substring(
                            parishPrefix.Length,
                            payload.Length - parishPrefix.Length - versionSuffix.Length);
                        lastParishDecodeTime = Time.time;
                        if (lastLoggedVerifiedMarker != lastDecodedParishMarker ||
                            Time.time - lastVerifiedPayloadLogAt > 8f)
                        {
                            Debug.Log("[SLRP AR QR] CONTENT VERIFIED: " +
                                lastDecodedParishMarker +
                                ". Waiting for stable AR image pose to finish calibration.");
                            lastLoggedVerifiedMarker = lastDecodedParishMarker;
                            lastVerifiedPayloadLogAt = Time.time;
                        }
                    }
                    else
                    {
                        lastDecodedParishMarker = string.Empty;
                        lastParishDecodeTime = -100f;
                    }
                    if (invalidQrModalVisible) HideModal();
                    return; // ARTrackedImageManager supplies the matching pose.
                }

                // Another QR is now in view. Never allow a previous marker's
                // cached identity to validate an unrelated tracked image.
                lastDecodedParishMarker = string.Empty;
                lastParishDecodeTime = -100f;

                // Avoid repeating the same warning every camera frame.
                // A disabled invalid-code POPUP must not disable identity
                // decoding or turn unrelated QR contents into a valid marker.
                if (!checkUnknownQrCodes) return;
                if (payload == lastInvalidPayload &&
                    Time.time - lastInvalidAlertAt < 20f) return;
                lastInvalidPayload = payload;
                lastInvalidAlertAt = Time.time;
                scanHelpShown = true;
                ShowModal("INVALID QR CODE",
                    "This QR code is not registered for SLRP AR. Please scan an authorized location QR code.", true);
            }
            catch (Exception exception)
            {
                // Failure to decode is not proof a QR is invalid.
                // Never interrupt regular image tracking or route navigation.
                if (Time.time >= nextDecodeErrorLogAt)
                {
                    Debug.LogWarning("[SLRP AR QR] Camera QR-content decoder " +
                        "failed: " + exception.Message);
                    nextDecodeErrorLogAt = Time.time + 6f;
                }
            }
        }
    }

    // The first-plane fast path is intentionally unchanged in sampling and
    // resolution from the Android-working project.
    private Result DecodeQrFromLumaPlane(XRCpuImage image, XRCpuImage.Plane plane)
    {
        int width = Mathf.Min(800, image.width);
        if (width < 1 || image.width < 1 || image.height < 1) return null;
        int height = Mathf.Max(1, Mathf.RoundToInt(image.height * (float)width / image.width));
        byte[] luminance = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int sy = Mathf.Min(image.height - 1, y * image.height / height);
            for (int x = 0; x < width; x++)
            {
                int sx = Mathf.Min(image.width - 1, x * image.width / width);
                int offset = sy * plane.rowStride + sx;
                if (offset < 0 || offset >= plane.data.Length) return null;
                luminance[y * width + x] = plane.data[offset];
            }
        }
        return DecodeQrLuminance(luminance, width, height);
    }

    private Result DecodeQrLuminance(byte[] luminance, int width, int height)
    {
        var source = new PlanarYUVLuminanceSource(luminance,
            width, height, 0, 0, width, height, false);
        var bitmap = new BinaryBitmap(new ZXing.Common.HybridBinarizer(source));
        // ZXing versions differ on whether no-result returns null or throws.
        // Either case must allow the iOS conversion fallback to run.
        try { return qrReader.decode(bitmap); }
        catch (Exception) { return null; }
    }

#if UNITY_IOS && !UNITY_EDITOR
    // ARKit may expose camera CPU images in a layout that differs from ARCore's
    // directly readable Y plane. Use Unity's native conversion as an iOS-only
    // fallback. Keep original QR identity validation unchanged.
    private Result DecodeQrFromConvertedImage(XRCpuImage image)
    {
        int width = Mathf.Min(1200, image.width);
        if (width < 1 || image.height < 1) return null;
        int height = Mathf.Max(1, Mathf.RoundToInt(image.height * (float)width / image.width));
        var conversion = new XRCpuImage.ConversionParams
        {
            inputRect = new RectInt(0, 0, image.width, image.height),
            outputDimensions = new Vector2Int(width, height),
            outputFormat = TextureFormat.RGBA32,
            transformation = XRCpuImage.Transformation.None
        };

        using (var rgba = new NativeArray<byte>(image.GetConvertedDataSize(conversion),
            Allocator.Temp))
        {
            image.Convert(conversion, rgba);
            if (rgba.Length < width * height * 4) return null;
            byte[] luminance = new byte[width * height];
            for (int i = 0; i < luminance.Length; i++)
            {
                int j = i * 4;
                // Integer-weighted luma from R, G and B channels.
                luminance[i] = (byte)((77 * rgba[j] + 150 * rgba[j + 1] +
                    29 * rgba[j + 2]) >> 8);
            }
            Result decoded = DecodeQrLuminance(luminance, width, height);
            if (decoded != null) return decoded;

            // Some ARKit camera frames arrive in a different sensor rotation.
            // A rotated retry affects only scanning, not the AR tracked pose.
            byte[] rotated = new byte[luminance.Length];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    rotated[x * height + (height - 1 - y)] = luminance[y * width + x];
            return DecodeQrLuminance(rotated, height, width);
        }
    }
#endif

    private bool IsAuthorizedQr(string contents)
    {
        // Added marker images share these exact QR contents. Older scene
        // instances retain their serialized authorizedQrContents array, so
        // don't rely on new Inspector defaults alone when installing updates.
        string[] suppliedMarkerPayloads =
        {
            "WAYFINDER:CALIBRATION:Room1_Calibration",
            "WAYFINDER:CALIBRATION:Room2_Calibration",
            "WAYFINDER:CALIBRATION:Room4_Calibration",
            "WAYFINDER:CALIBRATION:Kitchen_Calibration",
            "WAYFINDER:CALIBRATION:LivingLeft_Calibration",
            "WAYFINDER:CALIBRATION:LivingRight_Calibration",
            "WAYFINDER:ST_LORENZO_RUIZ_PARISH:F1_LeftStairs_QR:V1",
            "WAYFINDER:ST_LORENZO_RUIZ_PARISH:F1_RightStairs_QR:V1",
            "WAYFINDER:ST_LORENZO_RUIZ_PARISH:F2_LeftStairs_QR:V1",
            "WAYFINDER:ST_LORENZO_RUIZ_PARISH:F2_RightStairs_QR:V1",
            "WAYFINDER:ST_LORENZO_RUIZ_PARISH:F1_FunctionHall_QR:V1"
        };
        // Parish codes must match the current five-image roster. Retired codes
        // cannot be re-authorized by a stale Inspector-serialized array.
        if (contents.StartsWith("WAYFINDER:ST_LORENZO_RUIZ_PARISH:", StringComparison.Ordinal))
        {
            foreach (string allowed in suppliedMarkerPayloads)
                if (string.Equals(contents, allowed, StringComparison.Ordinal)) return true;
            return false;
        }
        foreach (string allowed in suppliedMarkerPayloads)
            if (string.Equals(contents, allowed, StringComparison.Ordinal))
                return true;
        if (authorizedQrContents == null) return false;
        for (int i = 0; i < authorizedQrContents.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(authorizedQrContents[i]) &&
                string.Equals(contents, authorizedQrContents[i].Trim(),
                    StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void BuildModal()
    {
        RectTransform parent = navigationCanvas.GetComponent<RectTransform>();
        // The control must remain available even after a route is complete,
        // when the arrival UI may cover the ordinary NavigationCanvas.
        // Use a child Canvas with its own sorting order (no scene edits needed).
        GameObject persistent = NewUI("SLRP_AR_Persistent_Rescan_Canvas", parent);
        persistentRescanCanvas = persistent.GetComponent<RectTransform>();
        Stretch(persistentRescanCanvas);
        Canvas rescanCanvas = persistent.AddComponent<Canvas>();
        rescanCanvas.overrideSorting = true;
        rescanCanvas.sortingOrder = 1000;
        persistent.AddComponent<GraphicRaycaster>();

        // QRNavigationReadyUI's crimson status panel is centered, 944 units wide,
        // with its top at y=-48. Align the permanent button with THAT panel,
        // rather than the far edge of a wide Unity Game view.
        quickRescanButton = NewButton("SLRP_AR_Rescan_QR", persistentRescanCanvas,
            Vector2.zero, new Vector2(235f, 70f), Cream,
            "RESCAN QR", Crimson, RetryScan);
        RectTransform quickRect = quickRescanButton.GetComponent<RectTransform>();
        quickRect.anchorMin = new Vector2(0.5f, 1f);
        quickRect.anchorMax = new Vector2(0.5f, 1f);
        quickRect.pivot = new Vector2(1f, 1f);
        quickRect.anchoredPosition = new Vector2(440f, -62f);
        quickRect.sizeDelta = new Vector2(235f, 70f);

        // Keep this area button-only. QR location guidance belongs in the modal.

        overlay = NewUI("SLRP_AR_TrackingWarningOverlay", parent);
        RectTransform overlayRect = overlay.GetComponent<RectTransform>();
        Stretch(overlayRect);
        Image dim = overlay.AddComponent<Image>();
        dim.color = new Color(0.09f, 0.035f, 0.045f, 0.77f);
        dim.raycastTarget = true;

        GameObject panel = NewUI("Warning_Card", overlayRect);
        RectTransform cardRect = panel.GetComponent<RectTransform>();
        cardRect.anchorMin = new Vector2(0.07f, 0.5f);
        cardRect.anchorMax = new Vector2(0.93f, 0.5f);
        cardRect.pivot = new Vector2(0.5f, 0.5f);
        cardRect.anchoredPosition = Vector2.zero;
        cardRect.sizeDelta = new Vector2(0f, 460f);
        Image cardImage = panel.AddComponent<Image>();
        cardImage.color = Cream;

        GameObject band = NewUI("Crimson_Header", cardRect);
        RectTransform bandRect = band.GetComponent<RectTransform>();
        bandRect.anchorMin = new Vector2(0f, 1f);
        bandRect.anchorMax = new Vector2(1f, 1f);
        bandRect.pivot = new Vector2(0.5f, 1f);
        bandRect.sizeDelta = new Vector2(0f, 110f);
        bandRect.anchoredPosition = Vector2.zero;
        // Keep the solid crimson modal header, but draw its white title with
        // standard uGUI Text to avoid the TMP light-text material tint bug.
        band.AddComponent<Image>().color = Crimson;
        headingText = NewReliableModalTitle("Warning_Title", bandRect, 39, Color.white,
            new Vector2(0f, -8f), new Vector2(780f, 80f));

        explanationText = NewLabel("Warning_Details", cardRect, 29f, TextDark,
            TextAlignmentOptions.Center, new Vector2(0f, 15f), new Vector2(780f, 165f));
        explanationText.textWrappingMode = TextWrappingModes.Normal;

        // Put the stairs reminder IN the popup (not beneath the header's
        // always-available RESCAN QR button).
        GameObject reminderPanel = NewUI("Scan_Near_Stairs_Reminder", cardRect);
        RectTransform reminderRect = reminderPanel.GetComponent<RectTransform>();
        reminderRect.anchorMin = reminderRect.anchorMax = new Vector2(0.5f, 0.5f);
        reminderRect.anchoredPosition = new Vector2(0f, -103f);
        reminderRect.sizeDelta = new Vector2(730f, 48f);
        Image reminderImage = reminderPanel.AddComponent<Image>();
        reminderImage.color = new Color(0.98f, 0.89f, 0.80f, 1f);
        reminderImage.raycastTarget = false;
        stairsReminderText = NewLabel("Scan_Near_Stairs_Text", reminderRect,
            26f, Crimson, TextAlignmentOptions.Center, Vector2.zero,
            new Vector2(715f, 46f));
        stairsReminderText.text = "Scan a location QR";
        stairsReminderText.fontStyle = FontStyles.Bold;
        stairsReminderText.textWrappingMode = TextWrappingModes.NoWrap;

        // Gold primary button and dark lettering remain legible in both Game
        // view and on-device; keep the same RetryScan callback.
        rescanButton = NewButton("Rescan_QR_Button", cardRect,
            new Vector2(-185f, -169f), new Vector2(320f, 74f),
            new Color(0.97f, 0.81f, 0.49f, 1f),
            "Rescan QR", Crimson, RetryScan);
        dismissButton = NewButton("Dismiss_Button", cardRect,
            new Vector2(185f, -169f), new Vector2(290f, 74f),
            new Color(0.92f, 0.85f, 0.81f, 1f), "Dismiss", TextDark, HideModal);
        overlay.SetActive(false);
    }

    private static GameObject NewUI(string name, Transform parent)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.layer = 5;
        obj.transform.SetParent(parent, false);
        return obj;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    // Light-on-crimson modal titles are rendered using the standard uGUI
    // font shader; dark TMP buttons and description labels are unchanged.
    private static Text NewReliableModalTitle(string name, RectTransform parent,
        int size, Color color, Vector2 position, Vector2 dimensions)
    {
        GameObject obj = NewUI(name, parent);
        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = dimensions;
        Text label = obj.AddComponent<Text>();
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Font.CreateDynamicFontFromOSFont(
                new string[] { "Arial", "Roboto", "Helvetica", "Liberation Sans" }, size);
        label.font = font;
        label.fontSize = size;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.resizeTextForBestFit = true;
        label.resizeTextMinSize = 22;
        label.resizeTextMaxSize = size;
        label.color = color;
        label.raycastTarget = false;
        return label;
    }

    private static TMP_Text NewLabel(string name, RectTransform parent, float size,
        Color color, TextAlignmentOptions alignment, Vector2 position, Vector2 dimensions)
    {
        GameObject obj = NewUI(name, parent);
        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = dimensions;
        TextMeshProUGUI label = obj.AddComponent<TextMeshProUGUI>();
        label.fontSize = size;
        label.color = color;
        label.alignment = alignment;
        label.raycastTarget = false;
        label.textWrappingMode = TextWrappingModes.Normal;
        return label;
    }

    private static Button NewButton(string name, RectTransform parent,
        Vector2 position, Vector2 size, Color background, string text,
        Color foreground, UnityEngine.Events.UnityAction click)
    {
        GameObject obj = NewUI(name, parent);
        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        obj.AddComponent<Image>().color = background;
        Button button = obj.AddComponent<Button>();
        button.onClick.AddListener(click);
        TMP_Text buttonLabel = NewLabel(name + "_Label", rect, 27f, foreground,
            TextAlignmentOptions.Center, Vector2.zero, size);
        buttonLabel.text = text; // Previously omitted: caused the blank Rescan button.
        buttonLabel.fontStyle = FontStyles.Bold;
        return button;
    }
}
