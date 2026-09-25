using UnityEngine;

using Random = UnityEngine.Random;
using Debug = UnityEngine.Debug;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>ChasingState</c> represents the state when the tank is chasing the target.
    /// Instead of tailing the player, each tank drives to where the player will be when it can get there
    /// (intercept point), offset to its own flank slot, so the squad closes in as a pincer and cuts off the
    /// escape route. It keeps firing on the move whenever the player is within shell range and the simulated
    /// shot is safe. If the squad isn't together it hands off to RegroupState (fall back to the nearest mate)
    /// rather than arriving alone; on reaching firing range with the squad it hands off to AttackingState.
    /// </summary>
    internal class ChasingState : BaseState
    {
        private TankSM m_TankSM;
        private float m_FireCooldown; // Time until the next shot is allowed.

        public ChasingState(TankSM tankStateMachine) : base("Chasing", tankStateMachine) =>
            m_TankSM = (TankSM)m_StateMachine;

        public override void Enter()
        {
            base.Enter();
            // The destination is ahead of / beside the player, not the player itself, so drive all the way to it.
            m_TankSM.SetStopDistanceToZero();
            m_TankSM.NavMeshUpdateDeadline = 0f; // Plot the first intercept immediately.
            m_FireCooldown = Mathf.Max(m_FireCooldown, 0.5f);
            Debug.Log($"[ChasingState] Enter - intercept pursuit, flank side {m_TankSM.FlankSide()}");
        }

        /// <summary>
        /// Method <c>Update</c> called each frame.
        /// </summary>
        public override void Update()
        {
            base.Update();

            if (m_TankSM.Target == null)
            {
                // Lost the target - drop into Idle, which re-acquires the player.
                m_StateMachine.ChangeState(m_TankSM.m_States.Idle);
                return;
            }

            // Critically low health -> run away from the player instead of closing in.
            if (m_TankSM.ShouldRetreat())
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Retreat);
                return;
            }

            var dist = Vector3.Distance(m_TankSM.transform.position, m_TankSM.Target.position);

            // Would be taking on the player alone -> fall back to the squad first (also the opening rally,
            // since tanks spawn scattered). Uses the wider "broken up" radius so a moving squad doesn't flicker.
            if (m_TankSM.ShouldRegroup())
            {
                Debug.Log($"[ChasingState] dist={dist:F1}, squad not together -> Regroup");
                m_StateMachine.ChangeState(m_TankSM.m_States.Regroup);
                return;
            }

            // Within firing range with the squad -> attack together.
            if (dist <= m_TankSM.StopDistance)
            {
                Debug.Log($"[ChasingState] dist={dist:F1} + squad ready -> Attacking");
                m_StateMachine.ChangeState(m_TankSM.m_States.Attacking);
                return;
            }

            // Periodically re-plot the intercept (the player's course keeps changing) and weave toward it in an
            // S-shape so the tank is never an easy straight-line target while it closes in.
            // Sidestep shells that would really hit us, preferably toward the player (keep closing in).
            if (m_TankSM.UpdateDodge(m_TankSM.Target.position - m_TankSM.transform.position))
                m_TankSM.NavMeshUpdateDeadline = 0f; // Re-plot the pursuit as soon as the dodge ends.
            else if (Time.time >= m_TankSM.NavMeshUpdateDeadline)
            {
                m_TankSM.NavMeshUpdateDeadline = Time.time + m_TankSM.TargetNavMeshUpdate;
                m_TankSM.NavMeshAgent.SetDestination(m_TankSM.GetWeavePoint(m_TankSM.GetPursuitPoint()));
            }

            FireOnTheMove();
        }

        /// <summary>
        /// Method <c>FireOnTheMove</c> keeps shooting while closing in. Once the player is within shell range the
        /// tank turns its gun onto the lead point; a shot is only taken when the gun is on target and the
        /// simulated shell flight lands clear of this tank and its squad mates (no self or friendly damage).
        /// </summary>
        private void FireOnTheMove()
        {
            Vector3 aimPoint = m_TankSM.GetFiringSolution(out float launchSpeed);
            if (!m_TankSM.TargetInRange)
                return; // Out of reach - the shell would fall short, so just keep driving.

            m_TankSM.FaceTowards(aimPoint);

            m_FireCooldown -= Time.deltaTime;
            if (m_FireCooldown > 0f)
                return;

            if (m_TankSM.IsAimedAt(aimPoint) && m_TankSM.HasClearShot(launchSpeed))
            {
                m_TankSM.LaunchProjectile(launchSpeed);
                // Same 0.5s cooldown as AttackingState.
                m_FireCooldown = 0.5f;
            }
            else
            {
                m_FireCooldown = 0.1f; // Re-check soon.
            }
        }

        /// <summary>
        /// Method <c>Exit</c> called when exiting the chasing state.
        /// </summary>
        public override void Exit()
        {
            base.Exit();
            m_TankSM.NavMeshAgent.ResetPath();
        }
    }
}
