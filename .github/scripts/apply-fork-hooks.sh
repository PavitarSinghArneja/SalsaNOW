#!/usr/bin/env bash
# Wires the fork-only DevTools installer (SalsaNOW/Fork/DevToolsInstaller.cs) into the original
# SalsaNOW code. Safe to run any number of times: it only adds what is missing.
# Two hooks, nothing else in the original files is touched:
#   1. SalsaNOW.csproj: a <Compile> line for Fork\DevToolsInstaller.cs
#   2. Program.cs: one call to DevToolsInstaller.InstallAsync(globalDirectory) after settings load
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

csproj=SalsaNOW/SalsaNOW.csproj
program=SalsaNOW/Program.cs
compile='<Compile Include="Fork\DevToolsInstaller.cs" />'
hook='_ = DevToolsInstaller.InstallAsync(globalDirectory);'

if ! grep -qF "$compile" "$csproj"; then
  # Insert above the first existing <Compile> line, copying its indentation and line ending
  perl -0pi -e 's/^([ \t]*)(<Compile Include=[^\n]*?)(\r?\n)/$1<Compile Include="Fork\\DevToolsInstaller.cs" \/>$3$1$2$3/m' "$csproj"
  echo "Added Fork\\DevToolsInstaller.cs to $csproj"
fi

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

if ! grep -qF "$compile" "$csproj" || ! grep -qF "$hook" "$program"; then
  echo "::error::Could not wire the Node/OpenCode/Git installer into the original SalsaNOW code"
  exit 1
fi
