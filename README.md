# Project D Unity Starter

A bare Unity project foundation: folders, a Unity `.gitignore`, and nothing else.
There are no gameplay systems, sample scenes, or art in here.

## What is inside

```
Project_D_Unity_Starter/
  .gitignore
  README.md
  Assets/
    Audio/        Music/  SFX/
    Materials/
    Prefabs/
    Scenes/
    Scripts/      Core/  Player/  UI/  Audio/   (empty placeholder scripts)
    Settings/
    Textures/
    UI/
  Packages/            (empty on purpose, Unity writes manifest.json here)
  ProjectSettings/     (empty on purpose, Unity writes its settings here)
```

Every folder under `Assets/` has a `.meta` file already, and empty folders hold a
`.gitkeep` so Git keeps them. Unity ignores `.gitkeep`. You can delete a `.gitkeep`
once a folder has real files in it.

## Where the Unity version is set

The Unity version is **not set in this starter on purpose**. It lives in one file,
`ProjectSettings/ProjectVersion.txt`, which Unity writes the first time the project
is opened with an Editor you choose. Use the version your assignment requires.
Commit that file after Unity creates it. `Packages/manifest.json` is written the same way.

## Open it

1. Extract the zip and rename the folder to your project name.
2. In Unity Hub choose **Add > Add project from disk** and pick the folder.
3. Pick the Editor version your course requires, then open the project.
   Unity creates `ProjectSettings/ProjectVersion.txt`, `ProjectSettings/*.asset`,
   `Packages/manifest.json`, and the generated `Library/` folder.

If your Hub version will not add the folder, use this instead:

1. In Unity Hub, create a new project with the Editor version you need.
2. Copy this starter's `Assets/` contents, `.gitignore`, and `README.md` into that
   new project, keeping the `.meta` files.

Set these once in **Edit > Project Settings > Editor**:

- Version Control Mode: **Visible Meta Files**
- Asset Serialization: **Force Text**

## Connect it to a new GitHub repository

Create an empty repository on GitHub first (no README, no `.gitignore` template, no
license), then from the project folder:

```
git init
git add .
git status
git commit -m "Initial Unity project structure with Unity .gitignore"
git branch -M main
git remote add origin https://github.com/<your-username>/<repo-name>.git
git push -u origin main
```

Run `git status` before the commit and check that nothing from `Library/`, `Temp/`,
`Logs/`, `UserSettings/`, `Build/`, or `Builds/` is listed, and that there are no
`.csproj` or `.sln` files.

After you have opened the project in Unity once, confirm the ignore rules match:

```
git check-ignore -v Library/ Temp/ Logs/ UserSettings/
```

Each folder should print the `.gitignore` line that ignores it.

## Check the first commit on GitHub

The repository root should show `Assets`, `Packages`, `ProjectSettings`, `.gitignore`,
and `README.md`. If only `.gitattributes` or a README shows up, the project did not
get committed.

## Reuse notes

- Rename the folder and the GitHub repository, nothing inside needs editing.
- The four placeholder scripts are empty classes. Rename or delete them when the
  project has its own structure. Renaming a script in Unity keeps its `.meta` file.
