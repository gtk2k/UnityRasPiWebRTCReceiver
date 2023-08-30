using Newtonsoft.Json;
using System;
using System.Collections;
using System.Threading;
using Unity.WebRTC;
using UnityEngine;
using WebSocketSharp;

public class RasPiWebRTCReceiver : MonoBehaviour
{
    public string signalingURL;

    private WebSocket ws;
    private RTCPeerConnection pc;
    private SynchronizationContext ctx;

    private enum Side { Local, Remote }

    [Serializable]
    public class MomoIce
    {
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
    }

    [Serializable]
    public class MomoSignalingMessage
    {
        public string type;
        public string sdp;
        public MomoIce ice;

        public RTCSessionDescription ToDesc()
        {
            return new RTCSessionDescription
            {
                type = type == "offer" ? RTCSdpType.Offer : RTCSdpType.Answer,
                sdp = sdp,
            };
        }

        public RTCIceCandidate ToCand()
        {
            return new RTCIceCandidate(new RTCIceCandidateInit
            {
                candidate = ice.candidate,
                sdpMid = ice.sdpMid,
                sdpMLineIndex = ice.sdpMLineIndex
            });
        }

        public static MomoSignalingMessage FromDesc(RTCSessionDescription desc)
        {
            return new MomoSignalingMessage
            {
                type = desc.type == RTCSdpType.Offer ? "offer" : "asnwer",
                sdp = desc.sdp
            };
        }

        public static MomoSignalingMessage FromCand(RTCIceCandidate cand)
        {
            return new MomoSignalingMessage
            {
                type = "candidate",
                ice = new MomoIce
                {
                    candidate = cand.Candidate,
                    sdpMid = cand.SdpMid,
                    sdpMLineIndex = cand.SdpMLineIndex.Value
                }
            };
        }
    }

    private void Start()
    {
        Debug.Log($"=== Start");

        ctx = SynchronizationContext.Current;

        StartCoroutine(WebRTC.Update());

        ws = new WebSocket(signalingURL);
        ws.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
        ws.OnOpen += Ws_OnOpen;
        ws.OnMessage += Ws_OnMessage;
        ws.OnClose += Ws_OnClose;
        ws.OnError += Ws_OnError;
        ws.Connect();
    }

    private void OnDisable()
    {
        Debug.Log($"=== OnDisable");

        pc?.Close();
        pc = null;
        ws?.Close();
        ws = null;
    }

    private void Ws_OnError(object sender, ErrorEventArgs e)
    {
        ctx.Post(_ =>
        {
            Debug.Log($"=== Ws_OnError");
            Debug.LogError($"WS Error: {e.Exception.Message}");
        }, null);
    }

    private void Ws_OnClose(object sender, CloseEventArgs e)
    {
        ctx.Post(_ =>
        {
            Debug.Log($"=== Ws_OnClose");
            Debug.Log($"WS Close: code:{e.Code}, reason:{e.Reason}");
        }, null);
    }

    private void Ws_OnMessage(object sender, MessageEventArgs e)
    {
        ctx.Post(_ =>
        {
            Debug.Log($"=== Ws_OnMessage");

            var msg = JsonConvert.DeserializeObject<MomoSignalingMessage>(e.Data);

            Debug.Log($"=== {msg.type}");
            switch (msg.type)
            {
                case "answer":
                    StartCoroutine(SetDescription(Side.Remote, msg.ToDesc()));
                    break;
                case "candidate":
                    pc.AddIceCandidate(msg.ToCand());
                    break;
            }
        }, null);
    }

    private void Ws_OnOpen(object sender, EventArgs e)
    {
        ctx.Post(_ =>
        {
            Debug.Log($"=== Ws_OnOpen");
            SetupPeerConnection();
        }, null);
    }

    private void Send(MomoSignalingMessage msg)
    {
        Debug.Log($"=== Send > {msg.type}");
        var data = JsonConvert.SerializeObject(msg);
        ws.Send(data);
    }

    private void SetupPeerConnection()
    {
        Debug.Log($"=== SetupPeerConnection");

        RTCConfiguration config = default;
        config.iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } };

        // Debug.Log($"OnConfig");

        pc = new RTCPeerConnection(ref config);
        pc.OnIceCandidate = candidate =>
        {
            Send(MomoSignalingMessage.FromCand(candidate));
        };
        pc.OnIceGatheringStateChange = state =>
        {
            Debug.Log($"OnIceGatheringStateChange > {state}");
        };
        pc.OnConnectionStateChange = state =>
        {
            Debug.Log($"OnConnectionStateChange > {state}");
        };
        pc.OnTrack = evt =>
        {
            if (evt.Track is VideoStreamTrack videoTrack)
            {
                videoTrack.OnVideoReceived += (tex) =>
                {
                    GetComponent<Renderer>().material.mainTexture = tex;
                };
            }
        };

        var videoTransceiver = pc.AddTransceiver(TrackKind.Video);
        videoTransceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;
        var audioTransceiver = pc.AddTransceiver(TrackKind.Video);
        audioTransceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;

        StartCoroutine(CreateDescription(RTCSdpType.Offer));
    }

    private IEnumerator CreateDescription(RTCSdpType type)
    {
        Debug.Log($"=== CreateDescription");

        var op = type == RTCSdpType.Offer ? pc.CreateOffer() : pc.CreateAnswer();
        yield return op;
        if (op.IsError)
        {
            Debug.LogError($"Create {type} Error > {op.Error.message}");
            yield break;
        }
        yield return StartCoroutine(SetDescription(Side.Local, op.Desc));
    }

    private IEnumerator SetDescription(Side side, RTCSessionDescription desc)
    {
        Debug.Log($"=== SetDescription");
        var op = side == Side.Local ? pc.SetLocalDescription(ref desc) : pc.SetRemoteDescription(ref desc);
        yield return op;
        if (op.IsError)
        {
            Debug.LogError($"Set {desc.type} Error > {op.Error.message}");
            yield break;
        }
        if (side == Side.Local)
        {
            Send(MomoSignalingMessage.FromDesc(desc));
        }
        else if (desc.type == RTCSdpType.Offer)
        {
            yield return (CreateDescription(RTCSdpType.Answer));
        }
    }
}
