#!/usr/bin/env bash
# Wires the fork-only code (SalsaNOW/Fork: the DevTools installer and the Backups app) into the
# original SalsaNOW code. Safe to run any number of times: it only adds what is missing.
# Nothing else in the original files is touched:
#   1. SalsaNOW.csproj: <Compile> lines for the Fork\*.cs files and <EmbeddedResource> lines for Fork\Wallpaper.png and the Backups icon
#   2. Program.cs: one call to DevToolsInstaller.InstallAsync(globalDirectory) after settings load
#   3. Program.cs: one line right after SteamDetach that handles the Backups shortcut (SalsaNOW.exe --backups)
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

csproj=SalsaNOW/SalsaNOW.csproj
program=SalsaNOW/Program.cs
compile='<Compile Include="Fork\DevToolsInstaller.cs" />'
wallpaper='<EmbeddedResource Include="Fork\Wallpaper.png" />'
backups=(
  '<Compile Include="Fork\FolderBackups\BackupEngine.cs" />'
  '<Compile Include="Fork\FolderBackups\BackupsApp.cs" />'
  '<Compile Include="Fork\FolderBackups\BackupsForm.cs" />'
  '<Compile Include="Fork\FolderBackups\GitHubBackupApi.cs" />'
  '<EmbeddedResource Include="Fork\FolderBackups\Backups.ico" />'
)
hook='_ = DevToolsInstaller.InstallAsync(globalDirectory);'
backups_hook='if (await BackupsApp.HandleCommandLineAsync(args)) return;'
backups_anchor='SteamDetach.RemoveSteamEnvironments();'

for line in "$compile" "$wallpaper" "${backups[@]}"; do
  if ! grep -qF "$line" "$csproj"; then
    # Insert above the first existing <Compile> line, copying its indentation and line ending
    LINE="$line" perl -0pi -e 's/^([ \t]*)(<Compile Include=[^\n]*?)(\r?\n)/$1$ENV{LINE}$3$1$2$3/m' "$csproj"
    echo "Added $line to $csproj"
  fi
done

if ! grep -qF "$hook" "$program"; then
  # globalDirectory is set by Startup(), so the hook goes after the settings load (or Startup itself)
  for anchor in 'SalsaSettings.Load();' 'await Startup();'; do
    if grep -qF "$anchor" "$program"; then
      ANCHOR="$anchor" HOOK="$hook" perl -0pi -e 's/^([ \t]*)(\Q$ENV{ANCHOR}\E[^\n]*?)(\r?\n)/$1$2$3$1$ENV{HOOK}$3/m' "$program"
      echo "Added the DevTools call to $program after: $anchor"
      break
    fi
  done
fi

# The Backups shortcut starts SalsaNOW.exe --backups, which must not run the rest of SalsaNOW
if ! grep -qF "$backups_hook" "$program" && grep -qF "$backups_anchor" "$program"; then
  ANCHOR="$backups_anchor" HOOK="$backups_hook" perl -0pi -e 's/^([ \t]*)(\Q$ENV{ANCHOR}\E[^\n]*?)(\r?\n)/$1$2$3$1$ENV{HOOK}$3/m' "$program"
  echo "Added the Backups shortcut check to $program after: $backups_anchor"
fi

for line in "$compile" "$wallpaper" "${backups[@]}"; do
  if ! grep -qF "$line" "$csproj"; then
    echo "::error::Could not add $line to $csproj"
    exit 1
  fi
done
if ! grep -qF "$hook" "$program" || ! grep -qF "$backups_hook" "$program"; then
  echo "::error::Could not wire the Node/OpenCode/Git installer and Backups app into the original SalsaNOW code"
  exit 1
fi
