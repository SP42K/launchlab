# LaunchLab — Windows 啟動與回應性量測作品集

## Context

要應徵 Microsoft WECE / Hardware Engineering Enablement 的 Senior Software Engineer
（Windows 效能與回應性）。JD 的核心是：**ETW/WPR/WPA 抓 trace → TraceProcessing SDK
程式化分析 → 定義指標與量測方法 → 處理不穩定（flaky）量測 → 產出可行動的 RCA**，
加分項是 boot flow、記憶體/儲存/IO 延遲、ADK/WAT、WPA plugin model、AI 輔助 RCA。

履歷現況（`~/Downloads/丁浩鈞_履歷_中文設計版-2.pdf`）：七年資安／紅隊工具開發，
OSCP，C/C++/Go/Python，**WinDbg + IDA/Ghidra/x64dbg 逆向底子**，C2／Tunnel／Port Scan
自動化。逆向與除錯能力可直接平移，唯一缺的是「用 ETW 做量測與歸因」的公開證據。

目標產出：一個公開 GitHub repo（`launchlab`），README 是一份有數字的案例研究。
**職缺可能隨時關閉，所以做成階梯**：Stage 1 做完（約半天）repo 就能投；之後每加一階
只是在 README 多一節，隨時可停。

手上環境：
- 實體 Windows 11（x64）
- macOS + VMware Fusion → `~/Virtual Machines.localized/Windows 11 64-bit Arm.vmwarevm`（arm64 Win11）
- `vmrun` 在 `/Applications/VMware Fusion.app/Contents/Public/vmrun`（Stage 3 才會用到）

---

## 專案核心：一條管線，四個場景

**不要每個場景開一個新專案。** 一支 C# console app 帶三個 verb，配一個 PowerShell 驅動腳本，
四個場景全部共用：

```
launchlab/
  README.md              # 案例研究（英文為主，開頭放中文摘要）
  docs/plans/2026-09-06-launchlab.md   # 這份計畫（複製過去）
  scripts/run.ps1        # 場景執行器：設定初始狀態 → wpr 開錄 → 跑負載 → 停錄
  src/LaunchLab/         # 單一 C# console app：analyze / report / (stage 5) explain
  results/               # runs.csv, summary.json, report.md, WPA 截圖
```

C# 是**不得不**的選擇：`Microsoft.Windows.EventTracing.Processing.All`（TraceProcessing SDK，
最新 1.12.10）是唯一官方支援的程式化 ETL 讀取器，而它正是 JD 點名的「Performance Toolkit SDK」。
統計與報告也一起寫在 C#（中位數/MAD/p95 用 LINQ 幾行就好），不要為了畫圖多拉一個 Python。

---

## Stage 0 — 環境與骨架（約 1 小時）

在**實體 Windows 11** 上先做（環境單純，先讓管線跑起來；arm64 VM 留給 Stage 2）。

1. 裝 .NET 8 SDK。`wpr.exe` **是 Windows 內建的**（`C:\Windows\System32\wpr.exe`），
   Stage 0–1 不需要裝 ADK。
2. `dotnet new console -o src/LaunchLab`，加
   `<PackageReference Include="Microsoft.Windows.EventTracing.Processing.All" Version="1.12.10" />`
3. 用系統管理員 PowerShell 手抓一個 trace 驗通：
   ```powershell
   wpr -start GeneralProfile -start DiskIO -filemode
   7z.exe | Out-Null
   wpr -stop smoke.etl
   ```
4. 寫最小的 `analyze`：`TraceProcessor.Create(etl)` → `UseProcesses()` → 印出所有 process
   的 ImageName / CreateTime / ExitTime。

**Gate：能印出 smoke.etl 裡 7z.exe 的建立與結束時間。** 通過才往下走。

---

## Stage 1 — 冷/熱啟動 A/B 骨幹（約 3–4 小時）→ **此時 repo 已可投**

這一階把整條管線做完，題目選最單純的「同一支程式冷啟動 vs 熱啟動」。

### 指標定義（README 要明講，這是 JD 的「define metrics and measurement methodologies」）

- **工作負載**：`7z.exe`（無參數 → 印 usage 後立刻結束）。選它是因為它**同時有 arm64 與
  x64 官方建置**，Stage 2 可以做到唯一變因是架構。
- **T_startup** = process 建立 → process 結束（kernel Process 事件，trace 內自帶時間戳）。
  用「跑完即退」的 console 負載，就**不需要自訂 ETW 儀器**去偵測「視窗可互動」——
  這是最大的懶點。README 要誠實寫清楚這個指標涵蓋什麼（loader、DLL 映射、缺頁、
  首次程式碼執行）、不涵蓋什麼（GUI 首幀、輸入就緒）。
