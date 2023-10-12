// License: 
// This script is free to use exclusively with Platformer Project from PLAYER TWO
// If you want to use this differently, contact me on Discord at Dusan#6720

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using static PLAYERTWO.PlatformerProject.PlayerWeaponThrow;

namespace PLAYERTWO.PlatformerProject
{
	[AddComponentMenu("PLAYER TWO/Platformer Project/Community/Boomerang Weapon")]
	public class BoomerangWeapon : MonoBehaviour, IWeapon
	{
		public enum Interpolation
		{
			None,
			Linear,
			Radial
		}

		public enum Constraints
		{
			None,
			FixedX,
			FixedY,
			FixedZ,
		}

		[Tooltip("Platformer Project Player")]
		public Player player;

		[Tooltip("Speed of the boomerang movement")]
		public float speed = 50f;
		[Tooltip("Amount of the boomerang rotation per second")]
		public Vector3 torque = new Vector3(0, 1440, 0);

		[Tooltip("Throw position of boomerang, some slot, hand, or so...")]
		public Transform throwTransform;

		[Tooltip("Return position of boomerang, some slot, hand, or so (keep null to use Throw Transform)")]
		public Transform returnTransform;

		[Tooltip("Way how return direction is interpolated between current and straight direction")]
		public Interpolation interpolation = Interpolation.Linear;

		[Tooltip("Approximate time to follow the target transform")]
		public float navigatingDuration = 2;

		[Tooltip("Curve representing fraction parameter during interpolation")]
		public AnimationCurve interpolationCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

		[Tooltip("Constraints used for boomerang like fixed coordinate or height")]
		public Constraints constraints = Constraints.FixedY;

		[Tooltip("How far boomerang is from return position to skip applying constraints")]
		public float constraintsSkipDistance = 5f;

		[Tooltip("Radius used for collisions")]
		public float radius = 0.3f;

		[Tooltip("How far boomerang is from return position to be considered as returned")]
		public float returnedDistance = 0.5f;

		[Tooltip("Force applied to colliding rigid bodies")]
		public float pushForce = 0.2f;

		[Tooltip("Check this to return when hits something")]
		public bool returnOnHit = false;

		[Tooltip("If NavMesh is available, this settings allows boomerang to navigate using it. Set to 0 to ignore NavMesh at all")]
		public float navmeshPathUpdatePeriod = 0.5f;

		class NavMeshNavigator
		{
            public UnityEngine.AI.NavMeshPath path;
			float latestUpdateTime;

			public Vector3 ClosestPointOnLineSegment(Vector3 point, int a, int b)
			{
				var segment = path.corners[b] - path.corners[a];
				var direction = segment.normalized;
				var projection = Vector3.Dot(point - path.corners[a], direction);
				if (projection < 0)
					return path.corners[a];

				if (projection * projection > segment.sqrMagnitude)
					return path.corners[b];

				return path.corners[a] + projection * direction;
			}

			public Vector3 FindClosestPoint(Vector3 point, out int index, out int indexNext)
			{
				var shortestSqrDistance = float.MaxValue;
				var closestVert = Vector3.zero;
				index = -1;
				indexNext = -1;
				for (var i = 0; i < path.corners.Length; i++)
				{
					var nextIndex = (i + 1) % path.corners.Length;
					var closestPoint = ClosestPointOnLineSegment(point, i, nextIndex);
					var sqrDistance = Vector3.SqrMagnitude(point - closestPoint);
					if (sqrDistance < shortestSqrDistance)
					{
						shortestSqrDistance = sqrDistance;
						closestVert = closestPoint;
						index = i;
						indexNext = nextIndex;
					}
				}
				return closestVert;
			}

			public bool GetDirection(Vector3 point, out Vector3 direction)
			{
				if (path != null && path.corners != null && path.corners.Length > 0)
				{
					FindClosestPoint(point, out var i, out var j);
					if (i >= 0 && j >= 0)
					{
						direction = (path.corners[j] - path.corners[i]).normalized;
						return true;
					}
				}

				direction = Vector3.zero;
				return false;
			}

			public bool UpdatePath(Vector3 source, Vector3 target, float updatePeriod)
			{
				if (updatePeriod == 0)
					return false;

				if (path == null)
				{
					path = new UnityEngine.AI.NavMeshPath();
					latestUpdateTime = -updatePeriod;
				}

				if (Time.time - latestUpdateTime >= updatePeriod)
				{
					UnityEngine.AI.NavMesh.CalculatePath(source, target, UnityEngine.AI.NavMesh.AllAreas, path);
					latestUpdateTime = Time.time;
				}

				return path != null && path.status != UnityEngine.AI.NavMeshPathStatus.PathInvalid;
			}
		};

		[Header("Decorations")]
		public TrailRenderer[] trails;
		public ParticleSystem[] particles;

