using UnityEngine;

public class GroundSnap : MonoBehaviour
{
    [Header("Target (leave null = this)")]
    public Transform target;

    [Header("Raycast")]
    public float rayStartHeight = 5f;
    public float maxDistance = 50f;

    [Header("Feet offset (adjust)")]
    public float footOffset = 0.0f;

    [Header("Ground layers")]
    public LayerMask groundMask = ~0;

    void Awake()
    {
        if (!target) target = transform;
    }

    void LateUpdate()
    {
        Vector3 p = target.position;
        Vector3 origin = p + Vector3.up * rayStartHeight;

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
            rayStartHeight + maxDistance, groundMask, QueryTriggerInteraction.Ignore))
        {
            p.y = hit.point.y + footOffset;
            target.position = p;
        }
    }
}