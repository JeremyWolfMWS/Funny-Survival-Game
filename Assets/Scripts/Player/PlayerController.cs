using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Minecraft-style first person movement on a Rigidbody: WASD, Space jump / swim up, Ctrl or double-tap W sprint,
/// C sneak (won't walk off edges), tap Shift to dash, Esc frees the cursor. Handles slopes, small steps, swimming,
/// slippery blocks and reports fall distances. Speed from dashes, sprint jumps and head hitters carries through the
/// air. The transform pivot is at the feet. The body never turns; the camera does, so looking around stays crisp
/// while the body moves with physics interpolation.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
public class PlayerController : MonoBehaviour
{
    public VoxelWorld world;
    [Tooltip("Child camera at eye height. Falls back to a child Camera.")]
    public Camera playerCamera;
    [Tooltip("Move to dry land near this X/Z when the game starts.")]
    public bool spawnOnSurface = true;

    [Header("Movement (blocks per second)")]
    public float walkSpeed = 4.3f;
    public float sprintSpeed = 5.6f;
    public float sneakSpeed = 1.3f;
    public float groundAcceleration = 50f;
    [Range(0, 1)] public float airControl = 0.25f;
    public float jumpHeight = 1.25f;
    public float gravity = 32f;
    public float terminalVelocity = 78f;
    [Tooltip("Steepest ground you can walk up, in degrees. Ramps made of corner slopes are about 55.")]
    [Range(30, 80)] public float slopeLimit = MinSlopeLimit;
    [Tooltip("Ledges up to this high are walked onto without jumping. Under 1, so full blocks still need a jump.")]
    [Range(0, 0.9f)] public float stepHeight = 0.55f;

    [Header("Dash")]
    [Tooltip("Tap Shift: a quick roll toward where you're moving (or looking, when standing still). Only the press " +
             "counts, holding Shift does nothing.")]
    public float dashSpeed = 13f;
    public float dashDuration = 0.22f;
    public float dashCooldown = 0.75f;
    [Tooltip("Seconds at the start of a dash where hits miss you.")]
    public float dashInvulnerability = 0.25f;
    [Tooltip("Dashes allowed in the air before landing again.")]
    public int airDashes = 1;

    [Header("Momentum")]
    [Tooltip("Speed added each time you jump while sprinting. Kept through the air, lost to the ground.")]
    public float sprintJumpBoost = 1.2f;
    [Tooltip("Extra speed when your head bonks a ceiling mid jump while sprinting. Sprint-jumping under a ceiling 2 " +
             "blocks above the floor (a head hitter) chains short hops that build up speed fast.")]
    public float headHitterBoost = 0.8f;
    [Tooltip("Fastest you can go by chaining dashes, jumps and head hitters.")]
    public float maxMomentumSpeed = 13f;
    [Tooltip("How fast extra speed bleeds off in the air, in blocks per second per second.")]
    public float airDrag = 1.5f;

    [Header("Swimming")]
    [Range(0, 1)] public float swimSpeedFactor = 0.55f;
    public float swimUpSpeed = 3.4f;
    public float sinkSpeed = 1.8f;
    public float waterGravity = 10f;

    [Header("Body (blocks)")]
    public float bodyHeight = 1.9f;
    public float sneakBodyHeight = 1.6f;
    public float bodyRadius = 0.3f;
    public float eyeHeight = 1.72f;
    public float sneakEyeHeight = 1.37f;

    [Header("Look")]
    [Tooltip("Degrees per pixel of mouse movement.")]
    public float mouseSensitivity = 0.1f;
    public float fieldOfView = 70f;
    public float sprintFovBoost = 10f;

    [Tooltip("Layers the ground and wall checks hit.")]
    public LayerMask blockMask = Physics.DefaultRaycastLayers;

    /// <summary>Called on landing with the number of blocks fallen. Landing in water doesn't count.</summary>
    public event System.Action<float> Landed;

    public const float MinSlopeLimit = 60f;

