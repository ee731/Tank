/// <remarks>
/// <para>
/// Reflection should be used cautiously due to its performance overhead and the loss of compile-time type safety.
/// It is slower compared to direct access via properties, methods, or fields since it involves runtime type inspection.
/// Moreover, reflection can lead to less maintainable and harder-to-debug code, as it bypasses standard access
/// mechanisms and encapsulation principles.
/// </para>
/// <para>
/// * Performance: Reflection is considerably slower than direct field access, impacting application performance.
/// * Encapsulation: It bypasses access modifiers, potentially breaking encapsulation and leading to unintended consequences.
/// * Maintainability: Code using reflection can be less readable and harder to maintain, especially for developers unfamiliar with it.
/// * Type safety: Reflection bypasses compile-time type checks, increasing the risk of runtime errors.
/// </para>
/// <para>
/// Instead of reflection, it is recommended to use classical getters and setters or public properties to access
/// and manipulate field values. These provide better performance, type safety, and allow for encapsulation.
/// Reflection may be useful in scenarios where dynamic type access is required, such as in frameworks or libraries,
/// but should be avoided in general application logic.
/// </para>
/// </remarks>

using System.Linq;
using UnityEngine;
using UnityEngine.AI;

using Random = UnityEngine.Random;
using Debug = UnityEngine.Debug;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>TankSM</c> state machine for the tank.
    /// </summary>
    internal class TankSM : StateMachine
    {
        protected internal struct States
{
    public IdleState Idle;
    public PatrollingState Patrolling;
    public ChasingState Chasing;
    public RegroupState Regroup;      // Kite near the target while waiting for the squad.
    public AttackingState Attacking;
    public RetreatState Retreat;      // Run away from the target when health is critically low.

    internal States(TankSM sm)
    {
        Idle = new IdleState(sm);
        Patrolling = new PatrollingState(sm);
        Chasing = new ChasingState(sm);
        Regroup = new RegroupState(sm);
        Attacking = new AttackingState(sm);
        Retreat = new RetreatState(sm);
    }
}


        public States m_States;
        [HideInInspector] public GameManager GameManager;           // Reference to the GameManager.
        [HideInInspector] public NavMeshAgent NavMeshAgent;         // Reference to the NavMeshAgent.
        [Header("Patrolling")]
        [Tooltip("Minimum and maximum time delay for patrolling wait.")]
        public Vector2 PatrolWaitTime = new(1.5f, 3.5f);            // A minimum and maximum time delay for patrolling wait.
        [Tooltip("Minimum and maximum circumradius of the area to patrol at a given update time.")]
        public Vector2 PatrolMaxDist = new(15f, 30f);               // A minimum and maximum circumradius of the area to patrol.
        [Range(0f, 2f)] public float PatrolNavMeshUpdate = 0.2f;    // A delay between each parolling path update.
        [Header("Targeting")]
        [Tooltip("Minimum and maximum range for the targeting range.")]
        public Vector2 StartToTargetDist = new(28f, 35f);           // A minimum and maximum range for the targeting range.
        [HideInInspector] public float TargetDistance;              // The distance between the tank and the target.
        [Tooltip("Minimum and maximum range for the stopping range.")]
        public Vector2 StopAtTargetDist = new(18f, 22f);            // A minimum and maximum range for the stopping range.
        [HideInInspector] public float StopDistance;                // The distance between the tank and the target.
        [Range(0f, 2f)] public float TargetNavMeshUpdate = 0.2f;    // A delay between each targeting path update.
        [Header("Blending")]
        [Range(0f, 1f)] public float OrientSlerpScalar = 0.2f;      // A scalar for the slerp.
        // [Header("Target")]
        [HideInInspector] public Transform Target;                  // Reference to the target's transform.
        // [Header("NavMesh")]
        [HideInInspector] public float NavMeshUpdateDeadline;       // The time when the next path update is due.
        [Header("Firing")]
        [Tooltip("Minimum and maximum cooldown time delay between each firing in seconds.")]
        public Vector2 FireInterval = new(0.7f, 2.5f);              // A minimum and maximum cooldown time delay between each firing.
        [Tooltip("Force given to the shell if the fire button is not held, and the force given to the shell if the fire button is held for the max charge time in seconds.")]
        public Vector2 LaunchForceMinMax = new(6.5f, 30f);          // The force given to the shell if the fire button is not held, and the force given to the shell if the fire button is held for the max charge time.
        [Tooltip("How much of the target's movement to lead when aiming (0 = aim where it is now, 1 = aim where it will be when the shell lands).")]
        [Range(0f, 1.5f)] public float LeadFactor = 1f;             // Scales the predicted target displacement.
        [Tooltip("Height above the target's pivot to aim at (the pivot sits on the ground).")]
        public float AimHeight = 0.5f;                              // Aim at the hull rather than the ground under it.
        [Tooltip("The tank only fires once its gun points within this many degrees of the aim point.")]
        public float MaxFireAngle = 6f;                             // Yaw tolerance before a shot is taken.
        [Header("Pursuit")]
        [Tooltip("Longest look-ahead (seconds) used when predicting where to intercept the target.")]
        public float MaxInterceptTime = 3f;                         // Caps the intercept prediction.
        [Tooltip("Sideways distance flanking tanks aim off the target's path, so the squad closes in as a pincer instead of queuing behind it.")]
        public float FlankOffset = 10f;                             // Lateral offset for the flank slots.
        [Header("Evasive Movement")]
        [Tooltip("Sideways swing of the S-shaped weave while chasing (metres either side of the direct line).")]
        public float WeaveAmplitude = 5f;                           // How far the tank swings left/right.
        [Tooltip("Seconds for one full left-right-left weave cycle.")]
        public float WeavePeriod = 2.4f;                            // Weave rhythm.
        [Tooltip("How far ahead the weave waypoint is placed. Keep it above the agent's braking distance (~9 m at speed 12 / acceleration 8) so weaving never slows the tank down.")]
        public float WeaveLookAhead = 12f;                          // Distance of the weave waypoint.
        [Tooltip("How much the strafing circle's radius breathes in and out while attacking (metres), turning the circle into a wavy S-orbit.")]
        public float OrbitWeave = 3f;                               // Radial oscillation while attacking.
        [Header("Fire Safety")]
        [Tooltip("Extra distance on top of the shell's explosion radius that the predicted impact must keep from this tank and its squad mates.")]
        public float BlastSafetyMargin = 1f;                        // Buffer so our own blast never reaches us or an ally.
        [Header("Combat Distance")]
        [Tooltip("Radius of the strafing circle around the target while attacking. Closer means more pressure; it is never allowed below MinEngageDistance + 2 so the AI can still fire safely.")]
        public float AttackOrbitRadius = 11f;                       // How close the AI presses while attacking.
        [Header("Squad Tactics")]
        [Tooltip("The tank holds at firing range until this many squad members (itself included) have gathered near the target, then the whole group attacks together. Automatically clamped to the number of AI tanks still alive.")]
        public int MinSquadToAttack = 2;                            // How many AI tanks must be gathered before committing to the attack.
        [Tooltip("An ally counts as 'gathered' when it is within this distance of this tank. Once enough allies are this close, the group advances on the player together.")]
        public float RallyRadius = 24f;                             // Radius used to decide whether an ally has joined up.
        [Tooltip("Whether a regrouping tank takes a shot on the way to its ally if the player happens to be in range (it never turns toward the player either way).")]
        public bool FireWhileRegrouping = false;                    // Off = just drive to the ally.
        [Header("Low Health Fallback")]
        [Tooltip("The tank falls back behind its healthiest ally once this many more full-damage hits from the target's shell would kill it (2 = fall back when 2 hits are enough to destroy it).")]
        [Min(1)] public int RetreatShotsToKill = 2;                 // Fall back when health <= this many max-damage enemy shells.
        [Tooltip("Never retreat or regroup when the player is at least as close to death as this tank. On: compare shots-to-kill (accounts for AI 10 vs player 15 shell damage). Off: compare raw health.")]
        public bool CompareByShotsToKill = true;                    // How 'the player is weaker' is judged.
        [Tooltip("Fallback max damage of the target's shell, used only if it cannot be read from the target's TankShooting prefab.")]
        public float FallbackEnemyShellDamage = 15f;                // Matches Shell-VarPlayer MaxDamage.
        [Tooltip("How far behind the chosen ally (away from the target) the wounded tank tries to stay.")]
        public float FallbackBehindDistance = 9f;                   // Distance behind the ally, along the ally-to-target line.
        [Tooltip("Sideways offset from the ally's line of fire, so the wounded tank doesn't shoot through its own ally.")]
        public float FallbackSideOffset = 4f;                       // Perpendicular offset used to keep a clear shot.
        [Header("References")]
        [Tooltip("Prefab")] public Rigidbody Shell;                 // Prefab of the shell.
        [Tooltip("Transform")] public Transform FireTransform;      // A child of the tank where the shells are spawned.
        // public Slider AimSlider;                                 // A child of the tank that displays the current launch force.
        [Header("Firing Audio")]
        public AudioSource SFXAudioSource;                          // Reference to the audio source used to play the shooting audio. NB: different to the movement audio source.
        // public AudioClip ShotChargingAudioClip;                  // Audio that plays when each shot is charging up.
        public AudioClip ShotFiringAudioClip;                       // Audio that plays when each shot is fired.

        private bool m_Started = false; // Whether the tank has started moving.
        private Rigidbody m_Rigidbody;  // Reference used to the tank's regidbody.
        private TankSound m_TankSound;  // Reference used to play sound effects.
        private TankHealth m_TankHealth; // Reference used to read the tank's own health.
        private float m_EnemyShellDamage = -1f; // Cached max damage of the target's shell (-1 = not read yet).
        private Vector3 m_TargetVelocity;       // Smoothed ground velocity of the target, estimated from its motion.
        private Vector3 m_LastTargetPos;        // Target position on the previous frame.
        private bool m_HasLastTargetPos;        // Whether m_LastTargetPos is valid.
        private float m_WeavePhase;             // Per-tank phase offset of the weave wave (set in Awake).
        private const float k_DodgeDistance = 6f; // Length of a sidestep away from an incoming shell.
        private float m_EnemyShellRadius = 4f;  // Blast radius of the player's shell (read from its prefab).
        private float m_DodgeUntil;             // A dodge runs until this time (or until the point is reached).
        private float m_NextDodgeScan;          // Time of the next incoming-shell scan.
        private const int k_PinnedShots = 3;             // Shots at our spot that count as being pinned down...
        private const float k_PinnedWindow = 4f;         // ...within this many seconds...
        private const float k_PinnedMoveTolerance = 5f;  // ...while moving less than this.
        private const float k_PinnedShellRadius = 6f;    // A shell landing this close to us counts as aimed at us.
        private const float k_RelocateDistance = 12f;    // How far to relocate when pinned down.
        private struct FireEvent { public float Time; public Vector3 Position; }
        private readonly System.Collections.Generic.List<FireEvent> m_FireEvents = new();   // Recent shots at us.
        private readonly System.Collections.Generic.HashSet<int> m_SeenShells = new();      // Shells already counted.
        private float m_LastHealth = float.MaxValue;     // Health last frame, to detect hits.
        private float m_RelocateUntil;                   // A relocation runs until this time (or until reached).
        private Vector3 m_FacePoint;            // Point the current state wants the hull to face this frame.
        private bool m_HasFacePoint;            // Whether FaceTowards was called this frame.
        private NavMeshPath m_ScratchPath;      // Reused for reachability checks (allocated in Awake).
        private const float k_StuckCheckInterval = 1f; // Seconds between stuck checks.
        private const float k_StuckMinMove = 0.75f;    // Moving less than this per check while pathing counts as stuck.
        private const float k_UnstickDuration = 1.5f;  // How long a back-out manoeuvre runs before the state resumes.
        private Vector3 m_StuckCheckPos;        // Position at the last stuck check.
        private float m_NextStuckCheck;         // Time of the next stuck check.
        private int m_StuckStrikes;             // Consecutive checks that looked stuck.
        private float m_UnstickUntil;           // Recovery manoeuvre runs until this time.
        private bool m_Unsticking;              // Whether a recovery manoeuvre is in progress.
        private float m_SavedStoppingDistance;  // State's stopping distance, restored after recovery.
        private bool m_ReportedIsolated;        // Log the "disconnected NavMesh region" warning only once.
        private const float k_RallyCheckInterval = 0.5f; // Seconds between rally-ally path checks.
        private GameObject m_RallyAlly;         // Cached nearest reachable ally.
        private float m_NextRallyCheck;         // Time of the next rally-ally path check.
        private float m_ShellExplosionRadius = 4f; // Blast radius of our shell (read from the Shell prefab).
        private float m_ShellLifeTime = 2f;        // Lifetime of our shell (read from the Shell prefab).
        private float m_ShellMaxDamage = 10f;      // Max damage of our shell (read from the Shell prefab).
        private bool m_ShellStatsRead;             // Whether the values above have been read.
        private TankHealth m_TargetHealth;         // Cached health component of the target (the player).
        private const float k_ShellRadius = 0.15f; // Radius of the shell's trigger collider, for the flight sweep.

        /// <summary>
        /// Property <c>TargetInRange</c> is <c>true</c> when the last firing solution can actually reach its aim point
        /// within the launch speed limits (i.e. the shot would not fall short).
        /// </summary>
        public bool TargetInRange { get; private set; }

        /// <summary>
        /// Property <c>HealthFraction</c> is the current health as a fraction of the starting health (1 = full).
        /// </summary>
        public float HealthFraction => m_TankHealth == null ? 1f : m_TankHealth.CurrentHealth / m_TankHealth.StartingHealth;

        /// <summary>
        /// Method <c>MoveTurnSound</c> returns the current tank's velocity.
        /// </summary>
        private Vector2 MoveTurnSound() => new Vector2(Mathf.Abs(NavMeshAgent.velocity.x), Mathf.Abs(NavMeshAgent.velocity.z));

        /// <summary>
        /// Method <c>GetInitialState</c> returns the initial state of the state machine.
        /// </summary>
        protected override BaseState GetInitialState() => m_States.Chasing;

        /// <summary>
        /// Method <c>SetNavMeshAgent</c> sets the NavMeshAgent's speed and angular speed.
        /// </summary>
        private void SetNavMeshAgent()
        {
            NavMeshAgent.speed = GameManager.Speed;
            NavMeshAgent.angularSpeed = GameManager.AngularSpeed;
            // The hull is turned by LateUpdate (toward the aim point or the direction of travel) at the same
            // GameManager.AngularSpeed limit. Letting the agent turn it as well would stack two turns per frame
            // and exceed that limit, so the agent only steers position.
            NavMeshAgent.updateRotation = false;
        }

        /// <summary>
        /// Method <c>SetStopDistanceToZero</c> sets the NavMeshAgent's stopping distance to zero.
        /// </summary>
        public void SetStopDistanceToZero() => NavMeshAgent.stoppingDistance = 0f;

        /// <summary>
        /// Method <c>SetStopDistanceToTarget</c> sets the NavMeshAgent's stopping distance to the target's distance.
        /// </summary>
        public void SetStopDistanceToTarget() => NavMeshAgent.stoppingDistance = StopDistance;

        /// <summary>
        /// Method <c>Awake</c> is called when the script instance is being loaded.
        /// </summary>
        private void Awake()
        {
            m_States = new States(this);

            GameManager = GameManager.Instance;

            m_Rigidbody = GetComponent<Rigidbody>();
            NavMeshAgent = GetComponent<NavMeshAgent>();
            m_TankSound = GetComponent<TankSound>();
            m_TankHealth = GetComponent<TankHealth>();

            SetNavMeshAgent();
            m_ScratchPath = new NavMeshPath();
            m_WeavePhase = Random.Range(0f, 2f * Mathf.PI); // Desync weaving between squad mates.

            TargetDistance = Random.Range(StartToTargetDist.x, StartToTargetDist.y);
            StopDistance = Random.Range(StopAtTargetDist.x, StopAtTargetDist.y);

            SetStopDistanceToTarget();

            var tankManagers = GameManager.PlayerPlatoon.Tanks.Take(1);
            if (tankManagers.Count() != 0 && tankManagers.First().Instance != null)
            {
                Target = tankManagers.First().Instance.transform;
                Debug.Log($"[TankSM] Target acquired: {Target.name}, pos={Target.position}");
            }
            else
            {
                Debug.LogError("[TankSM] 'Player Platoon' is empty or player not spawned yet! Target will be null.");
            }
        }

        /// <summary>
        /// Method <c>OnEnable</c> is called when the object becomes enabled and active.
        /// </summary>
        private void OnEnable()
        {
            // When the tank is turned on, make sure it's not kinematic.
            m_Rigidbody.isKinematic = false;

            // New round / respawn: clear any stuck-recovery state from the previous round.
            m_Unsticking = false;
            m_UnstickUntil = 0f;
            m_StuckStrikes = 0;
            m_NextStuckCheck = 0f;
            m_StuckCheckPos = transform.position;
            m_ReportedIsolated = false;
            m_HasLastTargetPos = false;
            m_RallyAlly = null;
            m_NextRallyCheck = 0f;
            m_DodgeUntil = 0f;
            m_RelocateUntil = 0f;
            m_FireEvents.Clear();
            m_SeenShells.Clear();
            m_LastHealth = float.MaxValue;
        }

        /// <summary>
        /// Method <c>Start</c> is called on the frame when a script is enabled just before any of the Update methods are called the first time.
        /// </summary>
        private new void Start()
        {
            // base.Start(); // Moved to Update.

            m_TankSound.MoveTurnInputCalc += MoveTurnSound;
        }

        /// <summary>
        /// Method <c>OnDisable</c> is called when the behaviour becomes disabled or inactive.
        /// </summary>
        private void OnDisable()
        {
            // When the tank is turned off, set it to kinematic so it stops moving.
            m_Rigidbody.isKinematic = true;

            m_TankSound.MoveTurnInputCalc -= MoveTurnSound;
        }

        /// <summary>
        /// Method <c>FixedUpdate</c> is called every physics step. The target velocity is sampled here because the
        /// player tank only moves in FixedUpdate: sampling per rendered frame gives 0 on most frames and a spike on
        /// the rest, so the estimate would depend on the frame rate (and differ between the Editor and a build).
        /// </summary>
        private void FixedUpdate()
        {
            TrackTargetVelocity();
        }

        /// <summary>
        /// Method <c>Update</c> is called every frame, if the MonoBehaviour is enabled.
        /// </summary>
        private new void Update()
        {
            if (!m_Started && GameManager.IsRoundPlaying)
            {
                m_Started = true;
                base.Start();
            }
            else if (GameManager.IsRoundPlaying)
            {
                // While backing out of a stuck spot, pause the state so it can't overwrite the escape route.
                if (!HandleStuck())
                    base.Update();
            }
            else
            {
                m_Started = false;
                StopAllCoroutines();
            }
        }

        /// <summary>
        /// Method <c>TrackTargetVelocity</c> estimates the target's ground velocity from its step-to-step motion.
        /// The player tank moves with <c>Rigidbody.MovePosition</c>, so its rigidbody velocity can't be trusted.
        /// Called from <c>FixedUpdate</c>, so it runs in lockstep with the player's movement at any frame rate.
        /// </summary>
        private void TrackTargetVelocity()
        {
            float dt = Time.fixedDeltaTime;
            if (Target == null || dt <= 0f)
            {
                m_HasLastTargetPos = false;
                return;
            }

            Vector3 pos = Target.position;
            if (m_HasLastTargetPos)
            {
                Vector3 v = (pos - m_LastTargetPos) / dt;
                v.y = 0f;
                // A jump bigger than any tank can drive (respawn/teleport) resets the estimate.
                if (v.sqrMagnitude > 50f * 50f)
                    v = Vector3.zero;
                m_TargetVelocity = Vector3.Lerp(m_TargetVelocity, v, 1f - Mathf.Exp(-10f * dt));
            }
            m_LastTargetPos = pos;
            m_HasLastTargetPos = true;
        }

        /// <summary>
        /// Method <c>PredictTargetPosition</c> returns where the target will be in <paramref name="seconds"/> if it keeps its current velocity.
        /// </summary>
        public Vector3 PredictTargetPosition(float seconds) => Target.position + m_TargetVelocity * seconds;

        /// <summary>
        /// Method <c>InterceptTime</c> returns the earliest time this tank, driving straight at full speed, can meet
        /// the target on its current course (clamped to <c>MaxInterceptTime</c> when it can't be caught).
        /// </summary>
        public float InterceptTime()
        {
            Vector3 d = Target.position - transform.position;
            d.y = 0f;
            Vector3 v = m_TargetVelocity;
            float s = NavMeshAgent.speed;

            // |d + v·t| = s·t  =>  (v·v − s²)·t² + 2(d·v)·t + d·d = 0
            float a = v.sqrMagnitude - s * s;
            float b = 2f * Vector3.Dot(d, v);
            float c = d.sqrMagnitude;

            float t = MaxInterceptTime;
            if (Mathf.Abs(a) < 0.001f)
            {
                if (b < 0f)
                    t = -c / b;
            }
            else
            {
                float disc = b * b - 4f * a * c;
                if (disc >= 0f)
                {
                    float sq = Mathf.Sqrt(disc);
                    float t1 = (-b - sq) / (2f * a);
                    float t2 = (-b + sq) / (2f * a);
                    float best = float.PositiveInfinity;
                    if (t1 > 0f) best = t1;
                    if (t2 > 0f && t2 < best) best = t2;
                    if (!float.IsPositiveInfinity(best))
                        t = best;
                }
            }

            return Mathf.Clamp(t, 0f, MaxInterceptTime);
        }

        /// <summary>
        /// Method <c>FlankSide</c> returns this tank's flank slot among the living AI tanks: -1 (left), +1 (right)
        /// or 0 (straight at the target). A lone tank always goes straight in.
        /// </summary>
        public int FlankSide()
        {
            int index = 0;
            int alive = 0;
            foreach (var mate in GameManager.AIPlatoon.Tanks)
            {
                if (mate.Instance == null || !mate.Instance.activeSelf)
                    continue;
                if (mate.Instance == gameObject)
                    index = alive;
                alive += 1;
            }

            if (alive <= 1)
                return 0;
            int[] slots = { -1, 1, 0 };
            return slots[index % slots.Length];
        }

        /// <summary>
        /// Method <c>GetPursuitPoint</c> returns the NavMesh point to drive to: where the target will be when this
        /// tank can reach it, shifted to this tank's flank so the squad cuts the target off from several sides.
        /// </summary>
        public Vector3 GetPursuitPoint()
        {
            Vector3 predicted = PredictTargetPosition(InterceptTime());

            // Flank relative to the target's heading; if it's (nearly) stationary, relative to our approach line.
            Vector3 heading = m_TargetVelocity;
            if (heading.sqrMagnitude < 1f)
                heading = Target.position - transform.position;
            heading.y = 0f;
            Vector3 side = heading.sqrMagnitude > 0.001f ? Vector3.Cross(Vector3.up, heading.normalized) : Vector3.zero;

            Vector3 dest = predicted + side * (FlankSide() * FlankOffset);

            // Only accept a point we can actually drive to. SamplePosition alone can snap to a NavMesh patch on the
            // far side of a wall; the agent would then drive into the nearest dead end and sit there.
            if (TryReachablePoint(dest, out Vector3 point) ||
                TryReachablePoint(predicted, out point) ||
                TryReachablePoint(Target.position, out point))
                return point;
            return Target.position;
        }

        /// <summary>
        /// Method <c>WeaveWave</c> returns this tank's current weave value in [-1, 1] (a sine wave with period
        /// <c>WeavePeriod</c>, phase-shifted per tank so squad mates don't weave in lockstep).
        /// </summary>
        public float WeaveWave() => Mathf.Sin(Time.time * (2f * Mathf.PI / Mathf.Max(0.1f, WeavePeriod)) + m_WeavePhase);

        /// <summary>
        /// Method <c>GetWeavePoint</c> turns a straight run at <paramref name="goal"/> into an S-shaped one: it returns
        /// a waypoint <c>WeaveLookAhead</c> metres toward the goal, swung sideways by <c>WeaveAmplitude</c> along
        /// the weave wave. Falls back to the goal itself when close to it or when the swung waypoint is blocked
        /// on the NavMesh (the agent then paths around the obstacle normally).
        /// </summary>
        public Vector3 GetWeavePoint(Vector3 goal)
        {
            Vector3 pos = transform.position;
            Vector3 to = goal - pos;
            to.y = 0f;
            float d = to.magnitude;
            if (d < WeaveLookAhead * 1.5f || !NavMeshAgent.isOnNavMesh)
                return goal;

            Vector3 dir = to / d;
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            Vector3 waypoint = pos + dir * WeaveLookAhead + side * (WeaveWave() * WeaveAmplitude);

            // Straight-line NavMesh check: if something is in the way, don't weave into it.
            if (NavMesh.Raycast(NavMeshAgent.nextPosition, waypoint, out _, NavMesh.AllAreas))
                return goal;
            return waypoint;
        }

        /// <summary>
        /// Method <c>TryReachablePoint</c> snaps <paramref name="near"/> to the NavMesh and returns it only if a
        /// complete path exists from this tank to it.
        /// </summary>
        private bool TryReachablePoint(Vector3 near, out Vector3 point)
        {
            point = near;
            if (!NavMeshAgent.isOnNavMesh)
                return false;
            if (!NavMesh.SamplePosition(near, out NavMeshHit hit, FlankOffset, NavMesh.AllAreas))
                return false;
            if (!NavMesh.CalculatePath(NavMeshAgent.nextPosition, hit.position, NavMesh.AllAreas, m_ScratchPath) ||
                m_ScratchPath.status != NavMeshPathStatus.PathComplete)
                return false;
            point = hit.position;
            return true;
        }

        /// <summary>
        /// Method <c>HandleStuck</c> detects and recovers from the tank getting wedged. It returns <c>true</c> while
        /// a recovery manoeuvre is running (the current state is paused so it can't overwrite the escape route).
        /// Handles three cases that can occur because tanks spawn on random NavMesh vertices (map corners/edges):
        /// 1. Off the NavMesh: the agent can't move at all, so it is snapped to the nearest NavMesh point (≤ 2 m).
        /// 2. Physics/agent desync: collisions pushed the rigidbody away from where the agent thinks it is; the
        ///    agent is re-synced to the real position.
        /// 3. Wedged against cover: it wants to move but hasn't for two checks; it backs out to a reachable point.
        /// Every case is logged with position and path status so the cause can be confirmed in the Console.
        /// </summary>
        private bool HandleStuck()
        {
            // Case 1: not on the NavMesh - SetDestination would fail and the tank would never move.
            if (!NavMeshAgent.isOnNavMesh)
            {
                if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                {
                    Debug.LogWarning($"[TankSM] {name} off the NavMesh at {transform.position} - snapping to {hit.position}");
                    NavMeshAgent.Warp(hit.position);
                }
                return true;
            }

            // Case 2: keep the agent's simulated position on the real (physics) position.
            if ((NavMeshAgent.nextPosition - transform.position).sqrMagnitude > 1f)
                NavMeshAgent.nextPosition = transform.position;

            if (Time.time < m_UnstickUntil)
                return true;

            // Recovery finished - hand the agent back to the state with its own stopping distance.
            if (m_Unsticking)
            {
                m_Unsticking = false;
                NavMeshAgent.stoppingDistance = m_SavedStoppingDistance;
            }

            // Case 3: periodically compare how far we actually moved against whether we are trying to move.
            if (Time.time >= m_NextStuckCheck)
            {
                bool wantsToMove = NavMeshAgent.hasPath && NavMeshAgent.remainingDistance > 2f;
                float moved = Vector3.Distance(transform.position, m_StuckCheckPos);
                m_StuckStrikes = wantsToMove && moved < k_StuckMinMove ? m_StuckStrikes + 1 : 0;
                m_StuckCheckPos = transform.position;
                m_NextStuckCheck = Time.time + k_StuckCheckInterval;

                if (m_StuckStrikes >= 2)
                {
                    m_StuckStrikes = 0;
                    StartUnstick();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Method <c>StartUnstick</c> drives to a random reachable point 5-10 m away (preferring directions toward the
        /// target) for a short time. If no direction is reachable the tank is on a NavMesh region sealed off from
        /// the rest of the map; that is logged so it can be recognised.
        /// </summary>
        private void StartUnstick()
        {
            Debug.LogWarning($"[TankSM] {name} stuck at {transform.position} (pathStatus={NavMeshAgent.pathStatus}) - backing out");

            Vector3 toTarget = Target != null ? Target.position - transform.position : transform.forward;
            toTarget.y = 0f;
            for (int i = 0; i < 12; i++)
            {
                Vector3 dir = Quaternion.AngleAxis(Random.Range(-150f, 150f), Vector3.up) * toTarget.normalized;
                Vector3 candidate = transform.position + dir * Random.Range(5f, 10f);
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                    continue;
                if (!NavMesh.CalculatePath(NavMeshAgent.nextPosition, hit.position, NavMesh.AllAreas, m_ScratchPath) ||
                    m_ScratchPath.status != NavMeshPathStatus.PathComplete)
                    continue;

                m_SavedStoppingDistance = NavMeshAgent.stoppingDistance;
                m_Unsticking = true;
                NavMeshAgent.stoppingDistance = 0f;
                NavMeshAgent.SetPath(m_ScratchPath);
                m_ScratchPath = new NavMeshPath(); // The agent now owns the old one.
                m_UnstickUntil = Time.time + k_UnstickDuration;
                return;
            }

            if (!m_ReportedIsolated)
            {
                m_ReportedIsolated = true;
                Debug.LogWarning($"[TankSM] {name} found no reachable point around {transform.position} - " +
                                 "it is on a NavMesh region disconnected from the rest of the map");
            }
            m_UnstickUntil = Time.time + k_UnstickDuration;
        }

        /// <summary>
        /// Method <c>BallisticSpeed</c> returns the launch speed needed for a shell fired from the muzzle at its
        /// fixed pitch to land on <paramref name="point"/> (no drag, standard gravity).
        /// </summary>
        private float BallisticSpeed(Vector3 point)
        {
            Vector3 origin = FireTransform.position;
            Vector3 flat = point - origin;
            float dy = flat.y;
            flat.y = 0f;
            float d = flat.magnitude;

            float pitch = Mathf.Asin(Mathf.Clamp(FireTransform.forward.y, -1f, 1f));
            float cos = Mathf.Cos(pitch);
            float g = Physics.gravity.magnitude;

            // y = x·tanθ − g·x² / (2·v²·cos²θ)  =>  v² = g·d² / (2·cos²θ·(d·tanθ − dy))
            float denom = 2f * cos * cos * (d * Mathf.Tan(pitch) - dy);
            if (denom <= 0.0001f)
            {
                TargetInRange = false;
                return LaunchForceMinMax.y;
            }

            float speed = Mathf.Sqrt(g * d * d / denom);
            TargetInRange = speed <= LaunchForceMinMax.y;
            return Mathf.Clamp(speed, LaunchForceMinMax.x, LaunchForceMinMax.y);
        }

        /// <summary>
        /// Method <c>GetFiringSolution</c> predicts where the target will be when the shell lands and returns
        /// that aim point along with the launch speed needed to hit it.
        /// </summary>
        public Vector3 GetFiringSolution(out float launchSpeed)
        {
            Vector3 targetPos = Target.position + Vector3.up * AimHeight;
            Vector3 aim = targetPos;
            launchSpeed = BallisticSpeed(aim);

            float cos = Mathf.Max(0.1f, Mathf.Cos(Mathf.Asin(Mathf.Clamp(FireTransform.forward.y, -1f, 1f))));

            // Flight time depends on the aim point and vice versa - a few fixed-point iterations converge.
            for (int i = 0; i < 3; i++)
            {
                Vector3 flat = aim - FireTransform.position;
                flat.y = 0f;
                float flightTime = flat.magnitude / (launchSpeed * cos);
                aim = targetPos + m_TargetVelocity * (flightTime * LeadFactor);
                launchSpeed = BallisticSpeed(aim);
            }

            return aim;
        }

        /// <summary>
        /// Method <c>FaceTowards</c> requests that the tank (and so its gun) turn toward <paramref name="point"/> this
        /// frame. The actual turn is applied in <c>LateUpdate</c>, capped at the game's angular speed.
        /// </summary>
        public void FaceTowards(Vector3 point)
        {
            m_FacePoint = point;
            m_HasFacePoint = true;
        }

        /// <summary>
        /// Method <c>LateUpdate</c> runs the current state's LateUpdate, then turns the hull. The hull turns toward
        /// the aim point requested via <c>FaceTowards</c>, or toward the direction of travel if none was requested.
        /// The turn rate is capped at <c>GameManager.AngularSpeed</c> (the same 180°/s limit as the player's tank),
        /// so aiming never rotates the AI faster than the game allows.
        /// </summary>
        protected override void LateUpdate()
        {
            base.LateUpdate();

            bool hasFacePoint = m_HasFacePoint;
            m_HasFacePoint = false;

            if (!GameManager.IsRoundPlaying)
                return;

            Vector3 dir = hasFacePoint ? m_FacePoint - transform.position : NavMeshAgent.velocity;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f)
                return;

            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, Quaternion.LookRotation(dir), GameManager.AngularSpeed * Time.deltaTime);
        }

        /// <summary>
        /// Method <c>IsAimedAt</c> returns <c>true</c> when the gun points within <c>MaxFireAngle</c> of <paramref name="point"/>.
        /// </summary>
        public bool IsAimedAt(Vector3 point)
        {
            Vector3 toPoint = point - FireTransform.position;
            toPoint.y = 0f;
            Vector3 fwd = FireTransform.forward;
            fwd.y = 0f;
            return Vector3.Angle(fwd, toPoint) <= MaxFireAngle;
        }

        /// <summary>
        /// Method <c>ReadShellStats</c> caches the explosion radius and lifetime of this tank's own shell prefab,
        /// so the fire-safety check uses the real game values instead of hard-coded copies.
        /// </summary>
        private void ReadShellStats()
        {
            if (m_ShellStatsRead)
                return;
            var explosion = Shell != null ? Shell.GetComponent<ShellExplosion>() : null;
            if (explosion != null)
            {
                m_ShellExplosionRadius = explosion.ExplosionRadius;
                m_ShellLifeTime = explosion.MaxLifeTime;
                m_ShellMaxDamage = explosion.MaxDamage;
            }
            m_ShellStatsRead = true;
        }

        /// <summary>
        /// Method <c>HasClearShot</c> simulates the shell's actual flight (current gun direction, given launch
        /// speed, gravity, fixed-timestep integration) and finds where it would first touch a collider. The shot
        /// is only allowed if that impact point - and so the explosion - stays clear of this tank and every squad
        /// mate, both where they are now and where they will have driven to by the time the shell lands.
        /// This is how the AI avoids hurting itself: a shell that clips nearby cover (or a target that is too
        /// close) would explode within blast radius of the shooter, so the AI simply holds fire.
        /// </summary>
        public bool HasClearShot(float launchSpeed)
        {
            if (Target == null || FireTransform == null)
                return false;

            ReadShellStats();

            Vector3 pos = FireTransform.position;
            Vector3 vel = FireTransform.forward * launchSpeed;
            Vector3 g = Physics.gravity;
            float dt = Time.fixedDeltaTime;

            for (float t = 0f; t < m_ShellLifeTime; t += dt)
            {
                // Same integration order as the physics engine: velocity first, then position.
                vel += g * dt;
                Vector3 next = pos + vel * dt;

                if (TryShellHit(pos, next, out Vector3 impact))
                    return IsBlastSafe(impact, t + dt);

                pos = next;
            }

            // The shell expires in mid-air without exploding - harmless.
            return true;
        }

        /// <summary>
        /// Method <c>TryShellHit</c> sweeps the shell's collider along one simulation step and returns the first
        /// impact with anything that isn't this tank (triggers are ignored, like the shell's own trigger hits).
        /// </summary>
        private bool TryShellHit(Vector3 from, Vector3 to, out Vector3 impact)
        {
            impact = Vector3.zero;
            Vector3 seg = to - from;
            float len = seg.magnitude;
            if (len < 0.0001f)
                return false;

            var hits = Physics.SphereCastAll(from, k_ShellRadius, seg / len, len, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            foreach (var hit in hits)
            {
                if (hit.transform.IsChildOf(transform))
                    continue; // Our own hull - the shell spawns outside it.
                // SphereCast reports distance 0 / point zero for overlaps at the start; use the step start then.
                float d = hit.distance;
                Vector3 p = d <= 0f ? from : hit.point;
                if (d < best)
                {
                    best = d;
                    impact = p;
                }
            }
            return !float.IsPositiveInfinity(best);
        }

        /// <summary>
        /// Method <c>IsBlastSafe</c> returns <c>true</c> when an explosion at <paramref name="impact"/> after
        /// <paramref name="flightTime"/> seconds cannot reach this tank or any living squad mate.
        /// </summary>
        private bool IsBlastSafe(Vector3 impact, float flightTime)
        {
            float safe = m_ShellExplosionRadius + BlastSafetyMargin;

            if (TankNearAtImpact(transform.position, NavMeshAgent.velocity, flightTime, impact, safe))
                return false;

            foreach (var mate in GameManager.AIPlatoon.Tanks)
            {
                if (mate.Instance == null || mate.Instance == gameObject || !mate.Instance.activeSelf)
                    continue;
                var agent = mate.Instance.GetComponent<NavMeshAgent>();
                Vector3 v = agent != null ? agent.velocity : Vector3.zero;
                if (TankNearAtImpact(mate.Instance.transform.position, v, flightTime, impact, safe))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Method <c>TankNearAtImpact</c> returns <c>true</c> if a tank driving from <paramref name="pos"/> at
        /// <paramref name="vel"/> will be within <paramref name="radius"/> of <paramref name="point"/> at the moment
        /// the shell explodes (after <paramref name="time"/> seconds). The explosion is instantaneous, so only the
        /// tank's position at that instant matters; its current position is also checked in case it stops or turns.
        /// Distances are measured pivot-to-impact, the same way <c>ShellExplosion.CalculateDamage</c> does.
        /// </summary>
        private static bool TankNearAtImpact(Vector3 pos, Vector3 vel, float time, Vector3 point, float radius)
        {
            return Vector3.Distance(pos + vel * time, point) < radius ||
                   Vector3.Distance(pos, point) < radius;
        }

        /// <summary>
        /// Property <c>MinEngageDistance</c> is roughly the closest the target can be (pivot to pivot) for a shell
        /// hitting its near side to explode clear of this tank: blast radius + safety margin + half a hull length.
        /// Closer than this the AI holds fire, so combat movement keeps at least this much room.
        /// </summary>
        public float MinEngageDistance
        {
            get
            {
                ReadShellStats();
                return m_ShellExplosionRadius + BlastSafetyMargin + 1f;
            }
        }

        /// <summary>
        /// Method <c>AliveAllies</c> returns how many other AI tanks of the platoon are still alive.
        /// </summary>
        public int AliveAllies()
        {
            int count = 0;
            foreach (var mate in GameManager.AIPlatoon.Tanks)
            {
                if (mate.Instance == null || mate.Instance == gameObject || !mate.Instance.activeSelf)
                    continue;
                count += 1;
            }
            return count;
        }

        /// <summary>
        /// Method <c>EnemyShellDamage</c> returns the maximum damage of one shell fired by the target, read once
        /// from the target's <c>TankShooting</c> shell prefab (falls back to <c>FallbackEnemyShellDamage</c>).
        /// </summary>
        private float EnemyShellDamage()
        {
            if (m_EnemyShellDamage <= 0f && Target != null)
            {
                var shooting = Target.GetComponent<TankShooting>();
                var explosion = shooting != null && shooting.Shell != null ? shooting.Shell.GetComponent<ShellExplosion>() : null;
                m_EnemyShellDamage = explosion != null ? explosion.MaxDamage : FallbackEnemyShellDamage;
                if (explosion != null)
                    m_EnemyShellRadius = explosion.ExplosionRadius;
            }
            return m_EnemyShellDamage > 0f ? m_EnemyShellDamage : FallbackEnemyShellDamage;
        }

        /// <summary>
        /// Method <c>UpdateDodge</c> watches incoming shells and sidesteps the ones that would actually hurt this
        /// tank. Returns <c>true</c> while a dodge is running (the caller must not overwrite the destination).
        /// Compared with a naive "dodge anything flying roughly my way":
        /// * each shell's ballistic landing point is predicted, and only shells landing within blast radius of
        ///   where we are / will be trigger a dodge (misses are ignored, so the tank keeps doing its job);
        /// * a dodge target must be reachable in a straight line on the NavMesh (never into a wall or corner);
        /// * among valid sidesteps, the one heading toward <paramref name="preferredDir"/> wins (e.g. toward the
        ///   ally we are regrouping with), so dodging and moving on happen together instead of fighting each other;
        /// * if no safe sidestep exists, the tank simply keeps moving on its normal route.
        /// </summary>
        public bool UpdateDodge(Vector3 preferredDir)
        {
            preferredDir.y = 0f;
            if (preferredDir.sqrMagnitude > 0.001f)
                preferredDir.Normalize();

            // Getting pinned down (hit/bracketed repeatedly without moving) -> relocate right now.
            if (UpdatePinned(preferredDir))
                return true;

            if (Time.time < m_DodgeUntil)
            {
                if (NavMeshAgent.pathPending || NavMeshAgent.remainingDistance > 1f)
                    return true;
                m_DodgeUntil = 0f; // Reached the dodge point.
            }

            if (Time.time < m_NextDodgeScan || !NavMeshAgent.isOnNavMesh)
                return false;
            m_NextDodgeScan = Time.time + 0.1f;

            EnemyShellDamage(); // Makes sure the enemy blast radius has been read.
            float danger = m_EnemyShellRadius + 1f;
            Vector3 pos = transform.position;

            foreach (var shell in FindObjectsOfType<ShellExplosion>())
            {
                if ((shell.transform.position - pos).sqrMagnitude > 40f * 40f)
                    continue;
                var rb = shell.GetComponent<Rigidbody>();
                if (rb == null || rb.velocity.sqrMagnitude < 1f)
                    continue;
                if (!PredictShellImpact(shell.transform.position, rb.velocity, pos.y + 0.85f, out Vector3 impact, out float t))
                    continue;

                // Count every shell aimed at our spot (hit or near miss) towards the "pinned down" check.
                if (FlatDistance(impact, pos) <= k_PinnedShellRadius && m_SeenShells.Add(shell.GetInstanceID()))
                    RecordFireEvent();

                // Is it going to land on us (where we are now, or where we'll have driven to)?
                Vector3 future = pos + NavMeshAgent.velocity * t;
                if (FlatDistance(impact, pos) > danger && FlatDistance(impact, future) > danger)
                    continue;

                if (TryDodgePoint(impact, rb.velocity, preferredDir, danger, out Vector3 dodge))
                {
                    NavMeshAgent.SetDestination(dodge);
                    m_DodgeUntil = Time.time + 0.8f;
                    Debug.Log($"[TankSM] {name} dodging shell landing {FlatDistance(impact, pos):F1} m away in {t:F2}s");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Method <c>RecordFireEvent</c> logs one "we are being shot at here" event (a shell aimed at our spot, or
        /// a hit that cost health) with the time and our position at that moment.
        /// </summary>
        private void RecordFireEvent()
        {
            m_FireEvents.Add(new FireEvent { Time = Time.time, Position = transform.position });
        }

        /// <summary>
        /// Method <c>UpdatePinned</c> detects being a sitting target: <c>k_PinnedShots</c> or more shots aimed at
        /// / hitting us within <c>k_PinnedWindow</c> seconds while we have barely moved (less than
        /// <c>k_PinnedMoveTolerance</c>). When that happens the tank immediately relocates ~12 m to a reachable spot,
        /// preferring a direction across the player's line of fire (hardest to hit) blended with
        /// <paramref name="preferredDir"/>. Returns <c>true</c> while the relocation runs (states keep aiming and
        /// firing, they just don't overwrite the destination).
        /// </summary>
        private bool UpdatePinned(Vector3 preferredDir)
        {
            // Hits count too, even if the shell was never seen in flight.
            if (m_TankHealth != null)
            {
                if (m_LastHealth < float.MaxValue && m_TankHealth.CurrentHealth < m_LastHealth - 0.01f)
                    RecordFireEvent();
                m_LastHealth = m_TankHealth.CurrentHealth;
            }

            // Forget old events (and the shell IDs we've already counted, now and then).
            m_FireEvents.RemoveAll(e => Time.time - e.Time > k_PinnedWindow);
            if (m_FireEvents.Count == 0 && m_SeenShells.Count > 0)
                m_SeenShells.Clear();

            if (Time.time < m_RelocateUntil)
            {
                if (NavMeshAgent.pathPending || NavMeshAgent.remainingDistance > 1.5f)
                    return true;
                m_RelocateUntil = 0f; // Arrived.
            }

            if (m_FireEvents.Count < k_PinnedShots)
                return false;

            // Only "pinned" if we've stayed around the same spot through all of it.
            Vector3 pos = transform.position;
            foreach (var e in m_FireEvents)
                if (FlatDistance(e.Position, pos) > k_PinnedMoveTolerance)
                    return false;

            m_FireEvents.Clear();
            if (!NavMeshAgent.isOnNavMesh || !TryRelocatePoint(preferredDir, out Vector3 point))
                return false;

            Debug.Log($"[TankSM] {name} pinned down at {pos} ({k_PinnedShots}+ shots in {k_PinnedWindow}s) - relocating");
            NavMeshAgent.stoppingDistance = 0f;
            NavMeshAgent.SetDestination(point);
            m_RelocateUntil = Time.time + 2.5f;
            m_DodgeUntil = 0f;
            return true;
        }

        /// <summary>
        /// Method <c>TryRelocatePoint</c> tries 12 directions around the tank and returns a reachable point
        /// <c>k_RelocateDistance</c> away that best combines: moving across the player's line of fire, following
        /// <paramref name="preferredDir"/>, and not heading straight at the player.
        /// </summary>
        private bool TryRelocatePoint(Vector3 preferredDir, out Vector3 point)
        {
            point = Vector3.zero;
            Vector3 pos = transform.position;
            Vector3 toPlayer = Target != null ? Target.position - pos : transform.forward;
            toPlayer.y = 0f;
            toPlayer = toPlayer.sqrMagnitude > 0.01f ? toPlayer.normalized : transform.forward;

            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < 12; i++)
            {
                Vector3 dir = Quaternion.AngleAxis(i * 30f + Random.Range(-10f, 10f), Vector3.up) * Vector3.forward;
                Vector3 cand = pos + dir * k_RelocateDistance;
                if (!NavMesh.SamplePosition(cand, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                    continue;
                if (NavMesh.Raycast(NavMeshAgent.nextPosition, hit.position, out _, NavMesh.AllAreas))
                    continue; // Something in the way - we need a quick, direct move.

                float across = 1f - Mathf.Abs(Vector3.Dot(dir, toPlayer)); // 1 = perpendicular to the line of fire.
                float score = 2f * across + 1.5f * Vector3.Dot(dir, preferredDir) - 0.5f * Mathf.Max(0f, Vector3.Dot(dir, toPlayer));
                if (score > bestScore)
                {
                    bestScore = score;
                    point = hit.position;
                }
            }

            return !float.IsNegativeInfinity(bestScore);
        }

        /// <summary>
        /// Method <c>PredictShellImpact</c> solves the shell's ballistic arc for the time it comes down to
        /// <paramref name="height"/> (roughly our hull centre) and returns that point.
        /// </summary>
        private static bool PredictShellImpact(Vector3 p, Vector3 v, float height, out Vector3 impact, out float t)
        {
            float g = Physics.gravity.magnitude;
            // p.y + v.y·t − ½g·t² = height  =>  ½g·t² − v.y·t + (height − p.y) = 0
            float a = 0.5f * g, b = -v.y, c = height - p.y;
            float disc = b * b - 4f * a * c;
            impact = p;
            t = 0f;
            if (disc < 0f)
                return false;
            t = (-b + Mathf.Sqrt(disc)) / (2f * a); // Later root = on the way down.
            if (t <= 0f)
                return false;
            impact = new Vector3(p.x + v.x * t, height, p.z + v.z * t);
            return true;
        }

        /// <summary>
        /// Method <c>TryDodgePoint</c> picks a sidestep (left/right of the shell's path, or along
        /// <paramref name="preferredDir"/>, or a blend) that is reachable in a straight NavMesh line and ends
        /// outside the blast; the one best aligned with <paramref name="preferredDir"/> and farthest from the impact wins.
        /// </summary>
        private bool TryDodgePoint(Vector3 impact, Vector3 shellVel, Vector3 preferredDir, float danger, out Vector3 point)
        {
            point = Vector3.zero;
            Vector3 pos = transform.position;
            Vector3 flatVel = new Vector3(shellVel.x, 0f, shellVel.z);
            Vector3 side = flatVel.sqrMagnitude > 0.01f ? Vector3.Cross(Vector3.up, flatVel.normalized) : transform.right;

            Vector3[] dirs =
            {
                side, -side, preferredDir,
                (side + preferredDir).normalized, (-side + preferredDir).normalized,
            };

            float bestScore = float.NegativeInfinity;
            foreach (var dir in dirs)
            {
                if (dir.sqrMagnitude < 0.5f)
                    continue;
                Vector3 cand = pos + dir * k_DodgeDistance;
                if (FlatDistance(cand, impact) <= danger)
                    continue; // Still inside the blast.
                if (NavMesh.Raycast(NavMeshAgent.nextPosition, cand, out _, NavMesh.AllAreas))
                    continue; // Blocked - would drive into a wall/corner.

                float score = FlatDistance(cand, impact) + 4f * Vector3.Dot(dir, preferredDir);
                if (score > bestScore)
                {
                    bestScore = score;
                    point = cand;
                }
            }

            return !float.IsNegativeInfinity(bestScore);
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>
        /// Method <c>ShouldRetreat</c> returns <c>true</c> when this tank is critically low on health (a few more
        /// hits would destroy it) and should fall back behind an ally instead of taking point. Skipped when this
        /// is the last AI tank alive (the platoon only scores by killing the human, so the last tank has nothing
        /// to gain by hiding and keeps fighting normally instead).
        /// </summary>
        public bool ShouldRetreat()
        {
            if (Target == null || m_TankHealth == null)
                return false;
            if (m_TankHealth.CurrentHealth > RetreatShotsToKill * EnemyShellDamage())
                return false;
            // The player is at least as close to dying as we are - go all in instead of backing off.
            if (TargetIsWeaker())
                return false;
            return AliveAllies() > 0;
        }

        /// <summary>
        /// Method <c>TargetIsWeaker</c> returns <c>true</c> when the player is no better off than this tank in a
        /// straight exchange of fire. With <c>CompareByShotsToKill</c> (default) it compares how many of our shells
        /// the player can still take against how many of the player's shells we can take - this accounts for the
        /// two sides' different shell damage (AI 10 vs player 15 max). With it off it compares raw health.
        /// Used to skip retreating/regrouping and press the attack when the player is about to fall.
        /// </summary>
        public bool TargetIsWeaker()
        {
            if (Target == null || m_TankHealth == null)
                return false;
            if (m_TargetHealth == null || m_TargetHealth.gameObject != Target.gameObject)
                m_TargetHealth = Target.GetComponent<TankHealth>();
            if (m_TargetHealth == null)
                return false;

            float playerHealth = m_TargetHealth.CurrentHealth;
            float myHealth = m_TankHealth.CurrentHealth;
            if (!CompareByShotsToKill)
                return playerHealth <= myHealth;

            ReadShellStats();
            int shotsToKillPlayer = Mathf.CeilToInt(playerHealth / Mathf.Max(0.01f, m_ShellMaxDamage));
            int shotsToKillMe = Mathf.CeilToInt(myHealth / Mathf.Max(0.01f, EnemyShellDamage()));
            return shotsToKillPlayer <= shotsToKillMe;
        }

        /// <summary>
        /// Method <c>GetHealthiestAlly</c> returns the living ally (other than this tank) with the most current
        /// health, or <c>null</c> if no ally is alive. Used by <c>RetreatState</c> to pick who to fall back behind.
        /// </summary>
        public GameObject GetHealthiestAlly()
        {
            GameObject best = null;
            float bestHealth = float.NegativeInfinity;

            foreach (var mate in GameManager.AIPlatoon.Tanks)
            {
                if (mate.Instance == null || mate.Instance == gameObject || !mate.Instance.activeSelf)
                    continue;

                var health = mate.Instance.GetComponent<TankHealth>();
                float h = health != null ? health.CurrentHealth : 0f;
                if (h > bestHealth)
                {
                    bestHealth = h;
                    best = mate.Instance;
                }
            }

            return best;
        }

        /// <summary>
        /// Method <c>SquadReadyToAttack</c> returns <c>true</c> once enough squad members (this tank included)
        /// are within <c>RallyRadius</c> of this tank, so the AI tanks advance on the player together instead of
        /// trickling in one at a time. The requirement is clamped to the number of AI tanks still alive.
        /// </summary>
        public bool SquadReadyToAttack() => SquadReadyToAttack(1f);

        /// <summary>
        /// Overload of <c>SquadReadyToAttack</c> with the rally radius scaled by <paramref name="radiusScale"/>.
        /// A scale above 1 is used to decide when an assembled squad has *broken up* (hysteresis), so tanks don't
        /// flip between Regroup and Attacking/Chasing when an ally hovers around the rally radius.
        /// </summary>
        public bool SquadReadyToAttack(float radiusScale)
        {
            if (Target == null)
                return false;

            float radius = RallyRadius * radiusScale;
            int alive = 1;    // This tank.
            int gathered = 1; // This tank is, by definition, in position.
            foreach (var mate in GameManager.AIPlatoon.Tanks)
            {
                if (mate.Instance == null || mate.Instance == gameObject || !mate.Instance.activeSelf)
                    continue;

                alive += 1;

                // Only mates close to *this* tank count. (A mate that is merely near the player does not: that
                // mate is itself alone and should be coming to us, not be treated as backup.)
                if (Vector3.Distance(mate.Instance.transform.position, transform.position) <= radius)
                    gathered += 1;
            }

            int required = Mathf.Clamp(MinSquadToAttack, 1, alive);
            return gathered >= required;
        }

        /// <summary>
        /// Method <c>ShouldRegroup</c> returns <c>true</c> when this tank would be fighting the player alone: the
        /// squad has not assembled (or has broken up beyond 1.5× the rally radius) and there is a squad mate it can
        /// actually drive to. A tank with no reachable ally (last one alive, or cut off) fights on instead.
        /// </summary>
        public bool ShouldRegroup() => !TargetIsWeaker() && !SquadReadyToAttack(1.5f) && GetRallyAlly() != null;

        /// <summary>
        /// Method <c>GetRallyAlly</c> returns the nearest living squad mate this tank has a complete NavMesh path
        /// to, or <c>null</c> if none. Path checks are cached for <c>k_RallyCheckInterval</c> seconds.
        /// </summary>
        public GameObject GetRallyAlly()
        {
            if (Time.time < m_NextRallyCheck && (m_RallyAlly == null || m_RallyAlly.activeSelf))
                return m_RallyAlly;
            m_NextRallyCheck = Time.time + k_RallyCheckInterval;
            m_RallyAlly = null;

            if (!NavMeshAgent.isOnNavMesh)
                return null;

            float best = float.PositiveInfinity;
            foreach (var mate in GameManager.AIPlatoon.Tanks)
            {
                if (mate.Instance == null || mate.Instance == gameObject || !mate.Instance.activeSelf)
                    continue;

                if (!NavMesh.SamplePosition(mate.Instance.transform.position, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                    continue;
                if (!NavMesh.CalculatePath(NavMeshAgent.nextPosition, hit.position, NavMesh.AllAreas, m_ScratchPath) ||
                    m_ScratchPath.status != NavMeshPathStatus.PathComplete)
                    continue; // Cut off from this ally (e.g. it spawned on a sealed NavMesh region).

                float d = Vector3.Distance(transform.position, mate.Instance.transform.position);
                if (d < best)
                {
                    best = d;
                    m_RallyAlly = mate.Instance;
                }
            }

            return m_RallyAlly;
        }

        /// <summary>
        /// Method <c>GetRallyPoint</c> returns where to drive to join <paramref name="ally"/>: the ally's own
        /// (reachable) position. Every regrouping tank heads straight for its nearest mate, so mates converge and
        /// meet in the middle - nobody waits, nobody drifts backwards behind someone else.
        /// </summary>
        public Vector3 GetRallyPoint(GameObject ally)
        {
            Vector3 allyPos = ally.transform.position;
            return TryReachablePoint(allyPos, out Vector3 point) ? point : allyPos;
        }

        /// <summary>
        /// Method <c>LaunchProjectile</c> instantiate and launch the shell.
        /// </summary>
        public void LaunchProjectile(float launchForce = 1f)
        {
            launchForce = Mathf.Min(Mathf.Max(LaunchForceMinMax.x, launchForce), LaunchForceMinMax.y);

            // Set the fired flag so only Fire is only called once.
            // m_Fired = true;

            // Create an instance of the shell and store a reference to it's rigidbody.
            Rigidbody shellInstance = Instantiate(Shell, FireTransform.position, FireTransform.rotation) as Rigidbody;

            // Set the shell's velocity to the launch force in the fire position's forward direction.
            shellInstance.velocity = launchForce * FireTransform.forward; ;

            // Change the clip to the firing clip and play it.
            SFXAudioSource.clip = ShotFiringAudioClip;
            SFXAudioSource.Play();
        }
    }
}
