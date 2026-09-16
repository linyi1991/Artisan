# Artisan 4.0.3.136 — 恢復有界 Craftimizer 宇宙動態求解

## 版本與範圍

- Artisan `4.0.3.136-api13-tw-bounded-dynamic-quality`，API13/net9；Craftimizer Solver 2.11.0.3 source `a321109` 不變。
- source baseline `ac2cf2e`，integration baseline `bfbed74`。依使用者「保留好的求解品質、宇宙動態／一般標準快取、限制資源」追加要求，取代 .133～.135 宇宙純 Artisan 輕量模式。
- .135 一般 Allagan 預演的真正 Craftimizer 擇優、HQ/NQ 分段成功快取、食藥／職業能力快照及全部生命週期修正完整保留。未改使用者設定、ICE 任務／取料／維修或其他插件。

## 詳細變更

1. 宇宙重新使用 Craftimizer 根據每個真實製作步驟的 current state 求解，不把隨機狀態和素材奇蹟當固定巨集重播。素材奇蹟由 Artisan 已有判斷優先處理。
2. 不退回舊版無界搜尋：全森林共用 100000 iterations／750ms deadline，單 worker，與預演共用 gate；外層 2 秒取消、framework 3 秒等待恢復，上一工作未結束不再疊加。搜不到解或取消／錯誤會切 Artisan 備援；fallback原因會記錄，不宣稱所有步驟一定有MCTS結果。
3. 新增拒用「沒有任何完成配方的後續方案」之MCTS片段，避免把部分搜尋結果當成能完成的建議。在同一件回退輕量動態策略，不無限重試。
4. 真正採用 Craftimizer 時寫 `[Craftimizer Decision]` 的 recipe／step／action／elapsedMs／iterations及模型預測quality/progress，讓介面solver名稱不能掩蓋實際路徑。沿用 .135 每件開始／結束的共享 .NET 記憶體和實際成品結果觀測。
5. 普通 Allagan 大量製作僅新輸入預演一次；完整驗證成功計画快取32筆；相同能力值、配方與起始品質重用。999不會產生999次MCTS，状態偏離會停止固定重播。新搜尋較差不覆蓋成功基準，品質門檻不放寬。
6. 有限時間搜尋不等於全域最佳解；宇宙受到隨機狀態影響，也無法保證每件最高品質。這次選擇保留真正求解能力，同時用可測資源界線取代一律換輕量策略。

## 驗證與限制

- 實際 Artisan Release build：0 errors、109 warnings。核心34/34（包含Cosmic起始／中途／素材奇蹟剩餘時間、完成優先、迭代／取消預算）；快取與候選擇優生產碼harness23項通過。SDK10建置目標仍net9，测试以程序级roll-forward运行，非Air/Wine測試。
- 同核心獨立普通配方benchmark：4次搜尋每次100000iterations，299～517ms、配置23.30～27.07MiB；這不是宇宙品質／8GB Air峰值保證。
- 本機32GB，Air只有8GB但未提供現場log。熱換前遊戲footprint約7.4～7.5GB（peak7.6），遠高於RSS；共享.NET managed估計約663～678MiB，不能將全部遊戲占用歸因Craftimizer，也不能用RSS1GB宣稱安全。
- .135下recipe36650三件完成且任務348回報、下一任務啟動，但品質隨條件波動（例10545/12300及7747/12300）；這是本次恢復有界動態Craftimizer的原因之一，不宣稱同配方受控對照證明一定更高分。
- 本版實際換版／載入與後續製作觀測由integration更新文件記錄。即使成功完成一輪，仍需長跑HQ耗盡切NQ、修理、材料回報與Air8GB記憶體壓力驗收。

## 更新方式

Air先關遊戲與Launcher，在已連結runtime配置的整合庫執行：

```sh
git switch main
git pull --ff-only --recurse-submodules
git submodule update --init --recursive
```

確認載入4.0.3.136；宇宙 `[Craftimizer Resource Policy]` 應寫bounded search，`Decision` iterations不超100000。取消或fallback必須有原因、不能永遠計算。檢查完整任務與下一輪、共享.NET committed／managed趨勢，以及活動監視器記憶體壓力／整個遊戲footprint。重開遊戲可建立新進程基線；本次不會擅自結束遊戲或強制GC。

## 回退方式

關閉遊戲後整包回復換版前的Artisan備份（整合文件列出絕對路徑），包含所有companion與manifest/deps/zip，設定不變。Git可回復integration `bfbed74` 的Artisan build與source pin：即.135普通有界擇優＋宇宙輕量策略，core仍有界。不回到早期無界MCTS，不重寫歷史／force push。
