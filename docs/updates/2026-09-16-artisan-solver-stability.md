# Artisan 4.0.3.134 — 求解資源上限與 HQ 快取穩定性

## 版本與範圍

- Artisan `4.0.3.134-api13-tw-solver-stability`，Craftimizer Solver Core `2.11.0.3-api13-bounded-search`。API13、net9 保持不變；AllaganTools 13.1.21 與 IPC 契約不变。
- Integration 基線 `810ea6169625b42531eda81c0b228c8f40c9f3e9`；Artisan source 基線 `204029e`，core source 基線 `d667332`。
- 本機實際載入仍是 4.0.3.132，.133 原先只發布到 Git。Air 現場日誌未取得；本次是來源審查發現的確定缺陷修正，不宣稱已證明 Air 的唯一卡住原因。使用者明確授權強制替換。

## 詳細變更

1. 修正 MCTS 未完成解且樹仍展開時可能忽略 MaxIterations 的迴圈；取消檢查由每 1024 輪增加為每 128 輪，開始前也檢查。
2. NextAction 的時間模式原本把 iterations 設成 int.MaxValue。新增整個搜尋樹群共用的原子計數與 monotonic deadline；篩選、加深、並行候選一起受全域迭代上限約束，不以取消 timer 作為唯一資源界線。Artisan live 保留單執行緒、750ms 搜尋、100000 次總上限、2 秒取消及 3 秒 framework watchdog；有界不等於硬即時或固定記憶體位元組上限。
3. 保留 .133 的宇宙 Standard/Expert 輕量動態策略與素材奇蹟，宇宙不啟動 MCTS、也不使用固定步驟快取。這與舊 MCTS 的得分策略不同，仍需完整任務及下一輪實測。
4. 修正 .132 HQ 分段預演未再寫入成功計畫。只把完成、達到門檻且所有技能成功率 100% 的計畫寫入 32 筆有界快取。Key 含完整能力值、起始品質、配方標量、狀態資料與品質門檻，HQ 與 NQ 不再混用。
5. 每次重播比對完整製作狀態（CP、耐久、品質、buff、前一步與條件）；支援共享 step index 的專家動作。手動插入動作、變更配料／數值或條件偏離即轉輕量動態求解，不重啟 MCTS。999 次只重用相同輸入計畫。
6. 預演輸入比對不再依賴新建陣列的 reference equality，避免相同能力值再次計算；目標收藏品門檻改變會失效。含機率技能的估算明示非保證、不作固定快取，保持既有接受門檻及動態執行行為。
7. 取消／fault 結果改用同一步既有的輕量建議，避免永久等待或再次修改有狀態 solver。製作開始／結束清理舊工作；同步完成的宇宙與快取結果立即發布。CancellationTokenSource 等工作結束後才 Dispose，晚到錯誤會被觀測。
8. 預演工作表限 32 筆；任何替換（包含製作入口的隱式重算）都取消舊工作並在結束後釋放 token source。卸載後的 continuation 不再開製作清單。
9. 沒有修改食藥、換職、維修、ICE 流程、角色庫存或使用者設定；沒有重寫 Git 歷史，保留 .133 的 shallow submodule / zip 瘦身安排。

## 驗證與限制

- 實際 Artisan csproj Release build：0 errors、109 warnings。建置主機 SDK 10 目標仍為 net9；未變更全域 runtime。
- Craftimizer core：34/34。新增測試包含失敗樹迭代硬上限、時間模式不得忽略迭代限制、並行共享預算、預取消零工作與限時結束。
- `python3 tests/check-plan-cache.py`：14 項生產 cache 程式檢查通過，包括 HQ/NQ 隔離、能力／门檻失效、狀態偏離、同 step index、999 次重用與容量淘汰。此 harness 只 stub Lumina sheet handle，不測遊戲 native 材料／技能計算。
- net9 核心測試與 harness 在此主機以程序級 DOTNET_ROLL_FORWARD=Major 執行；不等同 Air net9/Wine 執行驗收。
- source、staging、live、root zip 與 nested zip 中的 Artisan／solver DLL、manifest、deps 逐位元組核對通過。
- Artisan DLL SHA-256：`19a5ea7d53a1a9ba4a6c77ff32172a26e3926f7bd3d3fd977d7c7e5b0819dd2c`。
- Craftimizer.Solver DLL SHA-256：`1110eea675d196ebcdacba3832eb25a9afb021581fdb4acd43f2d65c1fc7108f`。
- 本機已備份並強制替換，companion files 優先、watched DLL 最後。Dalamud 於 2026-09-16 14:39:54 載入 Artisan 4.0.3.134，14:39:55 完成載入；完整宇宙任務、下一輪及 Air CPU/RSS 長時間驗收仍未完成。

## 更新方式

Air 關閉遊戲／Launcher，已套用同步環境的 repo 中沿用：

```sh
git switch main
git pull --ff-only --recurse-submodules
git submodule update --init --recursive
```

若 tracked 設定有本機改動，先保存並處理衝突；本更新不自動覆蓋 Air 私有改動。確認 Artisan 4.0.3.134 載入與 Cosmic resource policy 日誌；測一完整宇宙任務＋下一任務啟動，再驗證 HQ 用盡轉 NQ、大量製作維修及食藥。觀察 RSS 是否持續增長、是否還停在下一步；保留故障前後日誌。沒有確認現場前不可稱完全穩定。

## 回退方式

關閉遊戲／Launcher，整包回復 Artisan build（包含 Craftimizer.Solver.dll、manifest、deps、zip），不要只換單一主 DLL。遠端上一版可從 integration `810ea6169625b42531eda81c0b228c8f40c9f3e9` 還原 `builds/Artisan-Mac-API13-fixed` 及兩個 source gitlink，其他插件／設定不動。

本機完整 .132 備份：`builds/.backups/Artisan-20260916-143730-pre-403134`。此舊版仍有已知的重型宇宙搜尋，若退回請明確選用 Artisan 輕量求解再觀察。需要重建時也須將 core source 同步到匹配的 pin。
