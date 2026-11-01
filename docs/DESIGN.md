# EjectScope — 设计文档（v0.1 草案）

> **一句话**：U 盘弹出失败时，告诉你**是谁占着、它重不重要、现在拔会丢什么、以及损失最小的解法**。
>
> 代号 `EjectScope`（占位，可换 `WhoHoldsMyUSB` / `EjectDoctor` / `UsbLockFinder`）。
> 本文件是仓库的第一份文档，也是 v1.0 的功能契约。

---

## 0. 定位与非目标

### 0.1 要解决的问题

Windows 上「无法停止"通用卷"设备」「设备正在使用中」，用户面对的是一个**没有任何信息**的对话框：
不知道是谁、不知道重不重要、不知道拔了会怎样。于是只能二选一：

- 直接拔 → 可能丢数据、可能损坏文件系统
- 一直等 → 可能永远等不到

本工具把这个黑盒变成一张**可判断的清单**。

### 0.2 非目标（明确不做，写进 README）

| 不做 | 原因 |
|---|---|
| 设备管控 / 只读 / 白名单 / 阻断 | 那是 DLP 领域，需要内核驱动 |
| **关闭单个文件句柄** | Windows 无用户态 API，需驱动伪造关闭，极易 BSOD，且与杀软行为无异 |
| 自动处置 / 静默杀进程 | 与恶意软件行为重合，且必然误伤 |
| 开机自启 / 常驻服务 / 后台监控 | 本工具是**用完即走**的诊断器；常驻会显著提高被 AV 标记的概率 |
| 联网 / 遥测 / 自动更新 / 崩溃上报 | 供应链与隐私风险 |
| 读取文件**内容** | 见 §8.1 硬性不变量 I1 |

---

## 1. 设计原则

1. **诊断优先，处置最后。** v1 的默认交付形态是"只读诊断器"，处置能力是可选开关。
2. **默认只读，处置需显式 + 二次确认。** 没有任何自动执行路径。
3. **永远标注覆盖率。** "有 3 个进程无权查看"比"只列出 2 个"重要得多——漏报比报错危险。
4. **说人话。** 不显示 `GrantedAccess=0x12019F`，显示"正在写入"。
5. **不留痕迹。** 不装服务、不写注册表、不建自启项、不缓存到临时目录以外。
6. **权限最小化。** `asInvoker` 启动；只有需要全量句柄枚举时才提示"以管理员身份重新启动"。

---

## 2. 真实案例库（"例子"）

这是本工具存在的理由。每一条都是 Windows 上真实会遇到的「弹不出」，并且**对应一种不同的处置策略**。

