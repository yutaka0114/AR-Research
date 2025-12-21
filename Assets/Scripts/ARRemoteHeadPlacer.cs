// Assets/Scripts/ARRemoteHeadPlacer.cs
// Unity 6 / AR Foundation 用：Ubuntuサーバの /latest.json を定期取得して「頭Prefab」を配置する
// - debugSpawnInFront=true なら、GPS/コンパス権限なしで「カメラの1m前」に必ず出す（まず表示確認用）
// - debugSpawnInFront=false なら、iPhoneのGPS/コンパスで「緯度経度→AR空間」へ配置する
//
// /latest.json 例（あなたのサーバはOK）:
// {"ts":"...","yaw_deg":161.1,"pos":{"x":..,"y":..,"z":..},"lat":33.88,"lon":130.88,"alt":...,"calibrated":true,...}

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class ARRemoteHeadPlacer : MonoBehaviour
{
    [Header("Server")]
    public string serverBaseUrl = "http://192.168.3.14:8000";
    public float pollIntervalSec = 0.2f;

    [Header("Prefab")]
    public GameObject headPrefab;

    [Header("Debug")]
    public bool debugSpawnInFront = true;      // まずは true 推奨（権限無しでも見える）
    public float debugFrontMeters = 1.0f;      // カメラの何m前に出すか
    public bool debugUseServerYaw = true;      // true: yaw_degで回転 / false: カメラ向きに合わせる

    [Header("Geo Placement")]
    public float maxDistanceMeters = 30f;      // 遠すぎると見失うので、まず30m推奨
    public float yOffsetMeters = -0.2f;        // 少し下げたい時（必要なら0に）
    [Range(0f, 1f)] public float posLerp = 0.25f;
    [Range(0f, 1f)] public float rotLerp = 0.25f;

    [Header("Yaw Handling")]
    public float yawOffsetDeg = 0f;            // ずれたら 90/180 など入れて補正
    public bool vrYawIsNorthBased = true;      // VR yawが「北=0,東=90」基準なら true

    // -------- JSON structs (JsonUtility用) --------
    [Serializable]
    public class Pos
    {
        public double x;
        public double y;
        public double z;
    }

    [Serializable]
    public class Latest
    {
        public string ts;
        public double yaw_deg;
        public Pos pos;
        public double lat;
        public double lon;
        public double alt;
        public bool calibrated;
        public string calib_method;
    }

    private Latest latest;
    private GameObject headObj;
    private Camera arCam;

    // Geo 変換用
    private bool geoReady = false;
    private Vector3 worldOriginPos;
    private double originLat;
    private double originLon;
    private Quaternion enuToWorldRot = Quaternion.identity;

    IEnumerator Start()
    {
        arCam = Camera.main;
        if (arCam == null)
        {
            Debug.LogError("[ARRemoteHeadPlacer] Main Camera not found.");
            yield break;
        }
        if (headPrefab == null)
        {
            Debug.LogError("[ARRemoteHeadPlacer] headPrefab is null.");
            yield break;
        }

        headObj = Instantiate(headPrefab);
        headObj.name = "RemoteHead";

        // /latest.json の取得開始
        StartCoroutine(PollLoop());

        // debugSpawnInFront=true のときは、位置情報の権限を要求しない（まず表示確認）
        if (!debugSpawnInFront)
        {
            yield return InitGeoOrigin();
        }
    }

    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return FetchLatest();
            yield return new WaitForSeconds(pollIntervalSec);
        }
    }

    IEnumerator FetchLatest()
    {
        string url = serverBaseUrl.TrimEnd('/') + "/latest.json";
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 3;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[ARRemoteHeadPlacer] Fetch failed: " + req.error);
                yield break;
            }

            try
            {
                latest = JsonUtility.FromJson<Latest>(req.downloadHandler.text);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ARRemoteHeadPlacer] JSON parse failed: " + e);
            }
        }
    }

    IEnumerator InitGeoOrigin()
    {
        // Location
        if (!Input.location.isEnabledByUser)
        {
            Debug.LogError("[ARRemoteHeadPlacer] Location service disabled by user.");
            yield break;
        }

        Input.location.Start(1f, 1f);

        int wait = 20;
        while (Input.location.status == LocationServiceStatus.Initializing && wait-- > 0)
            yield return new WaitForSeconds(1f);

        if (Input.location.status != LocationServiceStatus.Running)
        {
            Debug.LogError("[ARRemoteHeadPlacer] Location service not running.");
            yield break;
        }

        // Compass
        Input.compass.enabled = true;
        yield return new WaitForSeconds(0.5f);

        // Origin (AR world origin point at app start)
        worldOriginPos = arCam.transform.position;
        originLat = Input.location.lastData.latitude;
        originLon = Input.location.lastData.longitude;

        // Align ENU to AR world yaw
        float heading = Input.compass.trueHeading;         // north=0
        float camYaw = arCam.transform.eulerAngles.y;      // world yaw
        float yOffset = camYaw - heading;
        enuToWorldRot = Quaternion.Euler(0f, yOffset, 0f);

        geoReady = true;

        Debug.Log($"[ARRemoteHeadPlacer] Geo origin set lat={originLat}, lon={originLon}, yOffset={yOffset}");
    }

    void Update()
    {
        if (headObj == null || arCam == null) return;

        // サーバ値がまだ来てない間も「とにかく頭が見える」を優先
        if (debugSpawnInFront)
        {
            Vector3 targetPos = arCam.transform.position + arCam.transform.forward * debugFrontMeters;

            Quaternion targetRot;
            if (debugUseServerYaw && latest != null)
            {
                float yaw = (float)latest.yaw_deg + yawOffsetDeg;
                targetRot = Quaternion.Euler(0f, yaw, 0f);
            }
            else
            {
                targetRot = arCam.transform.rotation;
            }

            headObj.transform.position = Vector3.Lerp(headObj.transform.position, targetPos, posLerp);
            headObj.transform.rotation = Quaternion.Slerp(headObj.transform.rotation, targetRot, rotLerp);
            return;
        }

        // debug=false の場合、Geo初期化が必要
        if (!geoReady)
        {
            // 起動後に debugSpawnInFront を false に変えた場合の保険
            // ここで初期化を開始（1回だけ）
            StartCoroutine(InitGeoOrigin());
            return;
        }

        if (latest == null) return;

        // latest.lat/lon が取れてる前提（あなたのサーバはOK）
        double latT = latest.lat;
        double lonT = latest.lon;

        // origin -> target to ENU meters (approx)
        double latRad = originLat * Math.PI / 180.0;
        double metersPerDegLat = 111320.0;
        double metersPerDegLon = 111320.0 * Math.Cos(latRad);

        double dLat = latT - originLat;
        double dLon = lonT - originLon;

        float north = (float)(dLat * metersPerDegLat);
        float east = (float)(dLon * metersPerDegLon);

        Vector3 enu = new Vector3(east, 0f, north);
        float dist = enu.magnitude;

        if (dist > maxDistanceMeters && dist > 0.001f)
        {
            enu = enu.normalized * maxDistanceMeters;
        }

        Vector3 worldOffset = enuToWorldRot * enu;
        Vector3 targetPosGeo = worldOriginPos + worldOffset + Vector3.up * yOffsetMeters;

        // Rotation (yaw)
        float yawDeg = (float)latest.yaw_deg + yawOffsetDeg;
        Quaternion targetRotGeo;

        if (vrYawIsNorthBased)
        {
            Quaternion enuYaw = Quaternion.Euler(0f, yawDeg, 0f);
            targetRotGeo = enuToWorldRot * enuYaw;
        }
        else
        {
            targetRotGeo = Quaternion.Euler(0f, yawDeg, 0f);
        }

        headObj.transform.position = Vector3.Lerp(headObj.transform.position, targetPosGeo, posLerp);
        headObj.transform.rotation = Quaternion.Slerp(headObj.transform.rotation, targetRotGeo, rotLerp);
    }
}