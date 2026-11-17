# 设计方案：每日配额模式（v1.1）

> 目标：新增第三种模式——"每天最多累计玩 X 分钟，什么时候玩随意"。
> 与现有两种模式（固定时段 / 连续时长）并列，通过同一套模式切换机制选择。

## 一、概念与定位

| 模式 | 限制心智模型 | 适合场景 |
|---|---|---|
| 固定时段 | 只能在这个钟点区间玩 | 作息约束（如 20:00-22:30） |
| 连续时长 | 每轮最多玩 X 分钟，可续期 N 次 | 单次克制 |
| **每日配额（新）** | **今天总共只能玩 X 分钟** | 总量控制（更自然的自我管理模型） |

## 二、配置层（Cfg）

新增字段（config.json 键名 / C# 静态字段 / 默认值）：

```json
{
  "mode": 2,
  "quotaMinutes": 120,
  "quotaWarnMin": 10,
  "tempQuotaMin": 0,
  "quotaDate": "2026-11-17",
  "quotaUsedSec": 2700,
  "quotaExtraSec": 0
}
```

- `mode`（int）：0=固定时段，1=连续时长，2=每日配额
  - **向后兼容**：Load() 时若 config 里没有 `mode` 键，按旧 `useSchedule` 推导（true→0，false→1），旧配置无感升级
- `quotaMinutes`：每日累计可玩分钟数，默认 120
- `quotaWarnMin`：剩余多少分钟时预警，默认 10
- `tempQuotaMin`：今天的临时配额覆盖（0=不覆盖），配合现有 TempDate 机制，跨天自动失效
- `quotaDate` / `quotaUsedSec` / `quotaExtraSec`：**运行状态持久化**
  - 关键决策：已用时长写进 config.json。否则"重启 GameGuard = 配额清零"，绕过成本太低

## 三、引擎层（Engine）

### 3.1 运行时状态

```csharp
public static string QuotaDate = "";      // 已用时长归属的日期 yyyy-MM-dd
public static double QuotaUsedSec = 0;    // 今日已消耗秒数
public static double QuotaExtraSec = 0;   // 续期追加的秒数
public static DateTime? QuotaLastTick = null;  // 上次计费时刻
public static string QuotaWarnKey = null;      // 预警去重（同一天只弹一次）
```

### 3.2 状态机

```
                 ┌──────────── 日期变化（每天 00:00 或首次运行）────────────┐
                 │  QuotaDate=今天, QuotaUsedSec=0, ExtraSec=0, 持久化      │
                 ▼                                                          │
  ┌──────────┐   剩余≤预警线   ┌──────────┐   剩余≤0    ┌──────────────┐     │
  │ QuotaOK  │ ─────────────► │ QuotaWarn│ ──────────► │QuotaExhausted│─────┘
  └──────────┘                └──────────┘             └──────────────┘
                                  │ 预警弹窗（每天一次）        │
                                  ▼                            ▼
                            仅提示状态              弹出守护对话框：
                                                    ├─ 结束游戏 → StopGameNow
                                                    └─ 再玩 5 分钟（剩 N 次）
                                                        → ExtraSec += 300, ExtendUsed++
```

### 3.3 计费逻辑（Poll 内新增分支，mode==2）

```csharp
// 1. 日期切换检查（游戏跨零点运行 → 自动获得新一天配额，已知取舍）
if (QuotaDate != today) { 重置 QuotaDate/UsedSec/ExtraSec/ExtendUsed; Save(); }

// 2. 累计：游戏进程存在才计费（挂机也算，与连续模式哲学一致）
if (procs.Count > 0) {
    if (QuotaLastTick == null) { QuotaLastTick = now; }          // 刚启动，不补计
    else {
        double gap = (now - QuotaLastTick.Value).TotalSeconds;
        if (gap <= 120) QuotaUsedSec += gap;                      // 正常轮询累计
        // gap > 120s：系统睡眠/恢复，只记时刻不计费，防止把睡眠时间算进去
        QuotaLastTick = now;
    }
} else { QuotaLastTick = null; }                                   // 游戏退出即停止累计

// 3. 判断
double total = (临时覆盖 > 0 ? 临时覆盖 : QuotaMinutes) * 60 + QuotaExtraSec;
double remainSec = total - QuotaUsedSec;
```

