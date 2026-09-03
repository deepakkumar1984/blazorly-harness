# Restart-crash UX study — participant protocol

Materials: the harness web UI at http://localhost:5080, a session running a scripted
task (deterministic fake provider, `faketest/test`), and
`python3 scripts/study-restart-ux.py` (the automated pilot) for infrastructure.
Human trials use the same crash/restart mechanics with a person in the loop.

## Setup (experimenter)

1. `dotnet build` then start the app; run `python3 scripts/study-restart-ux.py --trials 0`
   is NOT used — instead the experimenter registers the fake provider once
   (`add_custom_provider`) and leaves `scripts/fake_openai.py`'s server running.
2. Per trial: create a fresh session, `/model faketest/test`, send "run the scripted
   task", wait a randomized 0.15–2.2 s, `kill -9` the app, restart it.
3. The participant is handed the browser with the app reloaded at `/` (Home), told only:
   "Your session was doing something. Get it finished, then tell us what you think happened."

## Per-trial measures (experimenter log)

| Measure | How |
|---|---|
| Comprehension | Participant's free-text answer, coded: correct (app restarted/crashed mid-task) / partial / wrong |
| Time-to-first-action | Reload → first input in the composer (screen recording) |
| Resume success | Session reaches a completed turn (footer renders) |
| Time-to-resume | Reload → completed turn |
| Wrong-recovery attempts | Refreshes, new sessions created instead of resuming, repeated sends |
| Confidence | 5-point Likert: "I knew what had happened" |

## Pilot context

The automated pilot (8 trials, seed 42; see paper §4.5) established the *system*
floor: explanation present 8/8, no stuck indicators 8/8, resume succeeded 8/8, median
resume 0.56 s after the user's message, crash-to-serving 4.6 s. Human trials measure
the part automation cannot: whether people notice the explanation, trust it, and
choose to resume rather than start over.

## Ethics

Local-only software, no personal data beyond screen recordings of interaction with a
test session; recordings deleted after coding; participants may stop at any time;
no compensation contingent on performance.
