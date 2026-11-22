# SLRP AR

A Unity augmented reality navigation project for St. Lorenzo Ruiz Parish.

## Requirements

- Unity Hub
- Unity Editor **6000.6.0f1** (the version used by this project)
- Internet access for the first import to download packages
- For Android builds: **Android Build Support**, including **Android SDK & NDK Tools** and **OpenJDK**, installed through Unity Hub

Use the same Unity Editor version when opening the project on another computer. If you want to try another version, make a backup first; Unity may update project files and packages.

## Open and run

1. Download and extract the project, or clone this repository.
2. Make sure the project folder contains `Assets`, `Packages`, and `ProjectSettings`. Keep all `.meta` files inside `Assets`.
3. Open **Unity Hub**, go to **Projects**, and add the project from disk. Select the folder containing those three folders, not the `Assets` folder itself.
4. Open it with **6000.6.0f1** and wait for Unity to import assets and restore packages. The first launch may take several minutes.
5. In the Project window, open `Assets/Scenes/ParishARNavigation.unity`.
6. Press **Play** to preview the scene. Check the **Console** for errors if it does not start.

Use a supported phone to test actual AR camera tracking and navigation. An Editor preview does not replace testing on the device.

## Run on Android

1. Connect an ARCore-compatible Android phone using USB and enable **USB debugging**. The project currently requires Android API level 30 or newer.
2. In Unity, open **File > Build Profiles**, select an Android profile, and switch to it. Install Android Build Support through Unity Hub if Android is unavailable.
3. Ensure `Assets/Scenes/ParishARNavigation.unity` is enabled in the profile's scene list.
4. Select your phone as the run device and choose **Build and Run**. Accept the phone's USB debugging prompt if shown.
5. Save the build in a separate output folder, such as `Builds/Android`, and allow camera access when the app opens.

## Common issues

- **Packages are missing:** check your internet connection and open **Window > Package Manager**. Keep both `Packages/manifest.json` and `Packages/packages-lock.json` with the project.
- **Unity asks to upgrade the project:** check that you selected **6000.6.0f1** in Unity Hub.
- **The scene is empty or incorrect:** open `Assets/Scenes/ParishARNavigation.unity` explicitly.
- **Build or script errors:** open **Window > General > Console** and resolve the first red error before trying again.

`Library`, `Logs`, and other generated folders are intentionally excluded from Git. Unity recreates them when needed.
