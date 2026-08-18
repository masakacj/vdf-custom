# VDF Customization Plan

Upstream: `https://github.com/0x90d/videoduplicatefinder` (`master`)

Initial customization goals:

1. Windows Everything integration for file enumeration/index acceleration, with native scan fallback.
2. HDD-friendly I/O scheduling and incremental/background indexing.
3. Reuse scan/media/hash caches after rename and move whenever file identity can be verified.
4. Add independent folder duplicate/containment analysis:
   - never compare a parent folder with its own descendants;
   - custom containment percentage;
   - resource-most-complete baseline and covered percentage;
   - structured-vs-flat folder analysis;
   - smart merge and smart exclude operations.
5. Show full path, total size, file count and subfolder count consistently in file/folder/result views.
6. Direct move-to-folder picker that remembers the last destination.
7. Keep visual media duplicate matching separate from exact/hash duplicate workflows where useful.
8. Windows x64 GitHub Actions build artifacts for custom builds.

## Branch model

- `upstream-master`: exact mirror of upstream `master`.
- `main`: custom branch, initialized from upstream and carrying this file plus custom automation.

This repository is private, but upstream licensing obligations still apply to redistributed builds.
