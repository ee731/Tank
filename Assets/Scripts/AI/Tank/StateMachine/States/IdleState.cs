using System.Linq;
using UnityEngine;

using Debug = UnityEngine.Debug;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>IdleState</c> represents the state of the tank when it is idle.
    /// </summary>
    internal class IdleState : BaseState
    {
        private TankSM m_TankSM; // Reference to the tank state machine.

        /// <summary>
        /// Constructor <c>IdleState</c> is the constructor of the class.
        /// </summary>
        public IdleState(TankSM tankStateMachine) : base("Idle", tankStateMachine) => m_TankSM = (TankSM)m_StateMachine;

        /// <summary>
        /// Method <c>Enter</c> is called when the state is entered.
        /// </summary>
        public override void Enter() => base.Enter();

        /// <summary>
        /// Method <c>Update</c> is called each frame.
        /// </summary>
        public override void Update()
        {
            base.Update();

            if (m_TankSM.Target == null)
            {
                Debug.LogWarning("[IdleState] Target is null! Trying to reacquire...");
                var tankManagers = m_TankSM.GameManager.PlayerPlatoon.Tanks.Take(1);
                if (tankManagers.Count() != 0 && tankManagers.First().Instance != null)
                    m_TankSM.Target = tankManagers.First().Instance.transform;
                return;
            }

            // Critically low health -> run away from the player.
            if (m_TankSM.ShouldRetreat())
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Retreat);
                return;
            }

            var dist = Vector3.Distance(m_TankSM.transform.position, m_TankSM.Target.position);

            // Already in attack range -> engage.
            if (dist <= m_TankSM.StopDistance)
            {
                Debug.Log($"[IdleState] dist={dist:F1} <= StopDistance={m_TankSM.StopDistance:F1} -> Attacking");
                m_StateMachine.ChangeState(m_TankSM.m_States.Attacking);
                return;
            }

            // Any other distance -> chase (no more "too close to chase" idle bug).
            Debug.Log($"[IdleState] dist={dist:F1} -> Chasing");
            m_StateMachine.ChangeState(m_TankSM.m_States.Chasing);
        }

    }
}
