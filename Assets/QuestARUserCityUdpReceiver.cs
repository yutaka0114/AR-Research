using System;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class QuestARUserCityUdpReceiver : MonoBehaviour
{
    [Header("UDP listen")]
    public int listenPort = 5010;

    [Header("Avatar")]
    public GameObject avatarPrefab; // unity-chan prefab
    public Transform cityRoot;       // CityRoot
    [Range(0f, 1f)] public float posLerp = 0.25f;
    [Range(0f, 1f)] public float rotSlerp = 0.25f;

    [Header("If no data")]
    public bool debugSpawnInFrontIfNoData = true;
    public Transform head;           // Main Camera (HMD)
    public float debugDistance = 1.5f;

    [Serializable] public class Pos { public float x, y, z; }
    [Serializable] public class Payload
    {
        public string ts;
        public Pos pos;
        public float yaw_deg;
        public float pitch_deg;
        public float roll_deg;
    }

    private UdpClient udp;
    private GameObject avatar;
    private Payload latest;
    private bool hasData;
    private float logNext;

    void Start()
    {
        if (head == null && Camera.main != null) head = Camera.main.transform;

        if (avatarPrefab == null)
        {
            Debug.LogError("avatarPrefab is null. Assign unity-chan prefab in Inspector.");
            enabled = false;
            return;
        }
        if (cityRoot == null)
        {
            Debug.LogError("cityRoot is null. Assign CityRoot in Inspector.");
            enabled = false;
            return;
        }

        avatar = Instantiate(avatarPrefab);
        avatar.name = "ARUser(UnityChan)";

        udp = new UdpClient(listenPort);
        udp.Client.ReceiveTimeout = 1; // 1msではなく1秒系にしたいが、UdpClientはmsではなく例外で抜けるだけ
        Debug.Log($"UDP receiver started <- *:{listenPort}");
    }

    void OnDestroy()
    {
        try { udp?.Close(); } catch { }
        udp = null;
    }

    void Update()
    {
        if (udp == null || avatar == null) return;

        // 受信（ノンブロッキング）
        try
        {
            while (udp.Available > 0)
            {
                var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                byte[] bytes = udp.Receive(ref ep);
                string json = Encoding.UTF8.GetString(bytes);

                var p = JsonUtility.FromJson<Payload>(json);
                if (p != null && p.pos != null)
                {
                    latest = p;
                    hasData = true;

                    if (Time.unscaledTime >= logNext)
                    {
                        logNext = Time.unscaledTime + 1f;
                        Debug.Log("UDP json: " + json);
                    }
                }
            }
        }
        catch { /* ignore */ }

        if (!hasData)
        {
            if (debugSpawnInFrontIfNoData && head != null)
            {
                Vector3 p = head.position + head.forward * debugDistance;
                avatar.transform.position = Vector3.Lerp(avatar.transform.position, p, 0.5f);
                avatar.transform.rotation = Quaternion.Slerp(
                    avatar.transform.rotation,
                    Quaternion.LookRotation(head.forward, Vector3.up),
                    0.5f
                );
            }
            return;
        }

        // cityRootローカル座標 -> ワールド座標
        Vector3 localPos = new Vector3(latest.pos.x, latest.pos.y, latest.pos.z);
        Vector3 targetPos = cityRoot.TransformPoint(localPos);

        // yaw_deg は cityRootローカルの +Z(前) 基準として解釈
        Quaternion localRot = Quaternion.Euler(latest.pitch_deg, latest.yaw_deg, latest.roll_deg);
        Quaternion targetRot = cityRoot.rotation * localRot;

        avatar.transform.position = Vector3.Lerp(avatar.transform.position, targetPos, posLerp);
        avatar.transform.rotation = Quaternion.Slerp(avatar.transform.rotation, targetRot, rotSlerp);
    }
}