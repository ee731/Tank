using UnityEngine;
using UnityEngine.AI;

using Debug = UnityEngine.Debug;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>RetreatState</c> represents the state when the tank is critically low on health.
    /// Rather than fully disengaging, the tank falls back behind its healthiest living ally - letting that
    /// ally take point - while continuing to aim and fire at the target from the safer position. It keeps
    /// re-picking the healthiest ally and repositioning behind them as the fight moves. If no ally is alive
    /// it has nothing to hide behind and fights on normally instead (see <c>TankSM.AliveAllies</c>).
    /// </summary>
    internal class RetreatState : BaseState
    {
        private const float k_PathUpdateInterval = 0.25f; // Seconds between fallback destination updates.

        private TankSM m_TankSM;
        private float m_FireCooldown;
        private float m_NextPathUpdate;
        private float m_SideSign; // +1 or -1, which side of the ally's firing line to hang back on.

        public RetreatState(TankSM tankStateMachine) : base("Retreat", tankStateMachine) =>
            m_TankSM = (TankSM)m_StateMachine;

        public override void Enter()
        {
            base.Enter();

            m_TankSM.SetStopDistanceToZero();
            m_FireCooldown = 0.35f;
            m_NextPathUpdate = 0f;
            m_SideSign = Random.value > 0.5f ? 1f : -1f;

            Debug.Log($"[RetreatState] Enter - health at {m_TankSM.HealthFraction:P0}, falling back behind an ally");
        }

        public override void Update()
        {
            base.Update();

            if (m_TankSM.Target == null)
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Idle);
                return;
            }

            // The player is now at least as close to dying as we are - stop hiding and go all in.
            if (m_TankSM.TargetIsWeaker())
            {
                Debug.Log("[RetreatState] player is weaker -> all in, Attacking");
                m_StateMachine.ChangeState(m_TankSM.m_States.Attacking);
                return;
            }

            // Nobody left to hide behind - hiding gains nothing on its own, so fight on normally.
            GameObject ally = m_TankSM.GetHealthiestAlly();
            if (ally == null)
            {
                Debug.Log("[RetreatState] no ally left to fall back behind -> Attacking");
                m_StateMachine.ChangeState(m_TankSM.m_States.Attacking);
                return;
            }

            // --- Sidestep shells that would really hit us, preferably toward the ally we're falling back to ---
            if (m_TankSM.UpdateDodge(ally.transform.position - m_TankSM.transform.position))
            {
                m_NextPathUpdate = 0f; // Resume the fallback as soon as the dodge ends.
            }
            // --- Movement: stay behind the ally, offset sideways so we don't shoot through them ---
            else if (Time.time >= m_NextPathUpdate)
            {
                m_NextPathUpdate = Time.time + k_PathUpdateInterval;

                Vector3 allyPos = ally.transform.position;
                Vector3 awayFromTarget = allyPos - m_TankSM.Target.position;
                awayFromTarget.y = 0f;
                if (awayFromTarget.sqrMagnitude < 0.01f)
                    awayFromTarget = -m_TankSM.transform.forward;
                awayFromTarget.Normalize();
                Vector3 side = Vector3.Cross(Vector3.up, awayFromTarget);

                Vector3 dest = allyPos + awayFromTarget * m_TankSM.FallbackBehindDistance + side * (m_SideSign * m_TankSM.FallbackSideOffset);

                if (NavMesh.SamplePosition(dest, out NavMeshHit hit, 6f, NavMesh.AllAreas))
                    m_TankSM.NavMeshAgent.SetDestination(hit.position);
            }

            // --- Aiming: keep the gun on the target ---
            Vector3 aimPoint = m_TankSM.GetFiringSolution(out float launchSpeed);
            m_TankSM.FaceTowards(aimPoint);

            // --- Firing: keep shooting from the safer position, same rate as a full attack ---
            m_FireCooldown -= Time.deltaTime;
            if (m_FireCooldown <= 0f)
            {
                if (!m_TankSM.HasClearShot(launchSpeed) || !m_TankSM.IsAimedAt(aimPoint))
                {
                    m_FireCooldown = 0.1f;
                }
                else
                {
                    m_TankSM.LaunchProjectile(launchSpeed);
                    m_FireCooldown = Random.Range(0.35f, 0.5f);
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
