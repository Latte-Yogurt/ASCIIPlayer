#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ASCIIPlayer 回归测试。

覆盖三块：
  1. 命令行行为（帮助、退出码、参数校验）
  2. 编码管线（帧率、分辨率、音轨、时长、ffmpeg 退出码）
  3. 终端输出（真彩色 / 256 色、帧数、行宽不超过窗口、混流时也能出声）

用法:
    python testtools/regress.py                 # 编译 + 全部测试
    python testtools/regress.py --no-build      # 用现有 exe
    python testtools/regress.py -k render       # 只跑名字里带 render 的
    python testtools/regress.py --verbose
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, "ASCIIPlayer")
EXE = os.path.join(PROJECT, "bin", "Release", "ASCIIPlayer.exe")
FFMPEG = os.path.join(PROJECT, "Resources", "ffmpeg.exe")
WORK = os.path.join(ROOT, "testtools", ".work")
SLN = os.path.join(ROOT, "ASCIIPlayer.sln")

CREATE_NO_WINDOW = 0x08000000

ANSI_RE = re.compile(rb"\x1b\[[0-9;?]*[A-Za-z]")
TRUE_RE = re.compile(rb"38;2;(\d+);(\d+);(\d+)")
PAL_RE = re.compile(rb"38;5;(\d+)")

VERBOSE = False


# ---------------------------------------------------------------- 基础工具

class Failure(AssertionError):
    pass


def check(cond, message):
    if not cond:
        raise Failure(message)


def log(message):
    if VERBOSE:
        print("      " + message)


def run(args, timeout=180, stdin_null=True):
    """跑 exe，返回 (returncode, stdout_bytes, stderr_bytes, seconds)。"""
    cmd = [EXE] + args
    log("$ " + " ".join(cmd))
    started = time.time()
    proc = subprocess.run(
        cmd,
        stdin=subprocess.DEVNULL if stdin_null else None,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=timeout,
        creationflags=CREATE_NO_WINDOW,
    )
    elapsed = time.time() - started
    log(f"  -> exit {proc.returncode}, {elapsed:.2f}s, "
        f"stdout {len(proc.stdout)}B, stderr {len(proc.stderr)}B")
    if VERBOSE and proc.stderr:
        for line in proc.stderr.decode("utf-8", "replace").splitlines()[:12]:
            log("  err| " + line)
    return proc.returncode, proc.stdout, proc.stderr, elapsed


def text(data):
    return data.decode("utf-8", "replace")


def ffmpeg(args, timeout=180):
    return subprocess.run(
        [FFMPEG] + args,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=timeout,
        creationflags=CREATE_NO_WINDOW,
    )


# ---------------------------------------------------------------- 素材与探测

def ensure_media():
    """生成测试素材。testsrc2 是动图，anullsrc 是静音音轨（测试不会真的响）。"""
    os.makedirs(WORK, exist_ok=True)
    fixtures = {
        "clip30.mp4": [
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=3",
            "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
            "-t", "3", "-shortest",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-c:a", "aac",
        ],
        "clip25_noaudio.mp4": [
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=2",
            "-t", "2",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
        ],
        "portrait24.mp4": [
            "-f", "lavfi", "-i", "testsrc2=size=240x320:rate=24:duration=2",
            "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
            "-t", "2", "-shortest",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-c:a", "aac",
        ],
        "red1s.mp4": [
            "-f", "lavfi", "-i", "color=c=red:size=320x240:rate=10:duration=1",
            "-t", "1",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
        ],
        "white1s.mp4": [
            "-f", "lavfi", "-i", "color=c=white:size=320x240:rate=10:duration=1",
            "-t", "1",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
        ],
    }

    for name, args in fixtures.items():
        path = os.path.join(WORK, name)
        if os.path.exists(path) and os.path.getsize(path) > 0:
            continue
        log(f"generating fixture {name}")
        proc = ffmpeg(["-y", "-hide_banner", "-loglevel", "error"] + args + [path])
        if proc.returncode != 0:
            raise SystemExit("生成测试素材失败: " + name + "\n" + text(proc.stderr))


