using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Puts a resting worker to bed: it walks to its bed, lies down on it and stays there until morning. The
    /// Dvergr animator has no sleeping animation, so lying down is done by tipping the model onto its back.
    /// The lying state lives in the worker's ZDO so every client shows it and a new owner carries on with it.
    /// </summary>
    internal sealed class WorkerSleep
    {
        public const string AsleepKey = "DW_Asleep";

        private const float ArriveDistance = 1.8f;
        private const float GiveUpAfter = 60f;
        private const float WakeStepOut = 1.4f;
        private static readonly Quaternion LyingTilt = Quaternion.Euler(-90f, 0f, 0f);

        private readonly ZNetView _nview;
        private readonly Character _character;
        private readonly MonsterAI _ai;
        private readonly Rigidbody _body;
        private readonly Collider _collider;
        private readonly Transform _model;
        private readonly Vector3 _modelRestPosition;

        private readonly List<Collider> _ignored = new List<Collider>();
        private bool _gaveUp;
        private float _walkTime;
        private bool _modelLying;

        public WorkerSleep(ZNetView nview, Character character, MonsterAI ai)
        {
            _nview = nview;
            _character = character;
            _ai = ai;
            _body = character.GetComponent<Rigidbody>();
            _collider = character.GetComponent<Collider>();
            Animator animator = character.GetComponentInChildren<Animator>();
            _model = animator != null ? animator.transform : null;
            if (_model != null && _model != character.transform)
            {
                _modelRestPosition = _model.localPosition;
            }
            else
            {
                _model = null;
            }
        }

        public bool IsAsleep => _nview.IsValid() && _nview.GetZDO().GetBool(AsleepKey, false);

        /// <summary>
        /// Moves a worker that should be resting towards its bed and into it. Returns true while it is handling
        /// the frame (walking or lying), false if the vanilla AI should run (no bed to use).
        /// </summary>
        public bool Rest(float dt, string bedId, out string status)
        {
            status = "Sleeping";
            WorkerBed bed = WorkerBed.FindLoaded(bedId);
            if (bed == null || !bed.TryGetSleepSpot(out Vector3 centre, out Vector3 axis))
            {
                WakeUp(Vector3.zero);
                return false;
            }

            Vector3 feet = centre + axis * 0.7f;
            if (IsAsleep)
            {
                Hold(bed, feet, axis);
                return true;
            }

            if (_gaveUp)
            {
                return false;
            }

            status = "Heading to bed";
            _walkTime += dt;
            if (_walkTime > GiveUpAfter)
            {
                _gaveUp = true;
                DvergrWorkersPlugin.Log.LogWarning("Dvergr could not reach its bed; resting where it is tonight");
                return false;
            }

            if (Utils.DistanceXZ(_character.transform.position, centre) > ArriveDistance)
            {
                _ai.MoveTo(dt, centre, ArriveDistance * 0.5f, false);
                return true;
            }

            _ai.StopMoving();
            _nview.GetZDO().Set(AsleepKey, true);
            foreach (Collider bedCollider in bed.GetComponentsInChildren<Collider>())
            {
                if (_collider != null)
                {
                    Physics.IgnoreCollision(_collider, bedCollider, true);
                    _ignored.Add(bedCollider);
                }
            }
            Hold(bed, feet, axis);
            return true;
        }

        /// <summary>Stands the worker up beside the bed (if it was lying) and resets the night's state.</summary>
        public void WakeUp(Vector3 bedPosition)
        {
            _walkTime = 0f;
            _gaveUp = false;

            if (!IsAsleep)
            {
                ReleaseColliders();
                return;
            }

            _nview.GetZDO().Set(AsleepKey, false);
            ReleaseColliders();
            if (_body != null)
            {
                _body.useGravity = true;
            }
            if (bedPosition != Vector3.zero)
            {
                Vector3 side = Vector3.ProjectOnPlane(_character.transform.position - bedPosition, Vector3.up);
                side = side.sqrMagnitude > 0.01f ? side.normalized : _character.transform.right;
                _character.transform.position += side * WakeStepOut + Vector3.up * 0.3f;
            }
        }

        private void ReleaseColliders()
        {
            foreach (Collider bedCollider in _ignored)
            {
                if (bedCollider != null && _collider != null)
                {
                    Physics.IgnoreCollision(_collider, bedCollider, false);
                }
            }
            _ignored.Clear();
        }

        /// <summary>Keeps the worker lying still on the bed.</summary>
        private void Hold(WorkerBed bed, Vector3 feet, Vector3 axis)
        {
            _character.transform.position = feet + Vector3.up * 0.1f;
            _character.transform.rotation = Quaternion.LookRotation(axis, Vector3.up);
            _character.SetLookDir(axis);
            if (_body != null)
            {
                _body.useGravity = false;
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>Runs on every client: tips the model over while the worker's ZDO says it is asleep.</summary>
        public void ApplyPose()
        {
            if (_model == null)
            {
                return;
            }

            bool asleep = IsAsleep;
            if (asleep)
            {
                _model.localRotation = LyingTilt;
                _model.localPosition = _modelRestPosition;
                _modelLying = true;
            }
            else if (_modelLying)
            {
                _model.localRotation = Quaternion.identity;
                _model.localPosition = _modelRestPosition;
                _modelLying = false;
            }
        }
    }
}
