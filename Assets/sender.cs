// ARLocationSenderToVps.cs
// 実行デバイス: iPhone (ARアプリ側)
// 役割: GPS/コンパスを取得して VPS の /ar/ingest に定期POSTする

using System;
using System.Text;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class ARLocationSenderToVps : MonoBehaviour
{
    [Header("VPS")]
    public string serverBaseUrl = "http://49.212.200.195:8000";
    public string arRelayToken = ""; // 必要なら "xxxxx" を入れる (Authorization: Bearer)

    [Header("Send rate")]
    [Range(0.1f, 5f)] public float sendIntervalSec = 0.5f;

    [Header("Location")]
    public float desiredAccuracyMeters = 5f;
    public float updateDistanceMeters = 1f;

    [Serializable]
    public class Payload
    {
        public double lat;
        public double lon;
        public double alt;
        public double heading_deg;
        public double pitch_deg;
        public double roll_deg;
    }

    IEnumerator Start()
    {
        // 位置情報ON
        if (!Input.location.isEnabledByUser)
        {
            Debug.LogError("[ARLocationSender] Location service disabled by user.");
            yield break;
        }

        Input.compass.enabled = true;

        Input.location.Start(desiredAccuracyMeters, updateDistanceMeters);

        // 初期化待ち
        int maxWait = 20;
        while (Input.location.status == LocationServiceStatus.Initializing && maxWait > 0)
        {
            yield return new WaitForSeconds(1);
            maxWait--;
        }

        if (Input.location.status != LocationServiceStatus.Running)
        {
            Debug.LogError("[ARLocationSender] Location service not running: " + Input.location.status);
            yield break;
        }

        Debug.Log("[ARLocationSender] Started. POST -> " + serverBaseUrl + "/ar/ingest");

        while (true)
        {
            yield return SendOnce();
            yield return new WaitForSeconds(sendIntervalSec);
        }
    }

    IEnumerator SendOnce()
    {
        var ld = Input.location.lastData;

        // コンパス（true heading）: 取得できない端末もあるので fallback
        double heading = (Input.compass.enabled ? Input.compass.trueHeading : 0.0);

        // カメラ姿勢も送りたいなら MainCamera の姿勢から取る（とりあえず0でもOK）
        double pitch = 0.0;
        double roll  = 0.0;

        var payload = new Payload
        {
            lat = ld.latitude,
            lon = ld.longitude,
            alt = ld.altitude,
            heading_deg = heading,
            pitch_deg = pitch,
            roll_deg = roll
        };

        string json = JsonUtility.ToJson(payload);
        byte[] body = Encoding.UTF8.GetBytes(json);

        var req = new UnityWebRequest(serverBaseUrl.TrimEnd('/') + "/ar/ingest", "POST");
        req.uploadHandler = new UploadHandlerRaw(body);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        if (!string.IsNullOrEmpty(arRelayToken))
            req.SetRequestHeader("Authorization", "Bearer " + arRelayToken);

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ARLocationSender] POST failed: {req.responseCode} err={req.error} body={req.downloadHandler.text}");
            yield break;
        }

        // たまにログ（必要なら間引く）
        Debug.Log($"[ARLocationSender] ok {req.responseCode} json={json} resp={req.downloadHandler.text}");
    }
}