DURATION_RE = re.compile(r"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")
VIDEO_RE = re.compile(r"Stream #\d+:\d+.*?Video:\s*([A-Za-z0-9_]+)")
AUDIO_RE = re.compile(r"Stream #\d+:\d+.*?Audio:\s*([A-Za-z0-9_]+)")
FPS_RE = re.compile(r"(\d+(?:\.\d+)?)\s*fps")
SIZE_RE = re.compile(r"(\d{2,5})x(\d{2,5})")


def probe(path):
    """用 ffmpeg 的元数据输出探测容器/流信息。"""
    proc = ffmpeg(["-hide_banner", "-nostdin", "-i", path])
    err = text(proc.stderr)

    info = {
        "exists": os.path.exists(path),
        "size": os.path.getsize(path) if os.path.exists(path) else 0,
        "duration": None,
        "width": None,
        "height": None,
        "fps": None,
        "video": None,
        "audio": None,
    }

    m = DURATION_RE.search(err)
    if m:
        info["duration"] = int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))

    m = VIDEO_RE.search(err)
    if m:
        info["video"] = m.group(1)

    m = AUDIO_RE.search(err)
    if m:
        info["audio"] = m.group(1)

    for line in err.splitlines():
        if "Video:" in line:
            m = SIZE_RE.search(line)
            if m:
                info["width"] = int(m.group(1))
                info["height"] = int(m.group(2))
            m = FPS_RE.search(line)
            if m:
                info["fps"] = float(m.group(1))
            break

    return info


def frames_in(stdout):
    """终端输出里有多少帧：每帧以 ANSI_HOME 开头。"""
    return stdout.count(b"\x1b[H")


def frame_texts(stdout):
    """按 ANSI_HOME 切帧，去掉转义，得到每帧的可见文本行。"""
    parts = stdout.split(b"\x1b[H")
    result = []
    for part in parts[1:]:
        plain = ANSI_RE.sub(b"", part)
        if not plain:
            continue
        result.append(plain.decode("latin-1").split("\r\n"))
    return result


# ---------------------------------------------------------------- 测试集合

TESTS = []


def test(fn):
    TESTS.append(fn)
    return fn


# ---- 命令行 ----

@test
def cli_help():
    rc, out, err, _ = run(["-h"])
    check(rc == 0, f"-h 退出码应为 0，实际 {rc}")
    check("用法" in text(out), "-h 应打印用法")
    check("--fps" in text(out), "帮助里应包含新增的 --fps")
    check("--color256" in text(out), "帮助里应包含 --color256")
    check(not out.startswith(b"\xef\xbb\xbf"), "重定向输出不应带 UTF-8 BOM")


@test
def cli_no_args():
    rc, out, err, _ = run([])
    check(rc == 2, f"不带参数应以 2 退出，实际 {rc}")
    check("用法" in text(out), "应打印用法")


@test
def cli_missing_file():
    rc, out, err, _ = run([os.path.join(WORK, "没有这个文件.mp4")])
    check(rc == 2, f"文件不存在应以 2 退出，实际 {rc}")
    check("找不到文件" in text(err), "stderr 应提示找不到文件")


@test
def cli_unknown_arg():
    rc, _, err, _ = run([os.path.join(WORK, "clip30.mp4"), "--bogus"])
    check(rc == 2, f"未知参数应以 2 退出，实际 {rc}")
    check("未知参数" in text(err), "stderr 应提示未知参数")


@test
def cli_bad_size():
    for bad in ["1920", "x1080", "100x100", "1920x", "abcxdef"]:
        rc, _, err, _ = run([os.path.join(WORK, "clip30.mp4"), "-n", bad])
        check(rc == 2, f"-n {bad} 应以 2 退出，实际 {rc}")
        check("尺寸" in text(err) or "分辨率" in text(err), f"-n {bad} 应有尺寸相关提示")


@test
def cli_bad_fps():
    src = os.path.join(WORK, "clip30.mp4")
    for bad in ["0", "61", "-5", "abc", "1.5"]:
        rc, _, err, _ = run([src, "-f", bad])
        check(rc == 2, f"-f {bad} 应以 2 退出，实际 {rc}")
        check("帧率" in text(err), f"-f {bad} 应有帧率提示")