- **歸因分項**（全部只算目標 process 的那段區間）：
  | 分項 | TraceProcessing 資料源 |
  |---|---|
  | CPU 執行時間（依模組分桶） | `UseCpuSamplingData()` |
  | 排程等待 / ready time | `UseContextSwitchData()` |
  | 磁碟服務時間、IO 次數、位元組 | `UseDiskIOData()` |
  | 硬缺頁次數與時間 | `UseHardFaults()` |

  **只做 module-level 歸因，不做 function-level** → 不需要 `_NT_SYMBOL_PATH`、
  不必下載符號，省掉一整套麻煩。要看 function 級的時候再開 WPA 手動看。

### 量測方法（這一段才是跟一般計時腳本拉開差距的地方）

- 每組 **N = 20** 次。
- **A/B 交錯執行**（A,B,A,B…），不是「先跑完 20 次 A 再跑 20 次 B」——消除溫度漂移、
  背景工作、host 負載造成的偏差。
- 每次之間 settle：等 CPU idle 數秒再開下一輪。
- 丟掉第 1 次（暖機）。
- 報告中**揭露環境**：Defender 即時掃描開/關、電源計畫、是否插電、VM 設定、Windows build。
- 冷態製造：`Stop-Process`+清 standby 太髒，直接用最可靠的兩招——
  熱態＝連續跑；冷態＝Stage 1 用「換一支沒跑過的複本」或重開機，Stage 3 之後用 VM snapshot。

### 統計與 flaky 偵測

`analyze` 輸出 `runs.csv`（每次一列，含所有分項）+ `summary.json`，`report` 產 `report.md`：

- 每組：median、MAD、p95、IQR、變異係數 CV、min/max
- **flaky run 標記**：`|x − median| > 3 × MAD`（用 MAD 不用標準差，因為離群值本身會污染 σ）
- **離群值歸因**：對每個被標記的 run，自動算它相對中位數 run 的 Δ 來自哪一分項
  （例：「run 7 慢 180 ms，其中 92% 來自磁碟服務時間」）
- **A/B 比較**：中位數差、Hodges–Lehmann 位移估計（或直接 bootstrap 中位數差的 95% 區間，
  stdlib 等級的十幾行），並把 ΔT 拆成上面四個分項

**Gate：`report.md` 有兩組分佈、有 flaky 標記、有一句「差異主要來自 X」的歸因結論。**
到這裡 README 寫完就可以投了。

---

## Stage 2 — Prism x64 模擬冷啟動稅（約 2–3 小時）→ **主打看點**

搬到 **arm64 Win11 VM**。管線完全不動，只是換 config：

1. **arm64 原生** `7z.exe`
2. **x64 模擬，熱快取**（`C:\Windows\XtaCache` 內已有該執行檔的 `.JC` 翻譯快取）
3. **x64 模擬，冷快取**（停掉 `XtaCache` 服務 → 清空 `C:\Windows\XtaCache\*` → 重啟服務）

Windows on Arm 跑 x64 時由 Prism（24H2 起）／`xtajit64.dll` 做 JIT 翻譯，結果以 `.JC`
檔快取在 `C:\Windows\XtaCache`；**第一次啟動要付翻譯成本，之後才命中快取**。
這是一個可控、可重現、效應巨大的非確定性來源——正好是 JD 說的
「diagnose non-deterministic or flaky measurement results」的活教材。

看點在於 module-level CPU 歸因會直接把 `xtajit64.dll` / Prism 相關模組的成本挑出來，
把「x64 app 在 Arm 上第一次比較慢」從傳聞變成有分項數字的結論。

**交叉驗證（可信度關鍵）**：在 VM 裝 WPA（ADK for Windows 11 22H2 起有原生 arm64 WPA，
Microsoft Store 也有），開同一份 etl，截 CPU Usage (Sampled)、Disk Usage、Wait Analysis
三張圖放進 README，證明「我的工具算出來的跟 WPA 看到的一致」。這一步同時證明你會用
真正的工具，不是只會呼叫 SDK。

**Gate：README 多一節，含三組對照表 + 2–3 張 WPA 截圖。**

---

## Stage 3 — 開機流程 boot trace（約 2–3 小時）

同一個 analyzer，換 profile 與觸發方式：

```powershell
wpr -boottrace -addboot GeneralProfile -filemode
# 重開機，開機後
wpr -boottrace -stopboot boot.etl
```

