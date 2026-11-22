using UnityEngine;
using Ilumisoft.HealthSystem;

/// <summary>
/// One arrow in flight. ArrowTrap makes these; you never put one in the scene yourself (but an Arrow Prefab on the trap can already
/// carry this script, or just a sprite, and it will be set up either way).
///
/// It flies in a straight line and stops when it:
///   - hits a player that counts (hurts them once, then is gone),
///   - hits a solid collider on the trap's Obstacle Mask (a wall),
///   - has flown its Max Range,
///   - or the floor changes / is restarted.
/// Players are found by their own collider box, so no layers and no collision matrix are needed.
/// </summary>
public class TrapArrow : MonoBehaviour
{
    private Vector2 direction = Vector2.right;
    private float speed;
    private float radius;
    private float damage;
    private float remaining;
    private LayerMask obstacleMask;
    private Collider2D ignoredWall;   // the wall the arrow was launched out of: it must not stop at its own wall
    private bool log;
    private bool launched;

    /// <summary>Starts the arrow. damage is the MEDIUM value (the difficulty scale is applied when it hits).</summary>
    public void Launch(Vector2 aim, float arrowSpeed, float hitRadius, float mediumDamage, float range, LayerMask obstacles,
                       Sprite sprite, Vector2 size, Color color, int sortingOrder, bool logHits)
    {
        direction = aim.sqrMagnitude > 0.0001f ? aim.normalized : Vector2.right;
        speed = arrowSpeed;
        radius = hitRadius;
        damage = mediumDamage;
        remaining = range;
        obstacleMask = obstacles;
        log = logHits;

        transform.right = new Vector3(direction.x, direction.y, 0f);
        ignoredWall = TrapGeometry.SolidAt(transform.position, obstacleMask);

        // A prefab brings its own picture. With none, a plain bar stands in so the arrow can always be seen.
        if (GetComponentInChildren<SpriteRenderer>() == null)
        {
            SpriteRenderer sr = gameObject.AddComponent<SpriteRenderer>();
            sr.sprite = sprite != null ? sprite : TrapFloor.WhiteSprite;
            sr.color = color;
            sr.sortingOrder = sortingOrder;
            if (sprite == null) transform.localScale = new Vector3(size.x, size.y, 1f);
        }

        launched = true;
    }

    private void OnEnable()
    {
        FloorController.FloorChanged += HandleFloorChanged;
        FloorManager.OnFloorRestarted += HandleFloorRestarted;
    }

    private void OnDisable()
    {
        FloorController.FloorChanged -= HandleFloorChanged;
        FloorManager.OnFloorRestarted -= HandleFloorRestarted;
    }

    private void HandleFloorChanged(int floor)
    {
        Destroy(gameObject);
    }

    private void HandleFloorRestarted()
    {
        Destroy(gameObject);
    }

    private void Update()
    {
        if (!launched || TrapBase.Paused) return;

        Vector2 from = transform.position;
        float step = Mathf.Min(speed * Time.deltaTime, remaining);

        // How far it can go this frame before a wall stops it.
        float reach = TrapGeometry.FirstSolidDistance(from, direction, step, obstacleMask, ignoredWall);
        bool hitWall = reach < step;
        Vector2 to = from + direction * reach;

        // The nearest player the arrow touches on the way.
        CoopHealth victim = null;
        float best = float.MaxValue;
        for (int n = 1; n <= TrapPlayers.MaxPlayers; n++)
        {
            CoopHealth p = CoopHealth.Get(n);
            if (!TrapPlayers.CanBeHurt(p)) continue;

            float along;
            if (TrapGeometry.SegmentHitsBounds(from, to, TrapPlayers.BoundsOf(p), radius, out along) && along < best)
            {
                best = along;
                victim = p;
            }
        }

        if (victim != null)
        {
            TrapDamage.Hurt(victim, damage, "an arrow", log);
            Destroy(gameObject);
            return;
        }

        transform.position = new Vector3(to.x, to.y, transform.position.z);
        remaining -= reach;

        if (hitWall || remaining <= 0.001f) Destroy(gameObject);
    }
}
