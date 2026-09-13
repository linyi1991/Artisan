# Artisan + Craftimizer Solver integration (API13/TW)

## 4.0.3.123-api13-tw-craftimizer7-quickinno

- 修正 `Quick Innovation`（快速改革）在 API13/TW 製作模擬器中被錯誤視為推進回合的問題。
- 快速改革現在不再錯誤遞減 Manipulation 等計時效果，也不會產生不存在的耐久回復，避免 Craftimizer 與遊戲狀態不同步。
- 保留 4.0.3.122 的繁中求解狀態面板、Craftimizer IPC 合約及 Artisan 安全備援。
- Dalamud API13 / .NET 9 Release 建置：0 errors；實機已確認後續宇宙配方可完成，但特定配方無解仍由 ICE 任務保護處理。

## 4.0.3.122-api13-tw-craftimizer6-ui

- 繁中化製作狀態面板的求解器名稱、推薦技能、計算耗時及失敗／備援原因。
- 內部求解器名稱與 IPC 合約維持 `Craftimizer Recipe Solver`，不影響 ICE 自動選用。
- Dalamud API13 / .NET 9 Release 建置：0 errors。

## Architecture

- Artisan remains the only workflow and action executor. It owns recipe selection,
  consumables, Endurance/Craft X, retries, lists, state synchronization, and IPC.
- Craftimizer 2.8.0.0 is linked as `Simulator` and `Solver` libraries only. Its
  Dalamud plugin UI, hooks, and action execution are not loaded.
- `CraftingProcessor` requests one recommendation asynchronously and publishes it
  back on Dalamud's framework thread. Artisan then validates and executes it.
- Existing Artisan solvers remain unchanged. `Craftimizer Recipe Solver` has
  priority 0 and is selected only by recipe configuration or ICE IPC.

## Cosmic bridge and safety behavior

- Each recommendation has an 8-second timeout. If Craftimizer times out,
  returns no solution, maps an unknown action, or proposes an action Artisan
  considers unusable, the adapter switches the remainder of that craft to
  Artisan's already-warmed Expert or Standard solver. It does not repeatedly
  re-enter a solver path that already failed for the current craft.
- The fallback recommendation is calculated once per step and reused. This
  avoids advancing Artisan's stateful fallback solver twice on the same step.
- Risky probabilistic actions are excluded from the action pool.
- Artisan's proven solver remains responsible for deciding when to use the
  Cosmic-only Material Miracle duty action. Craftimizer never executes or owns
  that action.
- While Material Miracle is active, the adapter reads its live remaining time
  and the simulator uses the Cosmic expert-condition pool. Simulated actions
  consume their macro wait time; every live recommendation refreshes the timer.
- `MaxStepCount` is relative to the current live craft (`ActionCount + 48`). This
  prevents resumed long-progress Cosmic crafts from becoming mathematically
  unreachable because an absolute 40-step ceiling was nearly exhausted.
- The adapter uses `OneshotForked`: it calculates only the next recommendation,
  then Artisan validates and executes it from the current game state.
- The adapter retains one live action of combo history. Advanced Touch receives
  its discounted CP cost only after the complete Basic Touch -> Standard Touch
  chain (or Observe), matching the game instead of treating every isolated
  Standard Touch as a valid combo starter.
- Solver fork tasks settle before their semaphore is disposed, preventing the
  timeout-related unobserved `ObjectDisposedException` seen in the first build.
- Cancelling or finishing a craft cancels any pending background recommendation.

## ICE contract

- Restored IPC endpoints: `Artisan.ChangeSolver` and
  `Artisan.SetTempSolverBackToNormal`.
- Paired ICE selects the exact temporary solver name
  `Craftimizer Recipe Solver`, then restores the user's configured solver.
- ICE's Progress Only leveling rule remains higher priority.
- Craftimizer and Raphael selection are mutually exclusive in ICE settings.

## Provenance and verification

- Solver source: Craftimizer tag `2.8.0.0`, commit
  `3359748d1382dab09f76aaed98ea7c2b07e36888` (MIT).
- Artisan candidate: `4.0.3.121-api13-tw-craftimizer5`, Dalamud API 13, net9.
- Paired ICE: `0.0.0.705-api13-tw40`, Dalamud API 13, net9.
- The adapter is a targeted API13 backport/extension of Craftimizer 2.8.0.0;
  it is not presented as the complete official 2.11 plugin.
- Craftimizer core tests: 28 passed, 0 failed. They include initial,
  resumed-active, and expired Material Miracle states for the long-progress
  Cosmic recipe model, completion-dominant scoring, and the resumed step limit.
- Artisan Release build: 0 errors (existing warnings remain). In-game verification
  must include a full Cosmic craft and the following mission start before this
  pair is treated as runtime-proven.

## Deployment

Deploy Artisan and the paired ICE build together while both plugins are stopped.
Do not install or enable the standalone Craftimizer plugin for this integration.
Keep the previous Artisan and ICE directories as a paired rollback set.
