using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Plays a tool swing on the Dvergr. Its animator only has crossbow-era triggers, so the trigger is the
    /// role's config override, else the tool's own, else any attack-like trigger the animator has.
    /// </summary>
    internal sealed class WorkerAnimation
    {
        private readonly Character _character;
        private readonly WorkerRole _role;
        private readonly Func<ItemDrop.ItemData> _tool;
        private bool _logged;

        public WorkerAnimation(Character character, WorkerRole role, Func<ItemDrop.ItemData> tool)
        {
            _character = character;
            _role = role;
            _tool = tool;
        }

        public void PlaySwing()
        {
            Animator animator = _character.GetComponentInChildren<Animator>();
            LogOnce(animator);

            string trigger = ResolveTrigger(animator);
            if (!string.IsNullOrEmpty(trigger))
            {
                _character.m_zanim.SetTrigger(trigger);
            }
        }

        private string ResolveTrigger(Animator animator)
        {
            string configured = _role.AnimationTrigger.Value;
            if (!string.IsNullOrEmpty(configured))
            {
                return configured;
            }

            string toolTrigger = _tool()?.m_shared.m_attack.m_attackAnimation;
            if (animator == null || (!string.IsNullOrEmpty(toolTrigger) && HasParameter(animator, toolTrigger)))
            {
                return toolTrigger;
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

        private void LogOnce(Animator animator)
        {
            if (_logged || animator == null)
            {
                return;
            }
            _logged = true;

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

            ItemDrop.ItemData tool = _tool();
            DvergrWorkersPlugin.Log.LogInfo("Dvergr animator parameters: " + string.Join(", ", parameters));
            DvergrWorkersPlugin.Log.LogInfo("Dvergr animator clips: " + string.Join(", ", clips));
            DvergrWorkersPlugin.Log.LogInfo(_role.Name + " tool anim: trigger='" + tool?.m_shared.m_attack.m_attackAnimation
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
