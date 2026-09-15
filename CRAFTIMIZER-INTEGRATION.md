# Artisan + Craftimizer Solver integration (API13/TW)

## 4.0.3.131-api13-tw-craftimizer14-consumable-preflight

- 預演日誌與結果現在同時列出基礎裝備能力、指定食物／藥水及預先套用後能力，明確證明人物尚未實際吃食藥時也會先用指定效果模擬。
- 1.5 秒單執行緒 Craftimizer MCTS 若未達標，會以完全相同的食藥後 `CraftState` 執行一次輕量 Artisan 安全模擬；備援達標就快取其標準化步驟，避免反覆按分析賭 MCTS 搜尋結果。
- 備援只在玩家明確按分析時執行一次，且最多 64 個同步模擬步驟；不會依大量製作 50／148／999 次倍增。
- 保留 4.0.3.130 的成功預演重用，以及 4.0.3.129 的大量製作自動維修繼承修正。

## 4.0.3.130-api13-tw-craftimizer13-reuse-preflight

- 修正按「模擬製作」已成功並存入標準化計畫後，實際按「製作」仍強制啟動第二次隨機 MCTS；第二次結果可能較差，造成明明第一次達標卻被安全門鎖拒絕。
- 製作入口現在會以完整 `PlanKey` 核對配方、製作／加工／CP、等級、技能解鎖、專家／宇宙旗標、配方難度與品質門檻。完全相符才沿用成功預演；裝備、食藥、設定或配方狀態改變時自動失效並重新分析。
- 若相同配方分析仍在執行，製作入口共用同一工作，不另開第二個求解器；日誌會顯示 `[Craftimizer Preview Cache] ... no second MCTS solve`。
- 保留 4.0.3.129 的 Allagan 暫時清單自動維修繼承修正。

## 4.0.3.129-api13-tw-craftimizer12-bulk-repair

- 修正 Allagan Tools／Craftimizer IPC 大量製作使用暫時清單時，因未呼叫 `NewCraftingList.Save()` 而讓 `Repair` 維持 `false` 的問題。
- 暫時清單現在明確繼承 Artisan 全域的自動維修、維修門檻與精製設定；Theo 現行全域門檻為 10%，因此會在裝備損壞前離開製作並進入 `RepairManager`。
- 大量製作啟動日誌會記錄 `repair` 與 `repairPercent`，方便確認跨插件請求實際採用的維護設定。
- Craftimizer 的標準化計畫快取與 Normal 條件重播邏輯維持不變；本修正不增加 MCTS 計算次數。

## 4.0.3.128-api13-tw-craftimizer11-cached-plan

- 修正大量製作雖然只做一次離線預演，但實際每一個遊戲動作仍重新建立 `NextActionForked` MCTS 搜尋樹；148 次配方會把成本放大為「製作步數 × 148」，造成 Wine 記憶體最高約 6.5GB、進入 swap、長時間 framework hitch，並曾以 exit code 137 終止。
- AllaganTools 的一件式安全預演成功後，Artisan 現在會快取整套已逐步驗證的技能序列；同配方的大量製作只依目前步驟重播，不再於每個真實動作啟動 MCTS。要求 999 次仍只有一次預演與一套計畫。
- 快取鍵包含配方、製作力、加工精度、CP、等級、專家／秘傳狀態、輝煌工具、HQ／收藏／專家分類、耐久、難度、品質門檻與條件旗標；換裝、食藥、狀態或配方條件改變時不會誤用舊計畫。
- 每次重播前仍由 Artisan 模擬器檢查技能是否可用；遇到非 Normal 的即時品質條件、步驟超界、資源或狀態不符時，該次製作立即切回 Artisan 標準備援，不重新啟動 Craftimizer MCTS。
- 標準非專家、非宇宙配方才使用固定計畫。宇宙與專家配方條件會變動，仍保留原本的逐步狀態感知路徑與 Material Miracle 防護。
- 記憶體快取最多 32 套，插件重載後清空；下一次按模擬或從 AllaganTools 開始製作時會重新預演並建立快取，不寫入玩家設定。

## 4.0.3.127-api13-tw-craftimizer11-bounded-preview

