using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using Newtonsoft.Json;
using System;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// WebRTC Stats Logger - 收集並記錄 RTCStats 中 candidate pair 的 RTT 資訊
    ///
    /// 此腳本透過 Unity WebRTC 3.0.0-pre.6 package 的 RTCPeerConnection.GetStats() API
    /// 來取得 RTCIceCandidatePairStats，並專門記錄網路連接的 RTT（Round Trip Time）資訊。
    ///
    /// 主要功能：
    /// - 自動偵測並連接到 WebRTCSessionManager
    /// - 定期收集 RTCStats 報告
    /// - 解析 candidate pair 統計資料
    /// - 記錄 RTT、頻寬、封包統計等網路品質指標
    /// - 提供事件回調供其他系統使用
    /// </summary>
    public class WebRTCStatsLogger : MonoBehaviour
    {
        [Header("Stats Configuration")]
        [SerializeField] private float m_statsUpdateInterval = 2.0f; // 統計資料更新間隔（秒）
        [SerializeField] private bool m_enableLogging = true; // 是否啟用記錄

        private RTCPeerConnection m_peerConnection;
        private Coroutine m_statsCoroutine;
        private WebRTCSessionManager m_sessionManager;
        private double m_currentMaxInferenceTime = 0.0; // 當前 JSON 中最大推理時間（毫秒）

        #region Public Properties
        /// <summary>
        /// 是否正在記錄統計資料
        /// </summary>
        public bool IsLogging => m_peerConnection != null && m_sessionManager != null;

        /// <summary>
        /// 統計資料更新間隔
        /// </summary>
        public float StatsUpdateInterval
        {
            get => m_statsUpdateInterval;
            set => m_statsUpdateInterval = Mathf.Max(0.1f, value);
        }

        /// <summary>
        /// 是否啟用記錄
        /// </summary>
        public bool EnableLogging
        {
            get => m_enableLogging;
            set => m_enableLogging = value;
        }
        #endregion

        #region Unity Lifecycle
        private void Start()
        {
            Debug.Log("[WebRTCStatsLogger] Starting WebRTCStatsLogger");

            // 尋找場景中的 WebRTCSessionManager
            var sessionManager = FindObjectOfType<WebRTCSessionManager>();
            if (sessionManager != null)
            {
                Debug.Log("[WebRTCStatsLogger] Found WebRTCSessionManager, starting connection wait");
                // 等待連接建立後開始收集統計資料
                StartCoroutine(WaitForConnectionAndStartStats(sessionManager));
            }
            else
            {
                Debug.LogError("[WebRTCStatsLogger] WebRTCSessionManager not found in scene!");
            }
        }

        private void OnDestroy()
        {
            StopStatsLogging();

            // 取消訂閱推理結果事件
            if (m_sessionManager != null)
            {
                m_sessionManager.OnInferenceResultReceived -= OnInferenceResultReceived;
            }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// 開始記錄統計資料（設置 PeerConnection 參考）
        /// </summary>
        /// <param name="peerConnection">RTCPeerConnection 實例</param>
        public void StartStatsLogging(RTCPeerConnection peerConnection)
        {
            if (peerConnection == null)
            {
                Debug.LogError("[WebRTCStatsLogger] PeerConnection is null!");
                return;
            }

            m_peerConnection = peerConnection;
            Debug.Log("[WebRTCStatsLogger] RTCStats logging ready - will trigger on inference results");
        }

        /// <summary>
        /// 停止記錄統計資料
        /// </summary>
        public void StopStatsLogging()
        {
            if (m_statsCoroutine != null)
            {
                StopCoroutine(m_statsCoroutine);
                m_statsCoroutine = null;
            }
            m_peerConnection = null;
            Debug.Log("[WebRTCStatsLogger] Stopped RTCStats logging");
        }
        #endregion

        #region Private Methods
        private IEnumerator WaitForConnectionAndStartStats(WebRTCSessionManager sessionManager)
        {
            // 儲存 SessionManager 參考
            m_sessionManager = sessionManager;

            // 訂閱推理結果事件
            m_sessionManager.OnInferenceResultReceived += OnInferenceResultReceived;
            Debug.Log("[WebRTCStatsLogger] Subscribed to OnInferenceResultReceived event");

            // 等待 WebRTC 連接建立
            yield return new WaitUntil(() => sessionManager.IsConnected);

            // 透過反射獲取 private 的 RTCPeerConnection
            var peerField = typeof(WebRTCSessionManager).GetField("m_peer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (peerField != null)
            {
                var peerConnection = peerField.GetValue(sessionManager) as RTCPeerConnection;
                if (peerConnection != null)
                {
                    StartStatsLogging(peerConnection);
                    Debug.Log($"[WebRTCStatsLogger] Setup complete. IsLogging: {IsLogging}");
                }
                else
                {
                    Debug.LogError("[WebRTCStatsLogger] Failed to get RTCPeerConnection from WebRTCSessionManager!");
                }
            }
            else
            {
                Debug.LogError("[WebRTCStatsLogger] Failed to find m_peer field in WebRTCSessionManager!");
            }
        }

        /// <summary>
        /// 立即收集並記錄統計資料
        /// </summary>
        private void CollectAndLogStats()
        {
            Debug.Log($"[WebRTCStatsLogger] CollectAndLogStats called. PeerConnection: {m_peerConnection != null}, EnableLogging: {m_enableLogging}");

            if (m_peerConnection == null || !m_enableLogging)
            {
                Debug.LogWarning("[WebRTCStatsLogger] Cannot collect stats - connection not ready or logging disabled");
                return;
            }

            // 啟動異步收集統計資料
            Debug.Log("[WebRTCStatsLogger] Starting stats collection coroutine");
            StartCoroutine(CollectStatsCoroutine());
        }

        private IEnumerator CollectStatsCoroutine()
        {
            // 收集統計資料
            var statsOp = m_peerConnection.GetStats();
            yield return statsOp;

            if (statsOp.IsError)
            {
                Debug.LogError($"[WebRTCStatsLogger] Failed to get stats: {statsOp.Error}");
            }
            else
            {
                ProcessRTCStats(statsOp.Value);
            }
        }

        private void ProcessRTCStats(RTCStatsReport statsReport)
        {
            if (statsReport == null || statsReport.Stats == null)
            {
                Debug.LogWarning("[WebRTCStatsLogger] RTCStatsReport is null or empty");
                return;
            }

            // 尋找 candidate-pair 統計資料
            var candidatePairCount = 0;
            foreach (var stat in statsReport.Stats)
            {
                if (stat.Value.Type == RTCStatsType.CandidatePair)
                {
                    candidatePairCount++;
                    ProcessCandidatePairStats(stat.Key, stat.Value);
                }
            }

            // 如果沒有找到任何 candidate pair，記錄警告
            if (candidatePairCount == 0)
            {
                Debug.LogWarning("[WebRTCStatsLogger] No candidate pair statistics found in the stats report");
            }
        }

        private void ProcessCandidatePairStats(string statsId, RTCStats stats)
        {
            var candidatePairStats = stats as RTCIceCandidatePairStats;
            if (candidatePairStats == null)
            {
                Debug.LogWarning($"[WebRTCStatsLogger] Failed to cast stats to RTCIceCandidatePairStats for ID: {statsId}");
                return;
            }

            // 記錄所有 candidate pair 的狀態（不僅僅是 succeeded）
            var state = candidatePairStats.state ?? "unknown";

            // 只處理成功的 candidate pair 或所有 pair（根據需求）
            if (state == "succeeded" || state == "in-progress")
            {
                var rtt = candidatePairStats.currentRoundTripTime;
                var localCandidateId = candidatePairStats.localCandidateId ?? "N/A";
                var remoteCandidateId = candidatePairStats.remoteCandidateId ?? "N/A";
                var bytesSent = candidatePairStats.bytesSent;
                var bytesReceived = candidatePairStats.bytesReceived;
                var packetsDiscarded = candidatePairStats.packetsDiscardedOnSend;

                // 計算額外的統計資訊
                var packetsSent = candidatePairStats.packetsSent;
                var packetsReceived = candidatePairStats.packetsReceived;
                var availableIncomingBitrate = candidatePairStats.availableIncomingBitrate;
                var availableOutgoingBitrate = candidatePairStats.availableOutgoingBitrate;
                var nominated = candidatePairStats.nominated;
                var totalRtt = candidatePairStats.totalRoundTripTime;

                // 記錄 RTT 和其他重要資訊
                // Debug.Log($"[WebRTCStatsLogger] === Candidate Pair Stats ===");
                // Debug.Log($"[WebRTCStatsLogger] Stats ID: {statsId}");
                var rttMs = rtt * 1000; // 轉換為毫秒
                var totalLatency = m_currentMaxInferenceTime + rttMs;

                // Debug.Log($"[WebRTCStatsLogger] ⭐ Current RTT: {rttMs:F2} ms"); // 主要關注的 RTT
                // Debug.Log($"[WebRTCStatsLogger] 🧠 Current Max Inference Time: {m_currentMaxInferenceTime:F2} ms");
                // Debug.Log($"[WebRTCStatsLogger] 🚀 Total Latency (Current Max Inference + RTT): {totalLatency:F2} ms");
                // Debug.Log($"[WebRTCStatsLogger] Total RTT: {totalRtt * 1000:F2} ms");
                // Debug.Log($"[WebRTCStatsLogger] State: {candidatePairStats.state} | Nominated: {nominated}");
                // Debug.Log($"[WebRTCStatsLogger] Local Candidate: {localCandidateId}");
                // Debug.Log($"[WebRTCStatsLogger] Remote Candidate: {remoteCandidateId}");
                // Debug.Log($"[WebRTCStatsLogger] 📤 Sent - Bytes: {bytesSent:N0} | Packets: {packetsSent:N0}");
                // Debug.Log($"[WebRTCStatsLogger] 📥 Received - Bytes: {bytesReceived:N0} | Packets: {packetsReceived:N0}");
                // Debug.Log($"[WebRTCStatsLogger] 🗑️ Packets Discarded: {packetsDiscarded:N0}");
                // Debug.Log($"[WebRTCStatsLogger] 🌐 Bitrate - In: {availableIncomingBitrate:F0} bps | Out: {availableOutgoingBitrate:F0} bps");
                // Debug.Log($"[WebRTCStatsLogger] ========================");

                
                Debug.Log($"[WebRTCStatsLogger] {DateTime.Now:HH:mm:ss.fff} - Total RTT: {totalLatency:F2} ms");

                // 如果需要，可以觸發事件或回調
                OnRTTReceived?.Invoke(rtt, statsId);
            }
        }

        /// <summary>
        /// 處理推理結果，提取當前 JSON 中最大的 inference time
        /// </summary>
        /// <param name="bytes">推理結果的 JSON 位元組</param>
        private void OnInferenceResultReceived(byte[] bytes)
        {
            Debug.Log("[WebRTCStatsLogger] OnInferenceResultReceived called!");
            try
            {
                var json = Encoding.UTF8.GetString(bytes);
                Debug.Log($"[WebRTCStatsLogger] Received inference result: {json}");

                // 解析當前 JSON 中最大的推理時間
                if (TryParseMaxInferenceTime(json, out double maxInferenceTimeMs))
                {
                    // 更新當前最大推理時間
                    m_currentMaxInferenceTime = maxInferenceTimeMs;
                    // Debug.Log($"[WebRTCStatsLogger] ⚙️ Current JSON max inference time: {m_currentMaxInferenceTime:F2} ms");

                    // 立即收集並記錄 RTCStats
                    CollectAndLogStats();
                }
                else
                {
                    Debug.LogWarning("[WebRTCStatsLogger] Could not extract inference time from JSON");
                    m_currentMaxInferenceTime = 0.0; // 重設為 0 如果解析失敗
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[WebRTCStatsLogger] Failed to process inference result: {ex.Message}");
                m_currentMaxInferenceTime = 0.0; // 重設為 0 如果發生錯誤
            }
        }

        /// <summary>
        /// 嘗試從當前 JSON 中找出最大的 execution_time，支援物件和陣列格式
        /// </summary>
        /// <param name="json">JSON 字串</param>
        /// <param name="maxInferenceTimeMs">當前 JSON 中最大的推理時間（毫秒）</param>
        /// <returns>是否成功解析</returns>
        private bool TryParseMaxInferenceTime(string json, out double maxInferenceTimeMs)
        {
            maxInferenceTimeMs = 0.0;
            var allInferenceTimes = new List<double>();

            try
            {
                // 首先判斷 JSON 是陣列還是物件
                var trimmedJson = json.Trim();

                if (trimmedJson.StartsWith("["))
                {
                    // JSON 陣列格式 - 嘗試解析為 InferenceResult 陣列
                    var inferenceResults = JsonConvert.DeserializeObject<InferenceResult[]>(json);

                    if (inferenceResults != null && inferenceResults.Length > 0)
                    {
                        // 收集所有有效的 execution_time
                        foreach (var result in inferenceResults)
                        {
                            if (result?.execution_time.HasValue == true)
                            {
                                // 將秒轉換為毫秒
                                var timeMs = result.execution_time.Value * 1000;
                                allInferenceTimes.Add(timeMs);
                                Debug.Log($"[WebRTCStatsLogger] Found execution_time: {result.execution_time.Value:F3}s = {timeMs:F2}ms");
                            }
                        }
                    }

                    // 如果沒有找到有效的 execution_time，嘗試解析為數值陣列
                    if (allInferenceTimes.Count == 0)
                    {
                        try
                        {
                            var numbers = JsonConvert.DeserializeObject<double[]>(json);
                            if (numbers != null && numbers.Length > 0)
                            {
                                foreach (var number in numbers)
                                {
                                    // 假設數字是以秒為單位，轉換為毫秒
                                    var timeMs = number * 1000;
                                    allInferenceTimes.Add(timeMs);
                                    Debug.Log($"[WebRTCStatsLogger] Found number: {number:F3}s = {timeMs:F2}ms");
                                }
                            }
                        }
                        catch
                        {
                            // 如果數值陣列解析失敗，繼續嘗試其他方法
                        }
                    }
                }
                else if (trimmedJson.StartsWith("{"))
                {
                    // JSON 物件格式
                    var inferenceResult = JsonConvert.DeserializeObject<InferenceResult>(json);

                    if (inferenceResult?.execution_time.HasValue == true)
                    {
                        // 將秒轉換為毫秒
                        var timeMs = inferenceResult.execution_time.Value * 1000;
                        allInferenceTimes.Add(timeMs);
                        Debug.Log($"[WebRTCStatsLogger] Found execution_time in object: {inferenceResult.execution_time.Value:F3}s = {timeMs:F2}ms");
                    }
                    else
                    {
                        // 嘗試動態解析物件中的其他可能欄位
                        var dynamicObj = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                        if (dynamicObj != null)
                        {
                            // 嘗試常見的推理時間欄位名稱
                            string[] possibleKeys = { "execution_time", "inference_time", "processing_time", "latency", "duration" };

                            foreach (var key in possibleKeys)
                            {
                                if (dynamicObj.TryGetValue(key, out var value) &&
                                    double.TryParse(value?.ToString(), out var timeSeconds))
                                {
                                    // 將秒轉換為毫秒
                                    var timeMs = timeSeconds * 1000;
                                    allInferenceTimes.Add(timeMs);
                                    Debug.Log($"[WebRTCStatsLogger] Found {key}: {timeSeconds:F3}s = {timeMs:F2}ms");
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 純數值格式
                    if (double.TryParse(trimmedJson, out var timeSeconds))
                    {
                        // 將秒轉換為毫秒
                        var timeMs = timeSeconds * 1000;
                        allInferenceTimes.Add(timeMs);
                        Debug.Log($"[WebRTCStatsLogger] Parsed raw number: {timeSeconds:F3}s = {timeMs:F2}ms");
                    }
                }

                // 如果找到任何推理時間，返回最大值
                if (allInferenceTimes.Count > 0)
                {
                    maxInferenceTimeMs = allInferenceTimes.Max();
                    Debug.Log($"[WebRTCStatsLogger] Found {allInferenceTimes.Count} inference times, max: {maxInferenceTimeMs:F2}ms");
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[WebRTCStatsLogger] JSON parsing attempt failed: {ex.Message}");
            }

            return false;
        }
        #endregion

        #region Events
        /// <summary>
        /// 當收到 RTT 資料時觸發的事件
        /// </summary>
        public System.Action<double, string> OnRTTReceived;
        #endregion

        #region Inspector Methods (for debugging)
        [ContextMenu("Start Stats Logging")]
        private void StartStatsLoggingFromInspector()
        {
            var sessionManager = FindObjectOfType<WebRTCSessionManager>();
            if (sessionManager != null && sessionManager.IsConnected)
            {
                StartCoroutine(WaitForConnectionAndStartStats(sessionManager));
            }
            else
            {
                Debug.LogWarning("[WebRTCStatsLogger] WebRTC is not connected yet!");
            }
        }

        [ContextMenu("Stop Stats Logging")]
        private void StopStatsLoggingFromInspector()
        {
            StopStatsLogging();
        }

        [ContextMenu("Toggle Logging")]
        private void ToggleLogging()
        {
            m_enableLogging = !m_enableLogging;
            Debug.Log($"[WebRTCStatsLogger] Logging {(m_enableLogging ? "enabled" : "disabled")}");
        }
        #endregion
    }

    /// <summary>
    /// 推理結果的 JSON 結構
    /// </summary>
    [System.Serializable]
    public class InferenceResult
    {
        /// <summary>
        /// 推理時間（毫秒）
        /// </summary>
        public int? x;
        public int? y;
        public int? width;
        public int? height;
        public double? confidence;
        public int? class_id;
        public string? class_name;
        public double? execution_time;

        /// <summary>
        /// 其他可能的欄位可以在這裡加入
        /// 例如: detections, confidence, etc.
        /// </summary>
    }
}