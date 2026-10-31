# On-device summarization for YAMV: models, OS APIs, runtimes

Date: 2026-10-21

Question: what small language models (SLMs) can YAMV ship, or have the user download, to summarize a whole document or a selected passage, on Windows and macOS, from a single self-contained .NET 10 / Avalonia 12 executable whose document is drawn in WebView2 / WKWebView?

Confidence key: **verified** means quoted from the linked primary page in this session; **secondary** means from a dated third-party roundup; **estimate** means derived here, with the arithmetic shown. Nothing was benchmarked on real hardware for this note.

## 1. Candidate models (0.5B to ~5B)

| Model | Params | Context | License | Q4 GGUF size | Source |
| --- | --- | --- | --- | --- | --- |
| Qwen3.5-4B | 4B (Hub shows 5B incl. vision encoder) | 262,144 native, extensible to ~1M | Apache 2.0 | Q4_K_M 2.74 GB, Q8_0 4.48 GB (verified) | [model card](https://huggingface.co/Qwen/Qwen3.5-4B), [unsloth GGUF](https://huggingface.co/unsloth/Qwen3.5-4B-GGUF) |
| Qwen3.5-2B / 0.8B | 2.3B / 0.9B | 262K | Apache 2.0 | ~1.5 GB / ~0.5 GB (estimate, params x 0.65) | [vdf.ai roundup, Oct 2026](https://vdf.ai/blog/best-small-language-models/), [tinyweights](https://tinyweights.dev/models/) |
| Gemma 4 E2B | 2.3B effective, 5.1B with embeddings | 128K | Apache 2.0 | Q4_K_M 3.11 GB, Q8_0 5.05 GB (verified) | [Gemma 4 model card](https://ai.google.dev/gemma/docs/core/model_card_4), [HF blog](https://huggingface.co/blog/gemma4), [unsloth GGUF](https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF) |
| Gemma 4 E4B | 4.5B effective, 8B with embeddings | 128K | Apache 2.0 | ~5 GB (estimate) | [Gemma 4 model card](https://ai.google.dev/gemma/docs/core/model_card_4) |
| Phi-4-mini-instruct | 3.8B | 128K | MIT | ~2.5 GB (secondary) | [vdf.ai](https://vdf.ai/blog/best-small-language-models/), [tinyweights](https://tinyweights.dev/models/) |
| Granite 4.2 3B | 3.7B | 128K (extendable to 512K) | Apache 2.0 | ~2.4 GB (estimate) | [vdf.ai](https://vdf.ai/blog/best-small-language-models/) (card release date 2026-08-25) |
| Ministral 3 3B | 3.8B | 256K | Apache 2.0 | ~2.0-2.5 GB (secondary) | [vdf.ai](https://vdf.ai/blog/best-small-language-models/) |
| SmolLM3-3B | 3.1B | 64K, 128K with YaRN | Apache 2.0, fully open data | ~2.0 GB (secondary) | [huggingface/smollm](https://github.com/huggingface/smollm) |
| Llama 3.2 1B / 3B | 1.2B / 3.2B | 128K | Llama 3.2 Community License | 0.8 / 2.1 GB (secondary) | [vdf.ai](https://vdf.ai/blog/best-small-language-models/) |
| LFM2.5 1.2B / 2.6B | 1.2B / 2.7B | 32K / 128K | LFM Open License 1.0 | ~0.8 / ~1.7 GB | [vdf.ai](https://vdf.ai/blog/best-small-language-models/) |
| Nemotron 3 Nano 4B | 4.0B | up to 262K | NVIDIA Nemotron Open Model License | ~2.6 GB (estimate) | [vdf.ai](https://vdf.ai/blog/best-small-language-models/) |
| MiniCPM5-2B | 2.5B | 128K | Apache 2.0 | ~1.6 GB (estimate) | [vdf.ai](https://vdf.ai/blog/best-small-language-models/) |

Quality signals for long input: the Qwen3.5-4B card reports AA-LCR 57.0 and LongBench v2 50.0 ([card](https://huggingface.co/Qwen/Qwen3.5-4B)). No vendor in this range publishes a dedicated summarization benchmark that was found in this session; YAMV should build a small eval set of its own Markdown documents.

Architecture matters for long documents: Qwen3.5 uses Gated DeltaNet (linear attention) in three of every four blocks ([card](https://huggingface.co/Qwen/Qwen3.5-4B)), and Gemma 4 small models use a 512-token sliding window on most layers ([model card](https://ai.google.dev/gemma/docs/core/model_card_4)).

Both keep the KV cache far smaller at 10K-30K tokens than a plain transformer such as Llama 3.2 or Phi-4-mini, which the [vdf.ai](https://vdf.ai/blog/best-small-language-models/) roundup notes is often what decides whether long context fits.

Licenses: Apache 2.0 and MIT permit redistribution inside a free or commercial app with the license text and notices kept.

Llama 3.2 requires "Built with Llama" displayed by redistributors and a separate license above 700M MAU; LFM grants commercial rights only below $10M annual revenue; Nemotron is a custom license to read in full ([vdf.ai, licence section](https://vdf.ai/blog/best-small-language-models/)). Gemma 4 moved to Apache 2.0 ([HF blog](https://huggingface.co/blog/gemma4)), unlike Gemma 3's custom Gemma terms.

## 2. OS-provided models

### Apple Foundation Models (macOS)

- Requires macOS 26+, Apple silicon, and Apple Intelligence enabled. The on-device model is ~3B params. Context window holds input and output combined: 4,096 tokens on macOS 26 and on M1/M2 Macs; 8,192 on macOS 27 with M3 or newer and 12 GB+ ([apfel README](https://github.com/arthur-ficial/apfel), secondary but reads `SystemLanguageModel.contextSize` at runtime).

  Apple's WWDC26 session shows `model.contextSize` returning 8192 and a `tokenCount(for:)` API added in 26.4 ([WWDC26 session 241](https://developer.apple.com/videos/play/wwdc2026/241/)).
- macOS 27 adds `PrivateCloudComputeLanguageModel` (32K context, reasoning) and a `LanguageModel` protocol with CoreAI and MLX backends ([WWDC26 241](https://developer.apple.com/videos/play/wwdc2026/241/), [WWDC26 339](https://developer.apple.com/videos/play/wwdc2026/339/)). PCC sends text off device, which changes the privacy story.
- Context management guidance: [TN3193](https://developer.apple.com/documentation/technotes/tn3193-managing-the-on-device-foundation-model-s-context-window).
- .NET access: the API is Swift-only (async, Swift concurrency). No .NET binding was found. Practical route is a small Swift dynamic library exposing `@_cdecl` C functions (prompt in, streamed chunks via callback) loaded by P/Invoke, built in the macOS packaging step. Unverified effort estimate: a few hundred lines plus build plumbing.
- Implication: 4K tokens means any document over roughly 3K tokens of input must be chunked (map-reduce), on every M1/M2 Mac. Intel Macs get nothing.

### Windows AI APIs / Phi Silica

- Phi Silica is a Limited Access Feature requiring an unlock token; it runs on the NPU of Copilot+ PCs, and on NVIDIA RTX 30+ or AMD RX 9060+ GPUs with 6+ GB VRAM only on Insider Experimental builds 26300.8553+ with Windows App SDK 2.2.2-experimental9, with Developer Mode on ([Microsoft Learn, updated 2026-10-19](https://learn.microsoft.com/en-us/windows/ai/apis/phi-silica)).
- **It is being replaced**: Aion Instruct rolls out to Insiders in November 2026 and to retail in January 2027, when Phi Silica is removed; Aion needs no LAF token ([same page](https://learn.microsoft.com/en-us/windows/ai/apis/phi-silica)).
- API: `Microsoft.Windows.AI.Text.LanguageModel` (`GetReadyState`, `EnsureReadyAsync`, `GenerateResponseAsync`) and a ready-made `TextSummarizer.SummarizeAsync` ([same page](https://learn.microsoft.com/en-us/windows/ai/apis/phi-silica)). Output may carry an invisible AI watermark from October 2026. Not available in China.
- .NET desktop use goes through the Windows App SDK projection; these APIs generally need package identity (MSIX or sparse package). That requirement was not re-verified this session and is the main friction for a single-file exe.
- Implication: reaches only Copilot+ PCs today, and the model is mid-replacement. Good as an opportunistic backend later, not as the primary path.

### Chrome/Edge built-in Summarizer API in the WebView

- Edge exposes `Summarizer` (enabled by default since 138.0.3309.2) backed by Phi-4-mini, needing 20 GB free disk, 5.5 GB VRAM and an unmetered network, with Aion-1.0-Instruct as a CPU-capable prerelease option from Edge 150 ([Edge docs](https://learn.microsoft.com/en-us/microsoft-edge/web-platform/writing-assistance-apis)). Chrome's version uses Gemini Nano, Chrome/Edge 138+, no Safari/Firefox ([Chrome docs](https://developer.chrome.com/docs/ai/summarizer-api)).
- WebView2: no Microsoft documentation was found saying WebView2 exposes `Summarizer`. Treat as unsupported until tested with `typeof Summarizer` in YAMV's page. WKWebView: Safari does not implement it, so macOS is out regardless.

## 3. Runtimes callable from .NET

| Runtime | NuGet | Windows accel | macOS accel | Intel Mac | Maturity |
| --- | --- | --- | --- | --- | --- |
| llama.cpp via LLamaSharp | `LLamaSharp` 0.27.0 + `LLamaSharp.Backend.Cpu` / `.Vulkan` / `.Cuda*` / Metal backend | CPU, Vulkan (any GPU), CUDA | Metal | CPU backend (x64) | Community; tracks llama.cpp with lag; Vulkan 0.27.0 published 2026-04-26 ([NuGet](https://www.nuget.org/packages/LlamaSharp/), [Vulkan](https://www.nuget.org/packages/LLamaSharp.Backend.Vulkan/)) |
| ONNX Runtime GenAI | `Microsoft.ML.OnnxRuntimeGenAI` 0.14.1 (2026-06-02), `.DirectML`, `.Cuda`, `.WinML` ([NuGet](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntimeGenAI/), [DirectML](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntimeGenAI.DirectML/0.14.1)) | DirectML, WinML, CUDA | CPU; CoreML/Metal support for GenAI not confirmed | CPU | Microsoft, pre-1.0; needs ONNX-exported models (int4), fewer ready models than GGUF |
| Foundry Local | `Microsoft.AI.Foundry.Local` + `Microsoft.AI.Foundry.Local.Runtime` 2.0.1 (2026-08-31; win-x64, win-arm64, linux, osx-arm64) ([NuGet](https://www.nuget.org/packages/Microsoft.AI.Foundry.Local.Runtime/)) | picks best EP | Apple silicon | No (no osx-x64 asset listed) | GA reported 2026-04-09 ([secondary](https://www.issoh.co.jp/tech/details/11773/)); curated model catalog; runs a local OpenAI-style endpoint |
| Apple Foundation Models | Swift shim + P/Invoke | n/a | Neural Engine / GPU | No | OS framework |
| Phi Silica / Aion | Windows App SDK | NPU, some GPUs | n/a | n/a | LAF / in transition |
| Microsoft.Extensions.AI | `Microsoft.Extensions.AI` (`IChatClient`) | abstraction only | | | Lets YAMV code against one interface with per-platform backends |

Native binary size added: not measured this session. llama.cpp CPU + Metal builds are typically tens of MB; CUDA backends are hundreds of MB and should be excluded. Measure with a trial publish before deciding.

## 4. Practical numbers

Measured reference: llama.cpp on a 7B Q4_0 model, M1 (8-core GPU) does 118 t/s prompt processing and 14 t/s generation; M2 does 146 t/s and 21.7 t/s ([llama.cpp Apple silicon benchmarks](https://github.com/ggml-org/llama.cpp/discussions/4167); Nov 2023 build, newer builds are faster).

Estimates for a ~4B Q4 model, scaling those by ~7/4: M1 roughly 200 t/s prefill and 25 t/s generation; M2 roughly 250 t/s and 35 t/s. Summarizing a 10K-token document then takes about 40-50 s of prefill (time to first token) plus ~10-15 s to write a 300-token summary on M1. A 2B model roughly halves that.

Mid-range Windows laptop (CPU or iGPU via Vulkan): not measured; expect prefill several times slower than an M1 GPU on CPU-only, so a 10K-token whole-document summary can take minutes. Selection summaries (a few hundred to 2K tokens) stay in the seconds range everywhere.

RAM: weights (2-3 GB at Q4 for 4B) plus KV cache plus ~1 GB runtime ([tinyweights](https://tinyweights.dev/models/)). Hybrid-attention models (Qwen3.5, Gemma 4) keep the KV cache small at 10-30K tokens. Plan for 4-5 GB free for a 4B model at 16K context; 8 GB machines are marginal.

Chunking: required for Apple Foundation Models beyond ~3K input tokens, and advisable for any model above ~16K tokens to bound memory and latency. Use heading-aware map-reduce over the Markdown AST: summarize each section, then summarize the summaries, streaming progress per section.

## 5. Distribution

- Bundling: a 1.5-3 GB GGUF inside a single-file exe multiplies download and update size for every user, including those who never use the feature, and every app update re-ships it. Not recommended.
- First-use download: fetch from a pinned Hugging Face revision URL (`/resolve/<commit-sha>/<file>`) or a mirrored CDN, with HTTP range resume, SHA-256 checked against a value compiled into the app, stored under the data folder (`YAMV_DATA_DIR` / `%APPDATA%\YAMV`), with a consent dialog stating size, like Microsoft's recommended pattern for the Phi Silica GPU model ([Learn](https://learn.microsoft.com/en-us/windows/ai/apis/phi-silica)).
- Obligations: Apache 2.0 / MIT need the license and NOTICE shown (an About / third-party notices entry naming the model). Llama adds "Built with Llama" and Meta's acceptable use policy; prefer avoiding it for that reason.

## Recommendation

**Primary**: llama.cpp via LLamaSharp (CPU + Vulkan on Windows, Metal on macOS, CPU on Intel Mac), behind `Microsoft.Extensions.AI` `IChatClient`, with Qwen3.5-4B Q4_K_M (2.74 GB, Apache 2.0, 262K context, linear-attention hybrid) downloaded on first use, and Qwen3.5-2B offered for low-RAM machines. Worst case: an 8 GB Windows laptop on CPU takes minutes for a long document and the native backend bloats the exe by tens of MB; selection summaries remain usable.

**Fallback**: OS models only (Apple Foundation Models via a Swift shim on macOS 26+; Windows AI `TextSummarizer` once Aion ships). Zero download, but worst case is no feature at all on Intel Macs, non-Copilot+ PCs, and a 4K-token window forcing chunking on M1/M2.

## Open questions for the owner

1. Is a 1.5-3 GB opt-in download acceptable, or must the feature be zero-download (OS models only)?
2. Minimum hardware to support: 8 GB RAM machines, Intel Macs, non-GPU Windows laptops?
3. Is a whole-document summary taking a minute or more acceptable if it streams progress, or should whole-document be limited to a size cap?
4. Can YAMV take package identity (MSIX / sparse package) on Windows if Windows AI APIs become a backend?
5. Should the Swift shim for Apple Foundation Models be built at all, given the 4K window on M1/M2?
6. Which test hardware is available to replace the estimates in section 4 with measurements (a 10K-token fixture, Qwen3.5-2B and 4B, both platforms)?
7. Test WebView2 for `typeof Summarizer` once, to close that question definitively.
