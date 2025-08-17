using Unity.WebRTC;
using UnityEngine;
using System;
using System.Collections;
using System.Text;
using UnityEngine.Networking;
using Newtonsoft.Json;
using UnityEngine.UI;

[Serializable]
public class SDPExchange
{
    public string Sdp;
    public string Type;
}
[Serializable]
public class IceCandidateExchange
{
    public string Candidate;
    public string SdpMid;
    public int SdpMLineIndex;
}

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public class WebRTCClientManager : MonoBehaviour
    {
        [SerializeField] private WebCamTextureManager m_webCamTextureManager;
        [SerializeField] private RawImage m_image;
        [SerializeField] private float m_inferenceInterval = 0.5f;
        private WebCamTexture m_webCamTexture;
        private RenderTexture m_renderTexture;
        private VideoStreamTrack m_videoTrack;
        private RTCPeerConnection m_peer;
        private RTCDataChannel m_dataChannel;

        public event Action<byte[]> OnInferenceResultReceived;

        private const string SIGNALING_URL = "https://140.113.24.246:8080/offer";
        private const string SIGNALING_ICE_URL = "https://140.113.24.246:8080/candidate";



        private IEnumerator InitConnection()
        {
            Debug.Log("[WebRTC] Init connection.");

            var config = new RTCConfiguration
            {
                iceServers = new[]
                {
                    new RTCIceServer { urls = new[] {"stun:stun.l.google.com:19302"} }
                }
            };
            m_peer = new RTCPeerConnection(ref config)
            {
                OnIceCandidate = candidate =>
                {
                    if (candidate != null)
                    {
                        Debug.Log($"[WebRTC] New ICE candidate: {candidate.Candidate}");
                        _ = StartCoroutine(SendIceCandidateToServer(candidate));
                    }
                },
                OnIceConnectionChange = state =>
                {
                    Debug.Log($"[WebRTC] ICE Connection State Changed: {state}");
                },

                OnDataChannel = channel =>
                {
                    m_dataChannel = channel;
                    m_dataChannel.OnMessage = OnMessageReceived;
                }
            };

            m_webCamTexture = m_webCamTextureManager.WebCamTexture;
            yield return new WaitUntil(() =>
                m_webCamTexture != null &&
                m_webCamTexture.isPlaying &&
                m_webCamTexture.width > 16 &&
                m_webCamTexture.height > 16
            );
            Debug.Log($"[WebRTC] m_webCamTexture is ready: {m_webCamTexture.width}x{m_webCamTexture.height}");

            var gfxType = SystemInfo.graphicsDeviceType;
            var format = WebRTC.GetSupportedRenderTextureFormat(gfxType);
            m_renderTexture = new RenderTexture(m_webCamTexture.width, m_webCamTexture.height, 0, format)
            {
                graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.B8G8R8A8_SRGB
            };
            _ = m_renderTexture.Create();
            Debug.Log("[WebRTC] m_renderTexture created.");

            _ = StartCoroutine(CopyWebcamToRenderTexture(m_webCamTexture, m_renderTexture));
            Debug.Log("[WebRTC] Started copying webcam to RenderTexture");

            m_videoTrack = new VideoStreamTrack(m_renderTexture);
            yield return new WaitUntil(() => m_videoTrack != null);
            Debug.Log($"[WebRTC] VideoTrack initialized: {m_videoTrack.Id}");
            m_videoTrack.OnVideoReceived += (Texture frame) =>
            {
                Debug.Log("[WebRTC] Video frame sent");
            };

            var sender = m_peer.AddTrack(m_videoTrack);
            yield return new WaitUntil(() => sender != null);
            Debug.Log($"[WebRTC] Added track with id: {sender.Track.Id}");

            var channelInit = new RTCDataChannelInit
            {
                ordered = true,
                protocol = "json"
            };
            m_dataChannel = m_peer.CreateDataChannel("detections", channelInit);
            Debug.Log($"[WebRTC] data channel id: {m_dataChannel.Id}");

            m_dataChannel.OnMessage = OnMessageReceived;
            //m_dataChannel.OnMessage = (bytes) =>
            //{
            //    Debug.Log($"[WebRTC] DataChannel received message: {Encoding.UTF8.GetString(bytes)}");
            //};

            var offerOp = m_peer.CreateOffer();
            yield return offerOp;
            var offerDesc = offerOp.Desc;
            Debug.Log($"[WebRTC] SDP offer:\n{offerDesc.sdp}");
            yield return m_peer.SetLocalDescription(ref offerDesc);

            yield return SendOfferToServer(offerDesc);
        }

        private IEnumerator SendOfferToServer(RTCSessionDescription offerDesc)
        {
            var offer = new SDPExchange { Sdp = offerDesc.sdp, Type = "offer" };
            var json = JsonUtility.ToJson(offer);

            var request = new UnityWebRequest(SIGNALING_URL, "POST")
            {
                certificateHandler = new BypassCertificate(),
                useHttpContinue = false
            };

            var bodyRaw = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[WebRTC] Signaling failed: {request.error}");
                yield break;
            }

            var responseText = request.downloadHandler.text;
            Debug.Log($"[WebRTC] Response from server: {responseText}");

            if (string.IsNullOrWhiteSpace(responseText))
            {
                Debug.LogError("[WebRTC] Server response is empty!");
                yield break;
            }

            var answer = JsonConvert.DeserializeObject<SDPExchange>(responseText);
            if (string.IsNullOrWhiteSpace(answer.Sdp))
            {
                Debug.LogError("[WebRTC] Parsed SDP is null or empty!");
                yield break;
            }

            if (answer.Sdp.Contains("m=video"))
            {
                Debug.Log("[WebRTC] SDP answer includes video m-line, server supports video stream.");
            }
            else
            {
                Debug.LogError("[WebRTC] SDP answer does not include video m-line, server does not support video stream.");
            }

            var answerDesc = new RTCSessionDescription
            {
                type = RTCSdpType.Answer,
                sdp = answer.Sdp
            };

            var result = m_peer.SetRemoteDescription(ref answerDesc);
            yield return result;

            if (result.IsError)
            {
                Debug.LogError($"[WebRTC] SetRemoteDescription failed: {result.Error.message}");
            }
            else
            {
                Debug.Log("[WebRTC] SetRemoteDescription success");
            }

            Debug.Log("[WebRTC] Connected to WebRTC Server.");

            Debug.Log($"[WebRTC] DataChannel readyState: {m_dataChannel.ReadyState}, Label: {m_dataChannel.Label}");

            RTCOfferAnswerOptions options = default;
            var op = m_peer.CreateAnswer(ref options);
            yield return op;
        }

        private IEnumerator SendIceCandidateToServer(RTCIceCandidate candidate)
        {
            var ice = new IceCandidateExchange
            {
                Candidate = candidate.Candidate,
                SdpMid = candidate.SdpMid,
                SdpMLineIndex = candidate.SdpMLineIndex ?? 0
            };

            var json = JsonConvert.SerializeObject(ice);

            var request = new UnityWebRequest(SIGNALING_ICE_URL, "POST")
            {
                certificateHandler = new BypassCertificate(),
                useHttpContinue = false
            };

            var bodyRaw = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[WebRTC] Send ICE candidate failed: {request.error}");
            }
        }

        private IEnumerator SendAnswerToServer(RTCSessionDescription answerDesc)
        {
            var answer = new SDPExchange { Sdp = answerDesc.sdp, Type = "answer" };
            var json = JsonConvert.SerializeObject(answer);

            var request = new UnityWebRequest(SIGNALING_URL + "/answer", "POST")
            {
                certificateHandler = new BypassCertificate(),
                useHttpContinue = false
            };

            var bodyRaw = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[WebRTC] Send answer failed: {request.error}");
                yield break;
            }

            Debug.Log("[WebRTC] Answer sent successfully to server");
        }

        private void SendImageForInference(byte[] imageBytes)
        {
            if (m_dataChannel != null && m_dataChannel.ReadyState == RTCDataChannelState.Open)
            {
                try
                {
                    m_dataChannel.Send(imageBytes);
                    Debug.Log($"[WebRTC] Image sent successfully, size: {imageBytes.Length} bytes");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[WebRTC] Failed to send image: {e.Message}");
                }
            }
            else
            {
                Debug.LogWarning("[WebRTC] DataChannel not open yet, state: " +
                    (m_dataChannel != null ? m_dataChannel.ReadyState.ToString() : "null"));
            }
        }
        private void OnMessageReceived(byte[] bytes)
        {
            Debug.Log("[WebRTC] OnMessageReceived called");
            var json = Encoding.UTF8.GetString(bytes);
            Debug.Log($"[WebRTC] Inference result received from server: {json}");

            OnInferenceResultReceived?.Invoke(bytes);
        }


        private IEnumerator CopyWebcamToRenderTexture(WebCamTexture source, RenderTexture target)
        {
            Debug.Log("[WebRTC] Copy coroutine started.");
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                if (source == null)
                {
                    Debug.LogWarning("[WebRTC] WebCamTexture is null!");
                    continue;
                }
                if (source.didUpdateThisFrame)
                {
                    Graphics.Blit(source, target);
                    m_image.texture = target;
                }
            }
        }

        private class BypassCertificate : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificate)
            {
                return true;
            }
        }

        private IEnumerator Start()
        {
            Debug.Log("[WebRTC] Starting WebRTC client.");
            var timeoutSeconds = 10.0f;
            var elapsedTime = 0f;

            while (m_webCamTextureManager.WebCamTexture == null)
            {
                yield return null;
                if (elapsedTime > timeoutSeconds)
                {
                    Debug.LogError("[WebRTC] Timeout waiting for WebCamTextureManager initialization.");
                    yield break;
                }
                elapsedTime += Time.deltaTime;
            }

            _ = StartCoroutine(WebRTC.Update());

            Debug.Log("[WebRTC] WebCamTextureManager initialized successfully.");
            Debug.Log("[WebRTC] Starting InitConnection...");
            _ = StartCoroutine(InitConnection());

        }

        private void Update()
        {
            if (PassthroughCameraPermissions.HasCameraPermission != true)
            {
                Debug.LogError("[WebRTC] No camera permission granted.");
            }

            if (m_peer != null && m_peer.ConnectionState == RTCPeerConnectionState.Disconnected)
            {
                Debug.Log("[WebRTC] Peer connection disconnected.");
            }
        }

        private void OnDestroy()
        {
            m_peer.Close();
            m_dataChannel.Close();
            m_videoTrack.Dispose();
            m_renderTexture.Release();
        }

    }
}