using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Meta.XR;
using UnityEngine;
using UnityEngine.UI;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public class WebRTCDetectionUiManager : MonoBehaviour
    {
        [Header("UI Display References")]
        [SerializeField] private RawImage m_displayImage;
        [SerializeField] private Canvas m_detectionCanvas;
        [SerializeField] private WebRTCClientManager m_webRTCClientManager;
        [SerializeField] private WebCamTextureManager m_webCamTextureManager;

        [Header("Bounding Box Styling")]
        [SerializeField] private Sprite m_boxTexture;
        [SerializeField] private Color m_boxColor = Color.green;
        [SerializeField] private Font m_font;
        [SerializeField] private Color m_fontColor = Color.white;
        [SerializeField] private int m_fontSize = 12;
        [SerializeField] private float m_confidenceThreshold = 0.5f;

        [Header("Environment Integration")]
        [SerializeField] private EnvironmentRaycastManager m_environmentRaycastManager;

        // Object pooling for bounding boxes
        private List<GameObject> m_boxPool = new();
        private Transform m_displayLocation;

        // Class labels mapping (YOLO classes)
        private readonly string[] m_classLabels = new string[]
        {
            "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light",
            "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow",
            "elephant", "bear", "zebra", "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
            "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard",
            "tennis racket", "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
            "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
            "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard",
            "cell phone", "microwave", "oven", "toaster", "sink", "refrigerator", "book", "clock", "vase",
            "scissors", "teddy bear", "hair drier", "toothbrush"
        };

#pragma warning disable IDE1006
        // These classes are used for JSON deserialization, so should be named with lowercase.
        [Serializable]
        public class DetectionResult
        {
            public float x;
            public float y;
            public float width;
            public float height;
            public float confidence;
            public int class_id;
        }
#pragma warning restore IDE1006

        private void Awake()
        {
            Debug.Log("[WebRTCUI] Awake called");
        }

        private void Start()
        {
            Debug.Log("[WebRTCUI] Start called");
            Debug.Log($"[WebRTCUI] WebRTCClientManager is null: {m_webRTCClientManager == null}");
            Debug.Log($"[WebRTCUI] DisplayImage is null: {m_displayImage == null}");
            Debug.Log($"[WebRTCUI] DetectionCanvas is null: {m_detectionCanvas == null}");

            m_displayLocation = m_displayImage.transform;

            // Subscribe to inference results
            if (m_webRTCClientManager != null)
            {
                Debug.Log("[WebRTCUI] Subscribing to OnInferenceResultReceived event");
                m_webRTCClientManager.OnInferenceResultReceived += HandleInferenceResult;
            }
            else
            {
                Debug.LogError("[WebRTCUI] WebRTCClientManager is not assigned!");
            }

            // Verify subscription
            _ = StartCoroutine(VerifySubscription());
        }

        private IEnumerator VerifySubscription()
        {
            yield return new WaitForSeconds(1f);

            if (m_webRTCClientManager != null)
            {
                Debug.Log("[WebRTCUI] Event subscription verified - WebRTCClientManager is not null");
            }
            else
            {
                Debug.LogError("[WebRTCUI] Event subscription failed - WebRTCClientManager is null");
            }
        }

        private void OnEnable()
        {
            Debug.Log("[WebRTCUI] OnEnable called");
            if (m_webRTCClientManager != null)
            {
                m_webRTCClientManager.OnInferenceResultReceived += HandleInferenceResult;
            }
        }

        private void OnDisable()
        {
            Debug.Log("[WebRTCUI] OnDisable called");
            if (m_webRTCClientManager != null)
            {
                m_webRTCClientManager.OnInferenceResultReceived -= HandleInferenceResult;
            }
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

                // Parse JSON array manually or use JsonUtility with wrapper
                if (jsonString.StartsWith("["))
                {
                    // Remove brackets and split by objects
                    jsonString = jsonString.Trim('[', ']');
                    var detectionStrings = SplitJsonObjects(jsonString);

                    Debug.Log($"[WebRTCUI] Found {detectionStrings.Length} detections");

                    // Draw UI boxes
                    DrawUIBoxes(detectionStrings);
                }
                else if (jsonString.Contains("msg"))
                {
                    // This is a test message, ignore it
                    Debug.Log("[WebRTCUI] Received test message, ignoring");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[WebRTCUI] Error handling inference result: {e.Message}");
                OnDetectionError();
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


        private void DrawUIBoxes(string[] detectionStrings)
        {
            var displayWidth = m_displayImage.rectTransform.rect.width;
            var displayHeight = m_displayImage.rectTransform.rect.height;
            float sourceWidth = 1920f, sourceHeight = 1080f;

            if (m_webCamTextureManager != null && m_webCamTextureManager.WebCamTexture != null)
            {
                sourceWidth = m_webCamTextureManager.WebCamTexture.width;
                sourceHeight = m_webCamTextureManager.WebCamTexture.height;
            }

            var scaleX = displayWidth / sourceWidth;
            var scaleY = displayHeight / sourceHeight;
            var halfWidth = displayWidth / 2;
            var halfHeight = displayHeight / 2;
            var validDetections = 0;

            for (var i = 0; i < detectionStrings.Length; i++)
            {
                try
                {
                    Debug.Log($"[WebRTCUI] detectionStrings[{i}] = {detectionStrings[i]}");

                    var detectionJson = detectionStrings[i];
                    var detection = JsonUtility.FromJson<DetectionResult>(detectionJson);

                    if (detection.confidence < m_confidenceThreshold)
                    {
                        continue;
                    }

                    Debug.Log($"[WebRTCUI] Drawing detection {i}: class={detection.class_id}, conf={detection.confidence:F2}");

                    var className = (detection.class_id < m_classLabels.Length) ?
                        m_classLabels[detection.class_id] : $"class_{detection.class_id}";

                    var box = new BoundingBox
                    {
                        CenterX = detection.x * scaleX - halfWidth,
                        CenterY = detection.y * scaleY - halfHeight,
                        Width = detection.width * scaleX,
                        Height = detection.height * scaleY,
                        Label = $"{className} ({detection.confidence:F2})",
                        ClassName = className,
                        Confidence = detection.confidence
                    };

                    DrawBox(box, validDetections);
                    validDetections++;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[WebRTCUI] Error parsing detection {i}: {e.Message}");
                }
            }
            Debug.Log($"[WebRTCUI] Drew {validDetections} bounding boxes");
        }

        private void DrawBox(BoundingBox box, int id)
        {
            // Create the bounding box graphic or get from pool
            GameObject panel;
            if (id < m_boxPool.Count)
            {
                panel = m_boxPool[id];
                if (panel == null)
                {
                    panel = CreateNewBox(m_boxColor);
                    m_boxPool[id] = panel;
                }
                else
                {
                    panel.SetActive(true);
                }
            }
            else
            {
                panel = CreateNewBox(m_boxColor);
                m_boxPool.Add(panel);
            }

            // Set box position
            panel.transform.localPosition = new Vector3(box.CenterX, -box.CenterY, 0);

            // Set box size
            var rt = panel.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(box.Width, box.Height);

            // Update color based on confidence
            var img = panel.GetComponent<Image>();
            if (img != null)
            {
                var color = m_boxColor;
                color.a = Mathf.Lerp(0.3f, 1f, box.Confidence);
                img.color = color;
            }

            // Set label text
            var label = panel.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.text = box.Label;
            }
        }

        private GameObject CreateNewBox(Color color)
        {
            // Create the box and set image
            var panel = new GameObject("ObjectBox");
            _ = panel.AddComponent<CanvasRenderer>();
            var img = panel.AddComponent<Image>();
            img.color = color;
            img.sprite = m_boxTexture;
            img.type = Image.Type.Sliced;
            img.fillCenter = false;
            panel.transform.SetParent(m_displayLocation, false);

            // Create the label
            var text = new GameObject("ObjectLabel");
            _ = text.AddComponent<CanvasRenderer>();
            text.transform.SetParent(panel.transform, false);
            var txt = text.AddComponent<Text>();
            txt.font = m_font ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.color = m_fontColor;
            txt.fontSize = m_fontSize;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.alignment = TextAnchor.UpperLeft;

            var rt2 = text.GetComponent<RectTransform>();
            rt2.offsetMin = new Vector2(2, rt2.offsetMin.y);
            rt2.offsetMax = new Vector2(0, rt2.offsetMax.y);
            rt2.offsetMin = new Vector2(rt2.offsetMin.x, 0);
            rt2.offsetMax = new Vector2(rt2.offsetMax.x, 20);
            rt2.anchorMin = new Vector2(0, 0);
            rt2.anchorMax = new Vector2(1, 1);

            return panel;
        }

        private void ClearAnnotations()
        {
            Debug.Log($"[WebRTCUI] Clearing {m_boxPool.Count} boxes from pool");

            foreach (var box in m_boxPool)
            {
                if (box != null)
                {
                    box.SetActive(false);
                }
            }
        }

        private void OnDetectionError()
        {
            Debug.LogError("[WebRTCUI] Detection error occurred");
            ClearAnnotations();
        }

        private void Update()
        {
            // Update display image if needed
            if (m_displayImage != null && m_webCamTextureManager != null && m_webCamTextureManager.WebCamTexture != null)
            {
                m_displayImage.texture = m_webCamTextureManager.WebCamTexture;
            }
        }

        private void OnDestroy()
        {
            if (m_webRTCClientManager != null)
            {
                m_webRTCClientManager.OnInferenceResultReceived -= HandleInferenceResult;
            }
        }

        // Bounding box data structure
        private struct BoundingBox
        {
            public float CenterX;
            public float CenterY;
            public float Width;
            public float Height;
            public string Label;
            public string ClassName;
            public float Confidence;
        }
    }
}