		RaycastHit[] hitBuffer = new RaycastHit[20];
		float attractionTime = 0;
		Vector3 direction;
		Transform oldParent;
		Vector3 oldLocalPos;
		Quaternion oldLocalRot;
		Vector3 dirScale;
		List<Collider> playerColliders = new List<Collider>(20);
		RaycastHit groundHit;
		float groundHeight = 0;
		Vector3 groundNormal = Vector3.up;
		Transform targetTransform = null;
		Transform targetTransformCandidate = null;
		HashSet<Collider> volumeColliders = new HashSet<Collider>();
		NavMeshNavigator navigator = new NavMeshNavigator();

		[Tooltip("Called when weapon has been thrown")]
		public UnityEvent OnThrow;

		[Tooltip("Called when weapon has returned")]
		public UnityEvent OnReturn;


		// End of Inspector Fields
		// *****************************************

		Func<Collider, bool> _onHit;

		public bool IsReturning { get; private set; }
		public bool IsThrown { get; private set; }
		public MonoBehaviour script => this;

		public Func<Collider, bool> onHit { set => _onHit = value; }

		public Vector3 Direction => direction;

		public float Speed => speed;

		public float Radius => radius;

		public Transform ThrowTransform => throwTransform;

		private void OnDrawGizmosSelected()
		{
			Gizmos.color = Color.yellow;
			Gizmos.DrawWireSphere(transform.position, radius);
		}