    /// <summary>Off while a menu is open: no moving, looking or cursor grabbing.</summary>
    public bool InputEnabled { get; set; } = true;
    /// <summary>Cursor locked and input enabled, so gameplay clicks and keys should be handled.</summary>
    public bool HasFocus => InputEnabled && Cursor.lockState == CursorLockMode.Locked;
    /// <summary>True on the frame a click only grabbed the cursor.</summary>
    public bool GrabbedCursorThisFrame { get; private set; }
    public bool IsSneaking => sneaking;
    public bool IsGrounded => grounded;
    public bool IsInWater { get; private set; }
    public bool IsHeadUnderwater { get; private set; }
    public Vector3 SpawnPoint { get; set; }
    /// <summary>Flat direction the player is looking.</summary>
    public Vector3 Forward => Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
    public Vector3 Right => Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
    public Bounds BodyBounds => capsule.bounds;
    public Rigidbody Body => body;
    public bool IsDashing => dashTimer > 0f;
    /// <summary>In the dodge window at the start of a dash, where hits miss.</summary>
    public bool IsDodging => Time.time - dashStartTime < dashInvulnerability;
    /// <summary>0 right after dashing, 1 when the next dash is ready.</summary>
    public float DashCharge => dashCooldown <= 0f ? 1f : Mathf.Clamp01((Time.time - dashStartTime) / dashCooldown);
    /// <summary>Feet position plus half the body height.</summary>
    public Vector3 Center => transform.position + Vector3.up * capsule.height * 0.5f;

    Rigidbody body;
    CapsuleCollider capsule;
    float yaw, pitch;
    bool sprinting, sneaking, grounded;
    Vector3 groundNormal = Vector3.up;
    float groundGap;
    float lastForwardTap = -1f;
    float blockedTime;
    float jumpCooldown;
    bool airborne;
    float fallStartY;
    float dashTimer, dashStartTime = -100f;
    Vector3 dashDirection;
    int airDashesLeft;
    bool bonked;
    float shake;

    // Input read every frame, used by the next physics step
    Vector2 moveInput;
    bool jumpHeld, sneakHeld, dashQueued;

    readonly RaycastHit[] hits = new RaycastHit[16];
    readonly Collider[] overlaps = new Collider[16];
    const float ProbeLift = 0.1f;
    const float Skin = 0.04f;

    void Reset()
    {
        SetUpBody(GetComponent<Rigidbody>(), GetComponent<CapsuleCollider>(), false);
        if (TryGetComponent(out capsule)) SetBodyHeight(bodyHeight);
    }

    void Awake()
    {
        // Players made for the old CharacterController version: swap it for the Rigidbody at runtime
        if (TryGetComponent(out CharacterController old)) Destroy(old);
        body = GetComponent<Rigidbody>();
        if (body == null) body = gameObject.AddComponent<Rigidbody>();
        capsule = GetComponent<CapsuleCollider>();
        if (capsule == null) capsule = gameObject.AddComponent<CapsuleCollider>();
        SetUpBody(body, capsule, true);
        SetBodyHeight(bodyHeight);
        // Update decides when the terrain is ready; until then don't let the first physics steps drop us
        if (world != null || FindAnyObjectByType<VoxelWorld>() != null) body.isKinematic = true;

        if (playerCamera == null) playerCamera = GetComponentInChildren<Camera>();
        if (world == null) world = FindAnyObjectByType<VoxelWorld>();
        yaw = transform.eulerAngles.y;
        transform.rotation = Quaternion.identity;
        SpawnPoint = transform.position;

        // Players set up with only this component still get the inventory, health, UI and mining
        Ensure<PlayerInventory>();
        Ensure<PlayerHealth>();
        Ensure<InventoryUI>();
        Ensure<PlayerInteraction>();
        Ensure<PlayerAchievements>();
        Ensure<CommandConsole>();
    }

    static PhysicsMaterial frictionless;

