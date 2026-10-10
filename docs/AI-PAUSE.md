# AI Pause

Frees GPU memory from local AI while a game runs, and brings it back afterwards. Ported from
[GamePause for Local AI](https://github.com/Vkuparin/gamepause-for-local-ai) (MIT), see
[REFERENCES.md](REFERENCES.md). Code: `src/DeviceMaster.AiPause` (logic, no UI) and
`src/DeviceMaster.Ui/AiPausePage.cs` (the tab).

## What happens

1. Every 2 s the running processes are matched against the game library. Launchers, overlays,
   installers, crash reporters, anti-cheat and redistributable folders never count.
2. A game with rule **On** starts a pause while automatic pausing is on. Before anything is
   unloaded, what each AI app has loaded is written to `%LOCALAPPDATA%\DeviceMaster\ai-pause-state.json`.
   Then each app is paused, and the journal is updated after every step.
3. More games share the same pause. Alt-tabbing changes nothing.
4. When the last game exits, a 30 s countdown starts (configurable). A new game cancels it.
   Then everything comes back and the journal is deleted.

If DeviceMaster or the PC dies mid-pause, the journal is still there on the next start, and
restoring happens once no game is running. A failed pause or restore is retried every 30 s and
shows **AI NEEDS ATTENTION**. An unreadable journal is never deleted automatically.

## Game rules

- **On**: pause automatically (the default for discovered games).
- **Ask**: AI keeps running and the tab and a notification offer **Pause AI for this game**.
  The answer covers only the running instance; **Always/Never pause** sets the rule.
- **Off**: ignore the game.

Games come from Steam, Epic, GOG, EA, Ubisoft Connect, Battle.net and Xbox (Game Pass folders),
plus **Add game…** (an exact .exe) and **Running apps → Add as game**. A program that stays
fullscreen in front for 10 s and isn't in any library is *suggested* (never paused for);
answer **Add as game** or **Not a game**.

**Pause AI** holds AI paused until **Resume AI**. Resuming while a game runs asks first; AI then
stays loaded alongside that game instance until it exits, and a new game pauses again.

## AI apps

| App | Pause | Restore |
|---|---|---|
| Ollama | unload each model (`keep_alive: 0`) and wait until `/api/ps` is empty (a busy model finishes its generation first) | reload completion models with the same context and the keep-alive they had left. Embedding/unknown models aren't reloaded, and cloud models are left alone |
| LM Studio | `lms unload <identifier>` for each loaded instance | `lms load <key> --identifier --context-length --ttl`. Other per-load settings return to LM Studio defaults |
| NInfer (WSL) | `ninfer.cmd stop` (stops the service **and terminates the WSL distro**) | `ninfer.cmd start`, only if it was running when the pause began |
| Other apps | each running copy of that exact exe is asked to close, then ended after 3 s | started again with the same command line (stored DPAPI-encrypted), minimized |

An app that isn't running is left alone and isn't an error. A closed LM Studio is never
contacted, because its CLI would start it. Settings are in `ai-pause.json` next to `config.json`.

## Limits

- An assistant or client that loads models on its own can undo the pause.
- Other-app relaunch can't keep environment variables set by a launcher script. A
  supervisor that restarts the server will fight the pause.
- Detection is best effort: protected processes and unusual installs may need **Add game…**.
