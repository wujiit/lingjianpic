# ModernImageViewer Perf Tests

这个项目用于落地 `P0.1 性能基准体系`，目标不是单次“跑个感觉”，而是每轮优化都能复跑同一套场景。

## 现在包含的场景

- `scan-100`：扫描 100 张常规图片目录
- `scan-1000`：扫描 1000 张常规图片目录
- `scan-5000-subfolders`：扫描 5000 张分层目录图片集合
- `preview-large-sequence`：切换大图预览序列
- `preview-long-image`：打开超长图预览
- `batch-export-100`：导出 100 张图片为 JPEG
- `batch-compress-target-size-100`：压缩 100 张图片到目标体积
- `exact-duplicate-scan`：扫描完全重复图片
- `similar-image-scan`：扫描相似图片

## 运行方式

在仓库根目录执行：

```powershell
dotnet run --project .\tests\ModernImageViewer.PerfTests\ModernImageViewer.PerfTests.csproj
```

只跑部分场景：

```powershell
dotnet run --project .\tests\ModernImageViewer.PerfTests\ModernImageViewer.PerfTests.csproj -- --filter scan-1000,preview-large-sequence
```

查看可用场景：

```powershell
dotnet run --project .\tests\ModernImageViewer.PerfTests\ModernImageViewer.PerfTests.csproj -- --list
```

切换性能模式：

```powershell
dotnet run --project .\tests\ModernImageViewer.PerfTests\ModernImageViewer.PerfTests.csproj -- --mode HighPerformance
```

## 输出内容

每次运行会写入：

- `artifacts/perf/<timestamp>/summary.json`
- `artifacts/perf/<timestamp>/summary.csv`
- `artifacts/perf/<timestamp>/summary.md`
- `artifacts/perf/latest.json`
- `artifacts/perf/latest.csv`
- `artifacts/perf/latest.md`

报告内容包括：

- 单场景耗时
- 吞吐量
- 平均 CPU 占用估算
- 峰值工作集
- 托管堆大小
- 分配字节数
- GC 次数

## 设计说明

- 协调器和单场景执行器是同一个程序
- 每个场景会在独立子进程中运行，避免峰值内存互相污染
- 数据集由程序自动生成，保证可复跑
- 当前更偏“稳定基线”，不是替代真实用户图库；后续可以继续补真实 RAW/HEIC/AVIF 样本集
