
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using EmeraldAI;

namespace PLAYERTWO.PlatformerProject
{
    [RequireComponent(typeof(Player))]
    [AddComponentMenu("PLAYER TWO/Platformer Project/Community/Player Weapon Throw")]
    public class PlayerWeaponThrow : MonoBehaviour
    {
        public interface IWeapon
        {
            System.Func<Collider, bool> onHit { set; }
            MonoBehaviour script { get; }
            bool IsReturning { get; }
            bool IsThrown { get; }
            Vector3 Direction { get; }
            float Speed { get; }
            Transform ThrowTransform { get; }
            bool SetTarget(Transform target);
            bool IsWithinReach(Transform target, float maxDistance);
            void ResetWeapon();
            void ReturnWeapon();
            void ThrowWeapon(Vector3 direction);
            void ThrowWeapon();
        }

        public enum ThrowDirection
        {
            PlayerForward,
            CameraAligned,
            CameraForward,
            CameraForwardSmart
        }

        public enum AutoAimSelection
        {
            Closest,
            SmallestAngle,
            Combined
        }

        [Header("Debug Settings")]
        public bool debugInEditMode = false;

        [Header("Input Action")]
        public InputActionAsset actions;
        public string throwActionName;
        public string returnActionName;

        [Header("Weapon")]
        public BoomerangWeapon boomerangWeapon;
        public Transform weaponObject;
        public ThrowDirection throwDirection = ThrowDirection.PlayerForward;

        [Tooltip("Time after which auto returning starts")]
        public float autoReturnAfter = 1.0f;
        [Tooltip("Time after which re-throw is possible while weapon hasn't returned")]
        public float allowRethrowAfter = .5f;

        [Tooltip("Distance between player and weapon under which weapon colliders are disabled")]
        public float collisionToggleDistance = 1.0f;
        [Tooltip("Sphere radius used for collecting, breaking and hitting enemies")]
        public float activeRadius = 1.0f;

        [Header("Auto-Aim")]
        public float autoAimAngle = 30f;
        public float autoAimDistance = 100;
        public bool autoAimRigidBodies = true;
        public AutoAimSelection autoAimSelection = AutoAimSelection.Combined;
        public bool followAimedTarget = true;
        public LayerMask autoAimIgnoreLayers;

        [Header("PP Enemy Damage")]
        public int enemyDamage = 1;
        public int minEnemyDamage = 1;
        public int maxEnemyDamage = 10;

        [Tooltip("Whether or not weapon collects items")]
        public bool collectItems = true;
        [Tooltip("Whether or not weapon breaks items")]
        public bool breakItems = true;
        [Tooltip("Whether or not weapon is solid and can bounce from surface")]
        public bool isSolid = true;

        public Func<Collider, bool> AutoAimFilterPre { get; set; }
        public Func<Collider, bool> AutoAimFilterPost { get; set; }

        public UnityEvent<Collider> OnAutoAimSelected;
        public UnityEvent<Collider> OnAutoAimUnselected;

        [Header("Emerald AI Settings")]
        public int minEmeraldDamage = 5;
        public int maxEmeraldDamage = 15;
        public int RagdollForceAmount = 50;
        public float criticalHitChance = 0.2f;
        public float criticalMultiplier = 2.0f;
        private HashSet<GameObject> recentlyHitEnemies = new HashSet<GameObject>();
        public float hitResetTime = 0.5f;
        [Header("On Enemy Hit Events")]
        public UnityEvent<GameObject> OnEnemyHit;

        private int CalculateDamage(int minDamage, int maxDamage)
        {
            int damage = UnityEngine.Random.Range(minDamage, maxDamage + 1);
            if (UnityEngine.Random.value <= criticalHitChance)
            {
                damage = Mathf.RoundToInt(damage * criticalMultiplier);
                Debug.Log("Critical Hit! Damage: " + damage);
            }
            return damage;
        }

        [Header("Orb Collection Settings")]
        public List<string> orbTags;
        public UnityEvent<Collider> OnOrbCollected;
        public OrbCollector orbCollector;