@test
def cli_render_without_path():
    rc, _, err, _ = run([os.path.join(WORK, "clip30.mp4"), "-r"])
    check(rc == 2, f"-r 缺路径应以 2 退出，实际 {rc}")
    check("输出视频路径" in text(err), "应提示 -r 需要路径")


@test
def cli_output_dir_missing():
    missing = os.path.join(WORK, "no_such_dir", "out.mp4")
    rc, _, err, _ = run([os.path.join(WORK, "clip30.mp4"), "-r", missing, "-n", "320x240"])
    check(rc == 1, f"输出目录不存在应以 1 退出，实际 {rc}")
    check("输出目录不存在" in text(err), "应提示输出目录不存在")


@test
def cli_selftest():
    rc, out, err, _ = run(["--selftest"], timeout=120)
    body = text(out)
    check(rc == 0, f"自检失败，退出码 {rc}")
    check("FAIL" not in body, "自检里有失败项:\n" + body)
    passes = body.count("PASS ")
    check(passes >= 40, f"自检通过项太少: {passes}")
    check("0 失败" in body, "自检汇总应为 0 失败")


# ---- 编码 ----

@test
def render_default_fps_follows_source():
    """帧率不再写死 10：默认应跟源视频的 30fps。"""
    out_path = os.path.join(WORK, "r_default.mp4")
    rc, out, err, elapsed = run([
        os.path.join(WORK, "clip30.mp4"), "-r", out_path, "-n", "320x240",
    ])
    check(rc == 0, f"编码失败: {text(err)[-800:]}")
    info = probe(out_path)
    check(info["width"] == 320 and info["height"] == 240,
          f"分辨率应为 320x240，实际 {info['width']}x{info['height']}")
    check(info["fps"] is not None and abs(info["fps"] - 30) < 1,
          f"帧率应跟源视频 30fps，实际 {info['fps']}")
    check(info["audio"] is not None, "源视频有音轨，输出应带音轨")
    check(info["duration"] and abs(info["duration"] - 3.0) < 0.5,
          f"时长应约 3s，实际 {info['duration']}")


@test
def render_fps_option():
    out_path = os.path.join(WORK, "r_fps15.mp4")
    rc, out, err, _ = run([
        os.path.join(WORK, "clip30.mp4"), "-r", out_path, "-n", "320x240", "-f", "15",
    ])
    check(rc == 0, f"编码失败: {text(err)[-800:]}")
    info = probe(out_path)
    check(info["fps"] is not None and abs(info["fps"] - 15) < 1,
          f"帧率应为 15，实际 {info['fps']}")
    check(info["duration"] and abs(info["duration"] - 3.0) < 0.5,
          f"时长应约 3s，实际 {info['duration']}")


@test
def render_fps_upper_bound():
    out_path = os.path.join(WORK, "r_fps60.mp4")
    rc, out, err, _ = run([
        os.path.join(WORK, "clip30.mp4"), "-r", out_path, "-n", "320x240", "-f", "60",
    ])
    check(rc == 0, f"编码失败: {text(err)[-800:]}")
    info = probe(out_path)
    check(info["fps"] is not None and abs(info["fps"] - 60) < 1,
          f"帧率应为 60，实际 {info['fps']}")


@test
def render_portrait():
    out_path = os.path.join(WORK, "r_portrait.mp4")
    rc, out, err, _ = run([
        os.path.join(WORK, "portrait24.mp4"), "-r", out_path, "-n", "320x240",
    ])
    check(rc == 0, f"编码失败: {text(err)[-800:]}")
    info = probe(out_path)
    check(info["width"] == 320 and info["height"] == 240,
          f"分辨率应为 320x240，实际 {info['width']}x{info['height']}")
    check(info["fps"] is not None and abs(info["fps"] - 24) < 1,
          f"帧率应跟源 24fps，实际 {info['fps']}")
    check(info["duration"] and abs(info["duration"] - 2.0) < 0.5,
          f"时长应约 2s，实际 {info['duration']}")


