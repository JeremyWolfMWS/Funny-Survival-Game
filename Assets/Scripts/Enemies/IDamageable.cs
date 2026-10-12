using UnityEngine;

/// <summary>Something the player can hit, like an enemy. Found on a collider's object or one of its parents.</summary>
public interface IDamageable
{
    bool IsAlive { get; }

    /// <summary>A hit for amount damage with weapon (null = bare hand), pushed by the knockback velocity.</summary>
    void TakeHit(float amount, Vector3 knockback, ItemDefinition weapon);
}
