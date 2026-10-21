# Evaluation tasks

`eval/tasks` is the default suite for `blazorly eval`, `eval compare`, and `eval refine`. It holds the file and recovery smoke tasks plus behavior, API, and migration fixtures.

`eval/heldout` is a separate suite. Refinement and the default compare path do not load it. Point `--tasks eval/heldout` at it only after a profile is frozen.

A one-trial Linux comparison on 21 October 2026 used the ambient deepseek / deepseek-flash route and `--sandbox none`. On `eval/heldout`, baseline, compression, and terse all passed; terse ranked first on total tokens. An equal-budget refinement on `failing-unit-test` did not beat repeated baseline attempts, and that adopted profile was more expensive than baseline on the held-out task. Dollar cost stays unavailable. One trial is not a stable result. Details are in `HARNESS2_REVIEW.md`.