@test
def render_without_audio():
    out_path = os.path.join(WORK, "r_noaudio.mp4")
    rc, out, err, _ = run([
        os.path.join(WORK, "clip25_noaudio.mp4"), "-r", out_path, "-n", "320x240",
    ])
    check(rc == 0, f"编码失败: {text(err)[-800:]}")
    info = probe(out_path)
    check(info["audio"] is None, "源视频没有音轨，输出也不该凭空多出音轨")
    check(info["fps"] is not None and abs(info["fps"] - 25) < 1,
          f"帧率应为 25，实际 {info['fps']}")
    check(info["duration"] and abs(info["duration"] - 2.0) < 0.5,
          f"时长应约 2s，实际 {info['duration']}")


@test
def render_high_res_performance():
    """高分辨率不再走「每字符一次 DrawString」，720p 应该在合理时间内完成。"""
    out_path = os.path.join(WORK, "r_720p.mp4")
    started = time.time()
    rc, out, err, _ = run([
        os.path.join(WORK, "clip30.mp4"), "-r", out_path, "-n", "1280x720", "-f", "30",
    ], timeout=300)
    elapsed = time.time() - started
    check(rc == 0, f"编码失败: {text(err)[-800:]}")
    info = probe(out_path)
    check(info["width"] == 1280 and info["height"] == 720,
          f"分辨率应为 1280x720，实际 {info['width']}x{info['height']}")
    check(elapsed < 90, f"720p/90 帧用了 {elapsed:.1f}s，太慢")
    log(f"720p 编码耗时 {elapsed:.2f}s ({90 / elapsed:.1f} 帧/秒，含编码）")


@test
def render_1440p_grid():
    """1440p 也要能在实时范围内跑完（旧版每帧光画字符就 90ms）。"""
    out_path = os.path.join(WORK, "r_1440p.mp4")
    started = time.time()
    rc, out, err, _ = run([
        os.path.join(WORK, "clip30.mp4"), "-r", out_path, "-n", "2560x1440", "-f", "30",
    ], timeout=300)
    elapsed = time.time() - started
    check(rc == 0, f"编码失败: {text(err)[-800:]}")
    info = probe(out_path)
    check(info["width"] == 2560 and info["height"] == 1440,
          f"分辨率应为 2560x1440，实际 {info['width']}x{info['height']}")
    fps = 90 / elapsed
    check(fps >= 8, f"1440p/90 帧只用 {elapsed:.1f}s，吞吐 {fps:.1f} 帧/秒，低于下限")
    log(f"1440p 吞吐 {fps:.1f} 帧/秒（含 x264 编码）")


@test
def render_invalid_video():
    """坏输入要明确报错并以 1 退出，而不是假装成功。"""
    bogus = os.path.join(WORK, "bogus.mp4")
    with open(bogus, "wb") as f:
        f.write(b"this is not a video\n")
    out_path = os.path.join(WORK, "r_bogus.mp4")
    rc, out, err, _ = run([
        bogus, "-r", out_path, "-n", "320x240",
    ], timeout=120)
    check(rc == 1, f"坏输入应以 1 退出，实际 {rc}")
    check("没有从 ffmpeg 收到任何帧" in text(err), "应说明没收到帧")


@test
def render_overwrites_existing():
    out_path = os.path.join(WORK, "r_overwrite.mp4")
    for _ in range(2):
        rc, _, err, _ = run([
            os.path.join(WORK, "clip25_noaudio.mp4"), "-r", out_path, "-n", "320x240",
        ])
        check(rc == 0, f"覆盖已有输出失败: {text(err)[-800:]}")
    check(os.path.getsize(out_path) > 0, "覆盖后输出文件为空")


# ---- 终端输出 ----

@test
def play_truecolor():
    rc, out, err, _ = run([
        os.path.join(WORK, "clip25_noaudio.mp4"), "-f", "20",
        "--truecolor", "--mute", "--no-wait",
    ])
    check(rc == 0, f"播放失败: {text(err)[-800:]}")
    frames = frames_in(out)
    check(frames >= 10, f"输出帧太少: {frames}")
    check(b"\x1b[38;2;" in out, "真彩色模式下应出现 38;2;r;g;b 转义")
    check(b"\x1b[38;5;" not in out, "真彩色模式下不该出现 256 色转义")
    for m in list(TRUE_RE.finditer(out))[:200]:
        for v in m.groups():
            check(int(v) <= 255, f"真彩色分量越界: {m.group(0)!r}")


