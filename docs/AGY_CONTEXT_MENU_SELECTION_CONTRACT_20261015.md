# VDF Custom — Results context menu cross-folder selection (AGY development contract)
Date: 2026-10-15
Repository: masakacj/vdf-custom
Base: main
Feature branch: feature/agy-vdf-context-selection-20261015
Stage: AGY CODE -> independent ChatGPT Web M0 review -> PR/CI -> release separately after approval.

## Objective and exact Chinese context menu actions
Extend the **file-row** context menu (not the group-header menu). Preserve the two existing bulk-check commands:
- 勾选此文件夹下所有命中文件 (currently CheckFolderResultHitsCommand)
- 勾选其他文件夹中与此文件同组的命中文件 (currently CheckOtherFolderGroupHitsCommand)

Add three independent actions ("选择" means setting deletion/operation checkboxes `DuplicateItemVM.Checked=true`, not only `ListBox.SelectedItem`):
1. **选择其他路径与此文件夹中项目相同的文件** (cross-folder, outside): Let F be the actual immediate parent folder of right-clicked anchor. For every duplicate GroupId represented in currently displayed/filtered result groups, if its members contain both folder F and some *different* parent folder, check all of that group's members **outside F**. This applies across ALL relevant duplicate groups, not only anchor's group. Do not check inside-F members in this action.
2. **选择此文件夹中与其他路径相同的文件** (cross-folder, inside): Same qualifying groups, but check all their members **inside F**. Do not check outside members in this action.
3. **选择组内其他** (same-group others): Within anchor's GroupId, check all OTHER file items in the currently displayed/filtered group, including other items in the same folder, but not anchor itself. An already-checked anchor remains untouched.

- "相同" means sharing GroupId, NOT a matching basename; file names can differ.
- Scope is **currently displayed/filtered result groups**, using existing `resultsGroups[].Rows`; collapse/virtualization should not omit members of an included group. Never toggle hidden groups excluded by filters or query filesystem/db anew.
- Folder identity uses the existing `ResultFolderKey` / `NormalizeFolderStatsKey` logic and Windows case-insensitive comparison. Paths share **immediate parent directory**, not child-prefix matching. Different drive/UNC folders are different.
- Preserve existing checked flags outside the target set; actions are additive only, never uncheck pre-existing selections; change checkboxes only. No file deletion, moving, blacklisting, DB writes, scan, or filter changes.
- Use `BeginSelectionUndoBatch` and `ScheduleCheckedStructureRefresh` like existing commands, only once per operation. No O(N²) group scanning: one pass to identify GroupIds for the folder and one pass to produce matching items; target O(N) time / O(G) memory.
- Context menu items should be inserted once via tagged dynamic creation; reused virtualized ContextMenu must refresh `CommandParameter = row.Item` on EVERY file-row right click. Preserve original menu items and group-header menu, avoid duplicated separators.
- Add precise unit tests under `VDF.GUI.Tests` for three operations, cross-group behavior, unrelated group excluded, same-folder-only group excluded for cross-folder actions, different basename same GroupId, anchor excluded for action #3, same-folder sibling included #3, Windows case and path separators/drive distinctions, empty result, source filtering, duplicate menu creation / parameter routing when feasible.
- Existing checkboxes, sort/filter Apply buttons, BEST/quality, undo, database restore, and launch/startup must not regress.

## Source landmarks verified on current main
- `VDF.GUI/Views/DuplicateResultsView.xaml.cs`: `OnResultsPointerPressed`, `EnsureBulkCheckMenu` with existing two menu Tags and commands.
- `VDF.GUI/ViewModels/MainWindowVM_ResultBulkSelection.cs`: `ResultFolderKey`, `EnumerateVisibleResultItems`, existing two command implementations and pure match helpers.
- `VDF.GUI/ViewModels/Results/ResultsRows.cs`: `ResultsGroupHeader.Rows` and `ResultsItemRow.Item`.
- `VDF.GUI/ViewModels/DuplicateItemVM.cs`: `ItemInfo.GroupId`, `ItemInfo.Path`, `Checked`.

## Stage workflow / acceptance
A1. AGY coding (Gemini-only native Claude CLI, select live verified Gemini model first; medium default; no provider fallback): code pure selector helpers and ViewModel command handlers in `MainWindowVM_ResultBulkSelection.cs`; write unit tests. If implementation requires codebehind changes, modify ONLY `DuplicateResultsView.xaml.cs` as well.
A2. AGY UI binding: three tagged context menu items, current row params updated per right click; do not remove old items. Verify menu routing.
A3. Independent build & tests: `dotnet test VDF.GUI.Tests/VDF.GUI.Tests.csproj -c Release`, and `dotnet build VDF.GUI/VDF.GUI.csproj -c Release` on Office or Windows GitHub Actions. Report unavailable tool/package restore as a blocker, not pass. Use source-only checks if full build is temporarily unavailable.
A4. ChatGPT Web M0 independent review of exact diff & test evidence, PR to main (draft until passing), no automatic merge or release. Release workflow triggers on merge to main; do NOT merge as part of coding task.

## Safety
Keep all private credentials out of output, logs and commits. No external repo edits, no force push, no production process/Chrome restarts, no existing app binary/installer changes. All development stays in isolated worktree and feature branch. Stop if model gateway cannot prove Gemini-only routing.
