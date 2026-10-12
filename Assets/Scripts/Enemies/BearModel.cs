using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A blocky grizzly built from boxes, animated in code. BearEnemy sets the pose values every frame (how upright, how
/// crouched, where the front legs reach, jaw, speed) and this eases the body toward them and adds the gait: a walk or
/// gallop on all fours, or a flat-out sprint on the hind legs once it stands up. Faces +Z, pivot at the feet.
/// </summary>
public class BearModel : MonoBehaviour
{
    [Header("Pose (set by BearEnemy)")]
    [Tooltip("Ground speed in blocks per second, drives the legs.")]
    public float speed;
    [Tooltip("0 = on all fours, 1 = standing on the hind legs. Below 0 slams the front down.")]
    public float upright;
    [Tooltip("How fast Upright is reached, higher = snappier.")]
    public float uprightSharpness = 6f;
    [Tooltip("0-1, sinking low on splayed legs, ready to spring.")]
    public float crouch;
    [Tooltip("Front legs reaching forward (0.6) or raised overhead (1).")]
    public float reach;
    [Tooltip("0-1 swing of the right front paw, for swipes.")]
    public float swipe;
    [Range(0, 1)] public float jaw;
    [Tooltip("Extra head tilt in degrees, negative looks up.")]
    public float headTilt;
    [Tooltip("Off the ground: hind legs kick back.")]
    public bool airborne;
    [Range(0, 1)] public float death;

    public float Upright => uprightNow;

    // Measurements, in blocks
    const float HipHeight = 0.95f, LegLength = 0.95f;

    Transform hips, head, jawPivot, frontLeft, frontRight, hindLeft, hindRight, tail;
    readonly List<Renderer> renderers = new();
    MaterialPropertyBlock block;
    float uprightNow, crouchNow, reachNow, swipeNow, jawNow, tiltNow, kickNow, phase;
    float flashUntil;
    Color flashColor;

    static Material fur, grizzled, darkFur, muzzle, nose, claw, mouth, innerEar, eye, glint;

    void Awake() => Build();

    /// <summary>Tints the whole bear for a moment, e.g. red when hurt.</summary>
    public void Flash(Color color, float seconds)
    {
        flashColor = color;
        flashUntil = Time.time + seconds;
    }

    static void LoadMaterials()
    {
        if (fur != null) return;
        Material M(Texture2D t) => BlockyModel.Material(t);
        Color C(byte r, byte g, byte b) => new Color32(r, g, b, 255);
        fur = M(BlockyModel.Speckled("Bear Fur", C(98, 63, 38), C(72, 45, 27), C(124, 85, 52), 11, 8, 0.3f));
        grizzled = M(BlockyModel.Speckled("Bear Grizzled Fur", C(116, 78, 47), C(86, 56, 33), C(160, 120, 78), 23, 9, 0.5f, 1));
        darkFur = M(BlockyModel.Speckled("Bear Dark Fur", C(66, 43, 27), C(48, 31, 20), C(84, 56, 35), 37, 6, 0.25f));
        muzzle = M(BlockyModel.Speckled("Bear Muzzle", C(170, 128, 86), C(142, 104, 68), C(190, 152, 108), 41, 5, 0.3f));
        innerEar = M(BlockyModel.Solid("Bear Inner Ear", C(84, 54, 38)));
        nose = M(BlockyModel.Solid("Bear Nose", C(28, 22, 20)));
        eye = M(BlockyModel.Solid("Bear Eye", C(16, 12, 10)));
        glint = M(BlockyModel.Solid("Bear Eye Glint", C(235, 235, 225)));
        claw = M(BlockyModel.Solid("Bear Claw", C(226, 214, 186)));
        mouth = M(BlockyModel.Speckled("Bear Mouth", C(132, 38, 40), C(104, 26, 30), C(160, 60, 62), 53, 4));
    }

    void Build()
    {
        LoadMaterials();
        block = new MaterialPropertyBlock();
        Transform root = transform;
        void Part(Transform parent, string name, Vector3 center, Vector3 size, Material m) =>
            renderers.Add(BlockyModel.Box(parent, name, center, size, m));

        // Body: hips at the back, the torso running forward from them with a shoulder hump and a shaggy ruff
        hips = BlockyModel.Pivot(root, "Hips", new Vector3(0f, HipHeight, -1.1f));
        Part(hips, "Torso", new Vector3(0f, 0.12f, 0.72f), new Vector3(1.15f, 0.95f, 1.95f), fur);
        Part(hips, "Belly", new Vector3(0f, -0.38f, 0.78f), new Vector3(0.98f, 0.14f, 1.5f), darkFur);
        Part(hips, "Rump", new Vector3(0f, 0.1f, -0.12f), new Vector3(1.08f, 0.86f, 0.4f), fur);
        Part(hips, "Hump", new Vector3(0f, 0.66f, 1.2f), new Vector3(0.95f, 0.3f, 0.8f), grizzled);
        Part(hips, "Ruff", new Vector3(0f, 0.18f, 1.6f), new Vector3(1.24f, 0.98f, 0.34f), grizzled);
        tail = BlockyModel.Pivot(hips, "Tail", new Vector3(0f, 0.4f, -0.32f));
        Part(tail, "Tail Fur", new Vector3(0f, 0f, -0.06f), new Vector3(0.22f, 0.2f, 0.16f), fur);

        // Head: broad skull with cheek fluff, a pale muzzle and a jaw that opens to show the teeth
        head = BlockyModel.Pivot(hips, "Head", new Vector3(0f, 0.38f, 1.75f));
        Part(head, "Skull", new Vector3(0f, 0.05f, 0.12f), new Vector3(0.78f, 0.68f, 0.62f), fur);
        Part(head, "Brow", new Vector3(0f, 0.27f, 0.4f), new Vector3(0.7f, 0.1f, 0.08f), darkFur);
        Part(head, "Cheek L", new Vector3(-0.42f, -0.08f, 0.06f), new Vector3(0.1f, 0.38f, 0.42f), grizzled);
        Part(head, "Cheek R", new Vector3(0.42f, -0.08f, 0.06f), new Vector3(0.1f, 0.38f, 0.42f), grizzled);
        Part(head, "Snout", new Vector3(0f, -0.08f, 0.6f), new Vector3(0.44f, 0.26f, 0.36f), muzzle);
        Part(head, "Nose", new Vector3(0f, 0.0f, 0.79f), new Vector3(0.2f, 0.12f, 0.04f), nose);
        Part(head, "Mouth", new Vector3(0f, -0.21f, 0.58f), new Vector3(0.34f, 0.1f, 0.28f), mouth);
        Part(head, "Fang L", new Vector3(-0.13f, -0.24f, 0.72f), new Vector3(0.05f, 0.08f, 0.05f), claw);
        Part(head, "Fang R", new Vector3(0.13f, -0.24f, 0.72f), new Vector3(0.05f, 0.08f, 0.05f), claw);
        jawPivot = BlockyModel.Pivot(head, "Jaw", new Vector3(0f, -0.21f, 0.44f));
        Part(jawPivot, "Lower Jaw", new Vector3(0f, -0.06f, 0.16f), new Vector3(0.38f, 0.12f, 0.32f), muzzle);
        Part(jawPivot, "Tongue", new Vector3(0f, 0.005f, 0.14f), new Vector3(0.26f, 0.02f, 0.22f), mouth);
        for (int side = -1; side <= 1; side += 2)
        {
            string s = side < 0 ? "L" : "R";
            Part(head, "Eye " + s, new Vector3(side * 0.2f, 0.13f, 0.435f), new Vector3(0.1f, 0.1f, 0.02f), eye);
            Part(head, "Glint " + s, new Vector3(side * 0.2f + 0.025f, 0.155f, 0.447f), new Vector3(0.035f, 0.035f, 0.01f), glint);
            Part(head, "Ear " + s, new Vector3(side * 0.3f, 0.43f, 0.03f), new Vector3(0.2f, 0.18f, 0.1f), darkFur);
            Part(head, "Inner Ear " + s, new Vector3(side * 0.3f, 0.42f, 0.085f), new Vector3(0.1f, 0.1f, 0.02f), innerEar);
        }

        frontLeft = Leg("Front Leg L", new Vector3(-0.36f, 0f, 1.42f), true);
        frontRight = Leg("Front Leg R", new Vector3(0.36f, 0f, 1.42f), true);
        hindLeft = Leg("Hind Leg L", new Vector3(-0.37f, 0f, 0f), false);
        hindRight = Leg("Hind Leg R", new Vector3(0.37f, 0f, 0f), false);

        foreach (Renderer r in renderers) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
    }

    // A leg hanging from a hip or shoulder, fur fading into a dark paw with three claws
    Transform Leg(string name, Vector3 joint, bool front)
    {
        Transform pivot = BlockyModel.Pivot(hips, name, joint);
        void Part(string n, Vector3 c, Vector3 s, Material m) => renderers.Add(BlockyModel.Box(pivot, n, c, s, m));
        if (front)
        {
            Part("Upper", new Vector3(0f, -0.3f, 0f), new Vector3(0.42f, 0.66f, 0.46f), fur);
            Part("Lower", new Vector3(0f, -0.67f, 0f), new Vector3(0.4f, 0.16f, 0.44f), darkFur);
        }
        else
        {
            Part("Thigh", new Vector3(0f, -0.2f, 0f), new Vector3(0.46f, 0.6f, 0.62f), fur);
            Part("Lower", new Vector3(0f, -0.62f, 0.02f), new Vector3(0.38f, 0.3f, 0.42f), darkFur);
        }
        Part("Paw", new Vector3(0f, -LegLength + 0.09f, 0.05f), new Vector3(0.46f, 0.18f, 0.54f), darkFur);
        for (int i = -1; i <= 1; i++)
            Part("Claw", new Vector3(i * 0.14f, -LegLength + 0.04f, 0.35f), new Vector3(0.06f, 0.07f, 0.12f), claw);
        return pivot;
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        uprightNow = Mathf.Lerp(uprightNow, upright, 1f - Mathf.Exp(-uprightSharpness * dt));
        crouchNow = Mathf.Lerp(crouchNow, crouch, 1f - Mathf.Exp(-10f * dt));
        reachNow = Mathf.Lerp(reachNow, reach, 1f - Mathf.Exp(-12f * dt));
        swipeNow = Mathf.Lerp(swipeNow, swipe, 1f - Mathf.Exp(-18f * dt));
        jawNow = Mathf.Lerp(jawNow, jaw, 1f - Mathf.Exp(-14f * dt));
        tiltNow = Mathf.Lerp(tiltNow, headTilt, 1f - Mathf.Exp(-8f * dt));
        kickNow = Mathf.Lerp(kickNow, airborne ? 1f : 0f, 1f - Mathf.Exp(-10f * dt));

        float stand = Mathf.Clamp01(uprightNow);
        float biped = Mathf.SmoothStep(0f, 1f, (stand - 0.5f) / 0.4f);
        // Longer strides on two legs, so the feet don't blur
        float stride = Mathf.Lerp(2.2f, 2.8f, biped);
        phase += speed / stride * Mathf.PI * 2f * dt;
        float amount = Mathf.Clamp01(speed / 5f);
        float gallop = Mathf.SmoothStep(0f, 1f, (speed - 2.5f) / 2f) * (1f - biped);

        // Walk: diagonal legs together. Gallop: front pair then back pair. Biped: hind legs only, arms pumping.
        float s0 = Mathf.Sin(phase), s1 = Mathf.Sin(phase + Mathf.PI);
        float frontL = Mathf.Lerp(s0, Mathf.Sin(phase), gallop), frontR = Mathf.Lerp(s1, Mathf.Sin(phase + 0.6f), gallop);
        float hindL = Mathf.Lerp(s1, Mathf.Sin(phase + Mathf.PI), gallop), hindR = Mathf.Lerp(s0, Mathf.Sin(phase + Mathf.PI + 0.6f), gallop);
        float legSwing = Mathf.Lerp(28f, 34f, gallop) * amount;

        // Hips: rear up around them, lean into a two-legged sprint, bob with each step, sink when crouching
        float lean = biped * Mathf.Clamp01(speed / 9f) * 16f;
        float hipPitch = -uprightNow * 80f + lean + crouchNow * 6f + gallop * amount * Mathf.Sin(phase * 1f) * 4f;
        float bob = Mathf.Abs(Mathf.Sin(phase)) * Mathf.Lerp(0.06f, 0.14f, biped) * amount;
        float crouchDrop = crouchNow * LegLength * (1f - Mathf.Cos(35f * Mathf.Deg2Rad));
        float sway = biped * amount * Mathf.Sin(phase) * 5f;
        // Standing up, the hips move under the middle of the body so the bear stays over its feet
        hips.localPosition = new Vector3(0f, HipHeight + bob - crouchDrop, Mathf.Lerp(-1.1f, -0.3f, biped));
        hips.localRotation = Quaternion.Euler(hipPitch, 0f, sway);

        // Hind legs stay under the hips however the body tips
        float hindSwing = Mathf.Lerp(legSwing, 55f * amount, biped);
        float kick = kickNow * 45f;
        hindLeft.localRotation = Quaternion.Euler(hindL * hindSwing + crouchNow * 35f + kick - hipPitch, 0f, 0f);
        hindRight.localRotation = Quaternion.Euler(hindR * hindSwing + crouchNow * 35f + kick - hipPitch, 0f, 0f);

        // Front legs: walking legs on all fours, pumping arms when upright, reaching forward or overhead on cue
        float armBase = biped * -25f;
        float armSwing = Mathf.Lerp(legSwing, 45f * amount, biped);
        float reachAngle = reachNow * -160f;
        float leftWorld = armBase + frontL * armSwing * (1f - Mathf.Abs(reachNow)) - crouchNow * 35f + reachAngle;
        float rightWorld = armBase + frontR * armSwing * (1f - Mathf.Abs(reachNow)) - crouchNow * 35f + reachAngle - swipeNow * 120f;
        // Raised arms spread out to the sides, clear of the face
        float spread = Mathf.SmoothStep(0f, 1f, (reachNow - 0.6f) / 0.4f) * 38f;
        frontLeft.localRotation = Quaternion.Euler(leftWorld - hipPitch, 0f, -spread);
        frontRight.localRotation = Quaternion.Euler(rightWorld - hipPitch, 0f, spread + swipeNow * 45f);

        // Head stays level, nods along with the gait, droops a bit on all fours
        float nod = Mathf.Sin(phase * 2f) * 3f * amount;
        float headPitch = -hipPitch * 0.9f + (1f - stand) * 8f + nod + tiltNow;
        float look = (1f - biped) * Mathf.Sin(phase * 0.5f) * 4f * amount;
        head.localRotation = Quaternion.Euler(headPitch, look, 0f);
        jawPivot.localRotation = Quaternion.Euler(jawNow * 35f, 0f, 0f);
        tail.localRotation = Quaternion.Euler(-20f + Mathf.Sin(phase * 2f) * 15f * amount, 0f, 0f);

        // Dying: topple onto its side
        transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.SmoothStep(0f, 90f, death));
        transform.localPosition = new Vector3(0f, -death * 0.3f, 0f);

        UpdateFlash();
    }

    void UpdateFlash()
    {
        float t = flashUntil - Time.time;
        Color tint = t > 0f ? Color.Lerp(Color.white, flashColor, Mathf.Clamp01(t * 6f)) : Color.white;
        block.SetColor("_BaseColor", tint);
        block.SetColor("_Color", tint);
        foreach (Renderer r in renderers) r.SetPropertyBlock(block);
    }
}
