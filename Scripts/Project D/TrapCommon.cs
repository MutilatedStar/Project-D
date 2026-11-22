using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using Ilumisoft.HealthSystem;

// Shared pieces for ArrowTrap, LaserTrap, TileRemoveTrap, TrapRoomCalculator and TrapPlacer. Nothing in here goes on an object.

/// <summary>Which way a trap shoots. Custom uses the angle next to it (0 = right, 90 = up).</summary>
public enum TrapDirection { Right, Up, Left, Down, Custom }

/// <summary>Timed: goes off by itself over and over. Proximity: goes off when a player comes close. Manual: only when Trigger() is called.</summary>
public enum TrapMode { Timed, Proximity, Manual }

/// <summary>Where a trap is in its cycle: waiting, showing the warning, or going off.</summary>
public enum TrapPhase { Idle, Warning, Firing }

/// <summary>Reads the floor through FloorManager, so every trap agrees with the foe spawner about where the floor is.</summary>
public static class TrapFloor
{
    private static FloorManager manager;
    private static Sprite whiteSprite;
    private static Material spriteMaterial;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        manager = null;
        whiteSprite = null;
        spriteMaterial = null;
    }

    /// <summary>The FloorManager in the scene (found once, found again if it goes away). Null if there is none.</summary>
    public static FloorManager Manager
    {
        get
        {
            if (manager == null) manager = UnityEngine.Object.FindAnyObjectByType<FloorManager>();
            return manager;
        }
    }

    /// <summary>FloorManager's "Floor Tilemaps" list (empty array if none are set).</summary>
    public static Tilemap[] FloorMaps
    {
        get
        {
            FloorManager m = Manager;
            return m != null ? m.FloorTilemapList : System.Array.Empty<Tilemap>();
        }
    }

    /// <summary>The list you gave the trap, or the floor Tilemaps from FloorManager when that list is empty.</summary>
    public static Tilemap[] MapsOrFloor(Tilemap[] overrideMaps)
    {
        if (overrideMaps != null && overrideMaps.Length > 0) return overrideMaps;
        return FloorMaps;
    }

    /// <summary>The Grid the floor sits on: the one of the first usable Tilemap, else any Grid in the scene.</summary>
    public static Grid GridFor(Tilemap[] overrideMaps)
    {
        Tilemap[] maps = MapsOrFloor(overrideMaps);
        for (int i = 0; i < maps.Length; i++)
        {
            if (maps[i] != null && maps[i].layoutGrid != null) return maps[i].layoutGrid;
        }
        return UnityEngine.Object.FindAnyObjectByType<Grid>();
    }

    /// <summary>A plain white one-unit square, for traps that have no sprite of their own yet (placeholder art).</summary>
    public static Sprite WhiteSprite
    {
        get
        {
            if (whiteSprite == null)
            {
                Texture2D t = Texture2D.whiteTexture;
                whiteSprite = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width);
                whiteSprite.name = "Trap White";
            }
            return whiteSprite;
        }
    }

    /// <summary>The material the laser beam uses when you don't give it one. Null if the shader can't be found.</summary>
    public static Material SpriteMaterial
    {
        get
        {
            if (spriteMaterial == null)
            {
                Shader s = Shader.Find("Sprites/Default");
                if (s != null) spriteMaterial = new Material(s);
            }
            return spriteMaterial;
        }
    }
}

/// <summary>Finding and testing the players, with no layers or collision matrix needed.</summary>
public static class TrapPlayers
{
    public const int MaxPlayers = 4;

    private static readonly Dictionary<CoopHealth, Collider2D> colliders = new Dictionary<CoopHealth, Collider2D>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        colliders.Clear();
    }

    /// <summary>Alive and counts (Single Player Mode), jumping or not.</summary>
    public static bool InPlay(CoopHealth p)
    {
        return p != null && p.IsAlive && CoopRules.Counts(p.PlayerNumber);
    }

    /// <summary>Alive, counts, and on the ground: a jumping player flies over arrows, lasers and holes (CoopHealth.Airborne).</summary>
    public static bool CanBeHurt(CoopHealth p)
    {
        return InPlay(p) && !p.Airborne;
    }

    /// <summary>The box of the player's solid collider (a trigger only if that is all they have), or a small box on them.</summary>
    public static Bounds BoundsOf(CoopHealth p)
    {
        Collider2D c;
        if (!colliders.TryGetValue(p, out c) || c == null)
        {
            if (colliders.Count > 16) colliders.Clear();
            c = Pick(p.gameObject);
            colliders[p] = c;
        }

        if (c != null && c.enabled) return c.bounds;
        return new Bounds(p.transform.position, new Vector3(0.5f, 0.5f, 0.5f));
    }

    private static Collider2D Pick(GameObject go)
    {
        Collider2D[] all = go.GetComponentsInChildren<Collider2D>();
        Collider2D fallback = null;
        for (int i = 0; i < all.Length; i++)
        {
            Collider2D c = all[i];
            if (!c.enabled) continue;
            if (!c.isTrigger) return c;
            if (fallback == null) fallback = c;
        }
        return fallback;
    }
}