        InputAction m_action;
        InputAction m_returnAction;

        Player m_player;
        IWeapon m_weapon;
        float m_thrown = 0.0f;
        Collider[] m_colliderBuffer = new Collider[50];
        List<Collider> m_colliderList = new List<Collider>(50);

        Collider m_autoAimCollider = null;
        Collider m_prevAutoAimCollider = null;
        int m_autoAimFrame = 0;

        private void OnValidate()
        {
            if (weaponObject != null && !weaponObject.TryGetComponent(out m_weapon))
            {
                Debug.LogError($"Object {weaponObject.name} does not contain component implementing IWeapon interface");
                weaponObject = null;
            }
        }

        protected virtual void Awake()
        {
            if (actions != null)
            {
                actions.Enable();
                if (!string.IsNullOrEmpty(throwActionName))
                    m_action = actions[throwActionName];
                if (!string.IsNullOrEmpty(returnActionName))
                    m_returnAction = actions[returnActionName];
            }
            if (boomerangWeapon == null)
            {
                Debug.LogError("BoomerangWeapon component not assigned in PlayerWeaponThrow!");
            }
            m_player = GetComponent<Player>();
            if (m_weapon == null)
                m_weapon = m_player.GetComponentInChildren<IWeapon>();
            m_weapon.onHit = OnWeaponHit;
        }

        private void OnDestroy()
        {
            m_weapon.onHit = null;
        }