    void SetUpBody(Rigidbody rb, CapsuleCollider col, bool runtime)
    {
        if (rb != null)
        {
            rb.mass = 70f;
            rb.useGravity = false;
            rb.freezeRotation = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            // Speculative also works while kinematic (waiting for terrain) and keeps fast falls from tunneling
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.linearDamping = 0f;
        }
        if (col != null)
        {
            col.direction = 1;
            col.radius = bodyRadius;
            // Made at runtime only, so the scene never references a material that isn't an asset
            if (!runtime) return;
            // No friction, so walls never hold the player up and movement is all ours
            frictionless ??= new PhysicsMaterial("Player (Frictionless)")
            {
                dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum, bounceCombine = PhysicsMaterialCombine.Minimum
            };
            col.sharedMaterial = frictionless;
        }
    }

    void SetBodyHeight(float height)
    {
        capsule.height = height;
        capsule.center = new Vector3(0f, height * 0.5f, 0f);
    }

    void Ensure<T>() where T : Component
    {
        if (!TryGetComponent(out T _)) gameObject.AddComponent<T>();
    }

    void Start()
    {
        if (world != null && spawnOnSurface)
        {
            Vector3 p = transform.position;
            SpawnPoint = FindDrySpawn(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z));
            Teleport(SpawnPoint);
        }