/// <summary>Small bits of maths and physics the traps share.</summary>
public static class TrapGeometry
{
    private static readonly RaycastHit2D[] rayBuffer = new RaycastHit2D[8];
    private static readonly Collider2D[] pointBuffer = new Collider2D[2];

    public static Vector2 DirectionOf(TrapDirection direction, float customAngleDegrees)
    {
        switch (direction)
        {
            case TrapDirection.Right: return Vector2.right;
            case TrapDirection.Up: return Vector2.up;
            case TrapDirection.Left: return Vector2.left;
            case TrapDirection.Down: return Vector2.down;
            default:
                float r = customAngleDegrees * Mathf.Deg2Rad;
                return new Vector2(Mathf.Cos(r), Mathf.Sin(r));
        }
    }

    public static float AngleOf(Vector2 v)
    {
        return Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
    }

    public static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(r);
        float sin = Mathf.Sin(r);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }

    /// <summary>The first solid collider on the mask at this point (the wall an emitter sits in), or null. Triggers are ignored.</summary>
    public static Collider2D SolidAt(Vector2 point, LayerMask mask)
    {
        if (mask.value == 0) return null;

        ContactFilter2D filter = new ContactFilter2D();
        filter.SetLayerMask(mask);
        filter.useTriggers = false;
        int n = Physics2D.OverlapPoint(point, filter, pointBuffer);
        return n > 0 ? pointBuffer[0] : null;
    }

    /// <summary>How far a ray goes before it hits a solid collider on the mask (maxDistance if it hits nothing). "ignore" is skipped:
    /// pass the wall the emitter sits in, so the shot doesn't stop at its own wall.</summary>
    public static float FirstSolidDistance(Vector2 origin, Vector2 direction, float maxDistance, LayerMask mask, Collider2D ignore)
    {
        if (mask.value == 0) return maxDistance;

        ContactFilter2D filter = new ContactFilter2D();
        filter.SetLayerMask(mask);
        filter.useTriggers = false;
        int n = Physics2D.Raycast(origin, direction, filter, rayBuffer, maxDistance);
        for (int i = 0; i < n; i++)
        {
            Collider2D c = rayBuffer[i].collider;
            if (c == null || c == ignore) continue;
            return rayBuffer[i].distance;
        }
        return maxDistance;
    }

    /// <summary>Does the line from a to b touch this box when the box is grown by pad on every side? distanceAlong is how far along the
    /// line (from a) it first touches. This is how arrows and lasers hit players without any physics layers.</summary>
    public static bool SegmentHitsBounds(Vector2 a, Vector2 b, Bounds bounds, float pad, out float distanceAlong)
    {
        distanceAlong = 0f;

        float minX = bounds.min.x - pad;
        float maxX = bounds.max.x + pad;
        float minY = bounds.min.y - pad;
        float maxY = bounds.max.y + pad;

        float dx = b.x - a.x;
        float dy = b.y - a.y;
        float tMin = 0f;
        float tMax = 1f;

        if (!ClipAxis(a.x, dx, minX, maxX, ref tMin, ref tMax)) return false;
        if (!ClipAxis(a.y, dy, minY, maxY, ref tMin, ref tMax)) return false;

        distanceAlong = tMin * Mathf.Sqrt(dx * dx + dy * dy);
        return true;
    }

    // One axis of the "slab" test: narrows the part of the line (tMin..tMax) that is inside lo..hi.
    private static bool ClipAxis(float origin, float delta, float lo, float hi, ref float tMin, ref float tMax)
    {
        if (Mathf.Abs(delta) < 0.00001f) return origin >= lo && origin <= hi;

        float t1 = (lo - origin) / delta;
        float t2 = (hi - origin) / delta;
        if (t1 > t2)
        {
            float swap = t1;
            t1 = t2;
            t2 = swap;
        }

        if (t1 > tMin) tMin = t1;
        if (t2 < tMax) tMax = t2;
        return tMin <= tMax;
    }
}

/// <summary>The one way a trap hurts a player.</summary>
public static class TrapDamage
{
    /// <summary>Hurts the player. mediumDamage is the MEDIUM value: it is multiplied by DifficultyRules.TrapDamageScale (Easy x0.5, Medium x1,
    /// Hard x2, same as the spike tiles). It goes through CoopHealth.ApplyDamage, so Invincible, Grace and the CoopRules still apply.</summary>
    public static void Hurt(CoopHealth player, float mediumDamage, string source, bool log)
    {
        if (player == null) return;

        float amount = mediumDamage * DifficultyRules.TrapDamageScale;
        if (amount <= 0f) return;

        if (log) Debug.Log("Trap: Player " + player.PlayerNumber + " hurt by " + source + " for " + amount + ".", player);
        player.ApplyDamage(amount);
    }
}
