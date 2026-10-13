using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Repair role: each round it collects the damaged player-built pieces inside the job radius, then walks
    /// to each one (nearest first) and hammers it.
    /// </summary>
    public class RepairWorker : WorkerBrain
    {
        private enum Phase { Walking, Windup, Recover }

        private const float WindupTime = 0.5f;
        private const float RecoverTime = 1.1f;

        private Phase _phase = Phase.Walking;
        private float _phaseTimer;
        private readonly List<WearNTear> _queue = new List<WearNTear>();
        private readonly HashSet<WearNTear> _failed = new HashSet<WearNTear>();
        private WearNTear _current;
        private int _repairedThisRound;
        private int _repairedLastRound;
        private bool _animLogged;

        internal override WorkerRole Role => WorkerRoles.Repair;

        protected override string IdleStatus()
        {
            return _repairedLastRound > 0
                ? "Idle (repaired " + _repairedLastRound + " pieces last round)"
                : "Idle";
        }

        protected override void OnRoundEnded(bool finished)
        {
            _queue.Clear();
            _current = null;
            _repairedLastRound = _repairedThisRound;
        }

        // ---------------------------------------------------------------- rounds

        protected override bool BeginRound()
        {
            _queue.Clear();
            _failed.Clear();
            _repairedThisRound = 0;

            foreach (WearNTear wnt in WearNTear.GetAllInstances())
            {
                if (NeedsRepair(wnt) && InJobArea(wnt.transform.position))
                {
                    _queue.Add(wnt);
                }
            }

            if (_queue.Count == 0)
            {
                _repairedLastRound = 0;
                return false;
            }

            Equipment.EquipTool();
            _current = null;
            _phase = Phase.Walking;
            DvergrWorkersPlugin.Log.LogInfo("Repair round started with " + _queue.Count + " damaged pieces");
            return true;
        }

        private static bool NeedsRepair(WearNTear wnt)
        {
            if (wnt == null || wnt.m_nview == null || !wnt.m_nview.IsValid())
            {
                return false;
            }
            Piece piece = wnt.GetComponent<Piece>();
            if (piece == null || !piece.IsPlacedByPlayer())
            {
                return false;
            }
            return wnt.GetHealthPercentage() < 0.999f;
        }

        // ---------------------------------------------------------------- task

        protected override bool RunTask(float dt)
        {
            switch (_phase)
            {
                case Phase.Walking:
                    return Walk(dt);

                case Phase.Windup:
                    Ai.StopMoving();
                    FacePiece();
                    _phaseTimer -= dt;
                    if (_phaseTimer <= 0f)
                    {
                        Hammer();
                        _phase = Phase.Recover;
                        _phaseTimer = RecoverTime;
                    }
                    return true;

                case Phase.Recover:
                    Ai.StopMoving();
                    _phaseTimer -= dt;
                    if (_phaseTimer <= 0f)
                    {
                        _current = null;
                        _phase = Phase.Walking;
                    }
                    return true;
            }
            return false;
        }

        private bool Walk(float dt)
        {
            if (_current == null && !PickNext())
            {
                EndRound(finished: true);
                return false;
            }

            if (!NeedsRepair(_current))
            {
                _current = null;
                return true;
            }

            switch (Navigator.Step(dt))
            {
                case NavResult.InReach:
                case NavResult.CloseEnough:
                    _phase = Phase.Windup;
                    _phaseTimer = WindupTime;
                    break;

                case NavResult.Failed:
                    _failed.Add(_current);
                    _current = null;
                    break;
            }
            return true;
        }

        /// <summary>Nearest-neighbour pick from the queue, choosing only pieces we can find a standing spot for.</summary>
        private bool PickNext()
        {
            while (_queue.Count > 0)
            {
                int best = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (_queue[i] == null)
                    {
                        continue;
                    }
                    float d = Vector3.Distance(_queue[i].transform.position, transform.position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = i;
                    }
                }

                if (best < 0)
                {
                    _queue.Clear();
                    return false;
                }

                WearNTear candidate = _queue[best];
                _queue.RemoveAt(best);

                if (!NeedsRepair(candidate) || _failed.Contains(candidate))
                {
                    continue;
                }

                if (Navigator.Begin(candidate.name, from => WorkerNavigator.ClosestPointOnColliders(candidate.gameObject, from)))
                {
                    _current = candidate;
                    SetStatus("Walking to a damaged piece (" + (_queue.Count + 1) + " left)");
                    return true;
                }

                DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to " + candidate.name + ", skipping");
            }
            return false;
        }

        // ---------------------------------------------------------------- hammering

        private void FacePiece()
        {
            if (_current == null)
            {
                return;
            }
            Vector3 dir = _current.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
            {
                Character.SetLookDir(dir.normalized);
                Ai.LookTowards(dir);
            }
        }

        private void Hammer()
        {
            if (!NeedsRepair(_current))
            {
                return;
            }

            if (_current.Repair())
            {
                _repairedThisRound++;
                SetStatus("Repairing (" + _queue.Count + " more queued)");

                Piece piece = _current.GetComponent<Piece>();
                if (piece != null && piece.m_placeEffect != null)
                {
                    piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation, null, 1f, -1, Character.GetZDOID());
                }
            }

            PlayHammerAnimation();
        }

        private void PlayHammerAnimation()
        {
            Animator animator = GetComponentInChildren<Animator>();
            LogAnimatorOnce(animator);

            string trigger = ResolveTrigger(animator);
            if (!string.IsNullOrEmpty(trigger))
            {
                Character.m_zanim.SetTrigger(trigger);
            }
        }

        /// <summary>Config override, then the hammer's own trigger, then any attack-like trigger the Dvergr's animator has.</summary>
        private string ResolveTrigger(Animator animator)
        {
            string configured = WorkerConfig.RepairAnimationTrigger.Value;
            if (!string.IsNullOrEmpty(configured))
            {
                return configured;
            }

            string hammerTrigger = Equipment.Tool?.m_shared.m_attack.m_attackAnimation;
            if (animator == null || (!string.IsNullOrEmpty(hammerTrigger) && HasParameter(animator, hammerTrigger)))
            {
                return hammerTrigger;
            }

            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Trigger
                    && (p.name.Contains("attack") || p.name.Contains("swing")))
                {
                    return p.name;
                }
            }
            return null;
        }

        private void LogAnimatorOnce(Animator animator)
        {
            if (_animLogged || animator == null)
            {
                return;
            }
            _animLogged = true;

            var parameters = new List<string>();
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                parameters.Add(p.name + ":" + p.type);
            }
            var clips = new List<string>();
            if (animator.runtimeAnimatorController != null)
            {
                foreach (AnimationClip c in animator.runtimeAnimatorController.animationClips)
                {
                    clips.Add(c.name);
                }
            }

            ItemDrop.ItemData tool = Equipment.Tool;
            DvergrWorkersPlugin.Log.LogInfo("Dvergr animator parameters: " + string.Join(", ", parameters));
            DvergrWorkersPlugin.Log.LogInfo("Dvergr animator clips: " + string.Join(", ", clips));
            DvergrWorkersPlugin.Log.LogInfo("Hammer anim: trigger='" + tool?.m_shared.m_attack.m_attackAnimation
                + "' state=" + tool?.m_shared.m_animationState + ", chosen trigger='" + ResolveTrigger(animator) + "'");
        }

        private static bool HasParameter(Animator animator, string name)
        {
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.name == name)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