| # | 场景 | 用户看到的现象 | 检测特征 | 推荐处置 | 为什么不能直接杀 |
|---|---|---|---|---|---|
| 1 | **同步盘正在同步**（OneDrive / 坚果云 / 百度网盘 / Dropbox） | 文件明明复制完了，还是弹不出 | 写句柄 + 目标文件 size/mtime 在 2s 采样内变化 | 托盘右键「暂停同步」→ 再弹出 | 强杀会中断上传，本地可能留半写文件 |
| 2 | **资源管理器窗口 / 缩略图预览** | 关掉窗口就能弹了 | `explorer.exe` 持有目录句柄；路径是目录本身 | 关闭该窗口 | 结束 `explorer.exe` 会闪屏重启、任务栏消失几秒 |
| 3 | **终端的工作目录在该盘** | 完全没打开任何文件，就是弹不出 | `cmd.exe` / `powershell.exe` / `WindowsTerminal.exe` 持有 `E:\` 目录句柄 | 工具直接给一条可复制命令：`cd C:\` | 杀终端会丢掉正在跑的任务与历史 |
| 4 | **编辑器打开了该盘的项目** | 项目关掉才行 | `Code.exe` / `notepad++.exe` 持有大量读句柄（含 `.git`、`node_modules`） | 编辑器里「关闭文件夹」 | 丢失未保存缓冲区 |
| 5 | **Windows Search 索引** | 新插的大容量盘尤其明显，插上几分钟内弹不出 | `SearchIndexer.exe` 持续读句柄，磁盘/CPU 上升 | 等它跑完（通常几分钟） | 受保护进程，杀不掉；且会触发重建索引 |
| 6 | **Defender / 第三方杀软扫描** | 查不到任何占用进程，可就是弹不出 | 要么 `MsMpEng.exe` 读句柄；要么**排他打开失败但进程列表全空** | 等扫描结束 | `MsMpEng` 受保护；杀软**驱动层**锁根本不在进程表里 |
| 7 | **Everything / 搜索类工具索引** | 一插上就弹不出 | `Everything.exe` 直接持有 `\\.\E:` 卷句柄（不是文件句柄） | 在工具里「从索引中移除该卷」 | 重建索引耗时 |
| 8 | **微信 / QQ / 钉钉 的"文件管理"扫描** | 插上就弹不出，过一会儿自己好了 | `WeChat.exe` / `QQ.exe` 扫全盘式读句柄 | 等它扫完，或退出该程序 | 可能丢聊天记录缓存 |
| 9 | **大文件复制/移动正在进行** ⚠️ | 进度条刚走完，还是弹不出 | 写句柄 + 目标文件 size 持续增长 + 卷上 USN 日志活动 | **等。什么也别做。** | **必丢数据。这是本工具最该拦住用户动手的场景** |
| 10 | **媒体播放器 / 照片 App 打开着盘里的文件** | 关掉就好了 | `vlc.exe` / `Photos.exe` / `dllhost.exe`(COM Surrogate) 读句柄 | 关闭播放器 | `dllhost` 是共享宿主，杀它会影响其他缩略图 |
| 11 | **卷影复制 / ReadyBoost / 加密进行中** | 查不到普通进程，排他打开失败 | `vssadmin list shadows` 有该卷；或无任何进程 | 等 VSS 完成；ReadyBoost 在属性里关闭 | 根本没有进程可杀 |
| 12 | **根本不是"被占用"**（反例） | 弹不出，但也没人说在用 | **排他打开成功** → 结论："没有任何进程占用" | 检查 USB 集线器 / 换口换线 / 设备管理器黄色感叹号 | 这一条是**信任的关键**：工具必须敢说"问题不在软件层" |

> 案例 9 和案例 12 是整份设计的两个锚点：
> **9 决定了必须有"写风险评估"并且默认阻止强拔；12 决定了必须有"排他打开测试"并能给出否定结论。**

---

## 3. 检测架构

### 3.1 五层管线

```
L0 卷识别     ── 这是什么盘？是不是 USB？
L1 盘符↔设备   ── E:  →  \Device\HarddiskVolume3     ← 整个匹配的地基
L2 Restart Manager ── 免管理员，精确到"哪个进程在用这个文件"
L3 全系统句柄枚举 ── 需管理员，全量（含后台无窗口进程）
L4 排他打开测试   ── 确认"到底是不是被占用"
L5 汇总          ── 去重、分级、算覆盖率、出结论
```

低层失败不影响高层；每层结果独立标注来源与可信度。

### 3.2 L0 卷识别

```c
UINT type = GetDriveTypeW(L"E:\\");   // DRIVE_REMOVABLE / DRIVE_FIXED / DRIVE_CDROM
```

> ⚠️ **坑**：USB **移动硬盘**绝大多数报告 `DRIVE_FIXED`，不是 `DRIVE_REMOVABLE`。
> 只靠 `GetDriveType` 会把移动硬盘判成内置盘。

必须补一次总线类型查询：

```c
// 1. 卷 → 物理磁盘
DeviceIoControl(hVolume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, ...);  // → DiskNumber
// 2. 物理磁盘 → 总线类型
DeviceIoControl(hDisk, IOCTL_STORAGE_QUERY_PROPERTY,
                StorageDeviceProperty, ...);     // STORAGE_DEVICE_DESCRIPTOR.BusType
// BusType == 0x07 (BusTypeUsb) 才是真 USB
```

同时读取：卷标、文件系统、总容量/可用、是否只读（`FILE_READ_ONLY_VOLUME`）。

### 3.3 L1 盘符 → NT 设备路径（关键地基）

内核返回的句柄名永远是 `\Device\HarddiskVolumeN\...`，不是 `E:\`。必须先做映射：

```c
WCHAR target[1024];
QueryDosDeviceW(L"E:", target, 1024);   // → "\Device\HarddiskVolume3"
```

之后所有句柄名以 `\Device\HarddiskVolume3` 为前缀即为命中。
（顺带处理 `\Device\Mup\...` 网络映射、`\Device\CdRomN` 光驱，避免误判。）

### 3.4 L2 Restart Manager（免管理员，第一优先）

Windows 自带的「文件正在使用」对话框就是它。**不需要管理员权限**，返回进程 + 服务名。

```c
DWORD h; WCHAR key[CCH_RM_SESSION_KEY+1];
RmStartSession(&h, 0, key);
RmRegisterResources(h, 1, &path, 0, NULL, 0, NULL);
UINT needed = 0, count = 0; RM_PROCESS_INFO info[16];
RmGetList(h, &needed, &count, info, &reason);
RmEndSession(h);
```

**限制**：它是**文件级**的。对卷根目录 `E:\` 注册，**不一定有结果**。
所以它只作为第一层，不能作为唯一层——这正是多数小工具失准的原因。

### 3.5 L3 全系统句柄枚举（需管理员，真正的答案）

```
NtQuerySystemInformation(SystemExtendedHandleInformation /* 64 */, buf, len, &ret)
  → SYSTEM_HANDLE_INFORMATION_EX { NumberOfHandles, Handles[] }