@test
def play_color256():
    rc, out, err, _ = run([
        os.path.join(WORK, "clip25_noaudio.mp4"), "-f", "20",
        "--color256", "--mute", "--no-wait",
    ])
    check(rc == 0, f"播放失败: {text(err)[-800:]}")
    check(b"\x1b[38;5;" in out, "256 色模式下应出现 38;5;n 转义")
    check(b"\x1b[38;2;" not in out, "256 色模式下不该出现真彩色转义")
    for m in list(PAL_RE.finditer(out))[:200]:
        v = int(m.group(1))
        check(16 <= v <= 255, f"256 色号越界: {v}")


@test
def play_solid_colors():
    """纯色片源：终端里输出的 24-bit 颜色必须真的对应源颜色。"""
    cases = [
        ("red1s.mp4", lambda c: c[0] >= 240 and c[1] <= 20 and c[2] <= 20, "纯红"),
        ("white1s.mp4", lambda c: min(c) >= 250, "纯白"),
    ]
    for name, ok, label in cases:
        rc, out, err, _ = run([
            os.path.join(WORK, name), "-f", "10", "--truecolor", "--mute", "--no-wait",
        ])
        check(rc == 0, f"{name} 播放失败: {text(err)[-800:]}")
        colors = [tuple(int(v) for v in m.groups()) for m in TRUE_RE.finditer(out)]
        check(len(colors) > 100, f"{name} 颜色样本太少: {len(colors)}")
        check(any(ok(c) for c in colors), f"{name}（{label}）没有出现对应颜色，样本: {colors[:5]}")


@test
def play_default_color_mode():
    """不指定颜色开关时按系统能力自动选，verbose 里要如实报告。"""
    rc, out, err, _ = run([
        os.path.join(WORK, "clip25_noaudio.mp4"), "-f", "15", "--mute", "--no-wait", "-v",
    ])
    body = text(err)
    check(rc == 0, f"播放失败: {body[-800:]}")
    if "颜色: 真彩色" in body:
        check(b"\x1b[38;2;" in out, "报告真彩色，输出里却没有 38;2;")
    else:
        check("颜色: 256 色" in body, "verbose 应报告生效的颜色模式")
        check(b"\x1b[38;5;" in out, "报告 256 色，输出里却没有 38;5;")


@test
def play_accepts_play_without_render():
    """--play 只在有 -r 时有意义，单独给出也不该报错。"""
    rc, out, err, _ = run([
        os.path.join(WORK, "clip25_noaudio.mp4"), "-f", "15", "--mute", "--no-wait", "--play",
    ])
    check(rc == 0, f"单独 --play 不应失败: {text(err)[-800:]}")
    check(frames_in(out) >= 5, "仍然应该正常播放")


@test
def play_width_never_exceeds_window():
    """窗口宽度变化时不能靠自动换行硬排，否则整帧会往下滚成乱码。"""
    rc, out, err, _ = run([
        os.path.join(WORK, "clip25_noaudio.mp4"), "-f", "20",
        "--mute", "--no-wait",
    ])
    check(rc == 0, f"播放失败: {text(err)[-800:]}")
    frames = frame_texts(out)
    check(len(frames) >= 5, f"帧太少: {len(frames)}")
    for i, lines in enumerate(frames):
        for line in lines:
            check(len(line) <= 80, f"第 {i} 帧有 {len(line)} 列，超过回退窗口宽度 80")
        check(len(lines) == 24, f"第 {i} 帧行数应为 24，实际 {len(lines)}")
    check(b"\x1b[K" in out, "每行应有擦除到行尾的 \\x1b[K")


@test
def play_render_has_audio():
    """-r 加 --play 时终端里必须有声音（以前渲染分支根本不准备音频）。"""
    out_path = os.path.join(WORK, "r_play_audio.mp4")
    rc, out, err, _ = run([
        os.path.join(WORK, "clip30.mp4"), "-r", out_path, "-n", "320x240",
        "--play", "--no-wait", "-v",
    ], timeout=180)
    body = text(err)
    check(rc == 0, f"编码+播放失败: {body[-800:]}")
    check("音频: 已加载" in body, "-r --play 应加载音频，stderr 里没有加载记录")
    check(frames_in(out) >= 10, f"终端输出帧太少: {frames_in(out)}")
    check(os.path.getsize(out_path) > 0, "输出视频为空")
    info = probe(out_path)
    check(info["audio"] is not None, "输出视频应带音轨")