- 修正 AllaganTools 單筆「模擬製作」會在每個虛擬製作步驟重新啟動一次完整 `NextActionForked` 搜尋，造成 CPU／記憶體持續攀升並可能被系統以 exit code 137 終止。
- 離線預覽現在只啟動一次 Craftimizer core：總搜尋預算 1.5 秒、單執行緒、最多 10 秒工作生命週期，取得完整候選動作後再以 Artisan 模擬器驗證。
- 所有配方的預覽工作共用單一 semaphore，避免快速點選多列時並行堆疊重型搜尋；再次預覽相同配方仍會取消舊工作。
- 預覽 IPC 只接收 `recipeId`，固定模擬一件。即使實際要求製作 999 次，也只做一次安全檢查，再由 Artisan 清單重複製作，不會建立 999 個預覽工作。
- 此段為 4.0.3.127 的歷史行為；4.0.3.128 已將標準非專家、非宇宙配方改為重播驗證過的單次計畫，宇宙／專家製作仍逐步求解。

## 4.0.3.125-api13-tw-craftimizer9-retainer-max

- Craftimizer 2.11 Solver Core 回補至 API13 / .NET 9，改用有時間上限、剪枝及品質目標的 `NextActionForked`。
- AllaganTools「我的庫存能做什麼」透過 start/poll IPC 在背景預測，不阻塞遊戲 UI；實際製作先通過 HQ／收藏品安全鎖，再由 Artisan 建立完整子配方清單。
- 清單內配方會暫時選用 `Craftimizer Recipe Solver`，結束或失敗後恢復原本的暫時求解器設定。
- AllaganTools MAX 只計角色四頁背包／水晶及僱員七頁背包／水晶；Artisan 直接以 InventoryTools IPC 初始化狀態判斷是否執行僱員取料。

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
- Craftimizer 2.11.0.2 is linked as `Simulator` and `Solver` libraries only. Its
  Dalamud plugin UI, hooks, and action execution are not loaded.
- Standard non-expert/non-Cosmic crafts replay one complete plan that the IPC
  preflight already validated; the plan cache is keyed by recipe and crafting
  capabilities. Expert and Cosmic crafts still request state-aware asynchronous
  recommendations. Artisan remains the validator and executor in both paths.
- Existing Artisan solvers remain available. `Craftimizer Recipe Solver` has
  priority 0 and is selected by recipe configuration, ICE IPC, or the guarded
  AllaganTools crafting route.

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
- During real Expert/Cosmic crafting the adapter uses `NextActionForked` with a
  1.8-second per-step wall-clock budget, action pruning and a 100% quality target.
  Standard crafts never start live MCTS after a validated plan was cached.
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

## AllaganTools contract

- `Artisan.StartCraftimizerHqPrediction(ushort) -> string` starts one bounded,
  background, zero-initial-quality simulation and returns `PENDING|...`.
- `Artisan.GetCraftimizerHqPrediction(ushort) -> string` returns
  `IDLE|...`, `PENDING|...`, `SAFE|...`, or `BLOCK|...`.
- `Artisan.PrepareAndCraftWithCraftimizer(ushort, int, bool)` repeats the
  one-item preflight and only then retrieves retainer materials and starts the
  requested list. The `amount` never multiplies preview jobs.
- Legacy `Artisan.GetHqPrediction` and `Artisan.PrepareAndCraft` remain for
  existing callers, but are not the new AllaganTools primary path.

## Provenance and verification

- Upstream solver source: Craftimizer tag `2.11.0.2`, commit
  `3b07695eb0636204d61b066dcca4b770d184ea2d` (MIT).
- API13/net9 backport: tag `2.11.0.2-api13-cosmic2`, commit `d667332`.
- Artisan build: `4.0.3.131-api13-tw-craftimizer14-consumable-preflight` (commit recorded by the release update).
- AllaganTools caller: `13.1.20.0`, commit `702a280`.
- Paired ICE: `0.0.0.705-api13-tw40`, Dalamud API 13, net9.
- The adapter contains the 2.11 solver/simulator core, not the standalone
  official Craftimizer Dalamud UI or action executor.
- Craftimizer core tests: 28 passed, 0 failed. They include initial,
  resumed-active, and expired Material Miracle states for the long-progress
  Cosmic recipe model, completion-dominant scoring, and the resumed step limit.
- Artisan Release build: 0 errors (113 existing warnings). Craftimizer core
  tests: 28 passed, 0 failed. Build-time verification proves cache routing and
  API13/net9 compatibility; a long 148/999-repeat session remains the runtime
  acceptance test and is not implied by compilation alone.

## Deployment

Deploy Artisan and the paired AllaganTools build together. Stop both plugins if
hot reload does not occur automatically.
Do not install or enable the standalone Craftimizer plugin for this integration.
Keep the previous Artisan and AllaganTools directories as a paired rollback set.