		static int SphereCast(Vector3 origin, float radius, Vector3 direction, ref RaycastHit[] hitsArray, float maxDistance = Mathf.Infinity, int layerMask = Physics.DefaultRaycastLayers, QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
		{
			int count = Physics.SphereCastNonAlloc(origin, radius, direction, hitsArray, maxDistance, layerMask, queryTriggerInteraction);
			while (count > 0 && count == hitsArray.Length)
			{
				hitsArray = new RaycastHit[count * 2];
				count = Physics.SphereCastNonAlloc(origin, radius, direction, hitsArray, maxDistance, layerMask, queryTriggerInteraction);
			}
			return count;
		}

		private void Start()
		{
			foreach (var vol in FindObjectsOfType<Volume>())
			{
				volumeColliders.UnionWith(vol.GetComponents<Collider>());
				volumeColliders.UnionWith(vol.GetComponentsInChildren<Collider>());
			}

			if (player == null)
				player = GetComponentInParent<Player>();

			playerColliders.AddRange(player.GetComponents<Collider>());
			playerColliders.AddRange(player.GetComponentsInChildren<Collider>());

			oldParent = transform.parent;
			oldLocalPos = transform.localPosition;
			oldLocalRot = transform.localRotation;

			switch (constraints)
			{
				case Constraints.FixedX:
					dirScale = new Vector3(0, 1, 1);
					break;
				case Constraints.FixedY:
					dirScale = new Vector3(1, 0, 1);
					break;
				case Constraints.FixedZ:
					dirScale = new Vector3(1, 1, 0);
					break;
				default:
					dirScale = Vector3.one;
					break;
			}
		}

		void SetDirection(Vector3 vec)
		{
			direction = vec;

			if ((returnTransform.position - transform.position).magnitude > constraintsSkipDistance)
				direction.Scale(dirScale);

			direction = direction.normalized;
		}

		void Update()
		{
			if (!IsThrown)
				return;

			if (IsReturning)
			{
				if (targetTransform != returnTransform)
				{
					targetTransform = returnTransform;
					attractionTime = 0;
				}

				var diff = returnTransform.position - transform.position;
				diff.Scale(dirScale);
				if (diff.magnitude < returnedDistance)
				{
					ResetWeapon();
					return;
				}
			}

			// Cast a sphere from the current position to the new position.
			int count = SphereCast(
				transform.position,
				radius,
				direction,
				ref hitBuffer,
				speed * Time.deltaTime,
				Physics.DefaultRaycastLayers,
				QueryTriggerInteraction.Collide);
				

			Vector3 reflectSum = Vector3.zero;
			int reflectCount = 0;			

			for (int i = 0; i < count; i++)
			{
				if (playerColliders.Contains(hitBuffer[i].collider))
				{
					if (!IsReturning)
						continue;

					ResetWeapon();
					return;
				}

				var diff = returnTransform.position - hitBuffer[i].point;
				diff.Scale(dirScale);
				if (diff.magnitude < returnedDistance)
				{
					if (!IsReturning)
						continue;

					ResetWeapon();
					return;
				}

				// reflection from water and mud
				if (hitBuffer[i].collider.isTrigger && volumeColliders.Contains(hitBuffer[i].collider))
				{					
					if (!Mathf.Approximately(1, MathF.Abs(Vector3.Dot(direction, hitBuffer[i].normal)))) // ignore parallel hits
					{
						reflectSum += Vector3.Reflect(direction, hitBuffer[i].normal).normalized;
						reflectCount++;
					}
				}
				// resolve hits and reflection
				else if ((_onHit == null && !hitBuffer[i].collider.isTrigger) || !_onHit(hitBuffer[i].collider))
				{
					if (!Mathf.Approximately(1, MathF.Abs(Vector3.Dot(direction, hitBuffer[i].normal)))) // ignore parallel hits
					{
						reflectSum += Vector3.Reflect(direction, hitBuffer[i].normal).normalized;
						reflectCount++;
					}
				}
				
				// resolve RB pushing
				if (pushForce > 0 && hitBuffer[i].collider.attachedRigidbody != null)
				{
					hitBuffer[i].collider.attachedRigidbody.AddForceAtPosition(pushForce * speed * direction, hitBuffer[i].point, ForceMode.Impulse);
					targetTransform = null;
					attractionTime = 0;
				}

				if (targetTransform != returnTransform &&
					targetTransform == hitBuffer[i].collider.transform)
				{
					targetTransform = IsReturning ? returnTransform : null;
					attractionTime = 0;
				}
			}

			if (reflectCount > 0)
			{
				if (returnOnHit)
					ReturnWeapon();

				SetDirection(reflectSum / reflectCount);

				return;
			}

			if (targetTransform != null && !IsReturning)
				direction = (targetTransform.position - transform.position).normalized;

			// apply new position
			transform.position = transform.position + direction * speed * Time.deltaTime;
			transform.Rotate(torque * Time.deltaTime, Space.Self);

			if (targetTransform != null &&
				navigator.UpdatePath(transform.position, targetTransform.position, navmeshPathUpdatePeriod) &&
				navigator.GetDirection(transform.position, out var navDirection))
			{
				SetDirection(navDirection);
				attractionTime += Time.deltaTime;
			}
			else if (targetTransform != null)
			{
				var attractingDirection = (targetTransform.position - transform.position).normalized;

				if (interpolation != Interpolation.None && attractionTime < navigatingDuration)
				{
					var value = interpolationCurve.Evaluate(attractionTime / navigatingDuration);

					if (interpolation == Interpolation.Linear)
						SetDirection(Vector3.Lerp(direction, attractingDirection, value));
					else if (interpolation == Interpolation.Radial)
						SetDirection(Vector3.Slerp(direction, attractingDirection, value));
				}
				else
				{
					SetDirection(attractingDirection);
				}

				attractionTime += Time.deltaTime;
			}
			else
			{
				attractionTime = 0;
			}
		}

		public virtual bool IsWithinReach(Transform target, float maxDistance)
		{
			if (target != null)
			{
				Vector3 targetDirection = target.position - throwTransform.position;
				float distance = targetDirection.magnitude;

				if (maxDistance > 0 && distance > maxDistance)
					return false;

				targetDirection /= distance; // normalize

				int count = SphereCast(
					throwTransform.position,
					radius,
					targetDirection,
					ref hitBuffer,
					distance,
					Physics.DefaultRaycastLayers,
					QueryTriggerInteraction.Collide);

				for (int i = 0; i < count; i++)
				{
					if (hitBuffer[i].transform == throwTransform)
						continue;

					if (hitBuffer[i].collider.isTrigger)
					{
						if (hitBuffer[i].collider.CompareTag(GameTags.VolumeWater))
							return false;

						continue;
					}

					if (player != null)
					{
						if (hitBuffer[i].transform == player.transform)
							continue;

						if (hitBuffer[i].transform.IsChildOf(player.transform))
							continue;
					}

					if (hitBuffer[i].transform == target)
						return true;

					return false;
				}

				return true;
			}

			return false;
		}

		public void ReturnWeapon()
		{
			if (IsThrown)
			{
				IsReturning = true;
				targetTransform = returnTransform;
				attractionTime = 0;
			}
		}

		public void ThrowWeapon()
		{
			ThrowWeapon(throwTransform.forward);
		}

		public void ThrowWeapon(Vector3 throwDirection)
		{
			if (IsReturning)
				ResetWeapon();

			transform.parent = null;
			transform.position = throwTransform.position;
			transform.rotation = throwTransform.rotation;
			targetTransform = targetTransformCandidate;
			targetTransformCandidate = null;

			SetDirection(throwDirection);

			IsThrown = true;
			IsReturning = false;
			attractionTime = 0;

			OnThrow?.Invoke();

			Invoke(nameof(EnableDecorations), 0.1f);
		}

		void HandleDecorations(float time, bool emit)
		{
			if (trails != null)
			{
				for (int i = 0; i < trails.Length; i++)
				{
					trails[i].time = time;
					trails[i].emitting = emit;
				}
			}

			if (particles != null)
			{
				if (emit)
				{
					for (int i = 0; i < particles.Length; i++)
						particles[i].Play();
				}
				else
				{
					for (int i = 0; i < particles.Length; i++)
						particles[i].Stop();
				}
			}
		}

		void EnableDecorations()
		{
			HandleDecorations(1, true);
		}

		public void ResetWeapon()
		{
			IsThrown = false;
			IsReturning = false;
			direction = Vector3.zero;
			targetTransform = null;
			attractionTime = 0;

			HandleDecorations(0, false);

			OnReturn?.Invoke();

			transform.parent = oldParent;
			transform.localPosition = oldLocalPos;
			transform.localRotation = oldLocalRot;
		}

		public bool SetTarget(Transform target)
		{
			if (!IsReturning)
			{
				targetTransformCandidate = target;
				return true;
			}

			return false;
		}
	}
}