每个 SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX:
  { Object, UniqueProcessId, HandleValue, GrantedAccess,
    ObjectTypeIndex, HandleAttributes, ... }
```

步骤：

1. **缓存类型索引**：先对几个已知句柄调 `NtQueryObject(ObjectTypeInformation)`，
   建立 `ObjectTypeIndex → "File"` 映射，先按类型过滤，避免对非 File 对象做昂贵查询。
2. **复制句柄**：`OpenProcess(PROCESS_DUP_HANDLE, pid)` +
   `DuplicateHandle(..., DUPLICATE_SAME_ACCESS)` 复制进本进程。
3. **取名字**：`NtQueryObject(ObjectNameInformation)` → `UNICODE_STRING`。
4. **前缀匹配**：以 L1 得到的 `\Device\HarddiskVolume3` 开头 → 命中。
5. **格式化**：`GetFinalPathNameByHandle` → `E:\照片\IMG_0231.jpg`。
6. **读权限**：从 `GrantedAccess` 判读/写/删除（见 §4）。

> ⚠️ **两个必须处理的坑**：
>
> **坑 A — 卡死**：对**同步命名管道**（pending I/O）调 `NtQueryObject` 会**永久阻塞**。
> 这是 Sysinternals `handle.exe` 的经典问题。解法：名字查询放到**独立工作线程 + 超时**，
> 超时则该句柄标为「无法确认」，不要重试第二次。
>
> **坑 B — 看不见**：`OpenProcess` 对**受保护进程（PPL）**（`csrss`、`lsass`、部分杀软）
> 和 `System` 进程会失败。**必须计数并在 UI 上明说「N 个进程无权查看」**，
> 绝不能静默跳过——这是"漏报比报错更危险"的直接体现。

### 3.6 L4 排他打开测试（区分"被占用" vs "根本不是软件问题"）

```c
HANDLE h = CreateFileW(L"\\\\.\\E:", GENERIC_READ | GENERIC_WRITE,
                       0 /* 不共享 */, NULL, OPEN_EXISTING, 0, NULL);
