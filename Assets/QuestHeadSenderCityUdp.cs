using System;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class QuestHeadSenderCityUdp : MonoBehaviour
{
    [Header("Receiver (Ubuntu)")]
    public string serverIp = "192.168.3.14";
    public int serverPort = 5005;

    [Header("Send rate")]
    [Range(1, 120)] public int sendHz = 30;

    [Header("References")]
    public Transform head;       // Main Camera (HMD)
    public Transform cityRoot;   // CityRoot

    [Serializable] public class Pos { public float x, y, z; }
    [Serializable] public class Payload { public Pos pos; public float yaw_deg; }

    private UdpClient udp;
    private float nextTime;
    private float logNextTime;

    void Start()
    {
        if (head == null && Camera.main != null) head = Camera.main.transform;

        if (head == null)
        {
            Debug.LogError("head is null. Assign Main Camera (HMD) in Inspector.");
            enabled = false;
            return;
        }
        if (cityRoot == null)
        {
            Debug.LogError("cityRoot is null. Assign CityRoot in Inspector.");
            enabled = false;
            return;
        }

        udp = new UdpClient();
        udp.Connect(serverIp, serverPort);

        Debug.Log($"UDP sender started -> {serverIp}:{serverPort}");
    }

    void OnDestroy()
    {
        try { udp?.Close(); } catch { }
        udp = null;
    }

    void Update()
    {
        if (udp == null) return;
        if (Time.unscaledTime < nextTime) return;
        nextTime = Time.unscaledTime + 1f / Mathf.Max(1, sendHz);

        Vector3 pCity = cityRoot.InverseTransformPoint(head.position);

        Vector3 fCity = cityRoot.InverseTransformDirection(head.forward);
        Vector3 flat = new Vector3(fCity.x, 0f, fCity.z);
        if (flat.sqrMagnitude < 1e-8f) flat = Vector3.forward;
        flat.Normalize();

        float yawDeg = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        if (yawDeg < 0f) yawDeg += 360f;

        if (float.IsNaN(pCity.x) || float.IsNaN(pCity.y) || float.IsNaN(pCity.z) || float.IsNaN(yawDeg))
            return;

        var payload = new Payload
        {
            pos = new Pos { x = pCity.x, y = pCity.y, z = pCity.z },
            yaw_deg = yawDeg
        };

        string json = JsonUtility.ToJson(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        udp.Send(bytes, bytes.Length);

        if (Time.unscaledTime >= logNextTime)
        {
            logNextTime = Time.unscaledTime + 1f;
            Debug.Log("UDP json: " + json);
        }
    }
}