using UnityEngine;

/// <summary>
/// Health (2 per heart), fall damage, damaging blocks, drowning and slow regeneration. On death the inventory is
/// dropped and the player respawns. Sneaking on a damaging block keeps your feet safe.
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class PlayerHealth : MonoBehaviour
{
    public float maxHealth = 20f;
    [Tooltip("Falls up to this many blocks are harmless. Each block beyond does 1 damage.")]
    public float safeFallDistance = 3f;
    [Tooltip("Seconds of air underwater before drowning starts.")]
    public float maxAir = 15f;
    public float drowningDamage = 2f;
    [Tooltip("Seconds without damage before regeneration starts.")]
    public float regenDelay = 5f;
    [Tooltip("Seconds per regenerated health point.")]
    public float regenInterval = 2f;
    [Tooltip("Seconds of invulnerability after being hurt.")]
    public float hurtCooldown = 0.5f;

    public float Health { get; private set; }
    public float Air { get; private set; }

    PlayerController controller;
    PlayerInventory inventory;
    float lastHurtTime = -100f;
    float regenTimer, drownTimer, contactTimer, burnTimer;
    float deathMessageTimer;
    GUIStyle messageStyle;

    void Awake()
    {
        controller = GetComponent<PlayerController>();
        inventory = GetComponent<PlayerInventory>();
        Health = maxHealth;
        Air = maxAir;
        controller.Landed += OnLanded;
    }

    void OnDestroy()
    {
        if (controller != null) controller.Landed -= OnLanded;
    }

    void OnLanded(float distance)
    {
        float damage = Mathf.Floor(distance - safeFallDistance);
        if (damage > 0f) Damage(damage, true);
    }

    /// <summary>No damage at all, for testing (the /god command).</summary>
    public bool Invulnerable { get; set; }

    /// <summary>
    /// A hit from an enemy: hurts, knocks back and shakes the view, unless the player dodges it at the start of a dash.
    /// Returns whether it landed.
    /// </summary>
    public bool Hurt(float amount, Vector3 knockback)
    {
        if (Health <= 0f || Invulnerable || controller.IsDodging) return false;
        if (Time.time - lastHurtTime < hurtCooldown) return false;
        controller.AddImpulse(knockback);
        controller.Shake(0.3f + Mathf.Clamp01(amount / 8f) * 0.7f);
        Damage(amount);
        return true;
    }

    public void Kill()
    {
        if (Health <= 0f) return;
        Health = 0f;
        Die();
    }

    /// <summary>Hurts the player. Ignored during the short cooldown after the last hit unless forced.</summary>
    public void Damage(float amount, bool ignoreCooldown = false)
    {
        if (amount <= 0f || Health <= 0f || Invulnerable) return;
        if (!ignoreCooldown && Time.time - lastHurtTime < hurtCooldown) return;
        lastHurtTime = Time.time;
        Health = Mathf.Max(0f, Health - amount);
        if (Health <= 0f) Die();
    }

    public void Heal(float amount) => Health = Mathf.Min(maxHealth, Health + amount);

    void Update()
    {
        if (controller.world == null) return;
        float dt = Time.deltaTime;
        deathMessageTimer -= dt;

        UpdateContactDamage(dt);
        UpdateHeldBurn(dt);
        UpdateAir(dt);

        if (Health < maxHealth && Time.time - lastHurtTime > regenDelay)
        {
            regenTimer += dt;
            if (regenTimer >= regenInterval)
            {
                regenTimer = 0f;
                Heal(1f);
            }
        }
        else regenTimer = 0f;
    }

    void UpdateContactDamage(float dt)
    {
        VoxelWorld world = controller.world;
        Vector3 feet = transform.position;
        float dps = 0f;
        if (!(controller.IsSneaking && controller.IsGrounded))
            dps = Mathf.Max(dps, world.GetBlockDefinition(Vector3Int.FloorToInt(feet + Vector3.down * 0.05f))?.damagePerSecond ?? 0f);
        // Blocks overlapping the body, such as the inside of a slope
        dps = Mathf.Max(dps, world.GetBlockDefinition(Vector3Int.FloorToInt(feet + Vector3.up * 0.1f))?.damagePerSecond ?? 0f);
        dps = Mathf.Max(dps, world.GetBlockDefinition(Vector3Int.FloorToInt(feet + Vector3.up * 1.2f))?.damagePerSecond ?? 0f);

        if (dps <= 0f)
        {
            contactTimer = 0f;
            return;
        }
        contactTimer += dt;
        if (contactTimer >= hurtCooldown)
        {
            Damage(dps * contactTimer);
            contactTimer = 0f;
        }
    }

    ItemDefinition HeldItem => inventory != null && inventory.Inventory != null ? inventory.SelectedInfo?.definition : null;

    // Items like uranium tools burn the hand holding them, once a second
    void UpdateHeldBurn(float dt)
    {
        float burn = HeldItem?.heldBurnPerSecond ?? 0f;
        if (burn <= 0f)
        {
            burnTimer = 0f;
            return;
        }
        burnTimer += dt;
        if (burnTimer < 1f) return;
        Damage(burn * burnTimer, true);
        burnTimer = 0f;
    }

    void UpdateAir(float dt)
    {
        if (!controller.IsHeadUnderwater)
        {
            Air = Mathf.Min(maxAir, Air + dt * maxAir * 0.5f);
            drownTimer = 0f;
            return;
        }

        Air = Mathf.Max(0f, Air - dt);
        if (Air > 0f) return;
        drownTimer += dt;
        if (drownTimer >= 1f)
        {
            drownTimer = 0f;
            Damage(drowningDamage, true);
        }
    }

    void Die()
    {
        if (inventory != null && inventory.Inventory != null) inventory.DropAll();
        controller.Teleport(controller.SpawnPoint);
        Health = maxHealth;
        Air = maxAir;
        lastHurtTime = -100f;
        deathMessageTimer = 3f;
        if (inventory != null) inventory.GiveStarterItems();
    }

    void OnGUI()
    {
        float scale = Screen.height / 720f;
        float slot = InventoryUI.HotbarSlotSize * scale;
        float hotbarWidth = Inventory.HotbarSize * slot;
        float x0 = (Screen.width - hotbarWidth) * 0.5f;
        float size = 12f * scale, gap = 2f * scale;
        float y = Screen.height - slot - 8f * scale - size - 6f * scale;

        // Hearts: full, half or empty
        for (int i = 0; i < 10; i++)
        {
            var r = new Rect(x0 + i * (size + gap), y, size, size);
            GUI.color = new Color(0.15f, 0f, 0f, 0.8f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            float fill = Mathf.Clamp01(Mathf.Ceil(Health) / 2f - i);
            if (fill <= 0f) continue;
            GUI.color = Time.time - lastHurtTime < 0.3f ? Color.white : new Color(0.85f, 0.1f, 0.1f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * (fill >= 1f ? 1f : 0.5f), r.height), Texture2D.whiteTexture);
        }

        // Dash recharging: a thin bar under the crosshair
        float charge = controller.DashCharge;
        if (charge < 1f)
        {
            float w = 28f * scale, h = 3f * scale;
            var bar = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.5f + 14f * scale, w, h);
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * charge, bar.height), Texture2D.whiteTexture);
        }

        // Air bubbles while underwater
        if (Air < maxAir)
        {
            int bubbles = Mathf.CeilToInt(Air / maxAir * 10f);
            for (int i = 0; i < bubbles; i++)
            {
                var r = new Rect(x0 + hotbarWidth - (i + 1) * (size + gap), y, size, size);
                GUI.color = new Color(0.4f, 0.7f, 1f, 0.9f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
            }
        }

        // A held glowing item lights the screen edges, throbbing like heat
        Color glow = HeldItem?.glow ?? Color.clear;
        if (glow.a > 0f)
        {
            float pulse = 0.65f + 0.35f * Mathf.Sin(Time.time * 4f) * Mathf.Sin(Time.time * 1.3f + 1f);
            float band = Screen.height * 0.035f;
            for (int i = 0; i < 6; i++)
            {
                GUI.color = new Color(glow.r, glow.g, glow.b, glow.a * pulse * 0.22f * (1f - i / 6f));
                float inset = i * band;
                float w = Screen.width - 2f * inset, h = Screen.height - 2f * inset;
                GUI.DrawTexture(new Rect(inset, inset, w, band), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(inset, Screen.height - inset - band, w, band), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(inset, inset + band, band, h - 2f * band), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(Screen.width - inset - band, inset + band, band, h - 2f * band), Texture2D.whiteTexture);
            }
        }

        if (controller.IsHeadUnderwater)
        {
            GUI.color = new Color(0.1f, 0.25f, 0.6f, 0.35f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        }

        if (Time.time - lastHurtTime < 0.25f)
        {
            GUI.color = new Color(1f, 0f, 0f, 0.2f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        }

        if (deathMessageTimer > 0f)
        {
            messageStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            messageStyle.fontSize = Mathf.RoundToInt(36f * scale);
            GUI.color = new Color(1f, 0.3f, 0.3f, Mathf.Clamp01(deathMessageTimer));
            GUI.Label(new Rect(0f, Screen.height * 0.3f, Screen.width, 60f * scale), "You died!", messageStyle);
        }
        GUI.color = Color.white;
    }
}
