using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PLAYERTWO.PlatformerProject;

[RequireComponent(typeof(MeshRenderer))]

public class AimingCross : MonoBehaviour
{
	public PlayerWeaponThrow pwt;

	MeshRenderer[] m_renderers;
	Collider m_target;
	public void SetTarget(Collider coll)
	{
        m_target = coll;
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

	// Update is called once per frame
	void Update()
    {
		if (m_target == null)
		{
			for (int i = 0; i < m_renderers.Length; i++)
				m_renderers[i].enabled = false;
		}
		else
		{
			for (int i = 0; i < m_renderers.Length; i++)
				m_renderers[i].enabled = true; 
			
			transform.position = m_target.transform.position;
			transform.LookAt(Camera.main.transform);
		}
	}
}
