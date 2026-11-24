using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Ilumisoft.HealthSystem;

/// <summary>
/// ENDING SCREENS. Put this on ONE object in the game scene (an empty object called "EndScreens", or the one FloorManager is on).
/// FloorManager finds it by itself, or you can drag it into FloorManager's "End Screens" slot.
///
///   ShowVictory()   loads the Ending scene. FloorManager calls it when the team goes down past the last floor (WinRun).
///   ShowFailure()   loads the Failure Ending scene. Nothing calls it by default: LightsOutScreen already handles everybody being
///                   knocked down. Tick "Load Failure When Team Wiped" if you want this script to do it instead, or call it from
///                   your own script.
///
/// Both scenes must be added in File > Build Settings (Add Open Scenes), or the load is refused and an error is printed.
/// Needs CoopHealth.cs (only for the optional team-wiped hookup).
/// </summary>
public class EndScreens : MonoBehaviour
{
    [Header("Scenes (must be in File > Build Settings)")]
    [Tooltip("Scene loaded on a win.")]
    [SerializeField] private string endingScene = "Ending";
    [Tooltip("Scene loaded on a loss.")]
    [SerializeField] private string failureEndingScene = "Failure Ending";

    [Header("Timing")]
    [Tooltip("Seconds to wait before the scene loads. Counted in real time, so it still works while the game is paused.")]
    [SerializeField, Min(0f)] private float delay = 0f;

    [Header("Failure")]
    [Tooltip("When every player that counts is at 0 health (CoopHealth.TeamWiped), load the Failure Ending scene. Leave OFF while " +
             "LightsOutScreen is the one ending the game, or the two would fight over which screen shows.")]
    [SerializeField] private bool loadFailureWhenTeamWiped = false;

    private bool loading;

    private void OnEnable()
    {
        CoopHealth.TeamWiped += HandleTeamWiped;
    }

    private void OnDisable()
    {
        CoopHealth.TeamWiped -= HandleTeamWiped;
    }

    private void HandleTeamWiped()
    {
        if (loadFailureWhenTeamWiped) ShowFailure();
    }

    /// <summary>Loads the Ending scene (the win).</summary>
    public void ShowVictory()
    {
        Load(endingScene);
    }

    /// <summary>Loads the Failure Ending scene (the loss).</summary>
    public void ShowFailure()
    {
        Load(failureEndingScene);
    }

    private void Load(string sceneName)
    {
        if (loading) return;   // a win and a wipe in the same moment must not load two scenes

        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogWarning("EndScreens: the scene name is empty, so nothing was loaded.", this);
            return;
        }

        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogError("EndScreens: the scene '" + sceneName + "' is not in File > Build Settings (or the name is spelled differently), so it can't be loaded.", this);
            return;
        }

        loading = true;
        StartCoroutine(LoadRoutine(sceneName));
    }

    private IEnumerator LoadRoutine(string sceneName)
    {
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);

        Time.timeScale = 1f;   // a paused game would carry its frozen time into the next scene
        SceneManager.LoadScene(sceneName);
    }
}
