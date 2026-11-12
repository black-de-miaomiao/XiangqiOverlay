# JJ象棋盘面分析 (XiangqiOverlay)

这是一个 Windows x64 桌面程序，用于识别目标窗口中的中国象棋棋盘，并在目标窗口上方显示 Pikafish 的本地分析结果。

程序只做盘面读取、分析和可视化，不向目标窗口注入鼠标、键盘或自动落子操作。使用实时辅助功能前，请确认目标平台和赛事规则允许。

## 一、项目结构

```text
XiangqiOverlay/
├─ XiangqiOverlay.sln                 解决方案文件
├─ src/
│  ├─ XiangqiOverlay.Core/            核心层：数据模型、枚举、坐标、FEN、UCI
│  ├─ XiangqiOverlay.Vision/          视觉层：棋盘检测、棋子识别（OpenCV）
│  ├─ XiangqiOverlay.Engine/          引擎层：Pikafish UCI 通信、分析管理
│  ├─ XiangqiOverlay.Platform/        平台层：窗口枚举、屏幕截图、悬浮层
│  └─ XiangqiOverlay.App/             应用层：WPF 主界面
└─ src/XiangqiOverlay.App/
   └─ settings.example.json            配置示例
```

## 二、与 GoOverlay 的架构对比

| 组件 | GoOverlay（围棋） | XiangqiOverlay（象棋） |
|------|-------------------|----------------------|
| 棋盘大小 | 19×19（361点） | 9×10（90点） |
| AI 引擎 | KataGo（OpenCL） | Pikafish（NNUE/CPU） |
| 通信协议 | KataGo JSON Analysis | UCI/UCCI 协议 |
| 坐标系 | SGF（a-s, 19路） | UCCI（a-i, 0-9） |
| 棋子表示 | 二维数组（黑/白/空） | 7种棋子 × 2种颜色 |
| 盘面格式 | SGF/Tromp-Taylor | FEN（中国象棋标准） |
| 悬浮层 | WPF 透明窗口 | WinForms 透明窗口 |
| 棋盘识别 | OpenCvSharp 霍夫变换 | OpenCvSharp 霍夫变换 |

## 三、系统要求

- Windows 10 或 Windows 11，64 位
- .NET 8 SDK（开发需要）或 .NET 8 Runtime（运行需要）
- 微信 Windows 客户端，以及已打开的 JJ象棋小程序
- Pikafish 引擎和 NNUE 评估文件

## 四、开发构建

### 安装 .NET 8 SDK

从 https://dotnet.microsoft.com/download/dotnet/8.0 下载安装 .NET 8 SDK。

### 构建

```powershell
cd path\to\XiangqiOverlay
dotnet restore XiangqiOverlay.sln
dotnet build XiangqiOverlay.sln --configuration Debug
```

### 回归测试

```powershell
dotnet run --project ..\LogicTest\LogicTest.csproj
```

盘面提交遵循两条不变量：未移动棋子的兵种不会被识别噪声改写；无法由合法走法解释的持续异常不会自动重锚定整张候选盘面。生产 BGRA 截图与离线 BGR 图片在识别前统一转换为三通道。

### 运行

```powershell
dotnet run --project src\XiangqiOverlay.App\XiangqiOverlay.App.csproj --configuration Debug
```

### 生成自包含发布版

```powershell
dotnet publish src\XiangqiOverlay.App\XiangqiOverlay.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output dist\XiangqiOverlay
```

## 五、Pikafish 引擎

### 下载

从 GitHub 获取 Pikafish：

```text
引擎：https://github.com/官方/Pikafish/releases
评估函数：default_nnue.bin（随引擎发布包一起提供）
```

### 目录结构

发布时需要将 Pikafish 文件放在 `runtime\pikafish\` 目录下：

```text
XiangqiOverlay/
├─ XiangqiOverlay.App.exe
├─ settings.json
└─ runtime/
   └─ pikafish/
      ├─ pikafish.exe              Pikafish 引擎
      ├─ default_nnue.bin          NNUE 评估函数
      └─ （其他 DLL）
