// Assets/Scripts/ARRemoteHeadPlacer.cs
// Unity 6 / AR Foundation 用：サーバの /latest.json を定期取得して「リモートPrefab」を配置する
// - debugSpawnInFront=true なら、GPS/コンパス権限なしで「カメラの前」に必ず出す（表示確認用）
// - debugSpawnInFront=false なら、iPhoneのGPS/コンパスで「緯度経度→AR空間」へ配置する
//
// 重要：サーバの pos.y は使わない（VR側のCityRootがどれだけマイナスでも影響させない）
//      表示Yは AR起動時の目線高さ（または現在の目線高さ）を基準にする。
//      Humanoid(例: UnityChan)なら、頭ボーンの高さを自動推定して「頭が目線の高さ」に来るよう補正できる。

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class ARRemoteHeadPlacer : MonoBehaviour
{
    [Header("Server")]
    public string serverBaseUrl = "http://49.212.200.195:8000";
    public float pollIntervalSec = 0.2f;

    [Header("Prefab (HeadでもUnityChanでもOK)")]
    public GameObject headPrefab;

    [Header("Debug (まずはON推奨)")]
    public bool debugSpawnInFront = true;      // true: カメラ前にスポーンして表示確認
    public float debugFrontMeters = 1.0f;      // カメラの何m前に出すか
    public bool debugUseServerYaw = true;      // true: yaw_degで回転 / false: カメラ向きに合わせる

    [Header("Geo Placement (debugSpawnInFront=false のとき使用)")]
    public float maxDistanceMeters = 30f;      // 遠すぎると見失うので、まず30m推奨
    public float yOffsetMeters = 0.0f;         // 追加の上下オフセット（必要なら -0.2 等）
    [Range(0f, 1f)] public float posLerp = 0.25f;
    [Range(0f, 1f)] public float rotLerp = 0.25f;

    [Header("Yaw Handling")]
    public float yawOffsetDeg = 0f;            // ずれたら 90/180 など入れて補正
    public bool vrYawIsNorthBased = true;      // VR yawが「北=0,東=90」基準なら true

    public enum YMode
    {
        EyeLevelAtAppStart,   // 起動時のカメラYを固定で使う（おすすめ）
        EyeLevelLive,         // 毎フレームのカメラYを使う（しゃがむ等で上下する）
        FixedY                // 固定Y（デバッグや特殊用途）
    }
    public YMode yMode = YMode.EyeLevelAtAppStart;
    public float fixedY = 0f;

    [Header("Humanoid Head Auto-Align (UnityChan向け)")]
    public bool alignHumanoidHeadToEye = true;     // Humanoidなら頭を目線に合わせる
    public float extraRootYOffsetMeters = 0f;      // 最終的な微調整（±0.1とか）

    [Header("On-screen Debug")]
    public bool showDebugOverlay = true;

    // -------- JSON structs (JsonUtility用) --------
    [Serializable]
    public class Pos
    {
        public double x;
        public double y;
        public double z;
    }

    [Serializable]
    public class LatestData
    {
        public string ts;
        public double yaw_deg;
        public Pos pos;
        public double lat;
        public double lon;
        public double alt;
        public bool calibrated;
        public string calib_method;
        public double calib_scale;
        public double calib_theta_deg;
    }

    [Serializable]
    public class NoDataResponse
    {
        public bool ok;
        public string reason;
    }

    private LatestData latest;
    private GameObject headObj;
    private Camera arCam;

    // Geo 変換用
    private bool geoReady = false;
    private bool geoInitStarted = false;
    private Vector3 worldOriginPos;            // AR起動時（原点決定時）のAR空間座標
    private double originLat;
    private double originLon;
    private Quaternion enuToWorldRot = Quaternion.identity;

    // 目線基準
    private float eyeYAtStart = 0f;

    // Humanoid頭高さ（ルート→頭のY距離）
    private bool hasHumanoidHead = false;
    private float rootToHeadY = 0f;
    private Animator cachedAnimator = null;

    // デバッグ用
    private string lastServerMsg = "";
    private float lastFetchTime = -999f;
    private float lastComputedDistance = -1f;

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

        // 起動時の目線高さを保存
        eyeYAtStart = arCam.transform.position.y;

        headObj = Instantiate(headPrefab);
        headObj.name = "RemoteAvatar";

        CacheHumanoidHeadOffsetIfPossible();

        StartCoroutine(PollLoop());

        // debugSpawnInFront=true のときは、位置情報の権限を要求しない（まず表示確認）
        if (!debugSpawnInFront)
        {
            yield return InitGeoOrigin();
        }
    }

    private void CacheHumanoidHeadOffsetIfPossible()
    {
        hasHumanoidHead = false;
        rootToHeadY = 0f;
        cachedAnimator = null;

        if (!alignHumanoidHeadToEye || headObj == null) return;

        cachedAnimator = headObj.GetComponentInChildren<Animator>();
        if (cachedAnimator == null) return;
        if (!cachedAnimator.isHuman) return;

        Transform head = cachedAnimator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) return;

        // 生成直後の姿勢で「ルート(このPrefabのroot) → 頭ボーン」までのY距離を取る
        rootToHeadY = head.position.y - headObj.transform.position.y;
        if (rootToHeadY > 0.01f)
        {
            hasHumanoidHead = true;
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

            lastFetchTime = Time.time;

            if (req.result != UnityWebRequest.Result.Success)
            {
                lastServerMsg = $"Fetch failed: {req.error}";
                Debug.LogWarning("[ARRemoteHeadPlacer] " + lastServerMsg);
                yield break;
            }

            string text = req.downloadHandler.text?.Trim() ?? "";
            if (string.IsNullOrEmpty(text))
            {
                lastServerMsg = "Empty response";
                Debug.LogWarning("[ARRemoteHeadPlacer] " + lastServerMsg);
                yield break;
            }

            // no_data 判定
            if (LooksLikeNoData(text))
            {
                try
                {
                    var nd = JsonUtility.FromJson<NoDataResponse>(text);
                    if (nd != null && nd.ok == false)
                    {
                        latest = null;
                        lastServerMsg = $"no_data: {nd.reason}";
                        yield break;
                    }
                }
                catch (Exception e)
                {
                    latest = null;
                    lastServerMsg = $"no_data parse failed: {e.Message}";
                    Debug.LogWarning("[ARRemoteHeadPlacer] " + lastServerMsg);
                    yield break;
                }
            }

            // データありとしてパース
            try
            {
                var parsed = JsonUtility.FromJson<LatestData>(text);
                if (parsed == null)
                {
                    lastServerMsg = "LatestData parse returned null";
                    Debug.LogWarning("[ARRemoteHeadPlacer] " + lastServerMsg);
                    yield break;
                }

                // lat/lon(0,0)は怪しいので弾く
                if (Math.Abs(parsed.lat) < 1e-9 && Math.Abs(parsed.lon) < 1e-9)
                {
                    latest = null;
                    lastServerMsg = "Suspicious lat/lon (0,0). Ignored.";
                    Debug.LogWarning("[ARRemoteHeadPlacer] " + lastServerMsg);
                    yield break;
                }

                latest = parsed;
                lastServerMsg = "ok";
            }
            catch (Exception e)
            {
                lastServerMsg = "JSON parse failed: " + e.Message;
                Debug.LogWarning("[ARRemoteHeadPlacer] " + lastServerMsg);
            }
        }
    }

    private bool LooksLikeNoData(string json)
    {
        return json.Contains("\"ok\"") && json.Contains("\"reason\"");
    }

    IEnumerator InitGeoOrigin()
    {
        if (geoInitStarted) yield break;
        geoInitStarted = true;

        if (!Input.location.isEnabledByUser)
        {
            Debug.LogError("[ARRemoteHeadPlacer] Location service disabled by user.");
            geoInitStarted = false;
            yield break;
        }

        Input.location.Start(1f, 1f);

        int wait = 20;
        while (Input.location.status == LocationServiceStatus.Initializing && wait-- > 0)
            yield return new WaitForSeconds(1f);

        if (Input.location.status != LocationServiceStatus.Running)
        {
            Debug.LogError("[ARRemoteHeadPlacer] Location service not running.");
            geoInitStarted = false;
            yield break;
        }

        // Compass
        Input.compass.enabled = true;
        yield return new WaitForSeconds(0.5f);

        // Origin（アプリ起動時のAR空間上の基準点）
        worldOriginPos = arCam.transform.position;
        eyeYAtStart = arCam.transform.position.y;

        originLat = Input.location.lastData.latitude;
        originLon = Input.location.lastData.longitude;

        // ENU(北基準) → ARワールドYaw合わせ
        float heading = Input.compass.trueHeading;     // north=0
        float camYaw = arCam.transform.eulerAngles.y;
        float yRot = camYaw - heading;
        enuToWorldRot = Quaternion.Euler(0f, yRot, 0f);

        geoReady = true;

        Debug.Log($"[ARRemoteHeadPlacer] Geo origin set lat={originLat}, lon={originLon}, yRot={yRot}");
    }

    private float ComputeTargetY()
    {
        float eyeY;
        switch (yMode)
        {
            case YMode.EyeLevelLive:
                eyeY = arCam.transform.position.y;
                break;
            case YMode.FixedY:
                eyeY = fixedY;
                break;
            case YMode.EyeLevelAtAppStart:
            default:
                eyeY = eyeYAtStart;
                break;
        }

        // Humanoidなら「頭が目線高さ」になるように、ルートYを下げる
        float rootY = eyeY;
        if (alignHumanoidHeadToEye && hasHumanoidHead)
        {
            rootY = eyeY - rootToHeadY;
        }

        rootY += yOffsetMeters + extraRootYOffsetMeters;
        return rootY;
    }

    void Update()
    {
        if (headObj == null || arCam == null) return;

        // サーバ値がまだ来てない間も「とにかく見える」を優先
        if (debugSpawnInFront)
        {
            Vector3 targetPos = arCam.transform.position + arCam.transform.forward * debugFrontMeters;
            targetPos.y = ComputeTargetY(); // 目線基準でY決定

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
            if (!geoInitStarted)
                StartCoroutine(InitGeoOrigin());
            return;
        }

        if (latest == null) return;

        // latest.lat/lon を使う（pos.x/y/z は使わない）
        double latT = latest.lat;
        double lonT = latest.lon;

        // origin -> target を ENU meters に近似変換
        double latRad = originLat * Math.PI / 180.0;
        double metersPerDegLat = 111320.0;
        double metersPerDegLon = 111320.0 * Math.Cos(latRad);

        double dLat = latT - originLat;
        double dLon = lonT - originLon;

        float north = (float)(dLat * metersPerDegLat);
        float east = (float)(dLon * metersPerDegLon);

        Vector3 enu = new Vector3(east, 0f, north);
        float dist = enu.magnitude;
        lastComputedDistance = dist;

        if (dist > maxDistanceMeters && dist > 0.001f)
        {
            enu = enu.normalized * maxDistanceMeters;
            dist = maxDistanceMeters;
        }

        Vector3 worldOffset = enuToWorldRot * enu;

        // XZはGeoで、Yは目線基準で決める
        Vector3 targetPosGeo = worldOriginPos + worldOffset;
        targetPosGeo.y = ComputeTargetY();

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

    void OnGUI()
    {
        if (!showDebugOverlay) return;

        GUI.color = Color.white;
        string s =
            $"ARRemoteHeadPlacer\n" +
            $"debugSpawnInFront: {debugSpawnInFront}\n" +
            $"geoReady: {geoReady}\n" +
            $"server: {serverBaseUrl}\n" +
            $"last: {lastServerMsg}  (t={lastFetchTime:F1})\n" +
            $"latest.ts: {(latest != null ? latest.ts : "null")}\n" +
            $"latest.latlon: {(latest != null ? $"{latest.lat:F6},{latest.lon:F6}" : "-")}\n" +
            $"dist(m): {lastComputedDistance:F2}\n" +
            $"yMode: {yMode}  eyeYAtStart: {eyeYAtStart:F2}  headAlign:{hasHumanoidHead}\n";

        GUI.Label(new Rect(10, 10, 800, 220), s);
    }
}
