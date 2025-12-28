using System.Collections.Generic;
using Meta.XR.Samples;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Text;
using Meta.XR;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class WebRTCInferenceUiManager : MonoBehaviour
    {
        [Header("WebRTC")]
        [SerializeField] private WebRTCSessionManager m_webRTCSessionManager;
        [SerializeField] private TextAsset m_labelsAsset;

        [Header("Placement configureation")]
        [SerializeField] private EnvironmentRayCastSampleManager m_environmentRaycast;
        [SerializeField] private WebCamTextureManager m_webCamTextureManager;
        private PassthroughCameraEye CameraEye => m_webCamTextureManager.Eye;

        [Header("UI display references")]
        [SerializeField] private WebRTCObjectDetectedUiManager m_detectionCanvas;
        [SerializeField] private RawImage m_displayImage;
        [SerializeField] private Sprite m_boxTexture;
        [SerializeField] private Color m_boxColor;
        [SerializeField] private Font m_font;
        [SerializeField] private Color m_fontColor;
        [SerializeField] private int m_fontSize = 80;
        [Space(10)]
        [Header("Box Auto Hide Settings")]
        [SerializeField] private float m_boxAutoHideDelay = 3.0f; // 3秒後自動隱藏

        private Coroutine m_autoHideCoroutine;
        private float m_lastDetectionTime;
        public UnityEvent<int> OnObjectsDetected;

        public List<BoundingBox> BoxDrawn = new();

        private string[] m_labels;
        private List<GameObject> m_boxPool = new();
        private Transform m_displayLocation;

        //bounding box data
        public struct BoundingBox
        {
            public float CenterX;
            public float CenterY;
            public float Width;
            public float Height;
            public string Label;
            public Vector3? WorldPos;
            public string ClassName;
            public float Confidence;
            public int ClassId;
        }

        #region Unity Functions
        private void Start()
        {
            // Initialize labels first
            if (m_labelsAsset != null)
            {
                SetLabels(m_labelsAsset);
            }
            else
            {
                Debug.LogError("[WebRTCUI] m_labelsAsset is not assigned in inspector");
                m_labels = new string[] { "unknown" };
            }

            m_displayLocation = m_displayImage.transform;

            if (m_webRTCSessionManager != null)
            {
                m_webRTCSessionManager.OnInferenceResultReceived += HandleInferenceResult;
            }
            else
            {
                Debug.LogError("[WebRTCUI] m_webRTCSessionManager is null in Start()");
            }
        }

        private void OnDestroy()
        {
            if (m_webRTCSessionManager != null)
            {
                m_webRTCSessionManager.OnInferenceResultReceived -= HandleInferenceResult;
            }

            // 停止自動隱藏協程
            if (m_autoHideCoroutine != null)
            {
                StopCoroutine(m_autoHideCoroutine);
                m_autoHideCoroutine = null;
            }

            ClearAnnotations();
        }
        #endregion

        #region Detection Functions
        public void OnObjectDetectionError()
        {
            // Clear current boxes
            ClearAnnotations();

            // Set obejct found to 0
            OnObjectsDetected?.Invoke(0);
        }

        private void HandleInferenceResult(byte[] data)
        {
            Debug.Log($"[WebRTCUI] HandleInferenceResult called with {data.Length} bytes");

            try
            {
                var jsonString = Encoding.UTF8.GetString(data);
                Debug.Log($"[WebRTCUI] Received inference result: {jsonString}");

                // Clear previous detections
                ClearAnnotations();

                bool hasDetections = false; // 新增標記

                // Parse JSON array manually or use JsonUtility with wrapper
                if (jsonString.StartsWith("["))
                {
                    // Remove brackets and split by objects
                    jsonString = jsonString.Trim('[', ']');
                    var detectionStrings = SplitJsonObjects(jsonString);

                    Debug.Log($"[WebRTCUI] Found {detectionStrings.Length} detections");

                    if (detectionStrings.Length > 0)
                    {
                        var detectionResults = new List<DetectionResult>();

                        foreach (var detection in detectionStrings)
                        {
                            var result = JsonUtility.FromJson<DetectionResult>(detection);

                            // Check if classification key exists in the original JSON string
                            result.hasClassification = detection.Contains("\"classification\":");

                            detectionResults.Add(result);
                        }

                        int rows = detectionResults.Count;
                        int cols = 4;
                        float[,] outputArray = new float[rows, cols];
                        for (int i = 0; i < rows; i++)
                        {
                            outputArray[i, 0] = detectionResults[i].x;
                            outputArray[i, 1] = detectionResults[i].y;
                            outputArray[i, 2] = detectionResults[i].width;
                            outputArray[i, 3] = detectionResults[i].height;
                        }

                        // Draw UI boxes
                        if (m_displayImage != null && m_displayImage.rectTransform != null)
                        {
                            SetDetectionCapture(m_displayImage.texture);

                            // 有偵測到物件，更新最後偵測時間
                            m_lastDetectionTime = Time.time;
                            hasDetections = true;

                            // 停止自動隱藏協程（如果正在執行）
                            if (m_autoHideCoroutine != null)
                            {
                                StopCoroutine(m_autoHideCoroutine);
                                m_autoHideCoroutine = null;
                            }

                            DrawUIBoxes(outputArray, detectionResults.ToArray(), m_displayImage.rectTransform.rect.width, m_displayImage.rectTransform.rect.height);
                        }
                        else
                        {
                            Debug.LogError("[WebRTCUI] m_displayImage or rectTransform is null");
                            OnObjectDetectionError();
                        }
                    }
                }
                else if (jsonString.Contains("msg"))
                {
                    // This is a test message, ignore it
                    Debug.Log("[WebRTCUI] Received test message, ignoring");
                    return; // 直接返回，不處理自動隱藏
                }

                // 檢查是否有偵測結果，沒有則啟動自動隱藏
                if (!hasDetections)
                {
                    Debug.Log("[WebRTCUI] No detections found, starting auto-hide timer");
                    StartAutoHideBoxes();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[WebRTCUI] Error handling inference result: {e.Message}");
                OnObjectDetectionError();
            }
        }

        private string[] SplitJsonObjects(string json)
        {
            var objects = new List<string>();
            var braceCount = 0;
            var startIndex = 0;

            for (var i = 0; i < json.Length; i++)
            {
                if (json[i] == '{')
                {
                    if (braceCount == 0) startIndex = i;
                    braceCount++;
                }
                else if (json[i] == '}')
                {
                    braceCount--;
                    if (braceCount == 0)
                    {
                        objects.Add(json.Substring(startIndex, i - startIndex + 1));
                    }
                }
            }
            return objects.ToArray();
        }
        #endregion

        #region BoundingBoxes functions
        public void SetLabels(TextAsset labelsAsset)
        {
            if (labelsAsset == null || string.IsNullOrEmpty(labelsAsset.text))
            {
                Debug.LogError("[WebRTCUI] labelsAsset is null or empty");
                m_labels = new string[] { "unknown" };
                return;
            }

            //Parse neural net m_labels
            m_labels = labelsAsset.text.Split('\n');

            if (m_labels == null || m_labels.Length == 0)
            {
                Debug.LogError("[WebRTCUI] Failed to parse labels from asset");
                m_labels = new string[] { "unknown" };
            }
            else
            {
                Debug.Log($"[WebRTCUI] Loaded {m_labels.Length} labels");
            }
        }

        public void SetDetectionCapture(Texture image)
        {
            m_displayImage.texture = image;
            m_detectionCanvas.CapturePosition();
        }

        public void DrawUIBoxes(float[,] output, DetectionResult[] detectionResults, float imageWidth, float imageHeight)
        {
            Debug.Log($"[WebRTCUI] DrawUIBoxes called with {output.GetLength(0)} detections, imageSize: {imageWidth}x{imageHeight}");

            // Check for null references
            if (m_detectionCanvas == null)
            {
                Debug.LogError("[WebRTCUI] m_detectionCanvas is null");
                return;
            }

            if (m_displayImage == null || m_displayImage.rectTransform == null)
            {
                Debug.LogError("[WebRTCUI] m_displayImage or rectTransform is null in DrawUIBoxes");
                return;
            }

            if (m_webCamTextureManager == null)
            {
                Debug.LogError("[WebRTCUI] m_webCamTextureManager is null");
                return;
            }

            if (detectionResults == null || detectionResults.Length == 0)
            {
                Debug.LogError("[WebRTCUI] detectionResults is null or empty");
                return;
            }

            // Updte canvas position
            m_detectionCanvas.UpdatePosition();

            // Clear current boxes
            ClearAnnotations();

            var displayWidth = m_displayImage.rectTransform.rect.width;
            var displayHeight = m_displayImage.rectTransform.rect.height;
            Debug.Log($"[WebRTCUI] Display size: {displayWidth}x{displayHeight}");

            var scaleX = displayWidth / imageWidth;
            var scaleY = displayHeight / imageHeight;
            Debug.Log($"[WebRTCUI] Scale factors: X={scaleX}, Y={scaleY}");

            var halfWidth = displayWidth / 2;
            var halfHeight = displayHeight / 2;

            // var boxesFound = output.shape[0];
            var boxesFound = output.GetLength(0);
            if (boxesFound <= 0)
            {
                OnObjectsDetected?.Invoke(0);
                return;
            }
            var maxBoxes = Mathf.Min(boxesFound, 200);

            Debug.Log($"[WebRTCUI] Drawing {maxBoxes} boxes");
            OnObjectsDetected?.Invoke(maxBoxes);

            //Get the camera intrinsics
            var intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye);
            var camRes = intrinsics.Resolution;

            //Draw the bounding boxes
            for (var n = 0; n < maxBoxes; n++)
            {
                var detection = detectionResults[n];

                // Check if classification key exists in JSON and use it if available
                int finalClassId;
                string finalClassName;
                float finalConfidence;

                if (detection.hasClassification && detection.classification != null)
                {
                    // Use classification data if key exists and data is available
                    finalClassId = detection.classification.class_id;
                    finalClassName = !string.IsNullOrEmpty(detection.classification.class_name) ? detection.classification.class_name : "unknown";
                    finalConfidence = detection.classification.confidence;
                    Debug.Log($"[WebRTCUI] Using classification data - class_id: {finalClassId}, class_name: {finalClassName}, confidence: {finalConfidence:F2}");
                }
                else
                {
                    // Fall back to detection data when classification key doesn't exist
                    finalClassId = detection.class_id;
                    finalClassName = !string.IsNullOrEmpty(detection.class_name) ? detection.class_name : "unknown";
                    finalConfidence = detection.confidence;
                    Debug.Log($"[WebRTCUI] Using detection data (no classification key or null value) - class_id: {finalClassId}, class_name: {finalClassName}, confidence: {finalConfidence:F2}");
                }

                Debug.Log($"[WebRTCUI] Processing detection {n}: raw coords({output[n, 0]}, {output[n, 1]}, {output[n, 2]}, {output[n, 3]}), final class_id: {finalClassId}, final class_name: {finalClassName}, final confidence: {finalConfidence:F2}");

                // Get bounding box center coordinates
                var centerX = output[n, 0] * scaleX - halfWidth;
                var centerY = output[n, 1] * scaleY - halfHeight;
                var perX = (centerX + halfWidth) / displayWidth;
                var perY = (centerY + halfHeight) / displayHeight;

                Debug.Log($"[WebRTCUI] Canvas coords: centerX={centerX}, centerY={centerY}, perX={perX}, perY={perY}");

                Debug.Log($"[WebRTCUI] Object class: {finalClassName}");

                // Get the 3D marker world position using Depth Raycast
                var centerPixel = new Vector2Int(Mathf.RoundToInt(perX * camRes.x), Mathf.RoundToInt((1.0f - perY) * camRes.y));
                var ray = PassthroughCameraUtils.ScreenPointToRayInWorld(CameraEye, centerPixel);
                var worldPos = m_environmentRaycast.PlaceGameObjectByScreenPos(ray);

                // Create a new bounding box
                var box = new BoundingBox
                {
                    CenterX = centerX,
                    CenterY = centerY,
                    ClassName = finalClassName,
                    Width = output[n, 2] * scaleX,
                    Height = output[n, 3] * scaleY,
                    Label = $"{finalClassId}, {finalClassName}, {finalConfidence:F2}",
                    WorldPos = worldPos,
                    Confidence = finalConfidence,
                    ClassId = finalClassId,
                };

                // Add to the list of boxes
                BoxDrawn.Add(box);

                // Draw 2D box
                DrawBox(box, n);
            }
        }

        private void ClearAnnotations()
        {
            foreach (var box in m_boxPool)
            {
                box?.SetActive(false);
            }
            BoxDrawn.Clear();
        }

        private void DrawBox(BoundingBox box, int id)
        {
            Debug.Log($"[WebRTCUI] DrawBox called for box {id}: Center({box.CenterX}, {box.CenterY}), Size({box.Width}, {box.Height}), Class: {box.ClassName}");

            //Create the bounding box graphic or get from pool
            GameObject panel;
            if (id < m_boxPool.Count)
            {
                panel = m_boxPool[id];
                if (panel == null)
                {
                    panel = CreateNewBox(m_boxColor);
                }
                else
                {
                    panel.SetActive(true);
                }
            }
            else
            {
                panel = CreateNewBox(m_boxColor);
            }

            if (panel == null)
            {
                Debug.LogError("[WebRTCUI] Failed to create or get panel for box");
                return;
            }

            if (m_detectionCanvas == null)
            {
                Debug.LogError("[WebRTCUI] m_detectionCanvas is null in DrawBox");
                return;
            }

            // Debug panel creation
            Debug.Log($"[WebRTCUI] Panel created/retrieved: {panel.name}, Active: {panel.activeInHierarchy}");

            //Set box position - Use 2D canvas coordinates, not 3D world position
            var finalPosition = new Vector3(box.CenterX, -box.CenterY, box.WorldPos.HasValue ? box.WorldPos.Value.z : 0.0f);
            panel.transform.localPosition = finalPosition;
            Debug.Log($"[WebRTCUI] Panel position set to: {finalPosition}");

            //Set box rotation
            panel.transform.rotation = Quaternion.LookRotation(panel.transform.position - m_detectionCanvas.GetCapturedCameraPosition());
            //Set box size
            var rt = panel.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(box.Width, box.Height);
            Debug.Log($"[WebRTCUI] Box size set to: {box.Width} x {box.Height}");

            //Set label text
            var label = panel.GetComponentInChildren<Text>();
            label.text = box.Label;
            label.fontSize = 12;

            // Final verification
            Debug.Log($"[WebRTCUI] Final panel state - Active: {panel.activeInHierarchy}, Position: {panel.transform.position}, LocalPosition: {panel.transform.localPosition}");
        }

        private GameObject CreateNewBox(Color color)
        {
            //Create the box and set image
            var panel = new GameObject("ObjectBox");
            _ = panel.AddComponent<CanvasRenderer>();
            var img = panel.AddComponent<Image>();
            img.color = color;
            img.sprite = m_boxTexture;
            img.type = Image.Type.Sliced;
            img.fillCenter = false;
            panel.transform.SetParent(m_displayLocation, false);

            //Create the label
            var text = new GameObject("ObjectLabel");
            _ = text.AddComponent<CanvasRenderer>();
            text.transform.SetParent(panel.transform, false);
            var txt = text.AddComponent<Text>();
            txt.font = m_font;
            txt.color = m_fontColor;
            txt.fontSize = m_fontSize;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;

            var rt2 = text.GetComponent<RectTransform>();
            rt2.offsetMin = new Vector2(20, rt2.offsetMin.y);
            rt2.offsetMax = new Vector2(0, rt2.offsetMax.y);
            rt2.offsetMin = new Vector2(rt2.offsetMin.x, 0);
            rt2.offsetMax = new Vector2(rt2.offsetMax.x, 30);
            rt2.anchorMin = new Vector2(0, 0);
            rt2.anchorMax = new Vector2(1, 1);

            m_boxPool.Add(panel);
            return panel;
        }
        #endregion

        #region Auto Hide Functions
        private void StartAutoHideBoxes()
        {
            // 如果已經有協程在執行，就不重複啟動
            if (m_autoHideCoroutine != null) return;

            m_autoHideCoroutine = StartCoroutine(AutoHideBoxesAfterDelay());
        }

        private IEnumerator AutoHideBoxesAfterDelay()
        {
            Debug.Log($"[WebRTCUI] Starting auto-hide timer for {m_boxAutoHideDelay} seconds");

            yield return new WaitForSeconds(m_boxAutoHideDelay);

            // 檢查是否在等待期間又有新的偵測結果
            if (Time.time - m_lastDetectionTime >= m_boxAutoHideDelay)
            {
                Debug.Log("[WebRTCUI] Auto-hiding boxes due to no detection");
                ClearAnnotations();
                OnObjectsDetected?.Invoke(0);
            }

            m_autoHideCoroutine = null;
        }
        #endregion
    }

    [System.Serializable]
    public class ClassificationResult
    {
        public int class_id;
        public string class_name;
        public float confidence;
    }

    public class DetectionResult
    {
        public float x;
        public float y;
        public float width;
        public float height;
        public float confidence;
        public int class_id;
        public string class_name;
        public ClassificationResult classification;

        // Helper field to track if classification key exists in JSON
        [System.NonSerialized]
        public bool hasClassification;
    }
}