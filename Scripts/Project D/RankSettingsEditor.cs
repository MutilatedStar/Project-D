#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The Inspector for RankSettings. Everything looks as before except the list of per-room rows, which becomes a table:
///
///   Room [3]  Fast s [45]  High [400]  Gems [-1]        one short line per room, a box left at 0 (Gems: -1) keeps the default
///   grey text under it: exactly what that row works out to (P / A requirements and every time and score tier)
///   More: the stage id, a name, and "Set every number myself" for full control of a row
///
/// At the bottom, "Try a result" asks "what rank would 55 seconds and 400 points give in room 3?" using the real rules.
/// Put this file anywhere in Assets (it only exists in the Editor).
/// </summary>
[CustomEditor(typeof(RankSettings))]
public class RankSettingsEditor : Editor
{
    // The "Try a result" box. Not saved anywhere.
    private static bool showTry = true;
    private static string tryStage = "";
    private static int tryRoom = 1;
    private static float trySeconds = 55f;
    private static int tryScore = 400;
    private static int tryGemsTotal = 5;
    private static int tryGemsTaken = 5;

    // The row buttons only note what was asked; the list itself is changed after everything has been drawn.
    private bool pendingAddRow;
    private bool pendingFillRows;
    private int pendingRemove = -1;

    private GUIStyle noteStyle;

    public override void OnInspectorGUI()
    {
        if (noteStyle == null)
        {
            noteStyle = new GUIStyle(EditorStyles.miniLabel);
            noteStyle.wordWrap = true;
        }

        serializedObject.Update();

        SerializedProperty it = serializedObject.GetIterator();
        bool enterChildren = true;
        while (it.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (it.propertyPath == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.PropertyField(it, true);
            }
            else if (it.propertyPath == "overrides")
            {
                DrawRoomTable(it);
            }
            else
            {
                EditorGUILayout.PropertyField(it, true);
            }
        }

        serializedObject.ApplyModifiedProperties();

        DrawTryIt();
        ApplyPending();
    }

    // ------------------------------------------------------------ the table

    private void DrawRoomTable(SerializedProperty list)
    {
        RankSettings settings = (RankSettings)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Targets per room", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "One row per room. Type only what is different for that room: a box left at 0 (Gems: -1) uses the default targets above. " +
            "The other tiers are worked out from your numbers in the same proportions as the defaults, and the grey text under each row shows exactly what you get. " +
            "Open 'More' on a row to set a stage or to type every number yourself.",
            MessageType.None);