@test
def play_render_silent_source():
    out_path = os.path.join(WORK, "r_play_silent.mp4")
    rc, out, err, _ = run([
        os.path.join(WORK, "clip25_noaudio.mp4"), "-r", out_path, "-n", "320x240",
        "--play", "--no-wait", "-v",
    ])
    body = text(err)
    check(rc == 0, f"编码+播放失败: {body[-800:]}")
    check("音频:" in body, "verbose 下应报告音频状态")
    check("没有音轨" in body or "提取失败" in body, "源视频无音轨时应明确说明")


@test
def play_muted_skips_audio():
    rc, out, err, _ = run([
        os.path.join(WORK, "clip30.mp4"), "--mute", "--no-wait", "-v", "-f", "15",
    ])
    body = text(err)
    check(rc == 0, f"播放失败: {body[-800:]}")
    check("音频: 已加载" not in body, "--mute 时不该加载音频")


@test
def play_pacing_realtime():
    """2 秒的片子，实时播放耗时应落在合理范围，不能一秒钟刷完。"""
    rc, out, err, elapsed = run([
        os.path.join(WORK, "clip25_noaudio.mp4"), "-f", "20", "--mute", "--no-wait",
    ])
    check(rc == 0, f"播放失败: {text(err)[-800:]}")
    check(1.2 <= elapsed <= 8.0, f"2s 视频播放耗时 {elapsed:.2f}s，节奏不对")


@test
def resize_adapts_while_playing():
    """
    改窗口大小不能把固定网格硬排进新窗口 —— 那会换行、错位、最后糊成乱码。
    用 ASCIIPLAYER_CONSOLE_SIZE 钩子在播放途中模拟拖动窗口：
    100x30 -> 60x20 -> 120x40，每一帧的列宽/行数都要跟着变，编码分辨率不变。
    """
    size_file = os.path.join(WORK, "console_size.txt")
    out_path = os.path.join(WORK, "r_resize.mp4")
    stdout_path = os.path.join(WORK, "play_resize.out")

    def set_size(value):
        with open(size_file, "w", encoding="ascii") as f:
            f.write(value)

    env = os.environ.copy()
    env["ASCIIPLAYER_CONSOLE_SIZE"] = size_file
    set_size("100x30")

    with open(stdout_path, "wb") as out_f:
        proc = subprocess.Popen(
            [EXE, os.path.join(WORK, "clip30.mp4"), "-r", out_path, "-n", "320x240",
             "--play", "-f", "20", "--mute", "--no-wait", "--truecolor"],
            stdin=subprocess.DEVNULL,
            stdout=out_f,
            stderr=subprocess.PIPE,
            env=env,
            creationflags=CREATE_NO_WINDOW,
        )
        time.sleep(0.9)
        set_size("60x20")
        time.sleep(0.7)
        set_size("120x40")
        try:
            rc = proc.wait(timeout=120)
        except subprocess.TimeoutExpired:
            proc.kill()
            raise
        err = proc.stderr.read()

    check(rc == 0, f"编码+播放失败: {text(err)[-800:]}")

    check(os.path.getsize(out_path) > 0, "输出视频为空")
    info = probe(out_path)
    check(info["width"] == 320 and info["height"] == 240,
          f"编码网格应固定为 320x240，实际 {info['width']}x{info['height']}")

    with open(stdout_path, "rb") as f:
        data = f.read()

    # 结尾会打印「已输出: ...」，先把收尾序列之后的内容切掉
    data = data.split(b"\x1b[?25h")[0]
    frames = frame_texts(data)
    check(len(frames) >= 30, f"帧太少: {len(frames)}")

    dims = []
    for i, lines in enumerate(frames):
        if not lines:
            continue
        width = len(lines[0])
        dims.append((width, len(lines)))
        for line in lines:
            check(len(line) == width, f"第 {i} 帧行宽不一致: {len(line)} != {width}")
        check(len(lines) <= 40, f"第 {i} 帧有 {len(lines)} 行，超出最大窗口")

    check(any(dim == (100, 29) for dim in dims), f"没出现 100x30 的网格: {sorted(set(dims))[:8]}")
    check(any(dim == (60, 19) for dim in dims), f"没出现 60x20 的网格: {sorted(set(dims))[:8]}")
    check(any(dim == (120, 39) for dim in dims), f"没出现 120x40 的网格: {sorted(set(dims))[:8]}")

    # 顺序也要对：先 100，再 60，最后 120
    order = [w for w, _ in dims]
    first = {}
    for index, w in enumerate(order):
        first.setdefault(w, index)
    check(first.get(100, -1) < first.get(60, 1 << 30) < first.get(120, 1 << 30),
          f"网格变化顺序不对: 首次出现位置 {first}")

    # 尺寸切换时必须有清屏，否则残留上一帧的行会叠成乱码
    check(b"\x1b[2J" in data, "改变尺寸时应清屏")