```

| 返回 | 含义 | 结论 |
|---|---|---|
| 成功 | 无人占用 | 「软件层无占用，问题在硬件/驱动」→ 进入 §6 |
| `ERROR_SHARING_VIOLATION` (32) | 确实被占用 | 继续找占用者 |
| `ERROR_ACCESS_DENIED` (5) | 只读介质 / 权限不足 | 换只读方式再测，标注限制 |

### 3.7 L5 汇总

- 按 `(PID, 进程路径)` 聚合，每个进程列出前 N 个句柄路径 + 总数
- 计算**覆盖率**：`已识别进程数 / (已识别 + 无权查看 + 查询超时)`
- 覆盖率 < 100% 时，结论区必须带警告，不允许给出"可以安全拔出"的强结论

---

## 4. 风险判定引擎

规则按优先级评估，命中第一条即定性：

| 规则 | 条件 | 结论标签 | UI 表现 |
|---|---|---|---|
| **R1** | 存在**写句柄** 且 采样 2s 内文件 size/mtime 变化 | 🔴 **正在写入** | 禁止一键强拔；二次确认文案见 §5 |
| **R2** | 进程属于系统关键集（`System`/`csrss`/`wininit`/`services`/`lsass` + 微软签名 + 在 `System32` 下） | 🟢 **系统关键** | 处置按钮**置灰禁用** |
| **R3** | `OpenProcess` 失败（PPL / System） | ⚫ **无权查看** | 只展示，不提供任何动作 |
| **R4** | 无数字签名 且 路径在 `%TEMP%` / `%APPDATA%` 临时区 | ⚪ **未知程序** | 提示"建议先确认它是什么再动手" |
| **R5** | 句柄仅读权限 | 🟡 **仅读取** | 允许"关闭窗口"（档位 1） |
| **R6** | 卷排他打开成功 | ✅ **无占用** | 进入 §6 结论引擎 |

### 4.1 写权限判读

不解析 `0x12019F` 这种原始值给用户看，直接映射成语义：

```
GENERIC_WRITE | FILE_WRITE_DATA | FILE_WRITE_ATTRIBUTES | FILE_WRITE_EA   → 可写
DELETE                                                                     → 可删除
FILE_READ_DATA | GENERIC_READ                                              → 只读
```

UI 只显示三档：**写 / 读写混合 / 只读**。

### 4.2 进程身份卡（回答"它重不重要"）

点开一行展示：

- 全路径、数字签名主体（微软 / 厂商名 / 未签名）、文件版本
- 命令行、父进程、启动时间、CPU、工作集
- **一句话判断**："这是微软签名系统组件，不要结束它" / "这是你打开的编辑器，关掉窗口即可"

---

## 5. 处置分级

从温和到暴力，**每一档按钮旁边写的是后果，不是"确定/取消"**。

| 档 | 动作 | 实现 | 后果说明（UI 原文） | 默认 |
|---|---|---|---|---|
| **0** | **刷新写缓存** | 打开 `\\.\E:` 后 `FlushFileBuffers` | "不会改变任何东西，很多情况下这一步就解决了" | ✅ 自动先跑一次 |
| **1** | **关闭窗口** | 对该进程主窗口发 `WM_CLOSE` | "等同你点了窗口的 ×，程序会自己决定是否保存" | ✅ 推荐 |
| **2** | **请求正常退出** | `WM_QUERYENDSESSION` / 进程自带的退出通道 | "程序走正常退出流程，可能弹窗问你" | ✅ 推荐 |
| **3** | **暂停该服务/同步程序** | 调用其官方 CLI/COM（如 `OneDrive.exe /shutdown`） | "可恢复，不丢数据" | ⚠️ 需确认 |
| **4** | **结束进程** | `TerminateProcess` | "**该进程未落盘的写入会直接丢失**" | 🔴 有写句柄时必须二次确认 |

> **档位 5「强制卸载卷」已删除**（`FSCTL_LOCK_VOLUME` → `FSCTL_DISMOUNT_VOLUME` / `IOCTL_STORAGE_EJECT_MEDIA`）。
> 理由：绕过所有占用者的代价是"未刷盘写入必然丢失"，而这恰恰是本工具存在的反面。用户随时可以自己物理拔出 U 盘——那是个比任何软件按钮都直接的动作，不需要软件代办。**少一个会毁数据的按钮，比多一个能救急的按钮更值得。**

### 5.1 二次确认文案（示范）

> **OneDrive.exe 正在写入 E:\照片\2024\ —— 现在结束它，最近约 3 秒的写入会丢失，可能导致该文件损坏。**
> 更安全的做法：右键托盘 OneDrive →「暂停同步」，然后重新弹出。
>
> `[ 暂停同步（推荐） ]` `[ 仍然结束进程 ]` `[ 取消 ]`

注意推荐项排第一，且**比危险项更醒目**。

---

## 6. 结论引擎（不是进程的锅时）

当 L4 显示"无占用"或杀掉进程后仍弹不出，按优先级给出排查清单：

1. **写缓存未刷完**（最常见）—— 提示重试"刷新缓存"，或等 10 秒
2. **Windows Search / Defender 正在扫**（驱动层，进程表看不到）
3. **系统还原点 / 卷影复制进行中**
4. **资源管理器窗口 / 预览窗格 / 缩略图缓存**
5. **某程序的当前工作目录在该盘**（给出可复制命令）
6. **该盘是挂载的 VHD / 虚拟光驱**
7. **硬件层**：USB 集线器、线材、供电不足、坏道、设备管理器黄色感叹号
8. **该卷是 BitLocker 加密中**

每一条都给一个「我怎么验证」的具体动作，而不是只给名词。

---

## 7. 已知盲区（写进 README，降低预期）

- **杀软/EDR 驱动层持有的卷锁**：用户态完全不可见 → 表现为"排他打开失败但零占用者"
- **`System` 进程与 PPL 进程的句柄**：即便管理员也打不开
- **网络映射盘 / 光驱 / WSL 挂载的盘**：不在本工具建模范围内
- **已被强制卸载但设备仍占用总线**：属驱动/硬件问题
- **覆盖率不可能是 100%**：工具必须诚实标注

---

## 8. 安全设计（三层）

### 8.1 第一层：用户数据安全

| 编号 | 硬性不变量 | 校验方式 |
|---|---|---|
| **I1** | **永不读取任何文件内容**，只查询句柄元数据、卷信息、文件大小/mtime | 代码审查；`CreateFile` 只允许 `FILE_READ_ATTRIBUTES` 与卷句柄；禁止 `ReadFile` |
| **I2** | **永不修改/删除/重命名用户文件** | 全局禁用写打开；CI 里加静态检查 |
| **I3** | **任何处置前先 `FlushFileBuffers`** | 处置管线强制前置 |
| **I4** | **存在写句柄时，档位 4/5 需二次确认并显示"具体丢什么"** | UI 走 `DangerousActionGuard` |
| **I5** | **覆盖率 < 100% 时禁止输出"可以安全拔出"** | 结论引擎硬门禁 |

### 8.2 第二层：程序行为安全（决定会不会被当成木马）

一个"枚举全系统句柄 + 结束进程"的程序，行为特征和恶意软件高度重合。**下面这些不是可选优化，是发布前提**：

| 编号 | 不变量 | 为什么 |
|---|---|---|
| **B1** | **零持久化**：不装服务、不写 `Run` 键、不建计划任务、不留后台进程 | 用完即走，与恶意软件最大的区别 |
| **B2** | **零网络**：不联网、不遥测、不检查更新、无崩溃上报 | 无外传通道 = 无法被当作窃密工具 |
| **B3** | **零注入**：不 `CreateRemoteThread`、不 `WriteProcessMemory`、不 hook、不读他进程内存 | 只做句柄复制，不做任何"进入别人"的动作 |
| **B4** | **零自动执行**：没有任何后台触发、没有"检测到就杀" | 所有动作都来自一次显式点击 |
| **B5** | **权限最小化**：`asInvoker` 启动，需要时提示"以管理员身份重新启动" | 不是一上来就弹 UAC |
| **B6** | 单文件、无安装器、无捆绑、无第三方二进制 | 可审计 |
| **B7** | 源码可复现构建 + Release 附 SHA256 + （尽量）代码签名 | 抗投毒 |

> 建议在 README 顶部用一句话概括：**「EjectScope 不联网、不自启、不读文件内容、不注入进程。它唯一的权限是用来'看'句柄，用来'动'的只有你点的那一下。」**

### 8.3 第三层：供应链与构建安全

- **构建**：GitHub Actions，公开 workflow，产物由 CI 生成而非个人机器
- **签名**：优先 [SignPath.io](https://signpath.io/)（对开源免费）或 Azure Trusted Signing；无证书时**必须**提供 SHA256 与构建日志
- **依赖**：争取零 NuGet 第三方包，只用 BCL + P/Invoke
- **发布**：只从 Releases 分发，README 里写明"不要从任何第三方站点下载"
- **SBOM**：`dotnet list package` 输出随 Release 附上

### 8.4 威胁模型（被滥用会怎样）

| 滥用方式 | 可行性 | 缓解 |
|---|---|---|
| 改成"一键杀光所有占用进程"的破坏工具 | 高（改一行代码） | 开源可审计；默认只读；危险档位需显式开启；License 免责 |
| 伪装成 EjectScope 投毒 | 中 | 代码签名 + SHA256 + 只从 Releases 下载 + 可复现构建 |
| 拿它当提权/横向工具 | 低 | 不联网、不持久化、不读内容 → 作为载荷毫无价值 |
| 用它发现"谁在监视我的 U 盘" | 低 | 与 Sysinternals `handle.exe` 能力重叠，非新增风险 |

---

## 9. UI 结构

单窗口，三段式（三块信息一次给全，不需要点来点去）：

```
┌ 卷信息 ───────────────────────────────────────────────────┐
│ [E:]  KINGSTON  exFAT   57.3 / 58.6 GB   可移动(USB)  可写 │
│ 状态：被占用 · 4 个进程 / 9 个句柄 · 其中 2 个可写          │
│ 覆盖率：4/5（有 1 个进程无权查看）                          │
└───────────────────────────────────────────────────────────┘

