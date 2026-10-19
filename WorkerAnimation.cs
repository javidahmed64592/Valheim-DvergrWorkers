using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Plays a tool swing on the Dvergr. The Dvergr and the player share a humanoid avatar, so the player's own
    /// swing clip for the role's tool (axe, hammer, hoe...) is swapped into the Dvergr's melee attack slot and
    /// played through that slot's trigger.
    /// </summary>
    internal sealed class WorkerAnimation
    {
        private readonly Character _character;
        private readonly WorkerRole _role;

        /// <summary>Trigger of the Dvergr's melee attack, which plays the clip swapped in by <see cref="ApplyPlayerClip"/>.</summary>
        private const string SwingTrigger = "attack_poke";

        /// <summary>The Dvergr's melee attack clips (crossbow butt-strike, staff poke), triggered by attack_poke.</summary>
        private static readonly string[] MeleeSlots = { "Crossbow Kolv Attack", "Staff Poke" };

        /// <summary>Clip in the player's animator used for each tool; anything else (or no tool) uses <see cref="DefaultClip"/>.</summary>
        private static readonly Dictionary<string, string> ToolClips = new Dictionary<string, string>
        {
            { "Hammer", "hammer_Swing" },
            { "AxeIron", "axe_swing" },
            { "Cultivator", "swing_hoe" },
        };
        private const string DefaultClip = "Interact";

        private static Dictionary<string, AnimationClip> _playerClips;

        public WorkerAnimation(Character character, WorkerRole role)
        {
            _character = character;
            _role = role;
            ApplyPlayerClip();
        }

        /// <summary>
        /// Chosen once from the role's tool, so every client (not just the one running the worker) plays the
        /// same swing without anything having to be synced.
        /// </summary>
        private void ApplyPlayerClip()
        {
            Animator animator = _character.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return;
            }

            string clipName;
            if (string.IsNullOrEmpty(_role.ToolPrefab) || !ToolClips.TryGetValue(_role.ToolPrefab, out clipName))
            {
                clipName = DefaultClip;
            }

            if (!PlayerClips().TryGetValue(clipName, out AnimationClip clip))
            {
                DvergrWorkersPlugin.Log.LogWarning(_role.Name + ": player clip '" + clipName + "' not found, keeping the Dvergr's own swing");
                return;
            }

            var overrides = new AnimatorOverrideController(animator.runtimeAnimatorController);
            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            overrides.GetOverrides(pairs);
            for (int i = 0; i < pairs.Count; i++)
            {
                if (Array.IndexOf(MeleeSlots, pairs[i].Key.name) >= 0)
                {
                    pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(pairs[i].Key, clip);
                }
            }
            overrides.ApplyOverrides(pairs);
            animator.runtimeAnimatorController = overrides;
        }

        private static Dictionary<string, AnimationClip> PlayerClips()
        {
            if (_playerClips != null && _playerClips.Count > 0)
            {
                return _playerClips;
            }

            _playerClips = new Dictionary<string, AnimationClip>();
            GameObject player = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
            Animator animator = player != null ? player.GetComponentInChildren<Animator>(true) : null;
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                foreach (AnimationClip c in animator.runtimeAnimatorController.animationClips)
                {
                    if (!_playerClips.ContainsKey(c.name))
                    {
                        _playerClips[c.name] = c;
                    }
                }
            }
            return _playerClips;
        }

        public void PlaySwing()
        {
            _character.m_zanim.SetTrigger(SwingTrigger);
        }
    }
}
