# SalsaNOW Backups

Keeps any folders you choose backed up in a private GitHub repo called `salsanow-backups`, and puts them back in a new session when you click Sync.

## Every session
1. SalsaNOW starts and opens the **Backups** window.
2. A small window shows a code like `AB12-CD34`. On your phone, open **github.com/login/device** and type it in. (Or click "Later" and use "Log in to GitHub" in the Backups window.)
3. Reinstall your games and programs as usual.
4. Select a slot and click **Sync (restore)...**, or click **Sync all**. The newest backup is unzipped into the slot's folder. Any files already in that folder are kept next to it as `<folder>_before_restore_<time>`.
5. Play or work. Every 10 minutes, slots with **Auto** on are zipped and uploaded, but only if their files changed.
6. Before ending the session, click **Back up all** (or **Back up now** for one slot) to be safe.

Closing the window only minimizes it, so automatic backups keep going. Open it again with the **Backups** shortcut on the desktop.

## Slots
- **New slot...**: give it a name (for example `My saves`) and the folder to protect. It is backed up right away.
- Paths are saved with variables such as `%APPDATA%\...`, so they work whatever the Windows user name is.
- **Edit...** changes the folder or turns Auto on or off. **Delete slot...** removes the slot and all of its backups from GitHub. The folder on the PC is not touched.

## Why auto backup waits for Sync
In a new session, auto backup leaves a slot alone until you **Sync** it or **Back up** it yourself. Otherwise a freshly reinstalled (empty) folder could be uploaded and become the newest backup. A manual backup of a slot you haven't synced in this session asks you to confirm first.

## Where things are on GitHub
```
salsanow-backups/
  settings.json            auto backup interval
  My saves/
    slot.json              name, folder, Auto on/off, last backup
```
The zips are under the repo's **Releases**, one release per slot. They live there instead of in the repo's files because uploading a zip every 10 minutes would push the repo past GitHub's size limits within days. Releases allow files up to 2 GB each.

For each slot, the newest 30 automatic and 30 manual backups are kept. Pick any of them in **Sync (restore)... > Version**.

## Limits
- Up to 2 GB per zipped folder.
- If a program is still writing files while they are zipped, the app zips again. Auto backup tries again later; a manual backup warns you.
- If the session is cut off, you lose at most the changes since the last automatic backup (10 minutes by default).
- The GitHub login is stored only in a temp folder and ends with the session. Nothing secret is built into SalsaNOW.exe.
