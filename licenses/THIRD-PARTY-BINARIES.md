# 第三方声明 · 随包 DLL（CUDA / FFmpeg / curl）

**本文件由分发方（打包人）编写，不是上游 NInfer 项目的文件。**
上游 `NOTICE.md` 明确写着这三类二进制的许可原文**未随包**（其 §6 第 4–7 条、§7），
并注明"对外分发这些二进制时，应把许可文本补进包内，或明确改写声明口径"。
本文件就是按这句话补的。

⚠️ **面向再分发者**：你若把这个包再转发给别人，**请连同本目录一起转**，不要只发 `engine\`。

---

## 1. 先说清楚这个包里有什么、没有什么

| 类别 | 是否随本包 | 许可后果 |
|---|---|---|
| `ninfer-serve-86/89/120a.exe` | ✅ 随包 | Apache-2.0 —— 见 `LICENSE-Apache-2.0.txt` 与 `NOTICE.md` |
| CUDA 运行库 3 个 | ✅ 随包 | NVIDIA CUDA Toolkit EULA 的**可再分发运行库**条款 |
| FFmpeg 运行库 5 个 | ✅ 随包 | **GPL v2 or later** ← 义务最重，见下 §3 |
| `libcurl-x64.dll` | ✅ 随包 | curl 许可（MIT 类） |
| **模型权重（`.ninfer`）** | ❌ **不随包** | 与引擎分开分发，各自许可自负 |

---

## 2. NVIDIA CUDA 运行库

| 文件 | 文件版本 |
|---|---|
| `cublas64_13.dll` | 6.14.11.1351 |
| `cublasLt64_13.dll` | 6.14.11.1351 |
| `cudart64_13.dll` | 6.14.11.13030 |

**许可**：NVIDIA CUDA Toolkit End User License Agreement（EULA）中的
**"可再分发的运行库"（Redistributable Runtime Libraries）** 条款。

**NVIDIA 的 EULA 原文不在本包内**，理由与现状：

- 该 EULA 由 NVIDIA 随 CUDA Toolkit 一起提供，官方获取地址：
  <https://docs.nvidia.com/cuda/eula/index.html>
- **上游 `NOTICE.md` §6 第 5 条自己登记为"未核"**：其"可再分发清单"是否覆盖
  这三支 CUDA 13 运行库，**未逐条对上**。
- 本包**沿用同一现状**：不代为断言"可再分发清单覆盖与否"，
  只如实说明 **NVIDIA 的 EULA 是这三支 DLL 的许可依据**，
  并要求再分发者自行阅读该 EULA 并遵守其条款。

> **这是本包唯一一处"许可原文未随包"的地方**，特此显著标出，不用好话糊过去。

---

## 3. FFmpeg 运行库 —— **GPL v2 or later**（义务最重，务必读）

| 文件 |
|---|
| `avcodec-63.dll` |
| `avformat-63.dll` |
| `avutil-61.dll` |
| `swresample-7.dll` |
| `swscale-10.dll` |

### 3.1 许可版本已**独立复核**，不是照抄

上游 `NOTICE.md` §6 第 6 条记载：随包二进制实证为 **GPL v2 or later**
（configure 含 `--enable-gpl --enable-version3`），旧文档写的"LGPL/GPL"**以 GPL 为准**。

本包**独立复核了同一结论**，方法是从 `avcodec-63.dll` 里提取内嵌的 FFmpeg configure 指纹串，
长度 1208 字符，其中包含：

```
--enable-gpl
--enable-version3
--enable-shared
--disable-static
--prefix=/ffbuild/prefix
--arch=x86_64 --target-os=mingw32
```

⇒ **该构建启用了 GPL 部件，属于 GPL v2 or later，不是 LGPL。**
（`--enable-version3` 表示 "or later"。）

### 3.2 这意味着什么（**请再分发者务必理解**）

GPL 与 Apache-2.0 / MIT 的**义务量级不同**：

| | Apache-2.0 / MIT | **GPL v2 or later** |
|---|---|---|
| 附许可全文 | 要求 | 要求 |
| 保留版权声明 | 要求 | 要求 |
| **提供对应源码** | 不要求 | **要求（或提供书面要约）** |
| 传染范围 | 无 | 对本衍生物有 copyleft 要求 |

**具体到本包**：这 5 个 DLL 是**独立的 FFmpeg 共享库**（`.dll`，动态链接），
不是与引擎静态链接的代码。因此：

- 引擎本身（Apache-2.0）**不因动态链接 FFmpeg 而变成 GPL**；
- 但**分发这 5 个 DLL 这一行为本身**受 GPL 约束 ⇒ 必须能提供**对应版本的 FFmpeg 源码**。

### 3.3 源码怎么拿（**未随包，是已知缺口**）

| 途径 | 说明 |
|---|---|
| FFmpeg 官方 | <https://ffmpeg.org/download.html> |
| 构建指纹线索 | 上面的 configure 串给出编译参数；`--prefix=/ffbuild/prefix`、`--target-os=mingw32` 是 **BtbN/FFmpeg-Builds** 这类 Windows 构建的典型形态 |
| **⚠️ 仍未确定** | **本包无法确认这 5 个 DLL 究竟来自哪一次具体构建**，因此**无法给出逐字节对应的源码包地址** |

**上游 `NOTICE.md` §6 第 6 条也登记了同一缺口**：
"文本与本机 DLL 是否同一构建**未与上游资产逐字对齐**"。

> **诚实结论**：本包**包含 GPL 二进制，但未随附对应源码**。
> 若你要把它用于**公开发布**，请先补上源码获取方式（写明具体版本与链接），
> 或者**考虑从包里移除这 5 个 DLL**——引擎的多媒体能力（`--vision` 相关）
> 可能会因此不可用，**该取舍需实测确认**。

---

## 4. libcurl

| 文件 | 说明 |
|---|---|
| `libcurl-x64.dll` | curl 库 |

**许可**：curl 的许可（**MIT/X 类衍生许可**，非 GPL）。

**原文不在本包内**，因为：

- 上游 `NOTICE.md` §6 第 4 条自己登记为 **"curl 许可文本不在手（我们自述'MIT 类'，未逐字核）"**；
- 本包尝试从 `libcurl-x64.dll` 里提取版本串与版权行，
  **未能提取到**（与上游 §7 声称的 `libcurl/8.22.0` 不一致 —— 可能是构建时去掉了版本串）。
  因此**本包不重复上游那个未能复现的版本号**。

**官方获取地址**：<https://curl.se/docs/copyright.html>
（curl 许可要求：再分发时保留版权声明与许可文本。）

---

## 5. 本文件中"未核 / 未随包"项目的汇总（不留含糊）

| # | 项目 | 状态 |
|---|---|---|
| 1 | NVIDIA CUDA EULA 原文 | ❌ 未随包（官方 URL 已在 §2 给出） |
| 2 | CUDA 可再分发清单是否覆盖这 3 支 | ⚠️ 未逐条对上（上游同样登记为未核） |
| 3 | FFmpeg GPL 全文 | ❌ 未随包 —— **公开发布前应补** |
| 4 | FFmpeg 对应源码 | ❌ 未随包，且**无法定位到具体构建** ⚠️ |
| 5 | curl 许可全文 | ❌ 未随包（官方 URL 已在 §4 给出） |
| 6 | `libcurl-x64.dll` 的具体版本 | ⚠️ 未能从二进制提取，故不断言 |

**我方（打包人）的立场**：以上逐条如实列出，不做"应该没问题"式的推断。
涉及 **GPL（第 3、4 条）** 的两项是**公开发布前必须解决**的，
其余四项属于"补链接即可"的程度。

---

*本文件随 `Bonsai2-Engine-Pack` 分发。与上游 `NOTICE.md` 冲突时，以更严格者为准。*