┌ 占用者（按风险排序）──────────────────────────────────────┐
│ 🔴 OneDrive.exe      写   E:\照片\2024\IMG_0231.jpg  ⚠正在写入 │
│ 🟡 explorer.exe      读   E:\（窗口打开 + 缩略图）           │
│ 🟡 SearchIndexer.exe 读   E:\（正在建索引）                  │
│ ⚫ lsass.exe          ?    无权查看                            │
└───────────────────────────────────────────────────────────┘

┌ 结论 ─────────────────────────────────────────────────────┐
│ ⚠ OneDrive 正在写入。现在强拔会丢失最近约 3 秒的写入。       │
│ 推荐：先「暂停同步」再弹出（不丢数据）                       │
│ [ 暂停同步（推荐） ] [ 关闭窗口 ] [ 刷新缓存 ] [ 更多… ]     │
└───────────────────────────────────────────────────────────┘
```

交互硬性要求：

- 首行永远是**结论**，不是列表（用户是来要答案的）
- 系统关键进程的按钮**置灰**，并在 tooltip 说明原因
- 「更多…」里才放档位 4（档位 5 已在设计上删除，永不出现）
- 首次运行 = **只读诊断模式**（无任何处置按钮），第二次起才解锁处置

---

## 10. 技术选型

| 方案 | 体积 | 运行时依赖 | 开发成本 | 适合度 |
|---|---|---|---|---|
| **C# + .NET Framework 4.8**（WPF/WinForms） | ~300 KB 单 exe | Win10 1803+/Win11 **自带** | 低 | ⭐⭐⭐⭐⭐ **推荐** |
| C# + .NET 8 self-contained | ~70 MB | 无 | 低 | ⭐⭐⭐ 体积劝退 |
| Rust + `windows` crate + egui | ~5 MB | 无 | 中高 | ⭐⭐⭐⭐ 想显得"硬核"且零依赖可选 |
| C++ Win32 + Dear ImGui | ~1 MB | 无 | 高 | ⭐⭐⭐ 审计门槛高 |
| Node / Electron | ~100 MB+ | 需 native addon | 中 | ⭐ 原生 API 要绕远，不推荐 |

**结论：C# + .NET Framework 4.8。**
理由：P/Invoke 写 NT 原生 API 最顺手；单 exe 300KB；Win10/11 无需安装任何东西；GitHub 上贡献门槛低。

> 若后期追求"零运行时依赖 + 小体积 + 内存安全"的叙事，再考虑 Rust 重写核心（`ejectscope-core` crate），UI 不动。

---

## 11. 代码骨架

```
EjectScope/
├─ docs/
│  ├─ DESIGN.md              ← 本文档
│  ├─ CASES.md               ← §2 案例库（可独立维护，欢迎 PR 补充）
│  └─ SECURITY-NOTES.md      ← 为什么"关句柄"做不到
├─ src/
│  ├─ EjectScope.App/        ← UI（WPF），只做展示与交互，无逻辑
│  ├─ EjectScope.Core/       ← 纯逻辑，可单测，无 UI 依赖
│  │   ├─ VolumeLocator.cs        L0 + L1（盘符↔设备、BusType）
│  │   ├─ RestartManagerProbe.cs  L2
│  │   ├─ HandleEnumerator.cs     L3（含超时线程）
│  │   ├─ ExclusiveOpenProbe.cs   L4
│  │   ├─ RiskEngine.cs           §4 规则
│  │   ├─ VerdictEngine.cs        §6 结论
│  │   └─ Native/                 所有 P/Invoke 集中于此
│  └─ EjectScope.Cli/        ← 命令行版（便于测试与 CI 冒烟）
├─ tests/
│  └─ EjectScope.Core.Tests/
└─ .github/workflows/build.yml
```

### 11.1 P/Invoke 清单（v1 需要的全部原生调用）

| 用途 | API |
|---|---|
| 卷识别 | `GetDriveTypeW`, `GetVolumeInformationW`, `QueryDosDeviceW` |
| 是否 USB | `DeviceIoControl(IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS)`, `IOCTL_STORAGE_QUERY_PROPERTY` |
| 排他测试 / flush | `CreateFileW`, `FlushFileBuffers` |
| Restart Manager | `RmStartSession`, `RmRegisterResources`, `RmGetList`, `RmEndSession` |
| 句柄枚举 | `NtQuerySystemInformation(64)`, `OpenProcess`, `DuplicateHandle`, `CloseHandle` |
| 句柄命名 | `NtQueryObject(ObjectNameInformation / ObjectTypeInformation)`, `GetFinalPathNameByHandleW` |
| 处置（P2+） | `PostMessageW(WM_CLOSE)`, `TerminateProcess` |

> **不会出现的 API**：`FSCTL_LOCK_VOLUME`、`FSCTL_DISMOUNT_VOLUME`、`IOCTL_STORAGE_EJECT_MEDIA`。
> 本工具**没有**卸载卷的能力，这是设计约束而非功能缺失 —— 代码里搜不到这几个常量，就是最硬的保证。

### 11.2 核心数据结构

```csharp
record Holder(
    int Pid, string ProcessPath, string? Signer,
    RiskLevel Risk,                       // Critical/System/ReadOnly/Unknown/Denied
    IReadOnlyList<HandleRef> Handles,     // 最多保留 32 条用于展示
    int TotalHandleCount,
    bool IsWriting,
    bool IsDenied                         // OpenProcess 失败
);