        if (playerCamera != null)
        {
            playerCamera.fieldOfView = fieldOfView;
            playerCamera.transform.localPosition = new Vector3(0f, eyeHeight, 0f);
            playerCamera.transform.localRotation = Quaternion.Euler(pitch, yaw, 0f);
        }
        SetCursorLocked(true);
    }

    /// <summary>Nearest column to (x, z) whose top isn't under water, spiraling outward.</summary>
    Vector3 FindDrySpawn(int x, int z)
    {
        for (int r = 0; r <= 96; r += 4)
        for (int i = 0; i < Mathf.Max(1, r * 2); i++)
        {
            float angle = i * Mathf.PI / Mathf.Max(1, r);
            int cx = x + Mathf.RoundToInt(Mathf.Cos(angle) * r), cz = z + Mathf.RoundToInt(Mathf.Sin(angle) * r);
            int top = world.GetSurfaceHeight(cx, cz);
            if (world.GetFluid(new Vector3Int(cx, top + 1, cz)) == Fluid.None)
                // One extra block of clearance in case a slope sits on top of the column
                return new Vector3(cx + 0.5f, top + 2f, cz + 0.5f);
        }
        return world.GetSpawnPosition(x, z) + Vector3.up;
    }

    public void Teleport(Vector3 feetPosition)
    {
        body.position = feetPosition;
        transform.position = feetPosition;
        if (!body.isKinematic) body.linearVelocity = Vector3.zero;
        grounded = false;
        airborne = false;
        fallStartY = feetPosition.y;
        dashTimer = 0f;
    }

    /// <summary>Changes the velocity at once, e.g. knockback. Anything upward lifts us off the ground.</summary>
    public void AddImpulse(Vector3 velocityChange)
    {
        if (body.isKinematic) return;
        body.linearVelocity += velocityChange;
        if (velocityChange.y > 0f)
        {
            grounded = false;
            jumpCooldown = 0.1f;
        }
    }

    /// <summary>Shakes the camera, 1 = a heavy hit. Fades out over about half a second.</summary>
    public void Shake(float amount) => shake = Mathf.Max(shake, amount);

    void Update()
    {
        Keyboard keyboard = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        GrabbedCursorThisFrame = false;
        if (InputEnabled)
        {
            if (keyboard.escapeKey.wasPressedThisFrame) SetCursorLocked(false);
            else if (Cursor.lockState != CursorLockMode.Locked && mouse.leftButton.wasPressedThisFrame)
            {
                SetCursorLocked(true);
                GrabbedCursorThisFrame = true;
            }
        }

        bool focused = HasFocus;
        if (focused) Look(mouse);
        ReadInput(keyboard, focused);
        UpdateWater();

        // Hold still until the ground under us exists, so we don't fall through the world
        bool ready = world == null || (world.IsChunkLoaded(transform.position) && !world.IsLoading);
        if (body.isKinematic == ready) body.isKinematic = !ready;

        UpdateCamera(Time.deltaTime);
    }

    // ---------- Input and looking ----------

    void Look(Mouse mouse)
    {
        Vector2 delta = mouse.delta.ReadValue() * mouseSensitivity;
        yaw = Mathf.Repeat(yaw + delta.x, 360f);
        pitch = Mathf.Clamp(pitch - delta.y, -90f, 90f);
    }

    void ReadInput(Keyboard keyboard, bool focused)
    {
        moveInput = Vector2.zero;
        if (focused)
        {
            if (keyboard.wKey.isPressed) moveInput.y += 1f;
            if (keyboard.sKey.isPressed) moveInput.y -= 1f;
            if (keyboard.dKey.isPressed) moveInput.x += 1f;
            if (keyboard.aKey.isPressed) moveInput.x -= 1f;
        }
        jumpHeld = focused && keyboard.spaceKey.isPressed;
        sneakHeld = focused && keyboard.cKey.isPressed;
        // Only the press dashes, so a finger resting on Shift does nothing
        if (focused && keyboard.leftShiftKey.wasPressedThisFrame) dashQueued = true;

        if (focused && keyboard.wKey.wasPressedThisFrame)
        {
            if (Time.time - lastForwardTap < 0.3f) sprinting = true;
            lastForwardTap = Time.time;
        }
        if (focused && keyboard.leftCtrlKey.isPressed && moveInput.y > 0f) sprinting = true;

        // Stop sprinting after running into a wall for a moment
        Vector3 v = body.isKinematic ? Vector3.zero : body.linearVelocity;
        bool slow = new Vector2(v.x, v.z).magnitude < walkSpeed * 0.5f;
        blockedTime = sprinting && slow ? blockedTime + Time.deltaTime : 0f;
        if (moveInput.y <= 0f || sneaking || blockedTime > 0.2f) sprinting = false;
    }

    void UpdateCamera(float dt)
    {
        if (playerCamera == null) return;
        Transform cam = playerCamera.transform;
        float eye = sneaking ? sneakEyeHeight : eyeHeight;
        float y = Mathf.MoveTowards(baseEyeY < 0f ? cam.localPosition.y : baseEyeY, eye, 4f * dt);
        baseEyeY = y;

        // The dash tucks the view down and tips it toward the roll, peaking halfway through
        float roll = 0f, dip = 0f, tuck = 0f;
        if (dashTimer > 0f && dashDuration > 0f)
        {
            float s = Mathf.Sin((1f - dashTimer / dashDuration) * Mathf.PI);
            roll = -Vector3.Dot(dashDirection, Right) * 14f * s;
            tuck = Vector3.Dot(dashDirection, Forward) * 7f * s;
            dip = 0.4f * s;
        }
        Vector3 jitter = Vector3.zero;
        if (shake > 0f)
        {
            jitter = Random.insideUnitSphere * shake * 0.12f;
            shake = Mathf.MoveTowards(shake, 0f, 2f * dt);
        }
        cam.localPosition = new Vector3(0f, y - dip, 0f) + jitter;
        cam.localRotation = Quaternion.Euler(pitch + tuck, yaw, roll);

        float fov = fieldOfView + (sprinting ? sprintFovBoost : 0f) + (dashTimer > 0f ? 8f : 0f);
        playerCamera.fieldOfView = Mathf.MoveTowards(playerCamera.fieldOfView, fov, 60f * dt);
    }

    float baseEyeY = -1f;

    void UpdateWater()
    {
        if (world == null) return;
        IsInWater = IsWaterAt(transform.position + Vector3.up * 0.4f);
        IsHeadUnderwater = playerCamera != null && IsWaterAt(playerCamera.transform.position);
    }

    /// <summary>Whether a point is below the water surface in its cell.</summary>
    public bool IsWaterAt(Vector3 point)
    {
        Vector3Int cell = Vector3Int.FloorToInt(point);
        byte fluid = world.GetFluid(cell);
        if (fluid == Fluid.None) return false;
        bool above = world.GetFluid(cell + Vector3Int.up) != Fluid.None;
        return point.y < cell.y + Fluid.SurfaceHeight(fluid, above);
    }

    /// <summary>Slipperiness (0-1) of the block we're standing on.</summary>
    float GroundSlipperiness()
    {
        if (world == null) return 0f;
        BlockDefinition ground = world.GetBlockDefinition(Vector3Int.FloorToInt(body.position + Vector3.down * 0.05f));
        return ground?.slipperiness ?? 0f;
    }

    // ---------- Physics step ----------

    void FixedUpdate()
    {
        if (body.isKinematic) return;
        float dt = Time.fixedDeltaTime;
        jumpCooldown = Mathf.Max(0f, jumpCooldown - dt);

        UpdateSneaking(sneakHeld);
        bool wasGrounded = grounded;
        // Right after a jump the ground is still close, so don't count it
        grounded = jumpCooldown <= 0f && ProbeGround(wasGrounded ? 0.35f : Skin, out groundNormal, out groundGap);

        float speed = sneaking ? sneakSpeed : sprinting ? sprintSpeed : walkSpeed;
        if (IsInWater) speed *= swimSpeedFactor;
        Vector3 wish = Right * moveInput.x + Forward * moveInput.y;
        if (wish.sqrMagnitude > 1f) wish.Normalize();

        Vector3 v = body.linearVelocity;
        if (grounded || IsInWater) airDashesLeft = airDashes;
        if (dashQueued)
        {
            dashQueued = false;
            TryStartDash(wish, ref v);
        }

        var horizontal = new Vector3(v.x, 0f, v.z);
        float accel = groundAcceleration;
        if (dashTimer > 0f)
        {
            // Fast at first, easing off toward the end of the roll
            dashTimer = Mathf.Max(0f, dashTimer - dt);
            float ease = 0.6f + 0.4f * (dashTimer / dashDuration);
            horizontal = dashDirection * dashSpeed * ease * (IsInWater ? swimSpeedFactor : 1f);
        }
        else if (grounded && !IsInWater)
        {
            accel *= Mathf.Lerp(1f, 0.04f, GroundSlipperiness());
            horizontal = Vector3.MoveTowards(horizontal, wish * speed, accel * dt);
        }
        else if (!IsInWater) horizontal = AirMove(horizontal, wish, speed, accel * airControl, dt);
        else horizontal = Vector3.MoveTowards(horizontal, wish * speed, accel * dt);
        if (sneaking && grounded && dashTimer <= 0f) horizontal = GuardEdges(horizontal, dt);

        if (IsInWater)
        {
            v.y = jumpHeld
                ? Mathf.MoveTowards(v.y, swimUpSpeed, 25f * dt)
                : Mathf.MoveTowards(v.y, -sinkSpeed, waterGravity * dt);
        }
        else if (grounded)
        {
            if (jumpHeld)
            {
                v.y = Mathf.Sqrt(2f * gravity * jumpHeight);
                grounded = false;
                jumpCooldown = 0.15f;
                bonked = false;
                if (sprinting) horizontal = Boost(horizontal, wish, sprintJumpBoost);
            }
            else
            {
                // Run along the slope at the same horizontal speed, and pull down onto the ground when cresting a
                // hill so we don't hop off it. No gravity while standing, so slopes don't slide us down.
                v.y = -(groundNormal.x * horizontal.x + groundNormal.z * horizontal.z) / Mathf.Max(groundNormal.y, 0.1f);
                if (groundGap > 0.01f) v.y = Mathf.Min(v.y, -groundGap / dt);
                TryStepUp(horizontal, dt);
                // Only jumps get the head hitter boost, not walking off a ledge under a ceiling
                bonked = true;
            }
        }
        else
        {
            // Air dashes float a little: gravity mostly lets go during the roll
            float g = dashTimer > 0f ? gravity * 0.25f : gravity;
            v.y = Mathf.Max(v.y - g * dt, -terminalVelocity);
            // The physics may have already stopped us at the ceiling, so check near the top of the jump either way
            if (!bonked && v.y > -3f && HitsCeiling(Mathf.Max(v.y, 0f) * dt))
            {
                v.y = Mathf.Min(v.y, 0f);
                if (sprinting) horizontal = Boost(horizontal, wish, headHitterBoost);
                bonked = true;
            }
        }

        v.x = horizontal.x;
        v.z = horizontal.z;
        body.linearVelocity = v;
        TrackFall();
    }

    void TryStartDash(Vector3 wish, ref Vector3 v)
    {
        if (dashDuration <= 0f || dashTimer > 0f || DashCharge < 1f) return;
        bool inAir = !grounded && !IsInWater;
        if (inAir && airDashesLeft <= 0) return;
        if (inAir) airDashesLeft--;

        dashDirection = wish.sqrMagnitude > 0.01f ? wish.normalized : Forward;
        dashTimer = dashDuration;
        dashStartTime = Time.time;
        if (inAir) v.y = Mathf.Max(v.y, 1f);
    }

    /// <summary>
    /// In the air: below normal speed it steers like before; above it (after a dash, sprint jumps or head hitters)
    /// it turns toward where you're heading without braking, bleeding the extra speed off slowly.
    /// </summary>
    Vector3 AirMove(Vector3 horizontal, Vector3 wish, float speed, float accel, float dt)
    {
        float current = horizontal.magnitude;
        if (current <= speed + 0.01f) return Vector3.MoveTowards(horizontal, wish * speed, accel * dt);

        Vector3 target = wish.sqrMagnitude > 0.01f ? wish.normalized * current : horizontal;
        horizontal = Vector3.MoveTowards(horizontal, target, accel * dt);
        float kept = Mathf.MoveTowards(horizontal.magnitude, speed, airDrag * dt);
        return horizontal.sqrMagnitude > 0.0001f ? horizontal.normalized * kept : horizontal;
    }

    /// <summary>Adds speed toward where we're heading, up to Max Momentum Speed.</summary>
    Vector3 Boost(Vector3 horizontal, Vector3 wish, float amount)
    {
        Vector3 dir = wish.sqrMagnitude > 0.01f ? wish.normalized
            : horizontal.sqrMagnitude > 0.01f ? horizontal.normalized : Forward;
        Vector3 boosted = horizontal + dir * amount;
        float max = Mathf.Max(maxMomentumSpeed, horizontal.magnitude);
        return Vector3.ClampMagnitude(boosted, max);
    }

    bool HitsCeiling(float rise)
    {
        float r = capsule.radius * 0.9f;
        Vector3 head = body.position + Vector3.up * (capsule.height - r - 0.02f);
        return SphereCast(head, r, Vector3.up, rise + Skin + 0.02f, out _);
    }

    /// <summary>
    /// Looks for walkable ground under the feet within maxGap. normal is the ground's slope, gap how far the feet are
    /// above it.
    /// </summary>
    bool ProbeGround(float maxGap, out Vector3 normal, out float gap)
    {
        normal = Vector3.up;
        gap = 0f;
        float r = capsule.radius * 0.95f;
        Vector3 origin = body.position + Vector3.up * (r + ProbeLift);
        if (!SphereCast(origin, r, Vector3.down, ProbeLift + maxGap, out RaycastHit hit)) return false;
        if (hit.normal.y < Mathf.Cos(slopeLimit * Mathf.Deg2Rad)) return false;
        normal = hit.normal;
        gap = Mathf.Max(0f, hit.distance - ProbeLift);
        return true;
    }

    // Walks up ledges lower than stepHeight: if something blocks the feet but there's room a step higher, rise onto it
    void TryStepUp(Vector3 horizontal, float dt)
    {
        if (stepHeight <= 0f || horizontal.sqrMagnitude < 0.01f) return;
        Vector3 dir = horizontal.normalized;
        float dist = horizontal.magnitude * dt + capsule.radius * 0.5f;
        float r = capsule.radius * 0.95f;
        Vector3 feet = body.position + Vector3.up * (r + 0.02f);
        if (!SphereCast(feet, r, dir, dist, out RaycastHit wall)) return;
        if (wall.normal.y >= Mathf.Cos(slopeLimit * Mathf.Deg2Rad)) return;

        Vector3 raised = feet + Vector3.up * stepHeight;
        if (SphereCast(raised, r, dir, dist, out _)) return;
        if (SphereCast(body.position + Vector3.up * (capsule.height - r), r, Vector3.up, stepHeight, out _)) return;

        Vector3 ahead = raised + dir * dist;
        if (!SphereCast(ahead, r, Vector3.down, stepHeight + 0.02f, out RaycastHit top)) return;
        if (top.normal.y < Mathf.Cos(slopeLimit * Mathf.Deg2Rad)) return;
        float rise = stepHeight + 0.02f - top.distance;
        if (rise > 0.01f) body.position += Vector3.up * rise;
    }

    void TrackFall()
    {
        float y = body.position.y;
        if (IsInWater)
        {
            airborne = false;
            fallStartY = y;
        }
        else if (grounded)
        {
            if (airborne && fallStartY > y) Landed?.Invoke(fallStartY - y);
            airborne = false;
            fallStartY = y;
        }
        else
        {
            if (!airborne) fallStartY = y;
            airborne = true;
            fallStartY = Mathf.Max(fallStartY, y);
        }
    }

    void UpdateSneaking(bool wantsSneak)
    {
        // Stay crouched while something is above our head
        if (!wantsSneak && sneaking && !HasRoomFor(bodyHeight)) wantsSneak = true;
        if (wantsSneak == sneaking) return;
        sneaking = wantsSneak;
        SetBodyHeight(sneaking ? sneakBodyHeight : bodyHeight);
    }

    bool HasRoomFor(float height)
    {
        float r = capsule.radius * 0.9f;
        Vector3 bottom = body.position + Vector3.up * (r + 0.05f);
        Vector3 top = body.position + Vector3.up * (height - r);
        int n = Physics.OverlapCapsuleNonAlloc(bottom, top, r, overlaps, blockMask & ~(1 << 2), QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (overlaps[i] != capsule && overlaps[i].attachedRigidbody != body) return false;
        return true;
    }

    // While sneaking, cancel movement that would step off a ledge taller than the step height
    Vector3 GuardEdges(Vector3 horizontal, float dt)
    {
        Vector3 step = horizontal * dt;
        if (!HasGroundBelow(new Vector3(step.x, 0f, 0f))) horizontal.x = 0f;
        if (!HasGroundBelow(new Vector3(0f, 0f, step.z))) horizontal.z = 0f;
        if (!HasGroundBelow(new Vector3(horizontal.x * dt, 0f, horizontal.z * dt))) horizontal = Vector3.zero;
        return horizontal;
    }

    bool HasGroundBelow(Vector3 offset)
    {
        float r = capsule.radius * 0.9f;
        Vector3 origin = body.position + offset + Vector3.up * (r + 0.05f);
        return SphereCast(origin, r, Vector3.down, stepHeight + 0.1f, out _);
    }

    // Sphere cast against the world that skips our own collider, item drops (Ignore Raycast) and triggers
    bool SphereCast(Vector3 origin, float radius, Vector3 direction, float distance, out RaycastHit closest)
    {
        closest = default;
        int n = Physics.SphereCastNonAlloc(origin, radius, direction, hits, distance, blockMask & ~(1 << 2), QueryTriggerInteraction.Ignore);
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = hits[i];
            // Distance 0 means we started inside it, which says nothing about the ground
            if (h.collider == capsule || h.collider.attachedRigidbody == body || h.distance <= 0f) continue;
            if (!found || h.distance < closest.distance) closest = h;
            found = true;
        }
        return found;
    }

    public static void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
