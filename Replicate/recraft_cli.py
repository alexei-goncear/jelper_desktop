import argparse
import json
import os
import sys
from pathlib import Path
from typing import BinaryIO, Optional, Tuple, Union

import replicate

MODEL_NAME = "recraft-ai/recraft-crisp-upscale"
DEFAULT_MODE = "upscale16mp"
TOKEN_ENV_VAR = "REPLICATE_API_TOKEN"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Wrapper around Recraft Crisp Upscale on Replicate")
    parser.add_argument("--image", required=True, help="Path or URL of the source image")
    parser.add_argument("--output", required=True, help="Where to store the resulting image")
    parser.add_argument("--mode", default=DEFAULT_MODE, help="Upscale quality preset (default: %(default)s)")
    return parser.parse_args()


def ensure_token() -> str:
    token = os.environ.get(TOKEN_ENV_VAR)
    if not token:
        raise RuntimeError(f"Environment variable {TOKEN_ENV_VAR} is not set.")
    return token


def resolve_image_source(image_arg: str) -> Union[str, BinaryIO]:
    if image_arg.startswith("http://") or image_arg.startswith("https://"):
        return image_arg

    path = Path(image_arg).expanduser().resolve()
    if not path.exists():
        raise FileNotFoundError(f"Source image not found: {path}")

    return path.open("rb")


def pick_file_like(value) -> Optional[BinaryIO]:
    if hasattr(value, "read") and callable(value.read):
        return value

    if isinstance(value, (list, tuple)):
        for item in value:
            file_like = pick_file_like(item)
            if file_like:
                return file_like

    return None


def read_remote_output(output) -> Tuple[bytes, Optional[str]]:
    file_like = pick_file_like(output)
    if not file_like:
        raise RuntimeError("Replicate response did not return a file-like object.")

    data = file_like.read()
    remote_url = getattr(file_like, "url", None)
    return data, remote_url


def save_bytes(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "wb") as fp:
        fp.write(data)


def main() -> int:
    args = parse_args()
    token = ensure_token()
    client = replicate.Client(api_token=token)

    image_source = resolve_image_source(args.image)
    try:
        inputs = {
            "image": image_source,
            "upscale": args.mode,
        }
        output = client.run(MODEL_NAME, input=inputs)
    finally:
        if hasattr(image_source, "close"):
            image_source.close()

    data, remote_url = read_remote_output(output)
    output_path = Path(args.output).expanduser().resolve()
    save_bytes(output_path, data)

    result = {
        "status": "ok",
        "output_path": str(output_path),
        "remote_url": remote_url,
        "message": f"mode={args.mode}",
    }

    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        error = {
            "status": "error",
            "error": str(exc),
        }
        print(json.dumps(error, ensure_ascii=False), file=sys.stderr)
        raise SystemExit(1)
