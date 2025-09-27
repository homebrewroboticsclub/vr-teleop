using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.XR;
using WebSocketSharp;

public class QuestRosPoseAndJointsPublisher : MonoBehaviour
{
    [Header("ROSBridge")]
    public string wsUrl = "ws://192.168.1.100:9090";
    public string poseArrayTopic = "/quest/poses";   // geometry_msgs/PoseArray
    public string jointStateTopic = "/quest/joints"; // sensor_msgs/JointState
    public string poseFrameId = "unity_world";       // header.frame_id для PoseArray
    public string headFrameId = "head";              // логический ид головы (для читаемости в отладке)

    [Header("XR")]
    public Camera xrCamera; // укажите Main Camera из XR Origin

    [Header("Rate")]
    [Range(1, 120)] public float sendHz = 10f;

    [Header("Debug")]
    public bool debugPrint = true;

    private WebSocket ws;
    private WaitForSeconds wait;
    private float lastDebug;

    private readonly List<InputDevice> tmp = new();

    private static readonly InputFeatureUsage<float> kPinchIndex = new("pinch_strength_index");
    private static readonly InputFeatureUsage<float> kPinchMiddle = new("pinch_strength_middle");
    private static readonly InputFeatureUsage<float> kPinchRing = new("pinch_strength_ring");
    private static readonly InputFeatureUsage<float> kPinchLittle = new("pinch_strength_little");

    [SerializeField]
    private TMP_Text IpText;

    [SerializeField]
    private TMP_Text PortText;
    void Awake()
    {
        if (!xrCamera) xrCamera = Camera.main;
        wait = new WaitForSeconds(1f / Mathf.Max(1f, sendHz));
    }

    public void InitConnection()
    {
        wsUrl = $"ws://{IpText.text}:{PortText.text}";
        Connect();
        StartCoroutine(SendLoop());
    }

    public void Disconnect()
    {
        try
        {
            if (ws != null && ws.ReadyState == WebSocketState.Open)
            {
                ws.Send(JsonConvert.SerializeObject(new { op = "unadvertise", topic = poseArrayTopic }));
                ws.Send(JsonConvert.SerializeObject(new { op = "unadvertise", topic = jointStateTopic }));
            }
            ws?.Close();
        }
        catch { }
    }

    void OnDisable()
    {
        Disconnect();
    }

    void Connect()
    {
        ws = new WebSocket(wsUrl);
        ws.Compression = WebSocketSharp.CompressionMethod.Deflate;
        ws.OnOpen += (_, __) =>
        {
            Debug.Log("[ROS TX] WS opened");
            // Явно объявляем паблишеров
            ws.Send(JsonConvert.SerializeObject(new
            {
                op = "advertise",
                topic = poseArrayTopic,
                type = "geometry_msgs/PoseArray",
                latch = false
            }));
            ws.Send(JsonConvert.SerializeObject(new
            {
                op = "advertise",
                topic = jointStateTopic,
                type = "sensor_msgs/JointState",
                latch = false
            }));
        };
        ws.OnError += (_, e) => Debug.LogWarning("[ROS TX] WS error: " + e.Message);
        ws.OnClose += (_, e) => Debug.LogWarning("[ROS TX] WS closed: " + e.Reason);
        ws.ConnectAsync();
    }

    IEnumerator SendLoop()
    {
        yield return new WaitForSeconds(0.5f); // даём XR подняться
        while (true)
        {
            TrySendOnce();
            yield return wait;
        }
    }

    void TrySendOnce()
    {
        if (ws == null || ws.ReadyState != WebSocketState.Open || xrCamera == null)
            return;

        var headPosW = xrCamera.transform.position;
        var headRotW = xrCamera.transform.rotation;

        var leftCtrl = GetDevice(true, true);   // left controller
        var rightCtrl = GetDevice(false, true);   // right controller
        bool anyCtrlTracked = IsTracked(leftCtrl) || IsTracked(rightCtrl);

        var leftHand = GetDevice(true, false);  // left hand
        var rightHand = GetDevice(false, false);  // right hand
        bool anyHandTracked = (!anyCtrlTracked) && (IsTracked(leftHand) || IsTracked(rightHand));

        bool useControllers = anyCtrlTracked;
        bool useHands = !useControllers && anyHandTracked;

        var poses = new JArray();

        // head (абсолютная поза в world)
        poses.Add(PoseJson(headPosW, headRotW));

        // helper: пишет позу девайса относ. головы
        Func<InputDevice, JObject> RelToHead = (dev) =>
        {
            if (!dev.isValid) return PoseJson(Vector3.zero, Quaternion.identity);
            if (!dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pW)) return PoseJson(Vector3.zero, Quaternion.identity);
            if (!dev.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rW)) return PoseJson(Vector3.zero, Quaternion.identity);

