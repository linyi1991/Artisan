# Artisan 4.0.3.135 — 品質不退步的有界 Craftimizer 與快取

## 版本、範圍與原因

- Artisan `4.0.3.135-api13-tw-quality-preserving-cache`，API13/net9；core source 仍為 `a321109` (2.11.0.3)，沿用 .134 修正的全森林 iteration/deadline 限制。
- 基線 Artisan `df78ffd`，integration `fdb68c8`。使用者追加要求保留 Craftimizer 求解品質、一般配方標準快取，並授權正在製作時強制替換。沒有修改使用者設定、食藥、維修、HQ 分段或 ICE 任務邏輯。
- .132～.134 的一般離線預演實際只走 Artisan 輕量同步 solver，名稱不代表真的使用 Craftimizer MCTS；本版不再用降低求解種類作為唯一降耗手段。

## 實作

1. 保留 Artisan 模擬器方案作比較基準；每個新的 HQ/NQ 起始品質允許一次真正的 Craftimizer 完整方案搜尋，不逐虛擬步驟重新搜尋。
2. 候選先以 Artisan 實際模擬規則重播驗證，再比較「完成並達標、完成、技能確定性、品質、步數」。較差隨機結果／逾時／無解不推翻成功基準；相同可靠度時也不以少步數換較低品質。這是有界候選擇優，不保證數學上的全域最優解。
3. 每次搜尋單 worker、最多 1.5 秒和 100000 次迭代；同一預演所有 HQ 段合計最多 6 秒／200000 次（依段數預先分配），外層工作 10 秒。預演和 live expert 共用一個 gate，避免各開一組森林。取消是合作式，非即時硬殺；時間限制不等於整個遊戲的固定 RAM 上限。
4. 完成且全技能 100% 的方案寫入既有 32 筆完整狀態快取。按製作／調整數量／重複預演相同輸入時重用完整計畫，不重新搜；HQ 耗盡、食藥／裝備／門檻改變分別處理。999 只屬於執行次數。
5. 顯式 Task.Run 在擷取遊戲資料後啟動背景預演，避免未競爭的 WaitAsync 同步完成把重工留在 UI。同步 Artisan 模擬面板仍是輕量基準，不謊稱兩邊都跑 MCTS；預演日誌明示最後採用 baseline、bounded Craftimizer 或 validated cache。
6. 宇宙仍保留 .133/.134 的 Standard/Expert 動態每步策略與素材奇蹟，不固定重播、不啟動 MCTS。無快取的普通直接製作仍可安全回退 Artisan；此版本主要強化 Allagan 預演→大量製作路徑，非所有入口都自動執行完整方案搜尋。
7. 新增每件開始／結束的 `[Craft Resources]`、`[Craft Result]`：共享 .NET runtime 的 managed estimate／上次 GC heap／committed／期間 allocation，以及實際 progress、quality、耐久、CP、步數與取消狀態。這些不是 Artisan 專屬用量，沒有強制 GC、沒有改遊戲畫質或其他插件。

## 驗證與限制

- Artisan Release：0 errors / 109 warnings。核心 34/34；生產快取／候選比較 harness 23 項通過（含失敗隨機結果不覆蓋成功、確定性、品質不降級、999 次重用、返回計畫副本隔離）。
- 獨立原生 .NET 測試連跑四次真實核心（起始品質 0/6000）：每次100000 iterations，299～517ms，allocation 23.30～27.07 MiB。兩次 NQ 結果 10483、11069 均不足11400，證實短搜尋不能被當成配方可行性證明；失敗仍須保留成功基準。測試程序 GC 後 managed 約0.18 MiB；只在隔離程序 GC，未對遊戲執行。這不是 Wine／Air benchmark，也非跨配方的 memory 保證。
- 本機 32GB，並非 Air 8GB。14:46:12～14:48:12 舊進程短測：RSS 1.08～1.28GiB，CPU樣點50.9～243.6%（每100%約一核心）；當中有插件庫刷新／PluginSuite卸載，不能全歸因 solver。vmmap physical footprint 約7.5GB、peak7.6GB，14:50再次觀察仍約7.5GB；系統 swap used2114.31MiB 未增加，但 pageouts有增加。低RSS不代表8GB安全，熱換不等於清掉原程序累積占用。
- .134 在14:48:32完整回報宇宙任務346並開始下一輪；下一配方36633於14:51出現材料耗盡而放棄，沒有足夠日誌確定是求解品質、材料狀態還是其他操作，不能將整體自動化宣稱驗收完畢。14:58:01另有外部插件停止Artisan要求，發生在本次替換之前，非下一步計算逾時。
- .135 14:58:47載入、14:58:48完成，重載有378ms framework hitch；不能宣稱零卡頓。預演擇優/HQ取料/999次長跑與Air8GB實機仍需驗收。
- 本機 source/stage/live/zip byte equality、manifest API13/net9 已通過。新版 DLL SHA256：`c9d2618d5dc548736894a67ca0318b3b7ec0f080e560edc3e2ce807f36f7a60f`；solver：`8f34ad1a41ca6dcf3fed17995731feff709bc7843160f9fb3c9366cb260378ff`。因建置嵌入修正版 source revision，core binary 可與 .134 hash不同，需成套更新。

## Air 更新與驗收

關閉遊戲／Launcher，在已連結同步環境的 Air repo 執行：

```sh
git switch main
git pull --ff-only --recurse-submodules
git submodule update --init --recursive
```

啟動後確認4.0.3.135；普通配方先分析一次，檢查採用來源和快取重播；數量999不應出現999次Preview；切換HQ/NQ、食藥／裝備需失效。宇宙完成一輪及下一輪，對照 `[Craft Result]` 與 `[Craft Resources]`，另用活動監視器記憶體壓力與遊戲footprint觀察，不只看RSS。8GB Air未提供現場日誌，不保證已排除其全部原因。

## 回退

關閉遊戲後整套回復 `/Users/theo/data/github/ffxiv/builds/.backups/Artisan-20260916-145845-pre-403135`，包括主DLL、solver/simulator、manifest、deps與zip，設定不動。Git previous integration `fdb68c8`，Artisan source `df78ffd`；不要只回復主DLL。此回退恢復 .134 輕量預演策略與已修正核心，不退到無資源上限的早期版本。
