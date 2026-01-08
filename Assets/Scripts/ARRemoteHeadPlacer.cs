// Assets/Scripts/ARRemoteHeadPlacer.cs
// Unity 6 / AR Foundation 用：サーバの /latest.json を定期取得して「リモートPrefab」を配置する
// - debugSpawnInFront=true なら、GPS/コンパス権限なしで「カメラの前」に必ず出す（表示確認用）
// - debugSpawnInFront=false なら、iPhoneのGPS/コンパスで「緯度経度→AR空間」へ配置する
//
// 重要：サーバの pos.y は使わない（VR側のCityRootがどれだけマイナスでも影響させない）
//      表示Yは、従来はAR起動時の目線高さ（または現在の目線高さ）を基準にしていたが、
//      全身アバター(UnityChan等)向けに AR平面へ下向きレイを当てる GroundSnap を追加した。

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class ARRemoteHeadPlacer : MonoBehaviour
{
    [Header("Server Settings")]
    public string serverBaseUrl = "http://YOUR_SERVER:8000";
    public string latestPath = "/latest.json";
    public float pollIntervalSec = 0.5f;
    public float requestTimeoutSec = 2.0f;

    [Header("Remote Prefab")]
    public GameObject remotePrefab;
    public bool keepOneInstance = true;

    [Header("Debug Spawn (debugSpawnInFront=true のとき使用)")]
    public bool debugSpawnInFront = true;
    public float debugFrontMeters = 1.0f;      // カメラの何m前に出すか
    public bool debugUseServerYaw = true;      // true: yaw_degで回転 / false: カメラ向きに合わせる

    [Header("Geo Placement (debugSpawnInFront=false のとき使用)")]
    public float maxDistanceMeters = 30f;      // 遠すぎると見失うので、まず30m推奨
    public float yOffsetMeters = 0.0f;         // 追加の上下オフセット（GroundSnapでも利用）
    [Range(0f, 1f)] public float posLerp = 0.25f;
    [Range(0f, 1f)] public float rotLerp = 0.25f;

    [Header("Yaw Handling")]
    public float yawOffsetDeg = 0f;            // ずれたら 90/180 など入れて補正
    public bool vrYawIsNorthBased = true;      // VR yawが「北=0,東=90」基準なら true

    public enum YMode
    {
        EyeLevelAtAppStart,   // 起動時のカメラYを固定で使う（おすすめ）
        EyeLevelLive,         // 毎フレームのカメラYを使う（しゃがむ等で上下する）
        FixedY,               // 固定Y（デバッグや特殊用途）
        GroundSnap            // AR平面へ下向きレイで地面吸着（全身アバター向け）
    }
    public YMode yMode = YMode.EyeLevelAtAppStart;
    public float fixedY = 0f;

    [Header("Ground Snap (YMode=GroundSnap のとき使用)")]
    [Tooltip("AR平面(検出Plane)へ下向きレイを飛ばして、足元が地面に接するようにYを決める")]
    public bool useGroundSnap = true;

    [Tooltip("AR平面レイキャストに使うARRaycastManager。未設定なら自動で検索")]
    public ARRaycastManager raycastManager;

    [Tooltip("Physics.Raycastのフォールバックで使うレイヤーマスク（基本はAllでOK）")]
    public LayerMask groundPhysicsMask = ~0;

    [Tooltip("下向きレイの開始高さ[m]（対象位置の上にどれだけ持ち上げてから下へ撃つか）")]
    public float groundRayStartHeightMeters = 3.0f;

    [Tooltip("下向きレイの最大距離[m]")]
    public float groundRayMaxDistanceMeters = 10.0f;

    [Tooltip("地面に対する追加オフセット[m]（足裏が埋まるなら +0.02 など）")]
    public float groundYOffsetMeters = 0.0f;

    [Tooltip("地面Yの平滑化 時定数[s]（大きいほど滑らかだが遅れる）")]
    public float groundYSmoothTau = 0.25f;

    [Tooltip("1サンプルで許容する地面Yジャンプ[m]（大きい外れ値を抑える）")]
    public float maxGroundYStepMeters = 0.5f;

    public bool debugDrawGroundRay = false;

    [Header("Humanoid Head Auto-Align (UnityChan向け)")]
    public bool alignHumanoidHeadToEye = true;     // Humanoidなら頭を目線に合わせる（GroundSnapでは基本OFF推奨）
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
        public Pos pos;
        public double yaw_deg;
        public double pitch_deg;
        public double roll_deg;
        public double lat;
        public double lon;
        public double alt;
        public bool calibrated;
        public string calib_method;
        public double calib_scale;
        public double calib_theta_deg;
    }

    [Serializable]
    public class HealthData
    {
        public bool ok;
        public string reason;
    }

    private LatestData latest;
    private GameObject headObj;
    private Camera arCam;

    // GroundSnap 用内部状態
    private static readonly List<ARRaycastHit> s_arHits = new List<ARRaycastHit>(8);
    private bool groundYInitialized = false;
    private float groundYFiltered = 0f;
    private float lastGroundYTime = 0f;

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

    // デバッグ
    private string lastServerMsg = "-";
    private float lastFetchTime = -999f;
    private float lastComputedDistance = 0f;

    void Start()
    {
        arCam = Camera.main;
        if (raycastManager == null) raycastManager = FindObjectOfType<ARRaycastManager>();

        if (arCam != null)
            eyeYAtStart = arCam.transform.position.y;

        if (keepOneInstance && remotePrefab != null)
        {
            headObj = Instantiate(remotePrefab);
            headObj.name = "RemoteUser";
        }

        CacheHumanoidHeadHeightIfPossible();

        StartCoroutine(PollLoop());
    }

    void CacheHumanoidHeadHeightIfPossible()
    {
        if (headObj == null) return;

        cachedAnimator = headObj.GetComponentInChildren<Animator>();
        if (cachedAnimator == null) { hasHumanoidHead = false; return; }
        if (!cachedAnimator.isHuman) { hasHumanoidHead = false; return; }

        Transform head = cachedAnimator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) { hasHumanoidHead = false; return; }

        Transform root = cachedAnimator.transform;
        rootToHeadY = head.position.y - root.position.y;
        hasHumanoidHead = true;
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
        string url = serverBaseUrl.TrimEnd('/') + latestPath;
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.timeout = Mathf.CeilToInt(requestTimeoutSec);
            yield return req.SendWebRequest();

            lastFetchTime = Time.time;

            if (req.result != UnityWebRequest.Result.Success)
            {
                lastServerMsg = $"ERR: {req.error}";
                yield break;
            }

            string json = req.downloadHandler.text;
            if (string.IsNullOrEmpty(json))
            {
                lastServerMsg = "ERR: empty json";
                yield break;
            }

            try
            {
                latest = JsonUtility.FromJson<LatestData>(json);
                lastServerMsg = "ok";
            }
            catch (Exception e)
            {
                lastServerMsg = $"ERR: json parse {e.Message}";
            }
        }
    }

    IEnumerator InitGeoOrigin()
    {
        geoInitStarted = true;

        // latest が来るまで待つ（サーバからのlat/lon必須）
        float t0 = Time.time;
        while (latest == null || (latest.lat == 0 && latest.lon == 0))
        {
            if (Time.time - t0 > 10f)
            {
                lastServerMsg = "ERR: geo init timeout (no lat/lon)";
                yield break;
            }
            yield return null;
        }

        // AR起動位置を worldOrigin とし、サーバの(lat,lon)を originLat/Lon とする
        worldOriginPos = arCam.transform.position;
        originLat = latest.lat;
        originLon = latest.lon;

        // ENU の +Z(北) を AR世界の forward に合わせる
        // ここでは「起動時のカメラforward（XZ）」を北として扱う
        Vector3 fwd = arCam.transform.forward;
        fwd.y = 0;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
        fwd.Normalize();
        enuToWorldRot = Quaternion.LookRotation(fwd, Vector3.up);

        geoReady = true;
        lastServerMsg = "geoReady";
    }

    float ComputeTargetY()
    {
        // GroundSnap は別関数で処理（ここはフォールバックYを返す）
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
            case YMode.GroundSnap:
            default:
                eyeY = eyeYAtStart;
                break;
        }

        // Humanoidなら「頭が目線高さ」になるように、ルートYを下げる（EyeLevel系のみ）
        float rootY = eyeY;
        if (yMode != YMode.GroundSnap && alignHumanoidHeadToEye && hasHumanoidHead)
        {
            rootY = eyeY - rootToHeadY;
        }

        rootY += yOffsetMeters + extraRootYOffsetMeters;
        return rootY;
    }

    bool TryGetGroundY(Vector3 xzWorldPos, out float groundY)
    {
        // まずはAR平面（Plane）を優先
        if (raycastManager != null)
        {
            var ray = new Ray(xzWorldPos + Vector3.up * groundRayStartHeightMeters, Vector3.down);
            if (debugDrawGroundRay) Debug.DrawRay(ray.origin, ray.direction * groundRayMaxDistanceMeters, Color.green);

            s_arHits.Clear();
            if (raycastManager.Raycast(ray, s_arHits, TrackableType.PlaneWithinPolygon))
            {
                groundY = s_arHits[0].pose.position.y + groundYOffsetMeters;
                return true;
            }
        }

        // フォールバック：Physics.Raycast（コライダを置いている場合）
        {
            var ray = new Ray(xzWorldPos + Vector3.up * groundRayStartHeightMeters, Vector3.down);
            if (debugDrawGroundRay) Debug.DrawRay(ray.origin, ray.direction * groundRayMaxDistanceMeters, Color.yellow);

            if (Physics.Raycast(ray, out RaycastHit hit, groundRayMaxDistanceMeters, groundPhysicsMask, QueryTriggerInteraction.Ignore))
            {
                groundY = hit.point.y + groundYOffsetMeters;
                return true;
            }
        }

        groundY = 0f;
        return false;
    }

    float SmoothGroundY(float measuredY)
    {
        float now = Time.unscaledTime;
        float dt = Mathf.Max(1e-3f, now - lastGroundYTime);
        lastGroundYTime = now;

        if (!groundYInitialized)
        {
            groundYInitialized = true;
            groundYFiltered = measuredY;
            return groundYFiltered;
        }

        // 外れ値を抑制（急な段差/誤検出）
        float diff = measuredY - groundYFiltered;
        float abs = Mathf.Abs(diff);
        if (abs > maxGroundYStepMeters)
        {
            measuredY = groundYFiltered + Mathf.Sign(diff) * maxGroundYStepMeters;
        }

        float tau = Mathf.Max(1e-3f, groundYSmoothTau);
        float a = 1f - Mathf.Exp(-dt / tau);
        groundYFiltered = Mathf.Lerp(groundYFiltered, measuredY, a);
        return groundYFiltered;
    }

    float ComputeTargetYGroundSnap(Vector3 xzWorldPos)
    {
        // まだ平面が取れない場合はフォールバック（とにかく見える）に戻す
        float fallbackY = ComputeTargetY();

        if (!useGroundSnap) return fallbackY;

        if (TryGetGroundY(xzWorldPos, out float gY))
        {
            return SmoothGroundY(gY) + yOffsetMeters + extraRootYOffsetMeters;
        }

        // 一度でも地面Yが取れていれば、それを維持して急な上下ジャンプを防ぐ
        if (groundYInitialized)
        {
            return groundYFiltered + yOffsetMeters + extraRootYOffsetMeters;
        }

        return fallbackY;
    }

    void Update()
    {
        if (remotePrefab == null || arCam == null) return;

        // サーバ値がまだ来てない間も「とにかく見える」を優先
        if (headObj == null)
        {
            headObj = Instantiate(remotePrefab);
            headObj.name = "RemoteUser";
            CacheHumanoidHeadHeightIfPossible();
        }

        // debug=true の場合は、常に目の前に配置
        if (debugSpawnInFront)
        {
            Vector3 targetPos = arCam.transform.position + arCam.transform.forward * debugFrontMeters;
            targetPos.y = (yMode == YMode.GroundSnap) ? ComputeTargetYGroundSnap(targetPos) : ComputeTargetY(); // Y決定

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

        // XZはGeoで、Yはモードで決める
        Vector3 targetPosGeo = worldOriginPos + worldOffset;
        targetPosGeo.y = (yMode == YMode.GroundSnap) ? ComputeTargetYGroundSnap(targetPosGeo) : ComputeTargetY();

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
            $"yMode: {yMode}  eyeYAtStart: {eyeYAtStart:F2}  headAlign:{hasHumanoidHead}  groundSnap:{useGroundSnap}\n";

        GUI.Label(new Rect(10, 10, 800, 220), s);
    }
}