        for (int i = 0; i < list.arraySize; i++)
        {
            DrawRow(settings, list.GetArrayElementAtIndex(i), i);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add a row")) pendingAddRow = true;
            using (new EditorGUI.DisabledScope(settings.MissingRoomRows() == 0))
            {
                if (GUILayout.Button("Add rows for rooms 1-" + settings.roomCount)) pendingFillRows = true;
            }
        }
    }

    private void DrawRow(RankSettings settings, SerializedProperty row, int index)
    {
        SerializedProperty label = row.FindPropertyRelative("label");
        SerializedProperty stage = row.FindPropertyRelative("stageId");
        SerializedProperty room = row.FindPropertyRelative("room");
        SerializedProperty quick = row.FindPropertyRelative("quick");
        SerializedProperty par = row.FindPropertyRelative("parSeconds");
        SerializedProperty top = row.FindPropertyRelative("topScore");
        SerializedProperty gems = row.FindPropertyRelative("gems");
        SerializedProperty all = row.FindPropertyRelative("targets");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        using (new EditorGUILayout.HorizontalScope())
        {
            Field("Room", room, 36, 34);
            if (quick.boolValue)
            {
                Field("Fast s", par, 42, 48);
                Field("High", top, 32, 52);
                Field("Gems", gems, 36, 36);
            }
            else
            {
                GUILayout.Label("(every number typed by hand: open More)", noteStyle);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("X", "Remove this row"), EditorStyles.miniButton, GUILayout.Width(22)))
            {
                pendingRemove = index;
            }
        }

        GUILayout.Label(Describe(settings, row), noteStyle);

        row.isExpanded = EditorGUILayout.Foldout(row.isExpanded, "More", true);
        if (row.isExpanded)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(label, new GUIContent("Name", label.tooltip));
            EditorGUILayout.PropertyField(stage, new GUIContent("Stage id", stage.tooltip));

            bool byHand = !quick.boolValue;
            bool newByHand = EditorGUILayout.ToggleLeft(
                new GUIContent("Set every number myself", "Turns Quick off for this row and lets you type every time and score."), byHand);
            if (newByHand != byHand)
            {
                if (newByHand)
                {
                    // Start from what the row means right now, so switching doesn't throw the numbers away.
                    RankTargetOverride current = ReadRow(row);
                    WriteTargets(all, current.Resolve(settings.defaults));
                }
                quick.boolValue = !newByHand;
            }

            if (!quick.boolValue) DrawChildren(all);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndVertical();
    }

    private static void Field(string text, SerializedProperty property, float labelWidth, float fieldWidth)
    {
        GUILayout.Label(new GUIContent(text, property.tooltip), GUILayout.Width(labelWidth));
        EditorGUILayout.PropertyField(property, GUIContent.none, GUILayout.Width(fieldWidth));
    }

    private static void DrawChildren(SerializedProperty parent)
    {
        SerializedProperty child = parent.Copy();
        SerializedProperty end = parent.GetEndProperty();
        bool enter = true;
        while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end))
        {
            enter = false;
            EditorGUILayout.PropertyField(child, true);
        }
    }

    // ------------------------------------------------------------ what a row works out to

    private static string Describe(RankSettings settings, SerializedProperty row)
    {
        int roomNumber = row.FindPropertyRelative("room").intValue;
        string stageId = row.FindPropertyRelative("stageId").stringValue;
        if (roomNumber <= 0 && string.IsNullOrEmpty(stageId))
        {
            return "This row does nothing yet: give it a room number (or a stage id under More).";
        }

        RankTargetOverride current = ReadRow(row);
        RankTargets t = current.Resolve(settings.defaults);

        string gemText;
        if (t.totalCollectables == 0) gemText = " (no gems in this room)";
        else if (t.totalCollectables > 0) gemText = " and all " + t.totalCollectables + " gems";
        else gemText = " and every gem";

        string start = current.IsBlank ? "Uses the defaults.  " : "";

        return start +
               "P: " + RankCalc.FormatTime(t.fastSeconds) + " or faster, " + RankCalc.FormatScore(t.highScore) + "+ points" + gemText + ".   " +
               "A: same time, " + RankCalc.FormatScore(t.mediumScore) + "+ points.\n" +
               "Time    FAST <= " + RankCalc.FormatTime(t.fastSeconds) + "    GOOD <= " + RankCalc.FormatTime(t.mediumSeconds) +
               "    SLOW <= " + RankCalc.FormatTime(t.slowSeconds) + "\n" +
               "Score    HIGH >= " + RankCalc.FormatScore(t.highScore) + "    MEDIUM >= " + RankCalc.FormatScore(t.mediumScore) +
               "    LOW >= " + RankCalc.FormatScore(t.lowScore);
    }

    private static RankTargetOverride ReadRow(SerializedProperty row)
    {
        RankTargetOverride result = new RankTargetOverride();
        result.room = row.FindPropertyRelative("room").intValue;
        result.stageId = row.FindPropertyRelative("stageId").stringValue;
        result.quick = row.FindPropertyRelative("quick").boolValue;
        result.parSeconds = row.FindPropertyRelative("parSeconds").floatValue;
        result.topScore = row.FindPropertyRelative("topScore").intValue;
        result.gems = row.FindPropertyRelative("gems").intValue;
        result.targets = ReadTargets(row.FindPropertyRelative("targets"));
        return result;
    }

    private static RankTargets ReadTargets(SerializedProperty t)
    {
        RankTargets r = new RankTargets();
        r.fastSeconds = t.FindPropertyRelative("fastSeconds").floatValue;
        r.mediumSeconds = t.FindPropertyRelative("mediumSeconds").floatValue;
        r.slowSeconds = t.FindPropertyRelative("slowSeconds").floatValue;
        r.highScore = t.FindPropertyRelative("highScore").intValue;
        r.mediumScore = t.FindPropertyRelative("mediumScore").intValue;
        r.lowScore = t.FindPropertyRelative("lowScore").intValue;
        r.totalCollectables = t.FindPropertyRelative("totalCollectables").intValue;
        return r;
    }

    private static void WriteTargets(SerializedProperty t, RankTargets r)
    {
        t.FindPropertyRelative("fastSeconds").floatValue = r.fastSeconds;
        t.FindPropertyRelative("mediumSeconds").floatValue = r.mediumSeconds;
        t.FindPropertyRelative("slowSeconds").floatValue = r.slowSeconds;
        t.FindPropertyRelative("highScore").intValue = r.highScore;
        t.FindPropertyRelative("mediumScore").intValue = r.mediumScore;
        t.FindPropertyRelative("lowScore").intValue = r.lowScore;
        t.FindPropertyRelative("totalCollectables").intValue = r.totalCollectables;
    }

    // ------------------------------------------------------------ try a result

    private void DrawTryIt()
    {
        RankSettings settings = (RankSettings)target;

        EditorGUILayout.Space();
        showTry = EditorGUILayout.Foldout(showTry, "Try a result", true);
        if (!showTry) return;

        EditorGUI.indentLevel++;
        tryStage = EditorGUILayout.TextField(new GUIContent("Stage id", "Leave empty for the dungeon, or when the room has no stage of its own."), tryStage);
        tryRoom = Mathf.Max(0, EditorGUILayout.IntField("Room", tryRoom));
        trySeconds = Mathf.Max(0f, EditorGUILayout.FloatField("Time (seconds)", trySeconds));
        tryScore = Mathf.Max(0, EditorGUILayout.IntField("Score (all players)", tryScore));
        tryGemsTotal = Mathf.Max(0, EditorGUILayout.IntField("Gems in the room", tryGemsTotal));
        tryGemsTaken = Mathf.Clamp(EditorGUILayout.IntField("Gems taken", tryGemsTaken), 0, tryGemsTotal);

        RankTargets t = settings.TargetsFor(tryStage, tryRoom);
        int timeTier;
        int scoreTier;
        LevelRank rank = RankCalc.Compute(settings, t, trySeconds, tryScore, tryGemsTaken, tryGemsTotal, out timeTier, out scoreTier);

        EditorGUILayout.HelpBox(
            "Rank " + rank + "  (" + RankCalc.Word((int)rank) + ")      time: " + RankCalc.TimeTierName(timeTier) + "      score: " + RankCalc.ScoreTierName(scoreTier),
            MessageType.None);
        EditorGUI.indentLevel--;
    }

    // ------------------------------------------------------------ add / remove rows

    private void ApplyPending()
    {
        if (!pendingAddRow && !pendingFillRows && pendingRemove < 0) return;

        RankSettings settings = (RankSettings)target;
        Undo.RecordObject(settings, "Edit Rank Rows");
        if (settings.overrides == null) settings.overrides = new List<RankTargetOverride>();

        if (pendingRemove >= 0 && pendingRemove < settings.overrides.Count) settings.overrides.RemoveAt(pendingRemove);
        if (pendingFillRows) settings.EnsureRoomRows();
        if (pendingAddRow) settings.overrides.Add(RankTargetOverride.ForRoom(NextFreeRoom(settings)));

        pendingAddRow = false;
        pendingFillRows = false;
        pendingRemove = -1;

        EditorUtility.SetDirty(settings);
        serializedObject.Update();
        GUIUtility.ExitGUI();
    }

    /// <summary>The lowest room number (from 1) that has no row of its own yet.</summary>
    private static int NextFreeRoom(RankSettings settings)
    {
        int r = 1;
        while (true)
        {
            bool used = false;
            for (int i = 0; i < settings.overrides.Count; i++)
            {
                RankTargetOverride o = settings.overrides[i];
                if (o != null && o.room == r && string.IsNullOrEmpty(o.stageId))
                {
                    used = true;
                    break;
                }
            }
            if (!used) return r;
            r++;
        }
    }
}
#endif
