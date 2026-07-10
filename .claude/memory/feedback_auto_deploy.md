---
name: auto-deploy after code changes
description: For this project, run deploy automatically after making code changes intended for the user to test — don't ask first
type: feedback
---
When working in a fix→deploy→test loop on this project, run `bash scripts/deploy.sh` (or invoke the deploy skill) automatically after making code changes intended for the user to test. Don't ask "want me to deploy?" first.

**Why:** The user is running a tight iteration loop and asking each time is friction. Deploy is reversible (the previous binary is replaced; no destructive remote effect). They explicitly opted in 2026-04-14 after I asked too many times.

**How to apply:**
- After editing code that affects runtime behavior, build to verify, then deploy without asking
- Still acknowledge what you deployed in the response (so the user knows when to test)
- Does NOT extend to genuinely destructive or remote-affecting actions (git push, repo creation, registry edits) — keep asking for those
- If a build fails, fix it before deploying — don't deploy a known-broken build
