# testtools

ASCIIPlayer 的回归测试。

```bat
python testtools\regress.py                 :: 编译 Release 后跑全部
python testtools\regress.py --no-build      :: 不编译
python testtools\regress.py -k resize       :: 只跑名字匹配的
python testtools\regress.py --verbose       :: 打印命令行和子进程输出
python testtools\regress.py --list          :: 列出所有测试名
```

脚本会自动探测 `MSBuild.exe`（PATH、VS 2022 各版本、Build Tools）；
素材用项目自带的 `ASCIIPlayer\Resources\ffmpeg.exe` 生成到 `testtools\.work\`，
已存在就复用（删掉这个目录即可从头再来）。

## 覆盖范围

| 分组 | 内容 |
|---|---|
| `cli_*` | `-h`、无参数、文件不存在、未知参数、`-n` / `-f` 越界、`-r` 缺路径、输出目录不存在、`--selftest` |
| `render_*` | 默认帧率跟源视频、`-f` 指定、上限 60、竖屏、无音轨源、覆盖写、坏输入、720p/1440p 吞吐 |
| `play_*` | 真彩色 / 256 色转义、纯色片的颜色、默认颜色模式、纯播放、行宽不超窗口、`-r --play` 有声、静音、实时节奏 |
| `resize_*` | 播放途中改窗口尺寸：网格随之变化、清屏、编码分辨率不变 |

断言尽量不依赖具体机器速度（性能只设很松的下限并打印实际吞吐），
其余都是确定性的：退出码、stderr 文案、ffmpeg 探测到的流信息、ANSI 转义与可见文本。

## 内部自检

`ASCIIPlayer.exe --selftest` 不读视频、不开 ffmpeg、不需要控制台，覆盖：

- 分辨率解析、帧率夹取
- `RGB_TO_256` / `XTERM256_TO_RGB` 的已知取值
- 亮度查找表单调、端点正确
- 字符点阵：空格为空、其余字符都有像素、坐标不越界
- 网格换算：最小尺寸夹取、字体高宽比 1.2 / 1.5 / 9.0 / 0.2
- 全白 / 全黑帧的采样、合成（背景是否真黑、抖动是否会把纯黑抬起来）
- 终端文本：真彩色/256 色转义、行数、纯空格帧不产生颜色转义
- 500x200 的极端网格不越界

## 窗口尺寸测试钩子

真实控制台尺寸没法在无头环境里改，所以程序支持一个调试用的环境变量：

```
ASCIIPLAYER_CONSOLE_SIZE=<文件路径>
```

文件内容是 `宽x高`，`GET_VISIBLE_CONSOLE_SIZE` 每帧都会重新读它，
`MEASURE_CELL_ASPECT` 在钩子生效时固定返回 2.0（8x16 字体）。

`resize_adapts_while_playing` 就是靠它在一段播放里把尺寸改成
`100x30 → 60x20 → 120x40`，然后检查每一帧的列宽/行数是否跟着变、是否清屏、
编码输出是否仍然是 `320x240`。这个变量只在测试里用，正常运行不设置即可。
