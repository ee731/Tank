using UnityEngine;

using Random = UnityEngine.Random;
using Debug = UnityEngine.Debug;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>AttackingState</c> represents the state when the tank is attacking the target.
    /// Features: rapid fire, circular strafing, incoming shell evasion.
    /// </summary>
    internal class AttackingState : BaseState
    {
        private TankSM m_TankSM;
        private float m_FireCooldown;
        private float m_StrafeAngle;      // Current angle on the circle around the target.
        private float m_StrafeDirection;  // +1 or -1, clockwise or counter-clockwise.
        private float m_NextDirectionChange; // When to randomly change strafe direction.

        public AttackingState(TankSM tankStateMachine) : base("Attacking", tankStateMachine) =>
            m_TankSM = (TankSM)m_StateMachine;

        public override void Enter()
        {
            base.Enter();

            // Don't stop completely — we strafe around the target.
            m_TankSM.SetStopDistanceToZero();

            // Fire no faster than a human player can (TankShooting.CooldownTime = 0.35s).
            m_FireCooldown = 0.35f;

            // Initialize strafing: pick a starting angle based on current position.
            // Start the orbit on our own side of the target, not the far side.
            Vector3 fromTarget = m_TankSM.transform.position - m_TankSM.Target.position;
            m_StrafeAngle = Mathf.Atan2(fromTarget.z, fromTarget.x);
            m_StrafeDirection = Random.value > 0.5f ? 1f : -1f;
            m_NextDirectionChange = Time.time + Random.Range(2f, 5f);

            Debug.Log("[AttackingState] Enter - rapid fire + strafing + evasion enabled");
        }

        public override void Update()
        {
            base.Update();

            if (m_TankSM.Target == null)
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Idle);
                return;
            }

            // Critically low health -> break off the attack and run away from the player.
            if (m_TankSM.ShouldRetreat())
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Retreat);
                return;
            }

            var dist = Vector3.Distance(m_TankSM.transform.position, m_TankSM.Target.position);

            // Squad broke up (allies killed or scattered) and a mate is reachable -> don't duel alone, fall back.
            if (m_TankSM.ShouldRegroup())
            {
                Debug.Log("[AttackingState] squad broke up -> Regroup");
                m_StateMachine.ChangeState(m_TankSM.m_States.Regroup);
                return;
            }

            // Player moved too far, go back to chasing.
            if (dist > m_TankSM.StopDistance * 1.3f)
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Chasing);
                return;
            }

            // --- Evasion: sidestep shells that would really hit us, preferably along our strafe direction ---
            Vector3 strafeTangent = new Vector3(-Mathf.Sin(m_StrafeAngle), 0f, Mathf.Cos(m_StrafeAngle)) * m_StrafeDirection;
            bool dodging = m_TankSM.UpdateDodge(strafeTangent);

            // --- Movement: strafe in a circle around the target (unless dodging) ---
            if (!dodging)
            {
                // Randomly change strafe direction occasionally.
                if (Time.time >= m_NextDirectionChange)
                {
                    m_StrafeDirection = -m_StrafeDirection;
                    m_NextDirectionChange = Time.time + Random.Range(1.5f, 4f);
                }

                // Advance angle on the circle.
                m_StrafeAngle += m_StrafeDirection * 1.2f * Time.deltaTime;

                // Press in close (AttackOrbitRadius), but never inside the distance where our own
                // shell's blast could reach us - there we'd have to hold fire.
                // The radius breathes in and out along the weave wave, so the orbit is a wavy S-curve rather
                // than a perfect circle the player can lead.
                float radius = Mathf.Max(
                    m_TankSM.AttackOrbitRadius + m_TankSM.WeaveWave() * m_TankSM.OrbitWeave,
                    m_TankSM.MinEngageDistance + 2f);
                // Circle around where the player is heading, so a fleeing player drives into us
                // instead of dragging us behind it.
                Vector3 dest = m_TankSM.PredictTargetPosition(1f) +
                    new Vector3(Mathf.Cos(m_StrafeAngle) * radius, 0f, Mathf.Sin(m_StrafeAngle) * radius);

                // Add small random jitter to make movement unpredictable.
                dest += new Vector3(Random.Range(-1.5f, 1.5f), 0f, Random.Range(-1.5f, 1.5f));

                m_TankSM.NavMeshAgent.SetDestination(dest);
            }

            // --- Aiming: lead the target, face where it will be when the shell lands ---
            Vector3 aimPoint = m_TankSM.GetFiringSolution(out float launchSpeed);
            m_TankSM.FaceTowards(aimPoint);

            // --- Rapid fire: almost no cooldown ---
            m_FireCooldown -= Time.deltaTime;
            if (m_FireCooldown <= 0f)
            {
                // Only fire when the shot won't hit an obstacle or a squad mate - a shell that
                // clips cover can ricochet straight back and damage this tank.
                if (!m_TankSM.HasClearShot(launchSpeed) || !m_TankSM.IsAimedAt(aimPoint))
                {
                    // Hold fire, but re-check quickly; the strafing movement keeps looking for an angle
                    // and the turret keeps turning onto the lead point.
                    m_FireCooldown = 0.1f;
                }
                else
                {
                    // Launch speed solved from the ballistic arc so the shell lands on the aim point.
                    m_TankSM.LaunchProjectile(launchSpeed);

                    // Cap at human-achievable rate: the player's minimum time between
                    // shots is TankShooting.CooldownTime = 0.35s (~2.9 shots/sec).
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