- 指標：開機各階段耗時、開機期間磁碟 IO 總量與服務時間、哪些服務／程序吃掉最多 CPU
- **VM 是這一階的優勢**：用 `vmrun` 從 host 自動化
  `revertToSnapshot` → `start` → 等待 → `runProgramInGuest` 收 trace，
  得到**逐次位元相同的初始狀態**——這是實體機做不到的量測嚴謹度，值得在 README 寫成
  一個方法論小節（冷態的可重現性本身就是 flaky 量測的解法）。
- 對照組建議：安裝一個開機自啟動項目 前/後，量它對開機的實際代價。

**Gate：README 多一節，含開機階段分解表 + 一組前後對照。**

---

## Stage 4 — 儲存 I/O 延遲微基準（約 3 小時以上）

自寫小型 IO 負載（C#，就放在同一個 exe 加一個 verb），掃 queue depth × block size ×
順序/隨機，同時錄 ETW Disk IO：

- 比對「應用層量到的延遲」與「ETW 的磁碟服務時間」——差距就是排隊與檔案系統層的成本
- 在 VM 上跑會看到 host 檔案系統的影響，這本身就是一個有趣的對照
- 這一階跟你既有經歷連結最弱，排最後；沒時間就跳過

---

## Stage 5 — 選配加分（有時間才做）

- **AI 輔助 RCA**：`summary.json` 餵給 `claude -p`，產生假設與「下一步該補抓哪個 trace」
  的建議段落。**框架要講清楚**：AI 是在**已由程式確定性萃取出的結構化指標**之上做推論，
  不是把 raw ETL 丟給模型——這個界線本身就是面試的加分回答。對應 JD 的
  「Drive the adoption of AI-powered solutions to accelerate performance root cause analysis」。
- **WPA add-in**：用 `Microsoft.Performance.SDK` 寫一個外掛，把 `summary.json` 當成 WPA
  表格顯示。直接命中 JD 的「WPA plugin model」，但成本最高，排最後。

---

## 關鍵風險與已知陷阱

- `wpr` 需要**系統管理員權限**；停止 trace（`-stop`）本身要數秒，計時要排除。
- 清 `C:\Windows\XtaCache` 需先停 `XtaCache` 服務，且需系統權限；重開機更保險。
- VM 內的時間戳來自虛擬化 QPC，可能比實體機抖——**所以 A/B 一定要交錯執行**，
  而且不要跨機器直接比絕對數字，只比同機同時段的相對差異。
- Defender 即時掃描會嚴重干擾冷啟動量測。不要偷偷關掉就跑，要**兩種都量並揭露**。
- ADK 安裝在 arm64 VM 上可行（22H2 起 WPA 有原生 arm64 建置），但下載大，Stage 2 再裝。
- 別花時間在符號伺服器上：module-level 歸因不需要 pdb。

---

## 驗證方式

每一階的 gate 就是驗證點，都是可執行的：

```powershell
# Stage 0
dotnet run --project src/LaunchLab -- analyze results/smoke.etl   # 應列出 7z.exe 的起訖時間

# Stage 1（系統管理員 PowerShell）
.\scripts\run.ps1 -Configs cold,warm -N 20 -Interleave
dotnet run --project src/LaunchLab -- analyze results/*.etl
dotnet run --project src/LaunchLab -- report                      # 產 results/report.md

# Stage 2（arm64 VM 內）
.\scripts\run.ps1 -Configs native-arm64,x64-warm,x64-cold -N 20 -Interleave
# 再用 WPA 開同一份 etl 對照，截圖存 results/
```

正確性自我檢查（不要跳過）：把 analyzer 算出的某一次 T_startup 與磁碟服務時間，
拿 WPA 手動框同一段區間比對，數字要對得上。**這個交叉驗證同時是作品的賣點，也是
唯一能證明分析器沒寫錯的方法。**

---

## README 該長什麼樣（決定這個作品的成敗）

順序：一句話結論（帶數字）→ 指標定義 → 量測方法（交錯、N、環境揭露）→ 結果表
→ 歸因與 RCA → 交叉驗證截圖 → 已知限制。中文摘要放最前面三行，其餘英文。

**最重要的一句是第一句**：例如「在 Windows on Arm 上，x64 模擬程式的首次啟動比原生
arm64 版慢 N 倍，其中 X% 的成本落在 `xtajit64.dll` 的翻譯，Y% 落在 `.JC` 快取寫入的磁碟
IO；快取建立後差距縮到 Z%。」——招募方掃一眼就知道你會做這件事。
