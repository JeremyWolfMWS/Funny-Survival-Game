using UnityEngine;

/// <summary>
/// A grizzly that hunts the player in two phases. On all fours it pounces from a distance, swipes up close and rears
/// up to slam the ground (be in the air when it lands to dodge the shockwave). Hurt past half health it roars, stands
/// up and sprints after you on its hind legs, faster than you can run. At that speed it turns slowly, so a dash to the
/// side as it closes in makes it overshoot. Weak to axes. Spawn with BearEnemy.Spawn or the /summon bear command.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
public class BearEnemy : MonoBehaviour, IDamageable
{
    public float maxHealth = 60f;
    [Tooltip("Hits with this tool type do 50% more damage. Adaptive weapons (black opal) always find the weakness.")]
    public ToolType weakness = ToolType.Axe;
    public float aggroRange = 28f;
    public float loseRange = 48f;
    public float gravity = 32f;

    [Header("Phase 1: on all fours")]
    public float chaseSpeed = 4.6f;
    public float swipeDamage = 4f;
    public float pounceDamage = 6f;
    public float pounceMinRange = 4f;
    public float pounceMaxRange = 10f;
    public float pounceCooldown = 3.5f;
    public float slamDamage = 8f;
    [Tooltip("How far the slam's shockwave reaches. Being off the ground when it lands dodges it.")]
    public float slamRadius = 3.6f;
    [Tooltip("Slams when the player is closer than this.")]
    public float slamRange = 3f;
    public float slamCooldown = 4.5f;

    [Header("Phase 2: on two legs")]
    [Tooltip("Share of health left that sets off phase 2.")]
    [Range(0, 1)] public float phaseTwoAt = 0.5f;
    [Tooltip("Faster than the player's sprint, so you need dashes and momentum to get away.")]
    public float runSpeed = 9f;
    [Tooltip("Degrees per second it turns while sprinting. Lower = wider overshoots, easier to dodge.")]
    public float runTurnSpeed = 150f;
    public float runSwipeDamage = 6f;
    [Tooltip("Damage for running straight into the player.")]
    public float trampleDamage = 4f;

    public float Health { get; private set; }
    public int Phase { get; private set; } = 1;
    public bool IsAlive => state != State.Dead;
    public Vector3 Forward => Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;

    enum State { Idle, Wander, Chase, PounceWindup, Pounce, PounceRecover, SlamRear, SlamDown, SlamRecover, SwipeWindup, SwipeRecover, Roar, Dead }

    State state = State.Idle;
    float stateTime;
    Rigidbody body;
    CapsuleCollider capsule;
    BearModel model;
    PlayerController player;
    PlayerHealth playerHealth;
    VoxelWorld world;
    float yaw;
    bool grounded;
    Vector3 wanderDirection;
    float stateLength;
    float nextPounce, nextSlam, nextSwipe, nextTrample;
    bool attackLanded;
    float stuckTime, sidestepUntil;
    Vector3 sidestep;
    float lastHitTime = -100f;
    float deadTime;
    GUIStyle labelStyle;

    readonly RaycastHit[] hits = new RaycastHit[8];
    static PhysicsMaterial frictionless;
    static ParticleSystem dust;

    /// <summary>A new bear standing at feet, facing yaw degrees. Enraged ones start in phase 2.</summary>
    public static BearEnemy Spawn(Vector3 feet, float yawDegrees = 0f, bool enraged = false)
    {
        var go = new GameObject("Bear");
        go.transform.SetPositionAndRotation(feet, Quaternion.Euler(0f, yawDegrees, 0f));
        var bear = go.AddComponent<BearEnemy>();
        if (enraged)
        {
            bear.Health = bear.maxHealth * bear.phaseTwoAt;
            bear.Phase = 2;
            bear.SetCollider(true);
            bear.Enter(State.Chase);
        }
        return bear;
    }

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        capsule = GetComponent<CapsuleCollider>();
        body.mass = 400f;
        body.useGravity = false;
        body.freezeRotation = true;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        frictionless ??= new PhysicsMaterial("Bear (Frictionless)")
        {
            dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum, bounceCombine = PhysicsMaterialCombine.Minimum
        };
        capsule.sharedMaterial = frictionless;
        SetCollider(false);

