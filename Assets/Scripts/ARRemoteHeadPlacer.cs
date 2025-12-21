// Assets/Scripts/ARRemoteHeadPlacer.cs
// Unity 6 / AR Foundation 用：サーバの /latest.json を定期取得して「頭Prefab」を配置する
// - debugSpawnInFront=true なら、GPS/コンパス権限なしで「カメラの1m前」に必ず出す（まず表示確認用）
// - debugSpawnInFront=false なら、iPhoneのGPS/コンパスで「緯度経度→AR空間」へ配置する
//
// /latest.json 例（データあり）:
// {"ts":"...","yaw_deg":161.1,"pos":{"x":..,"y":..,"z":..},"lat":33.88,"lon":130.88,"alt":...,"calibrated":true,...}
//
// /latest.json 例（データなし）:
// {"ok": false, "reason": "no_data"}

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

    // データあり版（従来のlatest.json）
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

    // データなし版（{"ok":false,"reason":"no_data"}）
    [Serializable]
    public class NoDataResponse
    {
        public bool ok;
        public string reason;
    }

    private LatestData latest;          // データありのときだけ入る
    private GameObject headObj;
    private Camera arCam;

    // Geo 変換用
    private bool geoReady = false;
    private bool geoInitStarted = false;
    private Vector3 worldOriginPos;
    private double originLat;
    private double originLon;
    private Quaternion enuToWorldRot = Quaternion.identity;

    // デバッグ用
    private string lastServerMsg = "";
    private float lastFetchTime = -999f;

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

            lastFetchTime = Time.time;

            if (req.result != UnityWebRequest.Result.Success)
            {
                lastServerMsg = $"Fetch failed: {req.error}";
                Debug.LogWarning("[ARRemoteHeadPlacer] " + lastServerMsg);
                yield break; // 次ループでまた試す
            }

            string text = req.downloadHandler.text?.Trim() ?? "";
            if (string.IsNullOrEmpty(text))
            {
                lastServerMsg = "Empty response";
                Debug.LogWarning("[ARRemoteHeadPlacer] " + lastServerMsg);
                yield break;
            }

            // no_data レスポンス判定
            // 重要：LatestData に ok を足すと、データありJSONでも ok=false になってしまうので分岐パースする
            if (LooksLikeNoData(text))
            {
                try
                {
                    var nd = JsonUtility.FromJson<NoDataResponse>(text);
                    if (nd != null && nd.ok == false)
                    {
                        latest = null; // ここが重要：変な0座標へ飛ばさない
                        lastServerMsg = $"no_data: {nd.reason}";
                        // Debug.Log("[ARRemoteHeadPlacer] " + lastServerMsg);
                        yield break;
                    }
                }
                catch (Exception e)
                {
                    // no_data っぽいけどパース失敗 → とりあえず latest null にして安全側
                    latest = null;
                    lastServerMsg = $"no_data parse failed: {e.Message}";
                    Debug.LogWarning("[ARRemoteHeadPlacer] " + lastServerMsg);
                    yield break;
                }
            }

            // データありのJSONとしてパース
            try
            {
                var parsed = JsonUtility.FromJson<LatestData>(text);
                if (parsed == null)
                {
                    lastServerMsg = "LatestData parse returned null";
                    Debug.LogWarning("[ARRemoteHeadPlacer] " + lastServerMsg);
                    yield break;
                }

                // 追加の安全策：lat/lonが両方0は怪しい（no_dataが誤パースされた等）
                if (Math.Abs(parsed.lat) < 1e-9 && Math.Abs(parsed.lon) < 1e-9)
                {
                    // ただし本当に(0,0)の可能性はほぼ無い想定なので安全側で弾く
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
        // 最小限の判定（厳密パーサは使わない）
        // {"ok": false, "reason": "no_data"} を想定
        // ※将来サーバが "ok" を通常レスポンスに含める仕様になったらここを調整
        return json.Contains("\"ok\"") && json.Contains("\"reason\"");
    }

    IEnumerator InitGeoOrigin()
    {
        if (geoInitStarted) yield break;
        geoInitStarted = true;

        // Location
        if (!Input.location.isEnabledByUser)
        {
            Debug.LogError("[ARRemoteHeadPlacer] Location service disabled by user.");
            geoInitStarted = false; // リトライ可能に戻す
            yield break;
        }

        Input.location.Start(1f, 1f);

        int wait = 20;
        while (Input.location.status == LocationServiceStatus.Initializing && wait-- > 0)
            yield return new WaitForSeconds(1f);

        if (Input.location.status != LocationServiceStatus.Running)
        {
            Debug.LogError("[ARRemoteHeadPlacer] Location service not running.");
            geoInitStarted = false; // リトライ可能に戻す
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
            // 起動後に debugSpawnInFront を false に変えた場合の保険（1回だけ起動）
            if (!geoInitStarted)
                StartCoroutine(InitGeoOrigin());
            return;
        }

        // 最新データがない（no_data等）なら更新しない
        if (latest == null) return;

        // latest.lat/lon が取れてる前提
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

    void OnDisable()
    {
        // 位置情報を使ってる場合だけ止めたいならここで判定してもOK
        // Input.location.Stop();
        // Input.compass.enabled = false;
    }
}