using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Holds a frozen foe's sprite and animation still for as long as the freeze lasts, then puts them back.
///
/// SwordController adds this by itself the moment the Frost Sword freezes a foe (FrozenFoeHold.Hold), so you never add it by hand and
/// there is nothing to set up. It sits on the same object as the foe's FoeController and switches itself off when the freeze is over.
///
/// WHILE FROZEN
///   - every Animator on the foe runs at speed 0, so the walk / attack animation stops on the frame it was on and does not move on.
///   - every SpriteRenderer on the foe keeps the sprite (and flip) it had when it froze, even if a script tries to swap it.
///
/// WHEN IT THAWS (the time runs out, or the foe dies, or this object is switched off)
///   - the animators go back to the speed they should have, so the animation carries on from where it stopped.
///   - the sprites go back to what the foe's own scripts / animator wanted to show during the freeze (the state "around it"), not to
///     the frozen frame. So a foe that turned to face you while frozen thaws facing you, and a foe that died shows its death.
///
/// The countdown follows the game like the foe's own timers: it stops while the notebook / a menu is open (FoeController.IsPaused).
/// Colours are never touched here, so the blue tint, the crit flash and the lava / damage effects stay with the scripts that own them.
/// </summary>
[DisallowMultipleComponent]
public class FrozenFoeHold : MonoBehaviour
{
    private const float MinSpeed = 0.0001f;

    private FoeController foe;
    private float timeLeft;
    private bool holding;

    private readonly List<Animator> animators = new List<Animator>();
    private readonly List<float> animatorSpeeds = new List<float>();         // the speed each animator gets back on thaw

    private readonly List<SpriteRenderer> renderers = new List<SpriteRenderer>();
    private readonly List<Sprite> heldSprites = new List<Sprite>();          // what is shown while frozen
    private readonly List<bool> heldFlipX = new List<bool>();
    private readonly List<Sprite> wantedSprites = new List<Sprite>();        // what the foe's own scripts last asked for
    private readonly List<bool> wantedFlipX = new List<bool>();

    /// <summary>Freezes this foe's sprite and animation for this many seconds. Safe to call again on a foe that is already held: the
    /// time is only ever extended, never cut short.</summary>
    public static void Hold(FoeController target, float seconds)
    {
        if (target == null || target.IsDead || seconds <= 0f) return;

        FrozenFoeHold hold = target.GetComponent<FrozenFoeHold>();
        if (hold == null) hold = target.gameObject.AddComponent<FrozenFoeHold>();
        hold.Begin(target, seconds);
    }

    private void Begin(FoeController target, float seconds)
    {
        foe = target;
        timeLeft = Mathf.Max(timeLeft, seconds);
        if (holding) return;   // already held: only the timer was extended

        Collect();
        holding = true;
        enabled = true;
    }

    // Finds everything on the whole foe (its Root, so a sprite on a child object counts) and stops it right now.
    private void Collect()
    {
        animators.Clear();
        animatorSpeeds.Clear();
        renderers.Clear();
        heldSprites.Clear();
        heldFlipX.Clear();
        wantedSprites.Clear();
        wantedFlipX.Clear();

        GameObject root = RootOf(foe);

        Animator[] foundAnimators = root.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < foundAnimators.Length; i++)
        {
            Animator a = foundAnimators[i];
            // A speed of 0 here would mean something else already froze it; thaw to normal speed then, never to a permanent stop.
            animators.Add(a);
            animatorSpeeds.Add(a.speed > MinSpeed ? a.speed : 1f);
            a.speed = 0f;
        }

        SpriteRenderer[] foundRenderers = root.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < foundRenderers.Length; i++)
        {
            SpriteRenderer r = foundRenderers[i];
            renderers.Add(r);
            heldSprites.Add(r.sprite);
            heldFlipX.Add(r.flipX);
            wantedSprites.Add(r.sprite);
            wantedFlipX.Add(r.flipX);
        }
    }

    // FoeController.Root is the whole foe (the same object FloorManager destroys). It is read as a plain Object so this works
    // whether Root is a GameObject or a Transform; the foe's own object is the fallback.
    private static GameObject RootOf(FoeController f)
    {
        UnityEngine.Object o = f.Root;

        GameObject go = o as GameObject;
        if (go == null)
        {
            Component c = o as Component;
            if (c != null) go = c.gameObject;
        }
        return go != null ? go : f.gameObject;
    }

    private void Update()
    {
        if (!holding)
        {
            enabled = false;
            return;
        }

        if (foe == null || foe.IsDead)
        {
            Release();   // a foe that dies while frozen goes on to its death, not stuck on the frozen frame
            return;
        }

        if (FoeController.IsPaused) return;   // notebook / menus: the freeze does not run down

        timeLeft -= Time.deltaTime;
        if (timeLeft <= 0f) Release();
    }

    // After the Animator has written this frame's sprite and after every Update: remember what the foe WANTED to show, then show the
    // held sprite instead. The Animator is kept at speed 0 too; if the foe's own code changes its speed, that value is kept for the thaw.
    private void LateUpdate()
    {
        if (!holding) return;

        for (int i = 0; i < animators.Count; i++)
        {
            Animator a = animators[i];
            if (a == null) continue;

            if (a.speed > MinSpeed) animatorSpeeds[i] = a.speed;
            a.speed = 0f;
        }

        for (int i = 0; i < renderers.Count; i++)
        {
            SpriteRenderer r = renderers[i];
            if (r == null) continue;

            wantedSprites[i] = r.sprite;
            wantedFlipX[i] = r.flipX;

            r.sprite = heldSprites[i];
            r.flipX = heldFlipX[i];
        }
    }

    private void OnDisable()
    {
        Release();   // never leave a foe stopped if it is switched off or pooled while frozen
    }

    // The freeze is over: animation runs again, and the sprite goes to whatever the foe would be showing anyway.
    private void Release()
    {
        if (!holding) return;
        holding = false;
        timeLeft = 0f;

        for (int i = 0; i < animators.Count; i++)
        {
            Animator a = animators[i];
            if (a != null) a.speed = animatorSpeeds[i] > MinSpeed ? animatorSpeeds[i] : 1f;
        }

        for (int i = 0; i < renderers.Count; i++)
        {
            SpriteRenderer r = renderers[i];
            if (r == null) continue;

            r.sprite = wantedSprites[i];
            r.flipX = wantedFlipX[i];
        }

        enabled = false;
    }
}
