# Build & Test (Steps 3, 7, 7b)

Goal: `x64/Debug/PowerToys.exe` launches and the changed feature works end-to-end, so the user can just run the exe. Run all build commands from the worktree directory.

## Spectre override (required on machines without Spectre libs)

Set this **before** building:
```powershell
$env:POWERTOYS_DISABLE_SPECTRE = "1"
```
This works because `Cpp.Build.props` respects `<SpectreMitigation Condition="'$(SpectreMitigation)' == ''">Spectre</SpectreMitigation>` and `Directory.Build.props` sets it to false when the env var is set. If the worktree lacks these conditions, add them (LOCAL to the worktree only — do **not** commit or push them):

1. In `Cpp.Build.props`:
   ```xml
   <SpectreMitigation Condition="'$(SpectreMitigation)' == ''">Spectre</SpectreMitigation>
   ```
2. In `Directory.Build.props`, inside the first `<PropertyGroup>`:
   ```xml
   <SpectreMitigation Condition="'$(POWERTOYS_DISABLE_SPECTRE)' == '1'">false</SpectreMitigation>
   ```

## Step 3: Prepare the validation plan

After rebasing and verifying the fork PR diff, inspect its changed paths and
write a focused validation plan into the per-PR checkpoint **before requesting
Copilot**. Record the affected project/module, the exact final build commands
from Step 7, required environment overrides, and any separate upstream-head or
suggestion-candidate checks that will be needed. This gives a resumed worker
actionable commands without spending a full serialized build before Copilot
has reviewed the branch.

Do not compile the ordinary fork worktree in Step 3. Request Copilot as soon as
the mirror, rebase, and scope checks pass; process the review while the async
review request is pending. Step 7 builds the converged fork tree once. If fixes
are made after a successful build, rebuild the changed final tree before
completion. Exact-upstream and apply-ready suggestion candidates remain
separate required validation trees; validate them when drafting the findings.

### Handling build failures during Step 7

- **Spectre errors** (`MSB8040`): ensure `POWERTOYS_DISABLE_SPECTRE=1` is set and the `Cpp.Build.props` condition is in place.
- **NuGet missing**: run `build-essentials.cmd` first (it restores packages).
- **spdlog/fmt errors** (old submodule): the branch was not rebased on main — go back to Step 2b.
- **Compilation errors in the PR's changed files**: attempt to fix; if not easily fixable, stop and leave a comment on the fork PR.
- **Resource file missing** (e.g., a `.scr`): build the dependency project first (check the vcxproj for `ProjectReference` items).
- **LIB path errors** (`cannot open file 'libcpmtd.lib'`): the build scripts should handle this via env var; if not, apply the LIB fix (see the known-issues table).

## Step 7: Final build (after Copilot convergence)

After the latest fresh Copilot round has zero published inline comments and
all Copilot threads are resolved, run the saved validation plan against the
converged fork tree. Acquire the batch-wide build lock only for this build;
never hold it while waiting for Copilot or processing findings. Run the
full chain once for this tree. `build-essentials.cmd` only builds the runner +
settings; it does **not** build module interface DLLs or managed module
executables. For end-to-end testing, build the full chain:

```powershell
$env:POWERTOYS_DISABLE_SPECTRE = "1"
cd <worktree-path>
# 1. Runner + settings + NuGet restore
.\tools\build\build-essentials.cmd
# 2. Native module interface DLL (the DLL the runner loads: PowerToys.<Module>.dll)
cd src\modules\<Module>\<NativeInterfaceProject>
..\..\..\..\tools\build\build.cmd
# 3. Managed module application, if separate (e.g. PowerToys.PowerLauncher.exe)
cd ..\<ManagedProject>
..\..\..\..\tools\build\build.cmd
# 4. The specific changed project/plugin
cd <ChangedProjectFolder>
..\..\..\..\tools\build\build.cmd
```

Common module interface projects:
- PowerToys Run: `src/modules/launcher/Microsoft.Launcher/` → `PowerToys.Launcher.dll`
- FancyZones: `src/modules/fancyzones/FancyZonesModuleInterface/` → `PowerToys.FancyZonesModuleInterface.dll`
- Keyboard Manager: `src/modules/keyboardmanager/KeyboardManagerModule/` → `PowerToys.KeyboardManager.dll`
- Color Picker: `src/modules/colorPicker/ColorPickerModuleInterface/` → `PowerToys.ColorPicker.dll`
- ZoomIt: `src/modules/ZoomIt/ZoomItModuleInterface/` → `PowerToys.ZoomItModuleInterface.dll`

To find it: look for a `.vcxproj` whose output the runner log references as "Failed to load X.dll".

**Why all four steps?** `build-essentials.cmd` only builds the runner shell and settings; the runner discovers modules by loading native interface DLLs; the managed app provides the actual UI/functionality; the plugin/changed-project is the code the PR modifies.