            var pRel = Quaternion.Inverse(headRotW) * (pW - headPosW);
            var rRel = Quaternion.Inverse(headRotW) * rW;
            return PoseJson(pRel, rRel);
        };

        if (useControllers)
        {
            poses.Add(RelToHead(leftCtrl));
            poses.Add(RelToHead(rightCtrl));
        }
        else if (useHands)
        {
            poses.Add(RelToHead(leftHand));
            poses.Add(RelToHead(rightHand));
        }
        else
        {
            // ничего не трекается — положим нули
            poses.Add(PoseJson(Vector3.zero, Quaternion.identity));
            poses.Add(PoseJson(Vector3.zero, Quaternion.identity));
        }

        var header = RosHeader(poseFrameId);
        var poseArrayMsg = new JObject
        {
            ["header"] = header,
            ["poses"] = poses
        };

        Publish(poseArrayTopic, "geometry_msgs/PoseArray", poseArrayMsg);

        var names = new List<string>();
        var vals = new List<float>();

        Action<string, string, InputDevice> AddCtrl = (side, key, dev) =>
        {
            if (!dev.isValid) { names.Add($"{side}_{key}"); vals.Add(0f); return; }
            float v = 0f;
            if (key == "grip") dev.TryGetFeatureValue(CommonUsages.grip, out v);
            if (key == "index") dev.TryGetFeatureValue(CommonUsages.trigger, out v);
            names.Add($"{side}_{key}"); vals.Add(v);
        };

        Action<string, InputDevice> AddHand = (side, dev) =>
        {
            float idx = 0, mid = 0, ring = 0, lit = 0, grip = 0, trig = 0;
            if (dev.isValid)
            {
                dev.TryGetFeatureValue(kPinchIndex, out idx);
                dev.TryGetFeatureValue(kPinchMiddle, out mid);
                dev.TryGetFeatureValue(kPinchRing, out ring);
                dev.TryGetFeatureValue(kPinchLittle, out lit);
                dev.TryGetFeatureValue(CommonUsages.grip, out grip);
                dev.TryGetFeatureValue(CommonUsages.trigger, out trig);
            }
            names.Add($"{side}_pinch_index"); vals.Add(idx);
            names.Add($"{side}_pinch_middle"); vals.Add(mid);
            names.Add($"{side}_pinch_ring"); vals.Add(ring);
            names.Add($"{side}_pinch_little"); vals.Add(lit);
            names.Add($"{side}_grip"); vals.Add(grip);
            names.Add($"{side}_index"); vals.Add(trig);
        };

        if (useControllers)
        {
            AddCtrl("L", "grip", leftCtrl);
            AddCtrl("L", "index", leftCtrl);
            AddCtrl("R", "grip", rightCtrl);
            AddCtrl("R", "index", rightCtrl);
        }
        else
        {
            AddHand("L", leftHand);
            AddHand("R", rightHand);
        }

        var jointHeader = RosHeader(headFrameId); // логически привязываем к голове
        var jointMsg = new JObject
        {
            ["header"] = jointHeader,
            ["name"] = new JArray(names),
            ["position"] = new JArray(vals),
            // velocity/effort опциональны — опустим
        };
        Publish(jointStateTopic, "sensor_msgs/JointState", jointMsg);

        // --- 4) Debug print раз в секунду ---
        if (debugPrint && Time.unscaledTime - lastDebug > 1f)
        {
            lastDebug = Time.unscaledTime;
            var l0 = vals.Count > 0 ? vals[0] : 0f;
            var l1 = vals.Count > 1 ? vals[1] : 0f;
            Debug.Log($"[ROS TX] mode={(useControllers ? "controllers" : useHands ? "hands" : "none")} " +
                      $"head=({headPosW.x:F2},{headPosW.y:F2},{headPosW.z:F2}) " +
                      $"L0={l0:F2} L1={l1:F2}");
        }
    }

    InputDevice GetDevice(bool left, bool controller)
    {
        tmp.Clear();
        var ch = InputDeviceCharacteristics.None;
        ch |= left ? InputDeviceCharacteristics.Left : InputDeviceCharacteristics.Right;
        if (controller)
            ch |= InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand;
        else
            ch |= InputDeviceCharacteristics.HandTracking;

        InputDevices.GetDevicesWithCharacteristics(ch, tmp);
        return tmp.Count > 0 ? tmp[0] : default;
    }

    bool IsTracked(InputDevice dev)
    {
        if (!dev.isValid) return false;
        if (dev.TryGetFeatureValue(CommonUsages.isTracked, out bool t)) return t;
        return dev.TryGetFeatureValue(CommonUsages.devicePosition, out _);
    }

    static JObject PoseJson(Vector3 p, Quaternion q)
    {
        return new JObject
        {
            ["position"] = new JObject { ["x"] = p.x, ["y"] = p.y, ["z"] = p.z },
            ["orientation"] = new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w }
        };
    }

    static JObject RosHeader(string frameId)
    {
        var now = DateTimeOffset.UtcNow;
        long nanos = now.ToUnixTimeMilliseconds() * 1_000_000;
        int secs = (int)(nanos / 1_000_000_000);
        int nsecs = (int)(nanos % 1_000_000_000);

        return new JObject
        {
            ["stamp"] = new JObject { ["secs"] = secs, ["nsecs"] = nsecs },
            ["frame_id"] = frameId
        };
    }

    void Publish(string topic, string rosType, JObject msg)
    {
        var envelope = new JObject
        {
            ["op"] = "publish",
            ["topic"] = topic,
            ["msg"] = msg
        };
        ws.Send(envelope.ToString(Formatting.None));
    }
}