要点：
- **按实际两次轮询的时间差累计**，不依赖 `PollSeconds` 常量，轮询间隔改动不影响准确性
- **120 秒断档保护**：休眠唤醒、进程短暂失检时不会把几分钟的 gap 一次性计入
- 状态文本（托盘 + 面板）：
  - 游玩中：`状态：游玩中，今日剩 X 小时 Y 分`
  - 未游玩：`状态：今日已用 45 分 / 配额 120 分`

### 3.4 耗尽处理

复用现有 `ShowGuardDialog`（和连续时长到点完全同构）：
- 「结束游戏」→ `StopGameNow("今日配额已用完")`
- 「再玩 5 分钟」→ `QuotaExtraSec += Cfg.ExtendMinutes * 60`，`ExtendUsed++`，仍受 `MaxExtend` 上限约束
- 续期次数**按天计数**（随日期重置一起清零）

## 四、UI 层改动

### 4.1 面板（PanelWindow）

- 模式单选组从 2 个变 3 个：`○ 固定时段  ○ 连续时长  ○ 每日配额`
- 选中「每日配额」时显示配额参数组（复用现有栅格）：

```
  每日配额   [ 120 ] 分钟
  预警       [ 10  ] 分钟（临近用完提醒）
```

- 状态行（GetStatusLines）新增：`今日已用 45 分 / 120 分，剩 75 分`
- 单选切换联动：选配额时禁用时段/连续参数控件（与现有两组互斥逻辑一致）

### 4.2 托盘菜单

- 新增 `ItemModeQuota`，文字 `每日配额（120 分钟）`
- `UpdateTrayMenuText` 与状态图标文字同步更新

### 4.3 临时调整（TempDialog）

- 新增一行：`今天配额临时改为 [    ] 分钟`（留空/0 = 不覆盖）
- 与现有 TempDate/TempStart/TempEnd 同日生效、同日失效

### 4.4 守护对话框

无需改：耗尽时直接复用 `ShowGuardDialog`，按钮文案 `再玩 5 分钟（剩 N 次）` 自动成立。

## 五、边界情况

| 情况 | 处理 | 取舍说明 |
|---|---|---|
| 游戏跨零点运行 | 00:00 自动重置，立即获得新配额 | 简单、可预期；不搞"按小时切分"的复杂度 |
| 系统睡眠/休眠 | gap > 120s 不计费 | 防止睡觉时忘关游戏把 8 小时算进去 |
| 游戏短暂崩溃重启 | QuotaLastTick 清空，重启后从头计时刻，漏计 ≤ 轮询间隔 | 可接受 |
| 多个游戏 | 所有受管游戏**共享同一配额池** | 符合"控制总游戏时间"的初衷；按游戏独立配额留作后续 |
| 手改 config.json 绕过 | 不在 v1.1 解决 | 留给"配置校验和"改进项，避免本次范围膨胀 |
| 暂停控制 | 暂停期间整条 Poll 短路，配额自然不计 | 复用现有 GetPausedState，零改动 |
| 与固定时段叠加 | v1.1 不做（三模式互斥） | "配额+时段"双约束留作 v1.2 的 `quotaInsideSchedule` 开关 |

## 六、改动清单与工作量

| 文件 | 改动 |
|---|---|
| `GameGuardApp.cs` | Cfg 加 7 字段 + Load/Save；Engine 加状态机与计费；Poll 加 mode==2 分支；PanelWindow 加第三单选与参数组；MainForm 托盘菜单；TempDialog 加一行 |
| `config.example.json` | 加新键示例 |
| `README.md` / `使用说明.txt` | 模式说明加第三种 |
| 版本 | 1.0.0 → 1.1.0（程序集属性 + make-release.bat 目录名） |

预计单文件改动 ~250 行，全部在现有架构分支内扩展，**不引入新依赖、不改动现有两种模式的行为**。
