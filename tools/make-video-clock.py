"""Generate the independent rendered-frame clock used by browser sync checks.

Usage: npm run make:video-clock -- --ffmpeg /path/to/ffmpeg
Requires Pillow and an FFmpeg build with libx264. No source game video is used.
"""

import argparse
import subprocess
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


WIDTH, HEIGHT = 1280, 720
FPS, FRAME_COUNT = 60, 630


def make_frame(index: int) -> Image.Image:
    image = Image.new("RGB", (WIDTH, HEIGHT), (220, 220, 220))
    draw = ImageDraw.Draw(image)
    large = ImageFont.load_default(size=64)
    small = ImageFont.load_default(size=28)
    draw.text((WIDTH // 2, 70), f"FRAME {index:03d}", font=large,
              fill="black", anchor="mt")
    draw.text((WIDTH // 2, 155), f"{index / FPS:.6f} seconds", font=large,
              fill="black", anchor="mt")
    for bit in range(10):
        left = 100 + bit * 80
        fill = "white" if (index >> bit) & 1 else "black"
        # Pillow rectangles include the final coordinate: these are 60 x 100.
        draw.rectangle((left, 280, left + 59, 379), fill=fill)
        draw.text((left + 30, 405), str(bit), font=small,
                  fill="black", anchor="mt")
    draw.text((WIDTH // 2, 500), "FRAME INDEX / BINARY / LSB FIRST", font=small,
              fill="black", anchor="mt")
    draw.text((WIDTH // 2, 565), "60 fps | 630 frames | 10.5 seconds", font=small,
              fill="black", anchor="mt")
    return image


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", required=True, help="FFmpeg executable path")
    parser.add_argument("--output", type=Path,
                        default=Path(__file__).resolve().parents[1] / "tests/fixtures/video-clock.mp4")
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    command = [args.ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
               "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", "1280x720",
               "-framerate", str(FPS), "-i", "pipe:0",
               "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
               "-t", str(FRAME_COUNT / FPS), "-c:a", "aac", "-b:a", "160k",
               "-c:v", "libx264", "-preset", "medium", "-crf", "18",
               "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(args.output)]
    with subprocess.Popen(command, stdin=subprocess.PIPE) as process:
        assert process.stdin is not None
        try:
            for index in range(FRAME_COUNT):
                process.stdin.write(make_frame(index).tobytes())
        finally:
            process.stdin.close()
        if process.wait() != 0:
            raise RuntimeError("FFmpeg failed to encode the video clock")
    print(f"Generated {args.output}: {WIDTH}x{HEIGHT}, {FPS} fps, "
          f"{FRAME_COUNT} frames, {FRAME_COUNT / FPS:g}s, H.264/yuv420p, silent AAC stereo")


if __name__ == "__main__":
    main()
