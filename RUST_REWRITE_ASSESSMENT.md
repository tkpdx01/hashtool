# 用 Rust 重构 fHash 的可行性评估

**问题**：能不能用 Rust 重构 fHash，并保持 UI 的简洁性？

**结论**：**能 —— 但只重构「核心」，不要重写 UI。** 把 C++ 哈希核心
（`trunk/source/Algorithms` + `trunk/source/Common/HashEngine.cpp`）换成一个
Rust 静态库，通过 C FFI 暴露给各平台；所有原生 UI（macOS 的 Swift、Windows 的
WinUI 3 C# / UWP C++ / MFC）**一行都不用动**。这恰恰是「保持 UI 简洁」的最佳方式：
当前的 UI 已经是各平台最原生、最轻量的形态，用 Rust GUI 框架（egui/iced/slint）重写
反而会丢掉原生质感、暗色模式、Liquid Glass、Finder/Explorer 右键集成等。

本仓库的 `rust-prototype/` 已用可运行代码证明了核心可行性（见下）。

---

## 1. 现状架构

```
        ┌────────── 共享 C++ 核心 ──────────┐
        │ Algorithms/  MD5 SHA1 SHA256 SHA512│
        │ Common/HashEngine.cpp（单次读文件，│
        │   一个缓冲喂给 4 个算法，多线程）   │
        └───────────────┬───────────────────┘
                        │ UIBridgeBase（纯虚回调接口）
   ┌────────────────────┼─────────────────────────┐
   │ macOS              │ Windows                  │
   │ Swift + Obj-C++ 桥  │ WinUI3(C#) / UWP / MFC   │
   └────────────────────┴─────────────────────────┘
```

关键事实：**核心是纯计算 + 一个回调接口（`UIBridgeBase`）**，UI 与算法已经解耦。
这正是替换核心的理想切面。

## 2. 目标架构

```
        ┌────────── Rust 核心（cargo staticlib）──────┐
        │ md-5 / sha1 / sha2 crate                     │
        │ MultiHasher：单次读文件，一个缓冲喂 4 个算法 │
        │ #[no_mangle] extern "C"  →  C FFI            │
        └───────────────┬──────────────────────────────┘
                        │  libfhash_core.a + fhash_core.h
   ┌────────────────────┼─────────────────────────┐
   │ macOS（Swift 不变）│ Windows（C#/C++ 不变）   │
   └────────────────────┴─────────────────────────┘
```

UI 层完全不变，只是底层链接的是 Rust 的 `.a`/`.lib` 而非 C++ 目标。

## 3. FFI 设计（原型已实现）

```c
typedef struct {
    char md5[33];     // 32 hex + NUL，大写（匹配 C++ %02X）
    char sha1[41];
    char sha256[65];
    char sha512[129];
} FHashDigestsC;

int fhash_core_hash_file(const char *path, FHashDigestsC *out);   // 0 成功
int fhash_core_hash_buffer(const uint8_t *data, size_t len, FHashDigestsC *out);
```

- 输出**大写 hex**，与现有引擎 `%02X` 逐字节一致；UI 仍按需 lower/upper（与今天行为相同）。
- 进度回调 / 取消：保留现有 `UIBridgeBase` 语义即可，FFI 增加
  `progress_cb`（函数指针 + 上下文）与 `should_stop` 标志；或保持 C++ 的
  `HashEngine` 线程编排不变，只把「算法更新」下沉到 Rust（见迁移路径 A）。

## 4. 各平台构建接入

| 平台 | 接入方式 |
|------|----------|
| macOS (Xcode) | 加一个 "Run Script" build phase 跑 `cargo build --release`，把 `libfhash_core.a` 加入 Link Binary；FFI 头加进 bridging header。universal binary 用 `cargo build --target aarch64-apple-darwin` + `x86_64-apple-darwin` 再 `lipo`。 |
| Windows WinUI 3 / UWP / MFC | MSBuild 加自定义 target 跑 `cargo build`，链接 `fhash_core.lib`；用 `corrosion` 或手写 `.targets` 都可。 |

## 5. 收益 vs 代价

**收益**
- 内存安全：哈希核心处理任意外部文件字节，Rust 消除该层的越界/UAF 风险。
- 代码大幅简化：`sha512.cpp` 等手写实现（数百行）→ 成熟 crate 几行调用。
- 现代工具链：`cargo test` 内建对拍测试（本原型已含 NIST 向量）、`cargo audit`。
- 可选加速：未来接 `sha2` 的 asm/硬件加速 feature。

**代价**
- 多一条工具链（Rust）与一个 FFI 边界要维护。
- CI / 打包脚本要加 cargo 步骤（macOS universal 需双 target + lipo）。
- 跨 FFI 的字符串/错误处理需谨慎（已用固定缓冲 + 返回码规避）。

## 6. 迁移路径（增量、低风险）

- **路径 A（推荐，先小步）**：只把 `Algorithms/*` 的 4 个算法换成 Rust，
  `HashEngine.cpp` 的多线程/进度/取消编排**保持 C++ 不动**，仅把
  `MD5Update/SHA*_Update/Final` 替换为对 Rust FFI 的调用。改动面最小，回归风险低。
- **路径 B（更彻底）**：把整个 `HashEngine` 的读文件循环也搬进 Rust，
  C++ 只剩一层薄 `UIBridge` 适配。收益更大，改动更多。

建议先走 A，绿测后再评估 B。

## 7. 工作量与风险

| 项 | 估计 |
|----|------|
| 路径 A（算法层替换 + 双平台构建接入） | 中等；核心代码已由本原型覆盖，主要成本在构建脚本与 CI |
| 路径 B（整引擎下沉） | 较大；需重做线程/进度/取消的 FFI |
| 风险 | 低-中：输出已证明逐字节一致；最大不确定性是 Windows MSBuild 接入与 macOS universal 打包 |

## 8. 原型证据（`rust-prototype/`）

- `cargo test`：MD5/SHA1/SHA256/SHA512 对 `"abc"` 与空串的 NIST/RFC 向量全部通过，
  并验证了 FFI buffer 往返。
- `cargo build --release`：产出 `libfhash_core.a`（即集成用静态库）。
- `fhash_demo ../LICENSE` 的输出与系统 `md5`/`shasum`（亦即 C++ 引擎口径）逐字节一致。

> 一句话：**Rust 重构核心可行、风险可控、且与「保持 UI 简洁」目标一致 —— 因为
> Rust 根本不碰 UI。**