        var modelObject = new GameObject("Model");
        modelObject.transform.SetParent(transform, false);
        model = modelObject.AddComponent<BearModel>();

        Health = maxHealth;
        yaw = transform.eulerAngles.y;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        world = FindAnyObjectByType<VoxelWorld>();
        stateLength = Random.Range(1f, 3f);
    }

    // Long and low on all fours, tall and thin standing up
    void SetCollider(bool standing)
    {
        if (standing)
        {
            capsule.direction = 1;
            capsule.radius = 0.6f;
            capsule.height = 3.2f;
            capsule.center = new Vector3(0f, 1.6f, 0f);
        }
        else
        {
            capsule.direction = 2;
            capsule.radius = 0.8f;
            capsule.height = 2.9f;
            capsule.center = new Vector3(0f, 0.8f, 0f);
        }
    }

    void FindPlayer()
    {
        player = FindAnyObjectByType<PlayerController>();
        playerHealth = player != null ? player.GetComponent<PlayerHealth>() : null;
    }

    void Enter(State next)
    {
        state = next;
        stateTime = 0f;
        attackLanded = false;
    }

    // ---------- Thinking and moving ----------

    void FixedUpdate()
    {
        if (state == State.Dead) return;
        if (player == null) FindPlayer();
        if (player != null && (transform.position - player.transform.position).sqrMagnitude > 200f * 200f)
        {
            Destroy(gameObject);
            return;
        }
        // Wait in unloaded chunks instead of falling out of the world
        bool loaded = world == null || world.IsChunkLoaded(transform.position);
        if (body.isKinematic == loaded) body.isKinematic = !loaded;
        if (!loaded) return;

        float dt = Time.fixedDeltaTime;
        stateTime += dt;
        grounded = ProbeGround();

        bool hasTarget = player != null && playerHealth != null && playerHealth.Health > 0f;
        Vector3 toPlayer = hasTarget ? player.transform.position - transform.position : Vector3.zero;
        toPlayer.y = 0f;
        float distance = hasTarget ? toPlayer.magnitude : float.MaxValue;
        Vector3 toward = distance > 0.01f && hasTarget ? toPlayer / distance : Forward;

        if (Phase == 1 && Health <= maxHealth * phaseTwoAt && grounded && state != State.Pounce)
        {
            Phase = 2;
            SetCollider(true);
            Enter(State.Roar);
            if (hasTarget && distance < 30f) player.Shake(0.5f);
        }

        Vector3 v = body.linearVelocity;
        var flat = new Vector3(v.x, 0f, v.z);
        Vector3 desired = Vector3.zero;
        float accel = 30f;

        switch (state)
        {
            case State.Idle:
            case State.Wander:
                if (hasTarget && (distance < aggroRange || Time.time - lastHitTime < 10f))
                {
                    Enter(State.Chase);
                    break;
                }
                if (state == State.Wander)
                {
                    desired = wanderDirection * 1.6f;
                    TurnToward(wanderDirection, 120f, dt);
                }
                if (stateTime > stateLength)
                {
                    bool wander = state == State.Idle;
                    Vector2 d = Random.insideUnitCircle.normalized;
                    wanderDirection = new Vector3(d.x, 0f, d.y);
                    stateLength = wander ? Random.Range(3f, 6f) : Random.Range(2f, 5f);
                    Enter(wander ? State.Wander : State.Idle);
                }
                break;

            case State.Chase:
                if (!hasTarget || distance > loseRange)
                {
                    Enter(State.Idle);
                    break;
                }
                if (Phase == 1)
                {
                    desired = toward * chaseSpeed;
                    TurnToward(toward, 300f, dt);
                    if (distance < 2.4f && Time.time >= nextSwipe) Enter(State.SwipeWindup);
                    else if (distance < slamRange && Time.time >= nextSlam && grounded) Enter(State.SlamRear);
                    else if (distance > pounceMinRange && distance < pounceMaxRange && Time.time >= nextPounce && grounded && CanSeePlayer())
                        Enter(State.PounceWindup);
                }
                else
                {
                    // Runs the way it faces and turns slowly, so a last moment sidestep makes it overshoot
                    TurnToward(toward, runTurnSpeed, dt);
                    desired = Forward * runSpeed;
                    accel = 14f;
                    if (distance < 2.3f && Time.time >= nextSwipe) Enter(State.SwipeWindup);
                    else TryTrample(flat);
                }
                break;

            case State.PounceWindup:
                TurnToward(toward, 500f, dt);
                if (stateTime > 0.6f)
                {
                    Leap();
                    Enter(State.Pounce);
                    return;
                }
                break;

            case State.Pounce:
                accel = 0f;
                desired = flat;
                if (!attackLanded && hasTarget && Vector3.Distance(FrontPoint, player.Center) < 1.6f)
                    attackLanded = HitPlayer(pounceDamage, Forward * 8f + Vector3.up * 5f);
                if ((grounded && stateTime > 0.2f) || stateTime > 2.5f)
                {
                    Dust(transform.position + Forward * 0.8f, 16, 1f, 3f, 2f);
                    nextPounce = Time.time + pounceCooldown;
                    Enter(State.PounceRecover);
                }
                break;

            case State.PounceRecover:
                if (stateTime > 0.8f) Enter(State.Chase);
                break;

            case State.SlamRear:
                TurnToward(toward, 120f, dt);
                if (stateTime > 0.75f) Enter(State.SlamDown);
                break;

            case State.SlamDown:
                if (stateTime > 0.14f)
                {
                    Slam(hasTarget);
                    nextSlam = Time.time + slamCooldown;
                    Enter(State.SlamRecover);
                }
                break;

            case State.SlamRecover:
                if (stateTime > 0.9f) Enter(State.Chase);
                break;

            case State.SwipeWindup:
                TurnToward(toward, 400f, dt);
                if (Phase == 2) desired = flat * 0.6f;
                if (stateTime > (Phase == 2 ? 0.22f : 0.35f))
                {
                    if (hasTarget && distance < 2.9f && Vector3.Dot(Forward, toward) > 0.4f)
                        HitPlayer(Phase == 2 ? runSwipeDamage : swipeDamage, Forward * 6f + Vector3.up * 3.5f);
                    nextSwipe = Time.time + (Phase == 2 ? 0.9f : 1.6f);
                    Enter(State.SwipeRecover);
                }
                break;

            case State.SwipeRecover:
                if (Phase == 2) desired = Forward * runSpeed * 0.4f;
                if (stateTime > 0.45f) Enter(State.Chase);
                break;

            case State.Roar:
                TurnToward(toward, 90f, dt);
                if (stateTime > 1.6f) Enter(State.Chase);
                break;
        }

        Move(ref v, flat, desired, accel, dt);
        body.linearVelocity = v;
        body.MoveRotation(Quaternion.Euler(0f, yaw, 0f));
    }

    void Move(ref Vector3 v, Vector3 flat, Vector3 desired, float accel, float dt)
    {
        bool swimming = world != null && world.GetFluid(Vector3Int.FloorToInt(transform.position + Vector3.up * 0.8f)) != Fluid.None;
        if (swimming) desired *= 0.5f;

        // Walk around whatever it got stuck on for a moment
        if (Time.time < sidestepUntil) desired = sidestep * Mathf.Max(desired.magnitude, 2f);
        else if (desired.sqrMagnitude > 1f && flat.magnitude < 0.5f && grounded)
        {
            stuckTime += dt;
            if (stuckTime > 1f)
            {
                stuckTime = 0f;
                sidestep = Vector3.Cross(Vector3.up, desired.normalized) * (Random.value < 0.5f ? 1f : -1f);
                sidestepUntil = Time.time + 0.8f;
                if (grounded) v.y = JumpSpeed(1.4f);
            }
        }
        else stuckTime = 0f;

        if (!grounded && !swimming) accel *= 0.15f;
        if (accel > 0f) flat = Vector3.MoveTowards(flat, desired, accel * dt);
        v.x = flat.x;
        v.z = flat.z;

        if (swimming) v.y = Mathf.MoveTowards(v.y, 2.5f, 20f * dt);
        else if (grounded && v.y <= 0.1f)
        {
            // Hop up single blocks in the way
            v.y = desired.sqrMagnitude > 1f && Blocked(desired.normalized) ? JumpSpeed(1.3f) : -1f;
        }
        else v.y = Mathf.Max(v.y - gravity * dt, -60f);
    }

    float JumpSpeed(float height) => Mathf.Sqrt(2f * gravity * height);

    void TurnToward(Vector3 direction, float degreesPerSecond, float dt)
    {
        if (direction.sqrMagnitude < 0.0001f) return;
        float target = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        yaw = Mathf.MoveTowardsAngle(yaw, target, degreesPerSecond * dt);
    }

    // Where its mouth and claws are, for checking hits
    Vector3 FrontPoint => transform.position + Forward * (Phase == 2 ? 0.7f : 1.3f) + Vector3.up * (Phase == 2 ? 1.6f : 0.9f);

    bool ProbeGround()
    {
        Vector3 origin = transform.position + Vector3.up * 0.8f;
        int n = Physics.SphereCastNonAlloc(origin, 0.4f, Vector3.down, hits, 0.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (hits[i].collider.attachedRigidbody != body && hits[i].distance > 0f) return true;
        return false;
    }

    /// <summary>Whether terrain (not creatures) is right in front of its knees.</summary>
    bool Blocked(Vector3 direction)
    {
        float reach = (Phase == 2 ? 0.6f : 1.45f) + 0.5f;
        return HitsTerrain(transform.position + Vector3.up * 0.45f, direction, reach);
    }

    bool CanSeePlayer()
    {
        Vector3 eye = transform.position + Vector3.up * 1.3f;
        Vector3 to = player.Center - eye;
        return !HitsTerrain(eye, to.normalized, to.magnitude);
    }

    bool HitsTerrain(Vector3 origin, Vector3 direction, float distance)
    {
        int n = Physics.RaycastNonAlloc(origin, direction, hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (hits[i].collider.attachedRigidbody == null) return true;
        return false;
    }

    // ---------- Attacks ----------

    bool HitPlayer(float damage, Vector3 knockback) => playerHealth != null && playerHealth.Hurt(damage, knockback);

    void Leap()
    {
        Vector3 target = player.transform.position;
        if (player.Body != null && !player.Body.isKinematic)
        {
            Vector3 pv = player.Body.linearVelocity;
            target += new Vector3(pv.x, 0f, pv.z) * 0.3f;
        }
        Vector3 offset = target - transform.position;
        float rise = offset.y;
        offset.y = 0f;
        float length = Mathf.Clamp(offset.magnitude, 3f, pounceMaxRange + 1f);
        const float airTime = 0.65f;
        Vector3 dir = offset.sqrMagnitude > 0.01f ? offset.normalized : Forward;
        body.linearVelocity = dir * (length / airTime) + Vector3.up * (gravity * airTime * 0.5f + Mathf.Max(0f, rise) / airTime);
        grounded = false;
    }

    void Slam(bool hasTarget)
    {
        Vector3 center = transform.position + Forward * 1.6f;
        Dust(center, 40, 0.6f, 7f, 2.5f);
        if (!hasTarget) return;

        Vector3 offset = player.transform.position - center;
        float height = offset.y;
        offset.y = 0f;
        float d = offset.magnitude;
        float shake = Mathf.Clamp01(1f - d / 14f);
        if (shake > 0f) player.Shake(shake * 0.8f);
        if (d < slamRadius && Mathf.Abs(height) < 1.5f && player.IsGrounded)
        {
            Vector3 away = d > 0.01f ? offset / d : Forward;
            HitPlayer(slamDamage * Mathf.Lerp(1f, 0.5f, d / slamRadius), away * 6f + Vector3.up * 8f);
        }
    }

    void TryTrample(Vector3 flat)
    {
        if (flat.magnitude < 5f || Time.time < nextTrample) return;
        if (Vector3.Distance(FrontPoint, player.Center) > 1.4f) return;
        if (HitPlayer(trampleDamage, Forward * 9f + Vector3.up * 5f)) nextTrample = Time.time + 1f;
    }

    // ---------- Getting hurt ----------

    public void TakeHit(float amount, Vector3 knockback, ItemDefinition weapon)
    {
        if (!IsAlive) return;
        lastHitTime = Time.time;
        // Roaring, it shrugs hits off
        if (state == State.Roar)
        {
            model.Flash(new Color(0.6f, 0.6f, 0.6f), 0.15f);
            return;
        }
        if (weapon != null && (weapon.adaptive || (weakness != ToolType.None && weapon.toolType == weakness))) amount *= 1.5f;
        Health = Mathf.Max(0f, Health - amount);
        model.Flash(new Color(1f, 0.3f, 0.3f), 0.3f);
        if (!body.isKinematic) body.linearVelocity += knockback * (Phase == 2 ? 0.25f : 0.4f);
        if (state == State.Idle || state == State.Wander) Enter(State.Chase);
        if (Health <= 0f) Die();
    }

    public void Kill()
    {
        if (!IsAlive) return;
        Health = 0f;
        Die();
    }

    void Die()
    {
        Enter(State.Dead);
        deadTime = 0f;
        body.linearVelocity = Vector3.zero;
        body.isKinematic = true;
        capsule.enabled = false;
    }

    // ---------- Animation and UI ----------

    void Update()
    {
        if (state == State.Dead)
        {
            deadTime += Time.deltaTime;
            model.death = Mathf.Clamp01(deadTime / 0.7f);
            model.speed = 0f;
            model.jaw = 0.6f;
            if (deadTime > 2.5f)
            {
                Dust(transform.position + Vector3.up * 0.6f, 30, 1.2f, 2f, 2f);
                Destroy(gameObject);
            }
            return;
        }
        UpdatePose();
    }

    void UpdatePose()
    {
        Vector3 v = body.isKinematic ? Vector3.zero : body.linearVelocity;
        model.speed = grounded ? new Vector2(v.x, v.z).magnitude : 0f;
        model.airborne = !grounded;

        float upright = Phase == 2 ? 1f : 0f, sharpness = 6f, crouch = 0f, reach = 0f, swipe = 0f, jaw = 0.05f, tilt = 0f;
        switch (state)
        {
            case State.Chase:
                if (Phase == 2) jaw = 0.5f;
                break;
            case State.PounceWindup:
                crouch = 1f;
                jaw = 0.4f;
                break;
            case State.Pounce:
                reach = 0.6f;
                jaw = 1f;
                break;
            case State.PounceRecover:
                crouch = 0.4f;
                break;
            case State.SlamRear:
                upright = 0.8f;
                sharpness = 5f;
                reach = 1f;
                jaw = 1f;
                tilt = -15f;
                break;
            case State.SlamDown:
                upright = -0.15f;
                sharpness = 35f;
                reach = -0.1f;
                jaw = 1f;
                break;
            case State.SlamRecover:
                upright = stateTime < 0.25f ? -0.15f : 0f;
                sharpness = 4f;
                jaw = 0.3f;
                break;
            case State.SwipeWindup:
                swipe = 1f;
                jaw = 0.8f;
                break;
            case State.SwipeRecover:
                jaw = 0.4f;
                break;
            case State.Roar:
                upright = 1f;
                sharpness = 3f;
                reach = 1f;
                jaw = 1f;
                tilt = -30f;
                break;
        }
        model.upright = upright;
        model.uprightSharpness = sharpness;
        model.crouch = crouch;
        model.reach = reach;
        model.swipe = swipe;
        model.jaw = jaw;
        model.headTilt = tilt;
    }

    void OnGUI()
    {
        if (!IsAlive) return;
        bool engaged = (state != State.Idle && state != State.Wander) || Time.time - lastHitTime < 5f;
        if (!engaged && Health >= maxHealth) return;
        Camera cam = player != null && player.playerCamera != null ? player.playerCamera : Camera.main;
        if (cam == null) return;

        Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * (Phase == 2 ? 3.6f : 2.3f));
        if (screen.z <= 0f || screen.z > 40f) return;
        float scale = Screen.height / 720f;
        float w = 70f * scale, h = 6f * scale;
        var bar = new Rect(screen.x - w * 0.5f, Screen.height - screen.y - h, w, h);

        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(bar, Texture2D.whiteTexture);
        GUI.color = Phase == 2 ? new Color(1f, 0.35f, 0.05f) : new Color(0.85f, 0.15f, 0.15f);
        GUI.DrawTexture(new Rect(bar.x + scale, bar.y + scale, (w - 2f * scale) * Health / maxHealth, h - 2f * scale), Texture2D.whiteTexture);
        if (Phase == 1)
        {
            // Where phase 2 kicks in
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            GUI.DrawTexture(new Rect(bar.x + w * phaseTwoAt, bar.y, scale, h), Texture2D.whiteTexture);
        }

        labelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerCenter, fontStyle = FontStyle.Bold };
        labelStyle.fontSize = Mathf.RoundToInt(11f * scale);
        GUI.color = Phase == 2 ? new Color(1f, 0.55f, 0.3f) : Color.white;
        GUI.Label(new Rect(bar.x - w, bar.y - 18f * scale, w * 3f, 17f * scale), Phase == 2 ? "Enraged Bear" : "Bear", labelStyle);
        GUI.color = Color.white;
    }

    // ---------- Dust ----------

    /// <summary>Puffs of dirt kicked up around pos, flung outward at speed and upward up to up.</summary>
    static void Dust(Vector3 pos, int count, float radius, float speed, float up)
    {
        if (dust == null) dust = CreateDust();
        var p = new ParticleSystem.EmitParams();
        for (int i = 0; i < count; i++)
        {
            Vector2 c = Random.insideUnitCircle.normalized;
            var dir = new Vector3(c.x, 0f, c.y);
            p.position = pos + dir * radius * Random.value + Vector3.up * 0.15f;
            p.velocity = dir * speed * Random.Range(0.4f, 1f) + Vector3.up * Random.Range(0.3f, up);
            p.startSize = Random.Range(0.15f, 0.35f);
            p.startLifetime = Random.Range(0.5f, 1f);
            float shade = Random.Range(0.8f, 1.1f);
            p.startColor = new Color(0.55f * shade, 0.45f * shade, 0.34f * shade, 0.9f);
            dust.Emit(p, 1);
        }
    }

    static ParticleSystem CreateDust()
    {
        var go = new GameObject("Bear Dust");
        var particles = go.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = particles.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0.6f;
        main.maxParticles = 1000;
        ParticleSystem.EmissionModule emission = particles.emission;
        emission.enabled = false;
        ParticleSystem.ShapeModule shape = particles.shape;
        shape.enabled = false;
        ParticleSystem.SizeOverLifetimeModule size = particles.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.8f, 1f, 1.8f));
        ParticleSystem.ColorOverLifetimeModule color = particles.colorOverLifetime;
        color.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) });
        color.color = fade;
        ParticleSystem.LimitVelocityOverLifetimeModule drag = particles.limitVelocityOverLifetime;
        drag.enabled = true;
        drag.drag = 2f;
        drag.multiplyDragByParticleSize = false;

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader != null)
        {
            var material = new Material(shader) { name = "Bear Dust" };
            VoxelWorld.MakeTransparent(material);
            material.mainTexture = Texture2D.whiteTexture;
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", Texture2D.whiteTexture);
            renderer.sharedMaterial = material;
        }
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        particles.Play();
        return particles;
    }
}
