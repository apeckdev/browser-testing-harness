# Collaborative Iteration & Validation Workflow

## 1. Fast Turnaround on Code and UI Tweaks
- For styling, UI adjustments, and localized code modifications, prioritize applying the fix quickly and cleanly.
- Avoid over-engineering the verification phase (e.g., inspecting DOM trees via devtools, launching background daemons, or capturing screenshots) unless explicitly requested by the user.

## 2. Keep the User in the Verification Loop
- When the user has the application or dev server running, hand control back to the user after applying code edits so they can verify live.
- Clearly state what changed and where (with clickable file links), and ask the user to confirm whether the change resolves the issue.

## 3. Respect Running Processes and Locks
- Do not blindly attempt to launch background servers or full rebuilds if the user is already running the project locally.
- If a build or file lock issue is encountered, check with the user before attempting process termination or running duplicate background instances.