```

## 六、使用方法

1. 将发布包完整解压到一个普通目录。
2. 打开微信 Windows 客户端，进入 JJ象棋小程序，让棋盘完整显示。
3. 双击 `XiangqiOverlay.App.exe`。
4. 在"目标窗口"下拉框中选择 JJ象棋窗口。
5. 选择"我的执子"和"当前行棋方"。
6. 点击"自动寻找棋盘"进行棋盘校准。
7. 点击"开始"。

## 七、分析结果说明

| 标记 | 含义 |
|------|------|
| 🔴 红色圆圈 + 箭头 | Pikafish 推荐的首选着法 |
| 🔵 蓝色虚线圆圈 | 其他候选着法 |
| 红色小点 | 已识别的棋子位置 |
| 黄色十字线 | 识别到的最后一手 |
| 信息面板 | 引擎名称、深度、胜率、评分、推荐着 |

## 八、配置文件

程序目录中的 `settings.json` 保存运行配置。主要配置说明：

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| PikafishExecutable | Pikafish 引擎路径 | runtime\pikafish\pikafish.exe |
| PikafishEvalFile | NNUE 评估函数文件 | runtime\pikafish\default_nnue.bin |
| MaxDepth | 最大搜索深度 | 30 |
| MaxTimeMs | 每次最长思考时间（毫秒） | 4000 |
| Threads | 搜索线程数 | 4 |
| HashSize | 哈希表大小 (MB) | 128 |
| TopMoves | 悬浮层显示候选着数量 | 5 |
| OwnColor | 我的执子颜色 | Red |
| CurrentPlayer | 启动时行棋方 | Red |
| BoardReadMode | 盘面读取方式 | Auto |
| ExcludeOverlayFromCapture | 禁止悬浮层截屏 | true |

## 九、盘面读取方式

当前版本的盘面读取实现为视觉识别：抓取目标窗口画面并识别棋盘。界面中的“自动”与“仅视觉”均使用这条实现路径；内存读取尚未实现。

## 十、中国象棋 FEN 格式

```
r n b a k a b n r / . . . . . . . . . / . c . . . . . c . / p . p . p . p . p / . . . . . . . . . /
. . . . . . . . . / P . P . P . P . P / . C . . . . . C . / . . . . . . . . . / R N B A K A B N R
w - - 0 1
```

- 红方：`K A B N R C P`（大写）
- 黑方：`k a b n r c p`（小写）
- 数字：连续空位数
- 行棋方：`w`=红方，`b`=黑方

## 十一、UCCI 坐标

中国象棋使用 9 列(a-i) × 10 行(0-9) 的坐标系：

```
    a  b  c  d  e  f  g  h i
0   车 马 象 士 将 士 象 马 车  ← 黑方底线
1
2      炮              炮
3   卒    卒    卒    卒   卒
4
5
6   兵    兵    兵    兵   兵
7      炮              炮
8
9   俥 傌 相 仕 帅 仕 相 傌 俥  ← 红方底线
```

UCCI 走法如 `h2e2` 表示从 h2 到 e2。

## 十二、限制

- 只支持中国象棋标准 9×10 棋盘
- 视觉识别效果依赖棋盘清晰度、校准准确度
- 棋子类型识别使用简化特征匹配，可扩展为 OCR 或模板匹配
- 程序不自动点击、不自动落子、不控制鼠标键盘

## 十三、开发计划

- [ ] 完善棋子 OCR 识别（替代简化特征匹配）
- [ ] 添加内存读取（Cocos renderer 内存扫描）
- [ ] 多线路分析（MultiPV）
- [ ] 棋谱记录和导出
- [ ] 手动框选交互窗口

## 十四、第三方组件

- [OpenCvSharp](https://github.com/shimat/opencvsharp) - OpenCV .NET 封装
- [Pikafish](https://github.com/官方/Pikafish) - 中国象棋 AI 引擎
