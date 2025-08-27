// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Assertions;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class WebRTCObjectDetectedUiManager : MonoBehaviour
    {
        [SerializeField] private WebCamTextureManager m_webCamTextureManager;
        private PassthroughCameraEye CameraEye => m_webCamTextureManager.Eye;
        private Vector2Int CameraResolution => m_webCamTextureManager.RequestedResolution;
        [SerializeField] private GameObject m_detectionCanvas;
        [SerializeField] private float m_canvasDistance = 1f;
        private Pose m_captureCameraPose;
        private Vector3 m_capturePosition;
        private Quaternion m_captureRotation;

        private IEnumerator Start()
        {
            if (m_webCamTextureManager == null)
            {
                Debug.LogError($"PCA: {nameof(m_webCamTextureManager)} field is required "
                            + $"for the component {nameof(SentisObjectDetectedUiManager)} to operate properly");
                enabled = false;
                yield break;
            }

            // Make sure the manager is disabled in scene and enable it only when the required permissions have been granted
            Assert.IsFalse(m_webCamTextureManager.enabled);
            while (PassthroughCameraPermissions.HasCameraPermission != true)
            {
                yield return null;
            }

            // Set the 'requestedResolution' and enable the manager
            m_webCamTextureManager.RequestedResolution = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye).Resolution;
            m_webCamTextureManager.enabled = true;

            var cameraCanvasRectTransform = m_detectionCanvas.GetComponentInChildren<RectTransform>();
            var leftSidePointInCamera = PassthroughCameraUtils.ScreenPointToRayInCamera(CameraEye, new Vector2Int(0, CameraResolution.y / 2));
            var rightSidePointInCamera = PassthroughCameraUtils.ScreenPointToRayInCamera(CameraEye, new Vector2Int(CameraResolution.x, CameraResolution.y / 2));
            var horizontalFoVDegrees = Vector3.Angle(leftSidePointInCamera.direction, rightSidePointInCamera.direction);
            var horizontalFoVRadians = horizontalFoVDegrees / 180 * Math.PI;
            var newCanvasWidthInMeters = 2 * m_canvasDistance * Math.Tan(horizontalFoVRadians / 2);
            var localScale = (float)(newCanvasWidthInMeters / cameraCanvasRectTransform.sizeDelta.x);
            cameraCanvasRectTransform.localScale = new Vector3(localScale, localScale, localScale);

            Debug.Log($"[WebRTCCanvasUI] Canvas setup - FOV: {horizontalFoVDegrees}°, Width: {newCanvasWidthInMeters}m, Scale: {localScale}, SizeDelta: {cameraCanvasRectTransform.sizeDelta}");

            // Add a visible border to help debug canvas position and size
            CreateCanvasBorder(cameraCanvasRectTransform);

            // Force canvas to be visible and in front of camera
            // ForceCanvasVisible();
        }

        private void ForceCanvasVisible()
        {
            if (m_detectionCanvas == null)
            {
                Debug.LogError("[WebRTCCanvasUI] m_detectionCanvas is null!");
                return;
            }

            // Ensure canvas and all components are active
            m_detectionCanvas.SetActive(true);

            // Check if Canvas component exists
            var canvas = m_detectionCanvas.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = m_detectionCanvas.AddComponent<Canvas>();
                Debug.Log("[WebRTCCanvasUI] Added missing Canvas component");
            }

            // Set canvas to world space and make it visible
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 100; // High sorting order to appear on top

            // Check if CanvasScaler exists
            var scaler = m_detectionCanvas.GetComponent<UnityEngine.UI.CanvasScaler>();
            if (scaler == null)
            {
                scaler = m_detectionCanvas.AddComponent<UnityEngine.UI.CanvasScaler>();
                Debug.Log("[WebRTCCanvasUI] Added missing CanvasScaler component");
            }

            // Check if GraphicRaycaster exists
            var raycaster = m_detectionCanvas.GetComponent<UnityEngine.UI.GraphicRaycaster>();
            if (raycaster == null)
            {
                raycaster = m_detectionCanvas.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                Debug.Log("[WebRTCCanvasUI] Added missing GraphicRaycaster component");
            }

            Debug.Log($"[WebRTCCanvasUI] Canvas forced visible - Active: {m_detectionCanvas.activeInHierarchy}, RenderMode: {canvas.renderMode}, SortingOrder: {canvas.sortingOrder}");
        }

        private void CreateCanvasBorder(RectTransform canvasRect)
        {
            Debug.Log($"[WebRTCCanvasUI] Creating canvas border on: {canvasRect.name}");

            // Create a simple background first to make canvas visible
            GameObject background = new GameObject("CanvasBackground");
            background.transform.SetParent(canvasRect, false);

            var bgImage = background.AddComponent<UnityEngine.UI.Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0.3f); // Semi-transparent black background

            var bgRect = background.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            // Create thick, bright border edges that are easy to see
            CreateBorderEdge(canvasRect, "TopBorder", new Vector2(0, 0.95f), new Vector2(1, 1), Color.red);
            CreateBorderEdge(canvasRect, "BottomBorder", new Vector2(0, 0), new Vector2(1, 0.05f), Color.red);
            CreateBorderEdge(canvasRect, "LeftBorder", new Vector2(0, 0), new Vector2(0.05f, 1), Color.red);
            CreateBorderEdge(canvasRect, "RightBorder", new Vector2(0.95f, 0), new Vector2(1, 1), Color.red);

            // Add center crosshair for reference - make it bigger and brighter
            CreateBorderEdge(canvasRect, "CenterHorizontal", new Vector2(0.3f, 0.48f), new Vector2(0.7f, 0.52f), Color.yellow);
            CreateBorderEdge(canvasRect, "CenterVertical", new Vector2(0.48f, 0.3f), new Vector2(0.52f, 0.7f), Color.yellow);

            Debug.Log($"[WebRTCCanvasUI] Canvas border and crosshair created - Parent active: {canvasRect.gameObject.activeInHierarchy}");
        }

        private void CreateBorderEdge(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Color color)
        {
            GameObject edge = new GameObject(name);
            edge.transform.SetParent(parent, false);

            var edgeImage = edge.AddComponent<UnityEngine.UI.Image>();
            edgeImage.color = color;

            var edgeRect = edge.GetComponent<RectTransform>();
            edgeRect.anchorMin = anchorMin;
            edgeRect.anchorMax = anchorMax;
            edgeRect.offsetMin = Vector2.zero;
            edgeRect.offsetMax = Vector2.zero;
        }

        public void UpdatePosition()
        {
            // Check if canvas exists
            if (m_detectionCanvas == null)
            {
                Debug.LogError("[WebRTCCanvasUI] m_detectionCanvas is null in UpdatePosition!");
                return;
            }

            // Position the canvas in front of the camera
            m_detectionCanvas.transform.position = m_capturePosition;
            m_detectionCanvas.transform.rotation = m_captureRotation;

            // Ensure canvas is active
            m_detectionCanvas.SetActive(true);

            Debug.Log($"[WebRTCCanvasUI] Canvas updated - Position: {m_capturePosition}, Rotation: {m_captureRotation}, Active: {m_detectionCanvas.activeInHierarchy}, Children: {m_detectionCanvas.transform.childCount}");
        }

        public void CapturePosition()
        {
            // Capture the camera pose and position the canvas in front of the camera
            m_captureCameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(CameraEye);
            m_capturePosition = m_captureCameraPose.position + m_captureCameraPose.rotation * Vector3.forward * m_canvasDistance;
            m_captureRotation = Quaternion.Euler(0, m_captureCameraPose.rotation.eulerAngles.y, 0);
        }

        public Vector3 GetCapturedCameraPosition()
        {
            return m_captureCameraPose.position;
        }

    }
}
