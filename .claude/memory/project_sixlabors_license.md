---
name: project-sixlabors-license
description: ImageSharp 4.x needs the committed sixlabors.lic community key to build; it expires 2028-01-07 and is renewed via a form
metadata:
  node_type: memory
  type: project
  modified: 2026-10-09T19:39:18.894Z
---

ImageSharp 4.x refuses to build without a Six Labors license. crop-stage uses a free Community key (OpenSource kind, scoped to the `CropStage` assembly), committed at the repo root as `sixlabors.lic` and wired via `SixLaborsLicenseFile` in `src/CropStage.csproj`. Obtained 2026-10-09; expires 2028-01-07.

**Why:** 3.1.x had five unpatched advisories (fixed only in 4.1.2+), so staying on 3.x was not an option.

**How to apply:** a build failing with "No Six Labors license found" after 2028-01-07 means the key expired. Renew by resubmitting https://licensing.sixlabors.com/ from the same email within the final 90 days before expiry, with assembly name `CropStage`, then replace `sixlabors.lic`. The auto-mode classifier blocks copying/reading the key file, so the user does the copy.