@test
def play_verbose_reports_fps():
    rc, out, err, _ = run([
        os.path.join(WORK, "clip30.mp4"), "-f", "12", "--mute", "--no-wait", "-v",
    ])
    body = text(err)
    check(rc == 0, f"播放失败: {body[-800:]}")
    check("帧率: 12" in body, "verbose 应报告生效帧率 12")


# ---------------------------------------------------------------- 入口

def build():
    msbuild = shutil.which("msbuild")
    candidates = [
        msbuild,
        r"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        r"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        r"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        r"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
    ]
    msbuild = next((c for c in candidates if c and os.path.exists(c)), None)
    if not msbuild:
        raise SystemExit("找不到 MSBuild.exe，请用 --no-build 并提供已编译的 exe")

    print("编译 Release ...", flush=True)
    proc = subprocess.run(
        [msbuild, SLN, "/t:Build", "/p:Configuration=Release", "/p:Platform=Any CPU",
         "/v:minimal", "/nologo"],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        creationflags=CREATE_NO_WINDOW,
    )
    if proc.returncode != 0:
        print(text(proc.stdout))
        raise SystemExit("编译失败")


def main():
    global VERBOSE
    parser = argparse.ArgumentParser(description="ASCIIPlayer 回归测试")
    parser.add_argument("--no-build", action="store_true", help="不编译，直接用现有 exe")
    parser.add_argument("-k", "--filter", default="", help="只跑名字包含该字符串的测试")
    parser.add_argument("--verbose", "-v", action="store_true", help="打印命令与输出")
    parser.add_argument("--list", action="store_true", help="列出测试名")
    args = parser.parse_args()
    VERBOSE = args.verbose

    selected = [t for t in TESTS if args.filter in t.__name__]
    if args.list:
        for t in selected:
            print(t.__name__)
        return 0

    if not os.path.exists(FFMPEG):
        raise SystemExit(f"找不到 ffmpeg: {FFMPEG}")

    if not args.no_build:
        build()

    if not os.path.exists(EXE):
        raise SystemExit(f"找不到 exe: {EXE}")

    ensure_media()

    print(f"ASCIIPlayer 回归测试：{len(selected)} 项\n")

    failures = []
    started = time.time()

    for fn in selected:
        name = fn.__name__
        t0 = time.time()
        try:
            fn()
        except Failure as ex:
            failures.append((name, str(ex)))
            print(f"FAIL {name}  ({time.time() - t0:.2f}s)")
            print("     " + str(ex).replace("\n", "\n     "))
        except subprocess.TimeoutExpired:
            failures.append((name, "超时"))
            print(f"FAIL {name}  (超时)")
        except Exception as ex:  # noqa: BLE001 - 测试聚合，任何异常都算失败
            failures.append((name, repr(ex)))
            print(f"FAIL {name}  ({type(ex).__name__}: {ex})")
        else:
            print(f"PASS {name}  ({time.time() - t0:.2f}s)")

    total = time.time() - started
    print(f"\n{len(selected) - len(failures)} 通过, {len(failures)} 失败, 用时 {total:.1f}s")

    if failures:
        print("\n失败详情:")
        for name, detail in failures:
            print(f"  - {name}: {detail}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
