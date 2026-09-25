using UnityEngine;

using Random = UnityEngine.Random;
using Debug = UnityEngine.Debug;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>RegroupState</c> is the rally phase, entered when a tank would otherwise take on the player alone
    /// (<c>TankSM.ShouldRegroup</c>). The tank does not hang around near the player: it drives straight to its
    /// nearest reachable squad mate, weaving on the way. Every regrouping tank does the same, so mates converge
    /// and meet in the middle. As soon as a mate is within <c>RallyRadius</c> (<c>TankSM.SquadReadyToAttack</c>)
    /// the group turns and advances on the player together (Attacking / Chasing).
    /// </summary>
    internal class RegroupState : BaseState
    {
        private const float k_PathUpdateInterval = 0.25f; // Seconds between rally destination updates.

        private TankSM m_TankSM;
        private float m_FireCooldown;        // Optional covering-fire timer (FireWhileRegrouping).
        private float m_NextPathUpdate;      // Time of the next rally destination update.

        public RegroupState(TankSM tankStateMachine) : base("Regroup", tankStateMachine) =>
            m_TankSM = (TankSM)m_StateMachine;

        public override void Enter()
        {
            base.Enter();

            // Drive all the way to the ally.
            m_TankSM.SetStopDistanceToZero();

            m_FireCooldown = Random.Range(0.3f, 0.8f);
            m_NextPathUpdate = 0f;

            Debug.Log("[RegroupState] Enter - alone, driving to the nearest squad mate");
        }

        public override void Update()
        {
            base.Update();

            if (m_TankSM.Target == null)
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Idle);
                return;
            }

            // Critically low health -> hide behind the healthiest ally.
            if (m_TankSM.ShouldRetreat())
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Retreat);
                return;
            }

            // A mate is close enough (or the player is weaker than us) -> advance on the player together.
            if (m_TankSM.SquadReadyToAttack() || m_TankSM.TargetIsWeaker())
            {
                Debug.Log("[RegroupState] joined up -> Attacking");
                m_StateMachine.ChangeState(m_TankSM.m_States.Attacking);
                return;
            }

            // Nobody we can reach (last one alive, or cut off) - no point looking, so fight.
            GameObject ally = m_TankSM.GetRallyAlly();
            if (ally == null)
            {
                Debug.Log("[RegroupState] no reachable ally -> Chasing");
                m_StateMachine.ChangeState(m_TankSM.m_States.Chasing);
                return;
            }

            // --- Dodge shells that would really hit us, preferring to sidestep toward the ally ---
            if (m_TankSM.UpdateDodge(ally.transform.position - m_TankSM.transform.position))
            {
                m_NextPathUpdate = 0f; // Resume the rally as soon as the dodge ends.
            }
            // --- Movement: straight to the nearest mate (weaving), never loitering near the player ---
            else if (Time.time >= m_NextPathUpdate)
            {
                m_NextPathUpdate = Time.time + k_PathUpdateInterval;
                m_TankSM.NavMeshAgent.SetDestination(m_TankSM.GetWeavePoint(m_TankSM.GetRallyPoint(ally)));
            }

            // --- Optional covering fire (off by default): only if already in range, never turns back to duel ---
            var dist = Vector3.Distance(m_TankSM.transform.position, m_TankSM.Target.position);
            if (!m_TankSM.FireWhileRegrouping || dist > m_TankSM.StopDistance)
                return; // Hull faces the direction of travel.

            Vector3 aimPoint = m_TankSM.GetFiringSolution(out float launchSpeed);
            m_TankSM.FaceTowards(aimPoint);

            m_FireCooldown -= Time.deltaTime;
            if (m_FireCooldown <= 0f)
            {
                if (m_TankSM.HasClearShot(launchSpeed) && m_TankSM.IsAimedAt(aimPoint))
                {
                    m_TankSM.LaunchProjectile(launchSpeed);
                    m_FireCooldown = Random.Range(0.7f, 1.2f);
                }
                else
                {
                    m_FireCooldown = 0.15f;
                }
            }
        }

        public override void Exit()
        {
            base.Exit();
            m_TankSM.NavMeshAgent.ResetPath();
        }
    }
}