record HandleRef(string Path, AccessMask Access, string NtDevicePath);

record Diagnosis(
    string VolumeLetter,
    IReadOnlyList<Holder> Holders,
    CoverageReport Coverage,              // 识别/无权/超时
    ExclusiveOpenResult ExclusiveOpen,
    Verdict Verdict,                      // NotInUse / InUse / Writing / Unknown / NotSoftware
    IReadOnlyList<Recommendation> Recommendations
);
```

---

## 12. 里程碑与验收标准

### P0 — 技术验证（1～2 天，不写 UI）

- [ ] `QueryDosDevice` 正确把 `E:` 映射到卷设备路径
- [ ] 枚举句柄能列出 `explorer.exe` 打开该盘的句柄
- [ ] 对同步命名管道**不卡死**（超时机制生效）
- [ ] 排他打开三种返回值都能正确区分
- [ ] **验收**：在真机上，用记事本打开 `E:\test.txt`，工具能报出 `notepad.exe` + 该文件路径

### P1 — 只读诊断器（可发布 v0.1）

- [ ] L0~L4 全通，UI 三段式
- [ ] 进程身份卡（签名/路径/命令行/父子关系）
- [ ] 写风险判定（2s 采样）
- [ ] 覆盖率展示 + 无权查看计数
- [ ] 结论引擎（§6 八条）
- [ ] **无任何处置按钮**
- [ ] **验收**：案例库 §2 中的 1/2/3/4/12 五个场景可复现并给出正确建议

### P2 — 安全处置（v0.2）

- [ ] 档位 0/1/2/4 + `DangerousActionGuard`
- [ ] 二次确认文案 + 推荐项优先
- [ ] 审计日志（可选、默认关闭、只记盘符+进程名，**不记文件名**）
- [ ] **验收**：案例 9（正在复制文件）下，工具必须阻止用户强拔

### P3 — 发布（v1.0）

- [ ] CI 构建、SHA256、代码签名
- [ ] README / CASES / SECURITY / LICENSE / CONTRIBUTING / issue 模板
- [ ] 提交微软误报反馈（如被 Defender 标记）

---

## 13. GitHub 工程化

### 13.1 README 大纲

```
1. 一句话 + 一张截图（三段式界面）
2. 为什么做这个（"设备正在使用中"对话框什么也不告诉你）
3. 安全承诺（B1~B7 那张表，放最显眼位置）
4. 它能告诉你什么（案例库前 6 条）
5. 它不能做什么（盲区 + 非目标）
6. 下载与校验（SHA256 / 签名）
7. 从源码构建（给定版本 .NET SDK 与命令）
8. 常见问题（"为什么需要管理员？" "为什么不能关句柄？"）
9. 许可证与免责
```

### 13.2 必需文件

| 文件 | 内容要点 |
|---|---|
| `LICENSE` | **MIT**（最省事）；若担心专利可换 Apache-2.0 |
| `SECURITY.md` | 漏洞报告渠道；明确"本工具需要管理员权限"这一固有风险；支持的版本 |
| `CONTRIBUTING.md` | 代码风格；**新增原生调用必须先补 P/Invoke 清单与 §4 规则说明** |
| `.github/workflows/build.yml` | `dotnet build` + `test` + 产出 artifact + 计算 SHA256 |
| `.github/ISSUE_TEMPLATE/bug.md` | 强制要求贴"卷信息 + 占用者列表"（脱敏后） |

### 13.3 免责声明要点

- 本工具按"现状"提供，不保证能解决所有弹出失败
- **本工具不会替你卸载卷，也不会替你拔 U 盘**
- 不承担因误杀进程导致的损失
- 建议在大规模使用前先在非关键数据上验证

---

## 14. 决策记录（已定夺）

| # | 问题 | 结论 | 理由 |
|---|---|---|---|
| 1 | 是否保留档位 5（强制卸载）？ | **删除** | 未刷盘写入必然丢失，与工具目的相反；用户可自行物理拔出 |
| 2 | 要不要做 CLI 版？ | **要** | CI 冒烟测试需要；高级用户会喜欢 `ejectscope E:` |
| 3 | 日志默认开还是关？ | **默认关** | 开启时提供"隐私模式"（只记盘符+进程名） |
| 4 | 是否支持非 USB 卷（内置盘 / VHD）？ | **支持诊断** | UI 标注"这不是 U 盘"，但仍可查占用 |

### 14.1 仍待定（随实现推进再定）

| 问题 | 备选 | 暂定 |
|---|---|---|
| UI 框架 | WPF / WinForms | **WPF**（样式可控，深色界面好做） |
| 提权时机 | 启动即提权 / 按需 | **按需**（先只读诊断，点处置才提权） |
| 进程签名验证 | `WinVerifyTrust` / 只读 WinTrust 缓存 | **`WinVerifyTrust`**，失败不阻断只降级显示 |

---

## 15. 参考

- [USB Raptor FAQ](https://sourceforge.net/p/usbraptor/wiki/FAQ/) — "检测 U 盘上特定文件 → 执行动作"的成熟先例（锁屏方向）
- [Microsoft Q&A：U 盘插入时启动程序](https://learn.microsoft.com/en-gb/answers/questions/2202955/launching-a-program-when-a-usb-drive-is-inserted-u) — 任务计划程序事件触发不可靠的实测；`Win32_VolumeChangeEvent` 方案
- [KB971029 讨论](https://learn.microsoft.com/en-za/answers/questions/2483125/kb971029-update-to-the-autoplay-functionality-in-w) — `autorun.inf` 对 U 盘失效的历史原因
- [npm usb-detection](https://registry.npmjs.org/usb-detection) — 已于 2023 停维护，官方指向 `node-usb`（仅供技术选型参考）

---

*本文档随实现推进更新；§2 案例库欢迎以 PR 形式补充真实场景。*

---

## 16. 实现状态与设计偏差（v0.1，2026-10）

已落地：L0 卷识别（含总线类型）、L1 盘符→NT 设备名、L3 全系统句柄枚举（按**卷序列号**定位文件句柄、按 NT 路径定位目录/卷句柄）、L4 卷级独占打开、R1 写活动观察窗口、覆盖率计数、命令行与 WPF 图形界面。

实现过程中被真机结果推翻/修正的设计点，逐条记录（这类记录比功能清单更有价值）：

| # | 原设计 | 真机事实 | 现在的做法 |
|---|---|---|---|
| 1 | 独占打开成功 ⇒ 判定「没有程序占用」 | **有进程持有盘上文件时，卷级独占打开依然成功**（Windows 11 10.0.26200 实测，U 盘与本机盘皆然） | 独占打开只能证明「没有卷级持有者」，不能证明「没有文件持有者」。因此永远先做句柄扫描，独占结果只用于区分「没人占但弹不出（驱动/硬件层）」 |
| 2 | 靠 `QueryDosDevice` 得到的 `\Device\HarddiskVolumeN` 匹配即可 | 同一个物理盘在不同进程里可能以不同卷对象出现；另有一批句柄名形如 `\Device\HarddiskVolume` 无编号 | 主判据改为**卷序列号**（`GetFileInformationByHandle`）；NT 路径退化为兜底 |
| 3 | Restart Manager 作为第一层信号 | 本轮先做了更底层的句柄枚举，RM 尚未接入 | 列为 v0.2 待办（免管理员快速预筛） |
| 4 | 档位 5 强制卸载卷 | 用户决定删除（自己会拔） | 已删除，且 `Native/Win32.cs` 顶部声明永不引入相关 FSCTL/IOCTL |
| 5 | UI 用 XAML | 本机只有 C# 5 自带编译器，驱动不了 XAML 构建任务 | 纯代码 WPF；仓库同时提供 csproj 供 SDK 构建，两条路径产物一致 |

尚未实现：处置（档 0–4）、进程签名验证（`WinVerifyTrust`）、日志/隐私模式、代码签名与发布流水线。

已知不完美：普通权限下覆盖率会因「无权查看的进程」而不完整，界面如实显示为「无法确认」；驱动层/杀软过滤驱动的持有者始终可能不可见。