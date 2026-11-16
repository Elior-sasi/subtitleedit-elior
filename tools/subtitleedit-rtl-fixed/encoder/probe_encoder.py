"""Real one-frame capability smoke; never treats an encoder listing as proof."""
import argparse
import json
import subprocess

def probe(ffmpeg, codec, width, height, pixel_format, gpu=None):
    if width <= 0 or height <= 0 or max(width, height) > 32768:
        raise ValueError('Invalid dimensions')
    args = [ffmpeg, '-hide_banner', '-nostdin', '-v', 'error', '-f', 'lavfi',
            '-i', f'color=size={width}x{height}:rate=24', '-frames:v', '1',
            '-an', '-c:v', codec, '-pix_fmt', pixel_format]
    if gpu is not None:
        if not codec.endswith('_nvenc') or gpu < 0:
            raise ValueError('GPU index is only supported for NVENC')
        args += ['-gpu', str(gpu)]
    args += ['-f', 'null', '-']
    try:
        run = subprocess.run(args, capture_output=True, text=True, timeout=20)
        return {'codec': codec, 'available': run.returncode == 0,
                'exit_code': run.returncode, 'diagnostic': run.stderr[-8192:]}
    except (OSError, subprocess.TimeoutExpired) as error:
        return {'codec': codec, 'available': False, 'diagnostic': str(error)}

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--ffmpeg', default='ffmpeg')
    parser.add_argument('--codec', required=True)
    parser.add_argument('--width', type=int, default=1920)
    parser.add_argument('--height', type=int, default=1080)
    parser.add_argument('--pixel-format', default='yuv420p')
    parser.add_argument('--gpu', type=int)
    args = parser.parse_args()
    print(json.dumps(probe(args.ffmpeg, args.codec, args.width, args.height,
                           args.pixel_format, args.gpu)))
