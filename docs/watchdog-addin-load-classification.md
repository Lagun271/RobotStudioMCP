# Watchdog Add-in Load Classification

How the watchdog's `ready` check (and `GetAddinLoadState`) reports an add-in's load state, from RobotStudio's own usage/error logs.

## States

| Code / status | ok | PowerPac-only? | Meaning |
|---|---|---|---|
| `ADDIN_LOAD_UNKNOWN` / `unknown` | false | no | Nothing logged yet for this add-in. |
| `ADDIN_LOAD_UNKNOWN` / `added` | false | no | Recognized, not yet loading. |
| `ADDIN_LOAD_UNKNOWN` / `loading` | false | no | Actively loading. |
| `ADDIN_LOAD_SUCCEEDED` / `loaded` | true | no | Loaded. Terminal. |
| `ADDIN_LOAD_SUCCEEDED` / `activated` | true | **yes** | Loaded **and** activated. Terminal. |
| `ADDIN_AWAITING_ACTIVATION` / `awaiting manual activation` | true | **yes** | Staged, waiting on a manual UI action. Not a failure. |
| `ADDIN_LOAD_FAILED` / `failed` | false | no | A failure line names this add-in, or matches a known load-failure pattern. |
| `ADDIN_LOAD_FAILED_WITH_EXCEPTION` / `failed` | false | no | A failure occurred mid-load, or (no evidence at all for this id) a last-resort guess. |
| `ADDIN_LOAD_LICENSE_FEATURE_MISSING` / `license_feature_missing` | false | no | Manifest requires a license feature the active license doesn't have. RobotStudio never logs this as an error — it just never loads. |
| `ADDIN_NOT_REQUESTED` / `not_requested` | true | no | No `--rsaddin-path` given; N/A, not a load outcome. |

A non-PowerPac add-in only ever reaches `loaded`; it never reaches `activated` or `awaiting manual activation`.

## General add-in

```
AddinAdded|<id>|General → AddinLoading|<id> → AddinLoaded|<id>
```

## PowerPac add-in

```
AddinAdded|<id>|PowerPac → user clicks Load → AddinLoading|<id> → AddinLoaded|<id> → (sometimes) ActivatedPP|<id>
```

PowerPac add-ins stay at `awaiting manual activation` until the user manually loads them.

Which terminal state results depends on *how* the user clicks Load:
- Ribbon "Load" button → `AddinLoaded` then `ActivatedPP` → classifier reports **`activated`**.
- Tree-view right-click → Load → `AddinLoaded` only, **no `ActivatedPP`** → classifier reports **`loaded`**.

Both are `ok=true`, successful end states. Don't assume a successfully-activated PowerPac add-in will show `activated` — it may show `loaded` instead, depending on which RobotStudio UI path the user took.

In the tree-view path (`loaded`, no `ActivatedPP`), the add-in's own ribbon tab never appeared in the RobotStudio UI, even though the log says it loaded. So `loaded` alone does not guarantee the add-in's UI surface actually came up — `activated` is the state that does.

## Caveat

`loaded`/`activated` can still carry `HadStartupException=true`. When set, `ready` always treats it as a hard failure — regardless of whether the exception names the target add-in. The only exceptions that don't set it are a small fixed set recognized as benign (a `UIThreadWatchdog` warning, `ABB.Kairos.ClientApi.AiService.ApiException`, or RobotStudio's own `|Silent`-tagged telemetry) — anything else sets it, related or not. See `ClassifyAddinLoad` in [`src/RobotStudioMCP.Watchdog/RobotStudioHealthTools.cs`](../src/RobotStudioMCP.Watchdog/RobotStudioHealthTools.cs) for the exact rule.