        static int OverlapBox(Vector3 center, Vector3 halfExtents, ref Collider[] buffer, Quaternion orientation, int layerMask = Physics.DefaultRaycastLayers, QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
        {
            int count = Physics.OverlapBoxNonAlloc(center, halfExtents, buffer, orientation, layerMask, queryTriggerInteraction);
            while (count > 0 && count == buffer.Length)
            {
                buffer = new Collider[count * 2];
                count = Physics.OverlapBoxNonAlloc(center, halfExtents, buffer, orientation, layerMask, queryTriggerInteraction);
            }
            return count;
        }

        public int GetCollidersWithinView(IList<Collider> colliders, Vector3 origin, Vector3 direction, float viewLength, float viewAngle, int resolution, LayerMask layerMask)
        {
            float halfAngle = viewAngle * 0.5f;
            float tanHalfAngleRad = Mathf.Tan(Mathf.Deg2Rad * halfAngle);
            Quaternion rotation = Quaternion.LookRotation(direction.normalized);

            colliders.Clear();

            for (int step = 1; step <= resolution; step++)
            {
                float stepFactor = (float)step / resolution;
                float stepHalfHeight = viewLength * stepFactor * 0.5f;
                float stepHalfWidth = tanHalfAngleRad * stepHalfHeight;

                Vector3 boxSize = new Vector3(2f * stepHalfWidth, 2f * stepHalfWidth, viewLength * stepFactor);
                Vector3 boxCenter = origin + direction.normalized * stepHalfHeight;

                int count = OverlapBox(boxCenter, boxSize * 0.5f, ref m_colliderBuffer, rotation, layerMask, QueryTriggerInteraction.Collide);

                for (int i = 0; i < count; i++)
                {
                    Vector3 directionToHit = m_colliderBuffer[i].bounds.center - origin;
                    float angleToHit = Vector3.Angle(direction, directionToHit);

                    if (angleToHit <= halfAngle)
                    {
                        colliders.Add(m_colliderBuffer[i]);
                    }
                }
            }

            return colliders.Count;
        }

        protected virtual bool IsAutoAimTarget(Collider coll)
        {
            if (AutoAimFilterPre != null)
                return AutoAimFilterPre(coll);

            Vector3 vpPos = Camera.main.WorldToViewportPoint(coll.transform.position);
            if (vpPos.x < 0f || vpPos.x > 1f || vpPos.y < 0f || vpPos.y > 1f || vpPos.z < 0f)
                return false;

            if (!m_weapon.IsWithinReach(coll.transform, autoAimDistance))
                return false;

            if (orbTags.Contains(coll.tag))
        return true;

            if (autoAimRigidBodies && coll.attachedRigidbody != null)
                return true;

            if (enemyDamage > 0 && coll.CompareTag(GameTags.Enemy) && coll.TryGetComponent(out Enemy enemy))
                return !enemy.health.isEmpty;

            if (collectItems && coll.TryGetComponent(out Collectable _))
                return !coll.gameObject.name.StartsWith("Hidden");

            if (breakItems && coll.TryGetComponent(out Breakable _))
                return true;

            if (AutoAimFilterPost != null)
                return AutoAimFilterPost(coll);

            return false;
        }

        private void UpdateAutoAim(Vector3 throwDirectonVector, bool forceReset = false)
        {
            if (!forceReset && m_autoAimCollider != null && m_autoAimFrame == Time.frameCount)
                return;

            m_autoAimCollider = null;
            if (!forceReset && autoAimAngle > 0 && autoAimDistance > 0)
            {
                int resolution = Mathf.Max(10, Mathf.CeilToInt(autoAimDistance / 10));
                GetCollidersWithinView(
                    m_colliderList,
                    m_player.transform.position,
                    throwDirectonVector,
                    autoAimDistance,
                    autoAimAngle,
                    resolution,
                    ~autoAimIgnoreLayers); // Use inverted LayerMask here

                float min = float.MaxValue;
                float distanceWeight = 0.5f;
                float angleWeight = 0.5f;

                foreach (var col in m_colliderList)
                {
                    if (col.transform == transform ||
                        col.transform.IsChildOf(transform) ||
                        col.transform == m_weapon.script.transform)
                        continue;

                    Vector3 hitOffset = col.transform.position - m_player.transform.position;
                    float angleToHit = Vector3.Angle(throwDirectonVector, hitOffset.normalized);
                    float distance = hitOffset.magnitude;

                    if (angleToHit < autoAimAngle * 0.5f)
                    {
                        float score = distanceWeight * (distance / autoAimDistance) + angleWeight * (angleToHit / (autoAimAngle * 0.5f));
                        if (score < min && IsAutoAimTarget(col))
                        {
                            min = score;
                            m_autoAimCollider = col;
                        }
                    }
                }
            }

            if (m_prevAutoAimCollider != m_autoAimCollider)
            {
                if (m_prevAutoAimCollider != null)
                {
                    if (followAimedTarget)
                        m_weapon.SetTarget(null);

                    OnAutoAimUnselected?.Invoke(m_prevAutoAimCollider);
                }

                if (m_autoAimCollider != null)
                {
                    if (followAimedTarget)
                        m_weapon.SetTarget(m_autoAimCollider.transform);

                    OnAutoAimSelected?.Invoke(m_autoAimCollider);
                }

                m_prevAutoAimCollider = m_autoAimCollider;
            }

            if (m_autoAimCollider != null)
            {
                m_autoAimFrame = Time.frameCount;

                if (m_autoAimCollider.TryGetComponent<EmeraldAI.EmeraldHealth>(out _))
                {
                    RaycastHit hit;

                    Vector3 raycastOrigin = m_autoAimCollider.transform.position + Vector3.up * 0.5f;

                    if (Physics.Raycast(raycastOrigin, -Vector3.up, out hit, 10f, Physics.DefaultRaycastLayers))
                    {
                        if (boomerangWeapon != null)
                        {
                            Vector3 throwDirection = (hit.point - boomerangWeapon.ThrowTransform.position).normalized;

                            m_weapon.ThrowWeapon(throwDirection);

                            UpdateCrosshairTarget(m_autoAimCollider);

                            m_autoAimCollider = null;

                            return;
                        }
                        else
                        {
                            Debug.LogError("BoomerangWeapon component not assigned in PlayerWeaponThrow.");
                            return;
                        }
                    }
                    else
                    {
                        Debug.LogError("Raycast failed to find ground below enemy. Origin: " + raycastOrigin);
                        m_weapon.SetTarget(m_autoAimCollider.transform);
                        UpdateCrosshairTarget(m_autoAimCollider);
                        return;
                    }
                }
            }
        }

        Vector3 GetThrowVector(bool autoAim = false)
        {
            var throwDirectionVector = m_player.transform.forward;
            if (throwDirection == ThrowDirection.CameraAligned)
                throwDirectionVector = Vector3.ProjectOnPlane(Camera.main.transform.forward, m_player.transform.up).normalized;
            else if (throwDirection == ThrowDirection.CameraForward || throwDirection == ThrowDirection.CameraForwardSmart)
                throwDirectionVector = Camera.main.transform.forward;

            if (autoAim)
            {
                UpdateAutoAim(throwDirectionVector);
                if (m_autoAimCollider != null)
                    return m_autoAimCollider.transform.position - m_weapon.ThrowTransform.position;
                else if (throwDirection == ThrowDirection.CameraForwardSmart)
                {
                    if (Physics.Raycast(m_weapon.ThrowTransform.position, throwDirectionVector, out RaycastHit hit, autoAimDistance))
                    {
                        if (!IsAutoAimTarget(hit.collider))
                        {
                            throwDirectionVector = Vector3.ProjectOnPlane(Camera.main.transform.forward, m_player.transform.up).normalized;

                            UpdateAutoAim(throwDirectionVector);

                            if (m_autoAimCollider != null)
                                return m_autoAimCollider.transform.position - m_weapon.ThrowTransform.position;
                        }
                    }
                }
            }

            return throwDirectionVector;
        }

        private void UpdateCrosshairTarget(Collider target)
        {
            if (target != null)
            {
                Debug.Log("Crosshair Target: " + target.name);
            }
        }

        private void OnDrawGizmos()
        {
            if (!debugInEditMode && !Application.isPlaying) return;

            if (m_player == null && !TryGetComponent(out m_player))
                return;

            if (m_player != null)
            {
                var throwDirectonVector = GetThrowVector(false);

                if (Physics.Raycast(m_player.transform.position, throwDirectonVector, out var hit, float.MaxValue, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    Gizmos.color = Color.yellow;
                    Gizmos.DrawLine(m_player.transform.position, hit.point);
                }
                else
                {
                    Gizmos.color = Color.yellow;
                    Gizmos.DrawLine(m_player.transform.position, m_player.transform.position + throwDirectonVector * autoAimDistance);
                }

                Vector3 left = Vector3.Cross(throwDirectonVector, m_player.transform.up).normalized;
                var halfBase = Mathf.Abs(Mathf.Tan(Mathf.Deg2Rad * autoAimAngle / 2) * autoAimDistance);
                var farLeft = m_player.transform.position + throwDirectonVector * autoAimDistance + halfBase * left;
                var farRight = m_player.transform.position + throwDirectonVector * autoAimDistance + halfBase * -left;

                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(m_player.transform.position, farLeft);
                Gizmos.DrawLine(m_player.transform.position, farRight);
                Gizmos.DrawLine(farLeft, farRight);

                UpdateAutoAim(throwDirectonVector);
                if (m_autoAimCollider != null)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawLine(m_autoAimCollider.bounds.center, m_player.transform.position);
                    Gizmos.DrawWireCube(m_autoAimCollider.bounds.center, m_autoAimCollider.bounds.size);
                }
            }
        }

        protected virtual void Update()
        {
            if (!m_player.onWater)
            {
                var throwVector = GetThrowVector(true);

                int allowThrow = m_weapon.IsThrown ? 0 : 1;

                if (allowThrow == 0 && allowRethrowAfter != 0 && m_thrown != 0 && Time.time - m_thrown > allowRethrowAfter)
                    allowThrow++;

                if (allowThrow != 0 &&
                    m_action != null && m_action.WasReleasedThisFrame())
                {
                    m_weapon.ThrowWeapon(throwVector);
                    m_thrown = Time.time;
                }
            }
            else
            {
                UpdateAutoAim(Vector3.zero, true);
            }

            if (!m_weapon.IsThrown)
                m_thrown = 0;

            if (m_weapon.IsThrown && !m_weapon.IsReturning && (
                (autoReturnAfter > 0 && m_thrown > 0 && Time.time - m_thrown > autoReturnAfter) ||
                (m_returnAction != null && m_returnAction.WasReleasedThisFrame())))
            {
                m_weapon.ReturnWeapon();
            }
        }

        bool OnWeaponHit(Collider collider)
        {
            GameObject hitObject = collider.gameObject;

            if (recentlyHitEnemies.Contains(hitObject))
            {
                return false;
            }

            if (collider.TryGetComponent<LocationBasedDamageArea>(out var damageArea))
            {
                GameObject enemyRoot = damageArea.transform.root.gameObject;

                if (recentlyHitEnemies.Contains(enemyRoot))
                {
                    return false;
                }

                int emeraldDamage = CalculateDamage(minEmeraldDamage, maxEmeraldDamage);

                damageArea.DamageArea(emeraldDamage, m_weapon.script.transform, RagdollForceAmount);

                OnEnemyHit?.Invoke(enemyRoot);

                recentlyHitEnemies.Add(enemyRoot);
                StartCoroutine(RemoveFromRecentlyHit(enemyRoot));
                return true;
            }
            else if (collider.TryGetComponent<Enemy>(out var enemy))
            {
                int enemyDamage = CalculateDamage(minEnemyDamage, maxEnemyDamage);

                enemy.ApplyDamage(enemyDamage, m_weapon.script.transform.position);

                OnEnemyHit?.Invoke(hitObject);

                recentlyHitEnemies.Add(hitObject);
                StartCoroutine(RemoveFromRecentlyHit(hitObject));
                return true;
            }

            if (orbTags.Contains(collider.tag))
            {
                OnOrbCollected?.Invoke(collider);
                UpdateOrbCollector(collider.tag);
                Destroy(collider.gameObject);
                return true;
            }

            if (collectItems && collider.TryGetComponent(out Collectable collectable))
            {
                collectable.Collect(m_player);
                return true;
            }
            else if (breakItems && collider.TryGetComponent(out Breakable breakable))
            {
                int count = OverlapBox(
                    collider.bounds.center,
                    collider.bounds.size * 2,
                    ref m_colliderBuffer,
                    Quaternion.identity,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);

                for (int i = 0; i < count; i++)
                {
                    if (m_colliderBuffer[i].attachedRigidbody != null &&
                        m_colliderBuffer[i].attachedRigidbody.IsSleeping())
                        m_colliderBuffer[i].attachedRigidbody.WakeUp();
                }

                int damageToBreak = breakable.HP;
                breakable.ApplyDamage(damageToBreak);

                return true;
            }

            return collider.isTrigger;
        }

        private void UpdateOrbCollector(string orbTag)
        {
            if (orbCollector == null) return;

            if (orbCollector.orbTypeLookup.TryGetValue(orbTag, out var orbType))
            {
                orbCollector.collectedOrbs[orbType.orbName]++;
                orbCollector.UpdateUIText(orbType.orbName);

                if (orbCollector.collectedOrbs[orbType.orbName] >= orbType.requiredCount)
                {
                    orbType.onAbilityGained.Invoke();
                }
                if (orbCollector.collectedOrbs[orbType.orbName] >= orbType.subGoalCount && orbType.subGoalCount > 0)
                {
                    orbType.onAbilitySubGained.Invoke();
                }

                if (orbCollector.AllOrbsMaxed())
                {
                    orbCollector.onAllOrbsMaxed.Invoke();
                }
            }
        }

        private System.Collections.IEnumerator RemoveFromRecentlyHit(GameObject hitObject)
        {
            yield return new WaitForSeconds(hitResetTime);
            recentlyHitEnemies.Remove(hitObject);
        }
    }
}