Verify: exit code 0 for all steps; `x64/Debug/PowerToys.exe` exists; the module interface DLL exists in `x64/Debug/`; and after launching, the runner log has no "Failed to load" line for the target module:
```powershell
Get-Content "$env:LOCALAPPDATA\Microsoft\PowerToys\RunnerLogs\runner-log_$(Get-Date -Format yyyy-MM-dd).log" | Select-String "<ModuleName>"
```

## Step 7a: Validate the exact upstream/suggestion tree

The fork build and this gate prove different things:

- the fork build proves the converged reviewer implementation;
- this gate proves what the upstream author actually has or will get by
  clicking the proposed suggestions.

For a clean zero-finding review, create a clean detached worktree at the exact
upstream `headSha` and run the smallest affected-project build there. For a
review with apply-ready suggestions, create a second clean worktree at that
same head, apply every suggestion block verbatim using its exact line range,
and build the affected project. When suggestions may be accepted separately,
also apply/build each structurally risky suggestion independently.

After that first pass succeeds, trim unchanged leading/trailing lines from
each suggestion range, recreate the candidate from the pinned upstream head,
and build again. The recorded passing result must cover the final minimized
suggestions that will be shown to the author, not an earlier context-heavy
version.

Record the exact head, applied item IDs, commands, and passing result under
`internalEvidence.validation`. Never reuse a build result from a tree with a
different diff. If the exact suggestion candidate fails syntax or compilation,
fix the suggestion and repeat this gate before presenting it.

## Step 7b: End-to-end testing instructions

After the Step 7 full build, `x64/Debug/PowerToys.exe` **already exists in the worktree** — the instructions you hand the user (and the `testInstructions` you put in `review-data.json`) should therefore be *run-and-verify*, not "build then run". Give the **concrete, resolved launch path**, not a `<worktree>` placeholder: substitute the actual worktree directory for this PR (e.g. `C:\PowerToys-review-N\x64\Debug\PowerToys.exe`), since the user is often coming straight from the dashboard and just wants to double-click / paste one path.

**Always include:**
1. **Launch command** — the exact, resolved path to the already-built `PowerToys.exe` in this PR's worktree (e.g. `C:\PowerToys-review-N\x64\Debug\PowerToys.exe`). Do **not** tell the user to rebuild; Step 7 already did.
2. **How to access the feature** — what to click / shortcut to press / setting to change.
3. **What to look for** — specific UI elements, output text, or behaviors that confirm the change.
4. **Test scenarios** — 2–4 concrete steps exercising the new behavior, including edge cases addressed by the review fixes.

If the Step 7 build did **not** run (e.g. review was held before build), say so explicitly and point at the Step 7 build commands instead of implying a ready exe.

**Template (build already done):**
```
## Run & verify (already built)
1. Launch: C:\PowerToys-<id>\x64\Debug\PowerToys.exe
2. <How to trigger the feature>
3. <What to verify — expected output/behavior>
4. <Edge case from the review fixes, if applicable>
```

> Dashboard note: `review-data.json` carries each PR's `worktree`; the dashboard auto-derives `<worktree>\x64\Debug\PowerToys.exe` as the Launch line, so your `testInstructions` can start at step 2 (how to trigger). Set an explicit `exePath` only if the built exe lives elsewhere.

## Known environment issues and workarounds

| Issue | Symptom | Fix |
|-------|---------|-----|
| Spectre libs missing | `error MSB8040: Spectre-mitigated libraries are required` | Set `$env:POWERTOYS_DISABLE_SPECTRE = "1"` + ensure the `Cpp.Build.props` condition |
| LIB env includes spectre paths | `LINK : fatal error LNK1104: cannot open file 'libcpmtd.lib'` | `$env:LIB = $env:LIB -replace 'spectre\\x64','x64'` after VS env init |
| PR based on old main | spdlog/fmt compilation errors, missing vcpkg deps | Rebase onto `origin/main` first (Step 2b) |
| Force-push blocked by repo rules | `remote rejected: Cannot force-push` | Push to a new branch name (e.g., `pr-iterate/N-v2`) |
| Resource file missing (e.g., `.scr`) | `error RC2135: file not found` | Build the dependency project first (check `ProjectReference` in the vcxproj) |
| NuGet packages missing | `This project references NuGet package(s) that are missing` | Run `build-essentials.cmd` first |

## Fallback: manual msbuild (only if the scripts fail)

```cmd
call "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\Tools\VsDevCmd.bat" -no_logo >nul 2>&1
cd /d <worktree-path>
set LIB=%LIB:spectre\x64=x64%
set POWERTOYS_DISABLE_SPECTRE=1
msbuild <project.vcxproj> /p:Platform=x64 /p:Configuration=Debug /p:SpectreMitigation=false /p:SolutionDir=<worktree-path>\ /nologo /v:minimal
```

Build logs live next to the project being built: `build.<config>.<platform>.errors.log` (check first) and `build.<config>.<platform>.all.log`.

## PowerToys style reference

- C#: `src/.editorconfig`, StyleCop.Analyzers
- C++: `src/.clang-format`
- XAML: XamlStyler
- Atomic changes, no drive-by refactors
- No noisy logs in hot paths
