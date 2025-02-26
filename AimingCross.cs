using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PLAYERTWO.PlatformerProject;

[RequireComponent(typeof(MeshRenderer))]
public class AimingCross : MonoBehaviour
{
    public PlayerWeaponThrow pwt;

    [Tooltip("Offset distance in front of the target.")]
    public float offsetDistance = 1.0f;

    [Tooltip("Smooth speed for crosshair position and scale adjustments.")]
    public float smoothSpeed = 5.0f;

    [Tooltip("Minimum and maximum crosshair scale.")]
    public Vector3 minScale = new Vector3(0.5f, 0.5f, 0.5f);
    public Vector3 maxScale = new Vector3(2.0f, 2.0f, 2.0f);

    private MeshRenderer[] m_renderers;
    private Collider m_target;
    private Vector3 targetScale;

    public void SetTarget(Collider coll)
    {
        m_target = coll;
        UpdateTargetScale();
    }

    public void UnsetTarget(Collider coll)
    {
        if (m_target == coll)
            m_target = null;
    }

    void Start()
    {
        pwt.OnAutoAimSelected.AddListener(SetTarget);
        pwt.OnAutoAimUnselected.AddListener(UnsetTarget);
        m_renderers = GetComponentsInChildren<MeshRenderer>();
    }

    void Update()
    {
        if (m_target == null)
        {
            // Hide the crosshairs if no target is selected
            for (int i = 0; i < m_renderers.Length; i++)
                m_renderers[i].enabled = false;
        }
        else
        {
            // Show the crosshairs
            for (int i = 0; i < m_renderers.Length; i++)
                m_renderers[i].enabled = true;

            // Smoothly adjust the position of the crosshair
            Vector3 targetPosition = m_target.transform.position + (m_target.transform.forward * offsetDistance);
            transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * smoothSpeed);

            // Smoothly adjust the scale of the crosshair
            transform.localScale = Vector3.Lerp(transform.localScale, targetScale, Time.deltaTime * smoothSpeed);

            // Ensure the crosshair faces the camera
            transform.LookAt(Camera.main.transform);
        }
    }

    private void UpdateTargetScale()
    {
        if (m_target != null)
        {
            // Get the bounds of the target's collider
            Bounds bounds = m_target.bounds;

            // Determine the target scale based on the bounds' size
            float largestDimension = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            targetScale = Vector3.Lerp(minScale, maxScale, largestDimension / 2.0f);
        }
    }
}
